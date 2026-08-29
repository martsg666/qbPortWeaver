using System.IO.Pipes;

namespace qbPortWeaver;

/// <summary>
/// Result of a helper service request. <see cref="Completed"/> is true when the helper
/// processed the command and returned its WARN/ERROR counts; false when the request never
/// reached the helper or was rejected by it. <see cref="IsRejected"/> distinguishes the
/// session-token rejection case from a generic helper-unreachable failure.
/// </summary>
internal readonly record struct HelperResult(bool Completed, int WarnCount, int ErrorCount, bool IsRejected, int ProtocolVersion = 0)
{
    // Helper unreachable, target invalid, session token unavailable, or response unparseable.
    public static HelperResult Failed => default; // Completed=false, IsRejected=false
    // Helper actively rejected the command (session token mismatch).
    public static HelperResult Rejected => new(Completed: false, WarnCount: 0, ErrorCount: 0, IsRejected: true);
    // Helper processed the command and returned its WARN/ERROR counts. ProtocolVersion is 0 when the
    // response carried no version key, which means a helper built before versioning existed - the
    // one case that distinguishes an out-of-date peer from an unreachable or broken one.
    public static HelperResult Ok(int warn, int error, int protocolVersion) =>
        new(Completed: true, WarnCount: warn, ErrorCount: error, IsRejected: false, ProtocolVersion: protocolVersion);

    /// <summary>True when the helper answered but speaks an older protocol than this build, so it
    /// predates the running app and should be reinstalled. False for an unreachable helper, which is
    /// a different problem with a different fix.</summary>
    public bool IsHelperOutOfDate => Completed && ProtocolVersion < HelperProtocol.Version;

    // Upper bound on how many alerts a single helper response may replay. The counts come from the
    // helper's own per-request log counters and are realistically single digits, but ParseResult
    // accepts any int the helper sends - and these counts drive a loop that raises a UI event each
    // time. Clamping keeps a garbled magnitude from wedging the UI thread on the recovery path,
    // completing the garble defence ParseResult already applies to the response's structure.
    private const int MaxAlertReplay = 100;

    // Surfaces helper-side log entries in the tray badge, tooltip, and balloon tip.
    // The entries themselves are already in the shared log file (written by the helper directly).
    public void RaiseLogAlerts()
    {
        if (IsRejected)
        {
            LogManager.Instance.LogMessage("Helper service rejected the command - session token mismatch", LogLevel.Warn);
            return;
        }
        ReportProtocolVersion();

        // Negative counts need no guard - the loop simply does not execute.
        for (int i = 0; i < Math.Min(WarnCount, MaxAlertReplay); i++) LogManager.Instance.NotifyExternalWarnOrError(LogLevel.Warn);
        for (int i = 0; i < Math.Min(ErrorCount, MaxAlertReplay); i++) LogManager.Instance.NotifyExternalWarnOrError(LogLevel.Error);
    }

    // State key for LogStateChange. The condition persists until the user reinstalls, and recovery
    // can run repeatedly in the meantime, so this reports on the transition rather than once per
    // recovery - the same treatment every other standing condition gets.
    private const string ProtocolStateKey = "helper.protocolVersion";

    // Says which peer is out of step, when the helper answered at all. Only a completed response
    // carries this information: an unreachable helper is a different problem with a different fix,
    // and RaiseLogAlerts has already reported a rejection before reaching here.
    private void ReportProtocolVersion()
    {
        if (!Completed) return;

        if (IsHelperOutOfDate)
        {
            // Covers both a helper predating versioning (no key, so 0) and a genuinely older one.
            LogManager.Instance.LogStateChange(ProtocolStateKey,
                "The helper service is older than this version of qbPortWeaver - reinstall qbPortWeaver to update it. " +
                "Recovery still ran, but newer behaviour may be missing",
                LogLevel.Warn);
        }
        else if (ProtocolVersion > HelperProtocol.Version)
        {
            // The app is the older half, which happens after a downgrade: the MSI leaves the newer
            // helper installed. Worth saying plainly, because the obvious reading of the line above
            // would send the user to reinstall the thing that is already current.
            LogManager.Instance.LogStateChange(ProtocolStateKey,
                $"The helper service speaks a newer protocol (v{ProtocolVersion}) than this version of qbPortWeaver (v{HelperProtocol.Version}) - " +
                "this build is older than the installed helper",
                LogLevel.Warn);
        }
        else
        {
            // Clears the latch so a later mismatch is reported again rather than swallowed.
            LogManager.Instance.ClearLogState(ProtocolStateKey);
        }
    }
}

/// <summary>Sends privileged action requests to the helper Windows service via named pipe.</summary>
internal static class HelperServiceClient
{
    private const int PipeConnectTimeoutMs = 5000;
    // Bound for awaiting the helper's response. Covers the helper's pathological
    // restart path (stop ~45s + 5s pause + start ~30s = ~80s) with headroom. The
    // helper writes its response only after the action completes. (Named distinctly from
    // HelperPipeServer.RequestReadTimeoutMs, which bounds the opposite direction.)
    private const int ResponseTimeoutMs = 120_000;

    // Resolved once per session; the helper validates it against HKCU to reject unauthorized pipe
    // connections. Cached only on success, and that distinction is the whole point: this was a
    // Lazy<string>, and GetOrCreatePipeSessionToken reports a registry failure by *returning an empty
    // string* rather than throwing. Lazy treats that as a successful result and caches it forever, so a
    // single transient read - a hive still loading at logon, a moment of policy contention - disabled
    // auto-recovery for the entire process lifetime, with every later attempt failing on the empty-token
    // guard below and nothing ever looking again. Note LazyThreadSafetyMode.PublicationOnly would not
    // have helped: it retries after a thrown exception, and nothing is thrown here.
    // Same rule as VpnRegistryConfig's exe-path cache - write back only a real resolution.
    // volatile: written at most once from empty to a token, read from the sync loop and diagnostics.
    // A race costs one extra registry read; the read is idempotent (CreateSubKey then GetValue).
    private static volatile string? _sessionToken;

    private static string GetSessionToken()
    {
        if (_sessionToken is { Length: > 0 } cached) return cached;
        string token = RegistrySettingsManager.GetOrCreatePipeSessionToken();
        if (token.Length > 0) _sessionToken = token;
        return token;
    }

    /// <summary>Asks the helper service to stop and restart the Windows service with the given <paramref name="serviceName"/>.</summary>
    internal static Task<HelperResult> SendRestartAsync(string serviceName, CancellationToken cancellationToken = default) =>
        SendAsync(HelperProtocol.ActionRestart, serviceName, cancellationToken);

    /// <summary>Asks the helper service to disable and re-enable the named network adapter.</summary>
    internal static Task<HelperResult> SendCycleAdapterAsync(string adapterName, CancellationToken cancellationToken = default) =>
        SendAsync(HelperProtocol.ActionCycleAdapter, adapterName, cancellationToken);

    // Sends a pipe-delimited command to the helper service: action|target|sessionToken.
    // Reads back a single result line: warn=N|error=M (helper-side WARN/ERROR counts).
    // Honors <paramref name="cancellationToken"/> so app shutdown is not blocked while waiting for the helper.
    private static async Task<HelperResult> SendAsync(string action, string target, CancellationToken cancellationToken)
    {
        if (target.Contains('|') || target.Contains('\n') || target.Contains('\r'))
        {
            LogManager.Instance.LogMessage($"Cannot send '{action}' request: target '{target}' contains an invalid character", LogLevel.Warn);
            return HelperResult.Failed;
        }

        // Resolved per attempt rather than once: a failed read is not cached, so a later attempt
        // retries instead of inheriting the first failure for the life of the process.
        string sessionToken = GetSessionToken();
        if (string.IsNullOrEmpty(sessionToken))
        {
            LogManager.Instance.LogMessage($"Cannot send '{action}' request: session token unavailable (registry error)", LogLevel.Warn);
            return HelperResult.Failed;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(".", HelperProtocol.PipeName, PipeDirection.InOut);
            await pipe.ConnectAsync(PipeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true); // StreamReader lacks IAsyncDisposable; synchronous Dispose is safe here (flushes no writes)

            await writer.WriteLineAsync($"{action}|{target}|{sessionToken}".AsMemory(), cancellationToken).ConfigureAwait(false);
            LogManager.Instance.LogMessage($"Sent '{action}' request for '{target}'", LogLevel.Info);

            return ParseResult(await ReadResponseAsync(reader, action, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // app shutdown - let the caller observe cancellation
        }
        catch (TimeoutException)
        {
            // ConnectAsync's PipeConnectTimeoutMs elapsed without an available pipe instance. The
            // helper accepts one connection at a time, so this means either the helper is not
            // installed/running, or it is busy serving another request (e.g. an in-progress service
            // restart) and could not accept this one in time.
            LogManager.Instance.LogMessage("Could not reach helper service (pipe connection timed out) - it may not be installed, not running, or busy with another request", LogLevel.Warn);
            return HelperResult.Failed;
        }
        catch (UnauthorizedAccessException ex)
        {
            // The pipe ACL denied the connection (AuthenticatedUserSid grant is in place by default
            // so this typically indicates a corrupted install or modified ACL).
            LogManager.Instance.LogMessage($"Access denied connecting to helper service pipe: {ex.Message} - check the helper service installation", LogLevel.Warn);
            return HelperResult.Failed;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to reach helper service: {ex.Message}", LogLevel.Warn);
            return HelperResult.Failed;
        }
    }

    // Reads one line from the pipe with two cancellation sources: the caller's <paramref name="cancellationToken"/>
    // (shutdown) and an internal timeout. Distinguishes the two so timeout returns null while
    // shutdown propagates as OperationCanceledException.
    private static async Task<string?> ReadResponseAsync(StreamReader reader, string action, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(ResponseTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogManager.Instance.LogMessage($"Helper service response timed out for '{action}' request", LogLevel.Warn);
            return null;
        }
    }

    // Parses "warn=N|error=M" from the helper. Returns Failed if the line is missing or malformed.
    // Returns Rejected if the helper sent the rejected sentinel (session token mismatch).
    // Requires at least one recognised key to parse so future protocol garble does not silently
    // succeed as Ok(0, 0).
    private static HelperResult ParseResult(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return HelperResult.Failed;
        if (response == HelperProtocol.ResultRejectedSentinel) return HelperResult.Rejected;

        int warn = 0, error = 0, protocolVersion = 0;
        bool anyKeyParsed = false;
        foreach (var part in response.Split('|'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1], out int value)) continue;
            if (kv[0] == HelperProtocol.ResultWarnKey) { warn = value; anyKeyParsed = true; }
            else if (kv[0] == HelperProtocol.ResultErrorKey) { error = value; anyKeyParsed = true; }
            // Deliberately does not set anyKeyParsed: a response carrying only a version is not a
            // result, and treating it as one would report a completed action that never ran.
            else if (kv[0] == HelperProtocol.ResultVersionKey) protocolVersion = value;
        }
        return anyKeyParsed ? HelperResult.Ok(warn, error, protocolVersion) : HelperResult.Failed;
    }
}

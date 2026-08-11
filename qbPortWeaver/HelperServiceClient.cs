using System.IO.Pipes;

namespace qbPortWeaver;

/// <summary>
/// Result of a helper service request. <see cref="Completed"/> is true when the helper
/// processed the command and returned its WARN/ERROR counts; false when the request never
/// reached the helper or was rejected by it. <see cref="IsRejected"/> distinguishes the
/// session-token rejection case from a generic helper-unreachable failure.
/// </summary>
internal readonly record struct HelperResult(bool Completed, int WarnCount, int ErrorCount, bool IsRejected)
{
    // Helper unreachable, target invalid, session token unavailable, or response unparseable.
    public static HelperResult Failed => default; // Completed=false, IsRejected=false
    // Helper actively rejected the command (session token mismatch).
    public static HelperResult Rejected => new(Completed: false, WarnCount: 0, ErrorCount: 0, IsRejected: true);
    // Helper processed the command and returned its WARN/ERROR counts.
    public static HelperResult Ok(int warn, int error) => new(Completed: true, WarnCount: warn, ErrorCount: error, IsRejected: false);

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
        // Negative counts need no guard - the loop simply does not execute.
        for (int i = 0; i < Math.Min(WarnCount, MaxAlertReplay); i++) LogManager.Instance.NotifyExternalWarnOrError(LogLevel.Warn);
        for (int i = 0; i < Math.Min(ErrorCount, MaxAlertReplay); i++) LogManager.Instance.NotifyExternalWarnOrError(LogLevel.Error);
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

    // Resolved once per session; the helper validates it against HKCU to reject unauthorized pipe connections.
    private static readonly Lazy<string> _sessionToken = new(() => RegistrySettingsManager.GetOrCreatePipeSessionToken());

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

        if (string.IsNullOrEmpty(_sessionToken.Value))
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

            await writer.WriteLineAsync($"{action}|{target}|{_sessionToken.Value}".AsMemory(), cancellationToken).ConfigureAwait(false);
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

        int warn = 0, error = 0;
        bool anyKeyParsed = false;
        foreach (var part in response.Split('|'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1], out int value)) continue;
            if (kv[0] == HelperProtocol.ResultWarnKey) { warn = value; anyKeyParsed = true; }
            else if (kv[0] == HelperProtocol.ResultErrorKey) { error = value; anyKeyParsed = true; }
        }
        return anyKeyParsed ? HelperResult.Ok(warn, error) : HelperResult.Failed;
    }
}

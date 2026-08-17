using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace qbPortWeaver.HelperService;

/// <summary>
/// Listens on a named pipe and dispatches privileged session 0 actions requested by the
/// user-session tray app. Runs as a hosted background service inside the helper Windows service.
/// Protocol: one text line per connection, pipe-delimited: action|target|sessionToken.
/// Supported actions: restart (restart a Windows service by name) and
/// cycle-adapter (disable and re-enable a network adapter via netsh).
/// The session token is validated against the caller's HKCU registry value via pipe impersonation.
/// Note what this does and does not prove: because RunAsClient impersonates the *caller*,
/// Registry.CurrentUser resolves to the caller's own hive, so a match shows only that the caller can
/// read a value they own - which any authenticated user can arrange for themselves. The token is a
/// handshake that rejects malformed and stale connections, not an authentication boundary between
/// users; the actual boundary is the pipe ACL. See "Trust boundary" below for what is genuinely
/// trusted, and do not treat this line as a per-user gate when deciding whether to widen that ACL.
/// The log file path is derived from the caller's HKCU Volatile Environment during impersonation
/// rather than being caller-supplied, so no path validation is needed.
///
/// Trust boundary: the helper trusts any caller that (a) has access to the named pipe ACL
/// (AuthenticatedUserSid) and (b) can read the pipeSessionToken value from their own HKCU hive.
/// In practice that is any authenticated user on the machine - neither condition narrows it
/// further, since the ACL grants that whole group and the token is read from the caller's own
/// hive, which any user can populate for themselves. Once trusted, the caller can name any
/// Windows service for restart and any adapter name for cycle - so on a multi-user machine a
/// second, non-administrator user can have SYSTEM restart an arbitrary service or cycle any
/// adapter. The helper does not allowlist service names because the service
/// search terms themselves are user-configurable in HKCU; an attacker with user-level write
/// access to HKCU would simply rewrite the search term to point at any other service before
/// sending the restart request, so an allowlist sourced from HKCU adds no protection. A baked-in
/// allowlist would help but the realistic threat (malware already running as the user) implies
/// the attacker has user-scope access already, and the user-to-SYSTEM escalation is accepted as
/// the documented privilege boundary the helper crosses.
/// </summary>
internal sealed class HelperPipeServer(ILogger<HelperPipeServer> logger) : BackgroundService
{
    private const int PipeErrorRetryDelayMs = 1000;
    // Bound for reading the client's request line after a connection is accepted. (Named
    // distinctly from HelperServiceClient.ResponseTimeoutMs, which bounds the opposite direction.)
    private const int RequestReadTimeoutMs = 5_000;

    // Registry paths and keys for impersonated HKCU reads. Subkey names not in AppIdentity are session-environment specific.
    private const string VolatileEnvironmentKey = @"Volatile Environment";
    private const string LocalAppDataValue = "LOCALAPPDATA";

    private static readonly PipeSecurity PipeSecurity = CreatePipeSecurity();

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("qbPortWeaver Helper Service started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ServeOneConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pipe server error - retrying");

                // The backoff needs its own guard because it runs inside this handler, where the
                // loop's cancellation handler above can no longer absorb anything: a stop signalled
                // during the wait would sail straight out of ExecuteAsync. BackgroundService would
                // then report the stop as a Critical host failure, which is exactly the wrong
                // signal for an ordinary service stop that happened to land in the retry window.
                try
                {
                    await Task.Delay(PipeErrorRetryDelayMs, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        logger.LogInformation("qbPortWeaver Helper Service stopped");
    }

    private async Task ServeOneConnectionAsync(CancellationToken cancellationToken)
    {
        // The pipe ACL grants ReadWrite to all authenticated users so the standard-user
        // qbPortWeaver client can send commands to this SYSTEM-level helper service.
        using var pipe = NamedPipeServerStreamAcl.Create(
            HelperProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurity);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(RequestReadTimeoutMs);
        string? message;
        try
        {
            message = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Pipe connection read timed out - client connected but sent no data");
            return;
        }
        if (string.IsNullOrWhiteSpace(message)) return;

        // Split on pipe character - avoids ambiguity with colons in Windows paths (e.g. C:\...)
        var parts = message.Split('|', 3);
        if (parts.Length != 3)
        {
            logger.LogWarning("Received malformed pipe message ({PartCount} part(s), expected 3): '{Message}'", parts.Length, message);
            return;
        }

        var action = parts[0];
        var target = parts[1];
        var pipeSessionToken = parts[2];

        if (!TryReadClientHkcu(pipe, pipeSessionToken, out var logFilePath, out bool debugMode))
        {
            logger.LogWarning("Rejected pipe message: session token mismatch or could not derive log file path");
            try
            {
                // No ConfigureAwait on the disposal, unlike every other await here: applying it to the
                // resource yields a ConfiguredAsyncDisposable, which no longer exposes WriteLineAsync.
                // Keeping both would need a separate variable and a block-form await using, for no
                // effect - a worker service host has no SynchronizationContext to capture.
                await using var w = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await w.WriteLineAsync(HelperProtocol.ResultRejectedSentinel).ConfigureAwait(false);
            }
            catch (IOException) { } // NOSONAR S108 - client likely disconnected before we could send the rejection; nothing more to report
            return;
        }

        var helperLogger = new HelperLogger(logFilePath, debugMode);

        switch (action)
        {
            case HelperProtocol.ActionRestart:
                await AutoRecovery.RestartServiceAsync(target, helperLogger, cancellationToken).ConfigureAwait(false);
                break;

            case HelperProtocol.ActionCycleAdapter:
                await AutoRecovery.CycleAdapterAsync(target, helperLogger, cancellationToken).ConfigureAwait(false);
                break;

            default:
                helperLogger.LogMessage($"Rejected unknown action '{action}'", LogLevel.Warn);
                break;
        }

        // Send back the helper-side WARN/ERROR counts so the tray app can raise its log-alert
        // event for entries the helper wrote directly to the shared log file.
        try
        {
            // No ConfigureAwait on the disposal - see the equivalent write in ServeOneConnectionAsync above.
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync($"{HelperProtocol.ResultWarnKey}={helperLogger.WarnCount}|{HelperProtocol.ResultErrorKey}={helperLogger.ErrorCount}").ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to send result line back to pipe client");
        }
    }

    // Impersonates the pipe client to validate the session token and read what the helper needs from
    // the caller's HKCU hive: the log file path, and whether debug logging is switched on. Returns
    // false if the token is invalid, impersonation fails, or LocalAppData cannot be read. Using the
    // caller's own registry avoids trusting any caller-supplied path.
    //
    // The debug flag is read here rather than sent over the pipe because this is the only moment the
    // SYSTEM service can see a per-user HKCU value: the impersonation ends with this method, well
    // before the action runs. Reading it now and handing it to the logger lets the helper honour the
    // same switch the main app does, without widening the pipe message format.
    private bool TryReadClientHkcu(NamedPipeServerStream pipe, string pipeSessionToken, out string logFilePath, out bool debugMode)
    {
        logFilePath = string.Empty;
        debugMode = false;
        bool tokenValid = false;
        bool derivedDebugMode = false;
        string derivedPath = string.Empty; // captured by lambda; out params cannot be used inside lambdas
        try
        {
            pipe.RunAsClient(() =>
            {
                // Registry.CurrentUser resolves to the impersonated user's HKCU hive on .NET Core/5+
                // (via NtOpenKey under the thread's impersonation token). This is the documented
                // .NET behavior on Windows and holds for all supported targets. The alternative -
                // RegOpenCurrentUser P/Invoke - is only needed if this assumption ever breaks.
                using var appKey = Registry.CurrentUser.OpenSubKey(AppIdentity.AppRegistryKey);
                var expectedToken = appKey?.GetValue(AppIdentity.PipeSessionTokenKey) as string;
                // Use constant-time comparison to prevent timing side-channel attacks.
                // string.Equals returns early on the first mismatch, leaking token length/prefix
                // information to a local attacker who can measure pipe response times.
                // Constant-time comparison is hygiene for comparing a secret on a SYSTEM-level
                // dispatch path, not a boundary in itself: the hive read here belongs to the caller,
                // so a caller can always present a matching value. See the class summary - the real
                // boundary is the pipe ACL. Kept because leaking prefix/length of the tray app's
                // token to a co-located process is still worth avoiding, and it costs nothing.
                tokenValid = !string.IsNullOrEmpty(expectedToken) &&
                             !string.IsNullOrEmpty(pipeSessionToken) &&
                             CryptographicOperations.FixedTimeEquals(
                                 Encoding.UTF8.GetBytes(expectedToken),
                                 Encoding.UTF8.GetBytes(pipeSessionToken));

                if (tokenValid)
                {
                    using var envKey = Registry.CurrentUser.OpenSubKey(VolatileEnvironmentKey);
                    var localAppData = envKey?.GetValue(LocalAppDataValue) as string;
                    if (!string.IsNullOrEmpty(localAppData))
                        derivedPath = Path.Combine(localAppData, AppIdentity.AppName, AppIdentity.LogFileName);

                    // Stored as "True"/"False" by the main app. Anything unreadable or unparseable
                    // leaves debug logging off, which is the quieter and safer default.
                    using var extraKey = Registry.CurrentUser.OpenSubKey(
                        $@"{AppIdentity.SettingsRegistryKey}\{AppIdentity.ExtraSettingsSection}");
                    derivedDebugMode = extraKey?.GetValue(AppIdentity.DebugModeValueName) is string flag &&
                                       bool.TryParse(flag, out bool parsed) && parsed;
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pipe client impersonation failed");
        }
        logFilePath = derivedPath;
        debugMode = derivedDebugMode;
        return tokenValid && !string.IsNullOrEmpty(logFilePath);
    }
}

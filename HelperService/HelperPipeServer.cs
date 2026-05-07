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
/// The session token is validated against the caller's HKCU registry value via pipe impersonation
/// so that only the user running the tray app can send commands to this SYSTEM-level service.
/// The log file path is derived from the caller's HKCU Volatile Environment during impersonation
/// rather than being caller-supplied, so no path validation is needed.
/// </summary>
internal sealed class HelperPipeServer(ILogger<HelperPipeServer> logger) : BackgroundService
{
    internal const string HelperServicePipeName = "qbPortWeaverHelper"; // Must match AppConstants.HelperServicePipeName in qbPortWeaver
    private  const int    PipeErrorRetryDelayMs  = 1000;
    private  const int    PipeReadTimeoutMs      = 5_000;

    private const string ActionRestart      = "restart";       // Must match HelperServiceClient.ActionRestart in qbPortWeaver
    private const string ActionCycleAdapter = "cycle-adapter"; // Must match HelperServiceClient.ActionCycleAdapter in qbPortWeaver

    // Result line keys returned to the tray client. Must match HelperServiceClient.ResultWarnKey/ResultErrorKey.
    private const string ResultWarnKey  = "warn";
    private const string ResultErrorKey = "error";

    // Registry paths and keys for impersonated HKCU reads.
    // AppRegistryKey / PipeSessionTokenKey must match RegistrySettingsManager.AppKeyPath / KeyPipeSessionToken in qbPortWeaver.
    private const string AppRegistryKey         = @"Software\qbPortWeaver";
    private const string PipeSessionTokenKey    = "pipeSessionToken";
    private const string VolatileEnvironmentKey = @"Volatile Environment";
    private const string LocalAppDataValue      = "LOCALAPPDATA";

    // Log file path components - must match AppConstants.AppName / AppConstants.LogFileName in qbPortWeaver.
    private const string AppSubFolderName = "qbPortWeaver";
    private const string LogFileName      = "qbPortWeaver.log";

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
                await Task.Delay(PipeErrorRetryDelayMs, stoppingToken).ConfigureAwait(false);
            }
        }
        logger.LogInformation("qbPortWeaver Helper Service stopped");
    }

    private async Task ServeOneConnectionAsync(CancellationToken ct)
    {
        // The pipe ACL grants ReadWrite to all authenticated users so the standard-user
        // qbPortWeaver client can send commands to this SYSTEM-level helper service.
        using var pipe = NamedPipeServerStreamAcl.Create(
            HelperServicePipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize:  0,
            outBufferSize: 0,
            PipeSecurity);

        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

        using var reader  = new StreamReader(pipe, leaveOpen: true);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(PipeReadTimeoutMs);
        string? message;
        try
        {
            message = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Pipe connection read timed out - client connected but sent no data");
            return;
        }
        if (string.IsNullOrWhiteSpace(message)) return;

        // Split on pipe character - avoids ambiguity with colons in Windows paths (e.g. C:\...)
        var parts = message.Split('|', 3);
        if (parts.Length != 3)
        {
            logger.LogWarning("Received malformed pipe message");
            return;
        }

        var action          = parts[0];
        var target          = parts[1];
        var pipeSessionToken = parts[2];

        if (!TryReadClientHkcu(pipe, pipeSessionToken, out var logFilePath))
        {
            logger.LogWarning("Rejected pipe message: session token mismatch or could not derive log file path");
            try
            {
                await using var w = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await w.WriteLineAsync($"{ResultWarnKey}=0|{ResultErrorKey}=0").ConfigureAwait(false);
            }
            catch (IOException) { }
            return;
        }

        var helperLogger = new HelperLogger(logFilePath);

        switch (action)
        {
            case ActionRestart:
                await AutoRecovery.RestartServiceAsync(target, helperLogger).ConfigureAwait(false);
                break;

            case ActionCycleAdapter:
                await AutoRecovery.CycleAdapterAsync(target, helperLogger).ConfigureAwait(false);
                break;

            default:
                helperLogger.LogWarn($"Rejected unknown action '{action}'");
                break;
        }

        // Send back the helper-side WARN/ERROR counts so the tray app can raise its log-alert
        // event for entries the helper wrote directly to the shared log file.
        try
        {
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync($"{ResultWarnKey}={helperLogger.WarnCount}|{ResultErrorKey}={helperLogger.ErrorCount}").ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to send result line back to pipe client");
        }
    }

    // Impersonates the pipe client to validate the session token and derive the log file path
    // from the caller's HKCU hive. Returns false if the token is invalid, impersonation fails,
    // or LocalAppData cannot be read. Using the caller's own registry avoids trusting any
    // caller-supplied path.
    private bool TryReadClientHkcu(NamedPipeServerStream pipe, string pipeSessionToken, out string logFilePath)
    {
        logFilePath = string.Empty;
        bool   tokenValid  = false;
        string derivedPath = string.Empty; // captured by lambda; out params cannot be used inside lambdas
        try
        {
            pipe.RunAsClient(() =>
            {
                using var appKey  = Registry.CurrentUser.OpenSubKey(AppRegistryKey);
                var expectedToken = appKey?.GetValue(PipeSessionTokenKey) as string;
                // Use constant-time comparison to prevent timing side-channel attacks.
                // string.Equals returns early on the first mismatch, leaking token length/prefix
                // information to a local attacker who can measure pipe response times.
                // The primary defence is the HKCU ACL (only the session owner can read the token),
                // but FixedTimeEquals adds defence-in-depth for a SYSTEM-level dispatch gate.
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
                        derivedPath = Path.Combine(localAppData, AppSubFolderName, LogFileName);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pipe client impersonation failed");
        }
        logFilePath = derivedPath;
        return tokenValid && !string.IsNullOrEmpty(logFilePath);
    }
}

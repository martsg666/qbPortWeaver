using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private  const int    PipeErrorRetryDelayMs = 1000;

    private const string ActionRestart      = "restart";       // Must match HelperServiceClient.ActionRestart in qbPortWeaver
    private const string ActionCycleAdapter = "cycle-adapter"; // Must match HelperServiceClient.ActionCycleAdapter in qbPortWeaver

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
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize:  0,
            outBufferSize: 0,
            PipeSecurity);

        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, leaveOpen: true);
        var message = await reader.ReadLineAsync(ct).ConfigureAwait(false);
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

        // Impersonate the pipe client to read from their HKCU hive:
        // validate the session token and derive the log file path from LocalAppData.
        // Deriving the path from the user's own registry avoids trusting any caller-supplied path.
        var tokenValid  = false;
        var logFilePath = string.Empty;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var appKey      = Registry.CurrentUser.OpenSubKey(AppRegistryKey);
                var expectedToken     = appKey?.GetValue(PipeSessionTokenKey) as string;
                tokenValid = !string.IsNullOrEmpty(expectedToken) &&
                             !string.IsNullOrEmpty(pipeSessionToken) &&
                             string.Equals(expectedToken, pipeSessionToken, StringComparison.Ordinal);

                if (tokenValid)
                {
                    using var envKey = Registry.CurrentUser.OpenSubKey(VolatileEnvironmentKey);
                    var localAppData = envKey?.GetValue(LocalAppDataValue) as string;
                    if (!string.IsNullOrEmpty(localAppData))
                        logFilePath = Path.Combine(localAppData, AppSubFolderName, LogFileName);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning("Pipe client impersonation failed: {Message}", ex.Message);
        }

        if (!tokenValid)
        {
            logger.LogWarning("Rejected pipe message: session token mismatch");
            return;
        }

        if (string.IsNullOrEmpty(logFilePath))
        {
            logger.LogWarning("Rejected pipe message: could not derive log file path");
            return;
        }

        var helperLogger = new HelperLogger(logFilePath);

        switch (action)
        {
            case ActionRestart:
                if (string.IsNullOrWhiteSpace(target))
                {
                    logger.LogWarning("Rejected restart request with empty service name");
                    return;
                }
                await AutoRecovery.RestartServiceAsync(target, helperLogger).ConfigureAwait(false);
                break;

            case ActionCycleAdapter:
                if (string.IsNullOrWhiteSpace(target))
                {
                    logger.LogWarning("Rejected cycle-adapter request with empty adapter name");
                    return;
                }
                await AutoRecovery.CycleAdapterAsync(target, helperLogger).ConfigureAwait(false);
                break;

            default:
                logger.LogWarning("Rejected unknown action '{Action}'", action);
                break;
        }
    }
}

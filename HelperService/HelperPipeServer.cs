using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace qbPortWeaver.HelperService;

/// <summary>
/// Listens on a named pipe and dispatches privileged session 0 actions requested by the
/// user-session tray app. Runs as a hosted background service inside the helper Windows service.
/// Protocol: one text line per connection, pipe-delimited: action|target|logFilePath.
/// Supported actions: restart (restart a Windows service by name) and
/// cycle-adapter (disable and re-enable a network adapter via netsh).
/// The log file path is sent per-call so the helper writes into the same log file as the
/// tray app, regardless of which user profile is active.
/// </summary>
internal sealed class HelperPipeServer(ILogger<HelperPipeServer> logger) : BackgroundService
{
    internal const string HelperServicePipeName  = "qbPortWeaverHelper"; // Must match AppConstants.HelperServicePipeName in qbPortWeaver
    private  const string ExpectedLogFileName   = "qbPortWeaver.log";   // Must match AppConstants.LogFileName in qbPortWeaver
    private  const int    PipeErrorRetryDelayMs = 1000;

    private const string ActionRestart      = "restart";       // Must match HelperServiceClient.ActionRestart in qbPortWeaver
    private const string ActionCycleAdapter = "cycle-adapter"; // Must match HelperServiceClient.ActionCycleAdapter in qbPortWeaver

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

        var action      = parts[0];
        var target      = parts[1];
        var logFilePath = parts[2];

        // Canonicalize to resolve any ".." segments before validation, preventing path traversal.
        // Validate the log file path to prevent a caller-controlled path being written
        // by this SYSTEM-level process to an arbitrary location. We check the filename,
        // that the path is rooted under the Windows user profiles directory, and that it
        // falls within the expected app data subfolder.
        logFilePath = Path.GetFullPath(logFilePath);
        var profilesDir = (Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList",
            "ProfilesDirectory", null) as string) ?? @"%SystemDrive%\Users";
        var usersRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(profilesDir));
        if (!Path.GetFileName(logFilePath).Equals(ExpectedLogFileName, StringComparison.OrdinalIgnoreCase)
            || !logFilePath.StartsWith(usersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || logFilePath.IndexOf(@"\AppData\Local\qbPortWeaver\", StringComparison.OrdinalIgnoreCase) < 0)
        {
            logger.LogWarning("Rejected unexpected log file path '{Path}'", logFilePath);
            return;
        }

        // Path.GetFullPath resolves ".." but NOT symlinks on Windows. On systems with Developer Mode
        // enabled, a standard user can create symlinks, so a symlink anywhere along the path would
        // pass name/directory validation but redirect writes to an attacker-chosen location under
        // SYSTEM privileges. Walk the entire path to the drive root to catch reparse points at
        // any level (e.g. AppData, AppData\Local, or the user profile directory itself).
        if (IsReparsePoint(logFilePath))
        {
            logger.LogWarning("Rejected log file path containing a reparse point '{Path}'", logFilePath);
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

    // Returns true if a reparse point (symlink, junction, etc.) exists anywhere along the path
    // from the file up to the drive root. This prevents an attacker from planting a symlink at
    // any intermediate directory (e.g. AppData, AppData\Local) to redirect SYSTEM writes.
    private static bool IsReparsePoint(string filePath)
    {
        var current = filePath;
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break; // reached drive root
            current = parent;
        }
        return false;
    }
}

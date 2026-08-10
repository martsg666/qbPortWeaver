using System.ComponentModel;
using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>
/// Handles auto-recovery from the user session.
/// Privileged operations (service restart, adapter cycling) are delegated to the helper
/// service via named pipe. Client process restart runs directly in the user session.
/// </summary>
internal static class AutoRecoveryManager
{
    private const int ClientRestartDelayMs = 2000;

    /// <summary>
    /// Asks the helper service to stop and restart the Windows service for the given VPN
    /// provider, then restarts the matching client process in the current user session.
    /// Blocks until the helper finishes the service stop/start cycle (the pipe response
    /// serves as synchronization so no head-start delay is needed before the client restart).
    /// </summary>
    internal static async Task TriggerRestartAsync(string providerKeyword, CancellationToken cancellationToken = default)
    {
        var provider = VpnProviderRegistry.FindByKeyword(providerKeyword);
        if (provider is null)
        {
            LogManager.Instance.LogMessage($"Unknown VPN provider '{providerKeyword}' - skipping recovery", LogLevel.Warn);
            return;
        }

        string? serviceName = provider.Config.FindServiceName();
        if (serviceName is null)
        {
            LogManager.Instance.LogMessage($"Could not find Windows service for '{providerKeyword}' - skipping recovery", LogLevel.Warn);
            return;
        }

        var restartResult = await HelperServiceClient.SendRestartAsync(serviceName, cancellationToken).ConfigureAwait(false);
        restartResult.RaiseLogAlerts();

        // Only restart the VPN client app if the helper actually completed the service restart.
        // If the helper was unreachable, rejected the request, or reported errors, killing the
        // VPN client UI alone does not fix the underlying VPN service - it just closes the user's
        // VPN client window for nothing.
        // The lower layer (HelperServiceClient / RaiseLogAlerts) already logged the specific cause
        // for unreachable / rejected; this line communicates the consequence (skipping the client
        // restart) with a reason tailored to each failure mode so the two log lines do not look
        // like duplicates.
        if (!restartResult.Completed || restartResult.ErrorCount > 0)
        {
            string reason;
            if (restartResult.IsRejected)
                reason = "helper service rejected the command (see prior log entry)";
            else if (!restartResult.Completed)
                reason = "helper service was unreachable (see prior log entry)";
            else
                reason = $"helper service reported {restartResult.ErrorCount} error{(restartResult.ErrorCount == 1 ? "" : "s")} during the service restart (see helper log entries)";
            LogManager.Instance.LogMessage($"Skipping VPN client app restart for '{providerKeyword}' - {reason}", LogLevel.Warn);
            return;
        }

        await RestartClientProcessAsync(provider.Config.GetClientProcessName(), provider.Config.GetClientExePath, cancellationToken).ConfigureAwait(false);
        LogManager.Instance.LogMessage($"Recovery completed for '{providerKeyword}'", LogLevel.Info);
    }

    /// <summary>
    /// Asks the helper service to disable and re-enable the named network adapter.
    /// No client process restart is involved.
    /// </summary>
    internal static async Task TriggerCycleAdapterAsync(string adapterName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            LogManager.Instance.LogMessage("No adapter name available - skipping adapter cycle", LogLevel.Warn);
            return;
        }

        LogManager.Instance.LogMessage($"Cycling adapter '{adapterName}'", LogLevel.Info);
        var cycleResult = await HelperServiceClient.SendCycleAdapterAsync(adapterName, cancellationToken).ConfigureAwait(false);
        cycleResult.RaiseLogAlerts();

        if (!cycleResult.Completed)
            LogManager.Instance.LogMessage($"Adapter cycle did not complete for '{adapterName}'", LogLevel.Warn);
        else if (cycleResult.ErrorCount == 0)
            LogManager.Instance.LogMessage($"Adapter cycle completed for '{adapterName}'", LogLevel.Info);
        else
            LogManager.Instance.LogMessage($"Adapter cycle for '{adapterName}' completed with {cycleResult.ErrorCount} error{(cycleResult.ErrorCount == 1 ? "" : "s")} - see helper log entries", LogLevel.Warn);
    }

    // Kills all instances of the named client process (capturing the exe path first),
    // waits briefly, then relaunches it. Runs in the main app's user session - no
    // elevation or WTS token manipulation needed.
    // If the process is not running, falls back to the registry-derived exe path via getInstalledExePath.
    private static async Task RestartClientProcessAsync(string processName, Func<string?> getInstalledExePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            LogManager.Instance.LogDebug("AutoRecoveryManager.RestartClientProcessAsync: Process name is empty - skipping");
            return;
        }

        string? exePath = null;
        try
        {
            exePath = KillClientProcesses(processName);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to kill client process '{processName}': {ex.Message}", LogLevel.Warn);
        }

        // Fall back to registry-derived exe path if the process was not running
        if (exePath is null)
        {
            exePath = getInstalledExePath();
            if (exePath is not null)
                LogManager.Instance.LogDebug($"AutoRecoveryManager.RestartClientProcessAsync: Using registry-discovered EXE path for '{processName}': {exePath}");
        }

        if (exePath is null)
        {
            LogManager.Instance.LogMessage($"No EXE path available for '{processName}' - cannot restart client process", LogLevel.Error);
            return;
        }

        await Task.Delay(ClientRestartDelayMs, cancellationToken).ConfigureAwait(false);
        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true })?.Dispose();
            LogManager.Instance.LogMessage($"Restarted client process '{processName}'", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to restart client process '{processName}': {ex.Message}", LogLevel.Error);
        }
    }

    // Kills all running instances of the named process and returns the exe path captured before killing.
    // Returns null if no instances were running.
    private static string? KillClientProcesses(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            string? exePath = null;
            try
            {
                exePath = processes.FirstOrDefault()?.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // MainModule access can throw Win32Exception on 32/64-bit mismatch or access denial,
                // or InvalidOperationException if the process exited between GetProcessesByName and here.
                // Leave exePath null so the registry fallback path is used below.
                LogManager.Instance.LogDebug($"AutoRecoveryManager.KillClientProcesses: Could not read exe path for '{processName}': {ex.Message}");
            }
            foreach (var p in processes)
            {
                try
                {
                    AppConstants.KillProcess(p, $"Client process '{processName}'");
                }
                catch (Exception ex) { LogManager.Instance.LogDebug($"AutoRecoveryManager.KillClientProcesses: Kill '{processName}': {ex.Message}"); }
            }
            // Discriminate on whether anything was running, not on whether the exe path was readable.
            // exePath is also null when instances *were* killed but MainModule threw (the 32/64-bit
            // mismatch and access-denied cases handled above), and VPN clients commonly run elevated -
            // so using it here would report "was not running" immediately after killing them.
            if (processes.Length > 0)
                LogManager.Instance.LogMessage($"Killed client process '{processName}'", LogLevel.Info);
            else
                LogManager.Instance.LogDebug($"AutoRecoveryManager.KillClientProcesses: Client process '{processName}' was not running");
            return exePath;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }
}

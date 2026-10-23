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
                reason = $"helper service reported {TextFormat.Pluralize(restartResult.ErrorCount, "error")} during the service restart (see helper log entries)";
            LogManager.Instance.LogMessage($"Skipping VPN client app restart for '{providerKeyword}' - {reason}", LogLevel.Warn);
            return;
        }

        bool clientRelaunched = await RestartClientProcessAsync(provider.Config.GetClientProcessName(), provider.Config.GetClientExePath, cancellationToken).ConfigureAwait(false);
        // The service restart did complete either way; the terminal line says whether the client
        // app came back with it, so a failed relaunch (already logged at Error above) is not
        // followed by a line that reads as a clean outcome.
        if (clientRelaunched)
            LogManager.Instance.LogMessage($"Recovery completed for '{providerKeyword}'", LogLevel.Info);
        else
            LogManager.Instance.LogMessage($"Recovery for '{providerKeyword}' restarted the service but could not relaunch the VPN client app - start it by hand if it is not running", LogLevel.Warn);
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

        // No "Cycling adapter" line here: the helper logs it, as it does "Restarting service" for a restart.
        var cycleResult = await HelperServiceClient.SendCycleAdapterAsync(adapterName, cancellationToken).ConfigureAwait(false);
        cycleResult.RaiseLogAlerts();

        // Every problem on the helper's cycle path (netsh failing, the disable or the re-enable not taking)
        // is logged as a Warn, so the outcome counts Warns as well as Errors: a cycle that left the adapter
        // disabled must not end with a "completed" line. The restart path differs - it verifies the service
        // state at the end and reports a failure as an Error.
        int problems = cycleResult.WarnCount + cycleResult.ErrorCount;
        if (!cycleResult.Completed)
            LogManager.Instance.LogMessage($"Adapter cycle did not complete for '{adapterName}'", LogLevel.Warn);
        else if (problems == 0)
            LogManager.Instance.LogMessage($"Adapter cycle completed for '{adapterName}'", LogLevel.Info);
        else
            LogManager.Instance.LogMessage($"Adapter cycle for '{adapterName}' reported {TextFormat.Pluralize(problems, "problem")} - see helper log entries and check that the adapter is enabled", LogLevel.Warn);
    }

    // Kills all instances of the named client process (capturing the exe path first),
    // waits briefly, then relaunches it. Runs in the main app's user session - no
    // elevation or WTS token manipulation needed.
    // If the process is not running, falls back to the registry-derived exe path via getInstalledExePath.
    // Returns false only when a relaunch was due and did not happen (no exe path, or the start failed);
    // a provider without a client process has nothing to relaunch and counts as done.
    private static async Task<bool> RestartClientProcessAsync(string processName, Func<string?> getInstalledExePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            LogManager.Instance.LogDebug("AutoRecoveryManager.RestartClientProcessAsync: Process name is empty - skipping");
            return true;
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
            return false;
        }

        // Not atomic across cancellation: a shutdown during this delay leaves the client killed and
        // never restarted, so the user finds their VPN client closed with the log ending at "Killed
        // client process" - and with a killswitch enabled that means no network until they notice.
        // Accepted rather than fixed: restarting during teardown risks orphaning a process on the way
        // out, passing CancellationToken.None here would make every shutdown wait on a launch that may
        // fail anyway, and the kill cannot be deferred because it is what frees the executable. The
        // exposure is the two seconds below.
        await Task.Delay(ClientRestartDelayMs, cancellationToken).ConfigureAwait(false);
        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true })?.Dispose();
            LogManager.Instance.LogMessage($"Restarted client process '{processName}'", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to restart client process '{processName}': {ex.Message}", LogLevel.Error);
            return false;
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
                    ProcessControl.KillProcess(p, $"Client process '{processName}'");
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

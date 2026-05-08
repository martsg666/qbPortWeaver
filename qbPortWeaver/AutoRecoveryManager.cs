using System.ComponentModel;
using System.Diagnostics;

namespace qbPortWeaver
{
    /// <summary>
    /// Handles auto-recovery from the user session.
    /// Privileged operations (service restart, adapter cycling) are delegated to the helper
    /// service via named pipe. Client process restart runs directly in the user session.
    /// </summary>
    internal static class AutoRecoveryManager
    {
        private const int ClientRestartDelayMs = 2000;

        private readonly record struct ProviderEntry(
            string        ProviderKeyword,
            Func<string>  GetClientProcessName,
            Func<string?>? GetInstalledExePath,
            Func<string?> FindServiceName);

        // Maps a provider token to the client process that must be restarted alongside the service.
        // GetInstalledExePath resolves the exe path from the service registry entry when the process is not running.
        private static readonly ProviderEntry[] _clientProcessMap =
        [
            new(RegistrySettingsManager.VpnProviderProtonVpn, ProtonVpnManager.Config.GetClientProcessName, ProtonVpnManager.Config.GetClientExePath, ProtonVpnManager.Config.FindServiceName),
            new(RegistrySettingsManager.VpnProviderPia,       PiaVpnManager.Config.GetClientProcessName,    PiaVpnManager.Config.GetClientExePath,    PiaVpnManager.Config.FindServiceName),
        ];

        /// <summary>
        /// Asks the helper service to stop and restart the Windows service for the given VPN
        /// provider, then restarts the matching client process in the current user session.
        /// Blocks until the helper finishes the service stop/start cycle (the pipe response
        /// serves as synchronization so no head-start delay is needed before the client restart).
        /// </summary>
        internal static async Task TriggerRestartAsync(string providerKeyword, CancellationToken cancellationToken = default)
        {
            var entry = _clientProcessMap.FirstOrDefault(e => e.ProviderKeyword.Equals(providerKeyword, StringComparison.OrdinalIgnoreCase));
            if (entry.FindServiceName is null)
            {
                LogManager.Instance.LogMessage($"Unknown VPN provider '{providerKeyword}' - skipping recovery", LogLevel.Warn);
                return;
            }

            string? serviceName = entry.FindServiceName.Invoke();
            if (serviceName is null)
            {
                LogManager.Instance.LogMessage($"Could not find Windows service for '{providerKeyword}' - skipping recovery", LogLevel.Warn);
                return;
            }

            var restartResult = await HelperServiceClient.SendRestartAsync(serviceName, cancellationToken).ConfigureAwait(false);
            RaiseHelperLogAlerts(restartResult);
            await RestartClientProcessAsync(entry.GetClientProcessName(), entry.GetInstalledExePath, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Asks the helper service to disable and re-enable the named network adapter.
        /// No client process restart is involved.
        /// </summary>
        internal static async Task TriggerCycleAdapterAsync(string adapterName, CancellationToken cancellationToken = default)
        {
            var cycleResult = await HelperServiceClient.SendCycleAdapterAsync(adapterName, cancellationToken).ConfigureAwait(false);
            RaiseHelperLogAlerts(cycleResult);
        }

        private static void RaiseHelperLogAlerts(HelperResult result) => result.RaiseLogAlerts();

        // Kills all instances of the named client process (capturing the exe path first),
        // waits briefly, then relaunches it. Runs in the main app's user session - no
        // elevation or WTS token manipulation needed.
        // If the process is not running, falls back to the registry-derived exe path via getInstalledExePath.
        private static async Task RestartClientProcessAsync(string processName, Func<string?>? getInstalledExePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                LogManager.Instance.LogDebug("AutoRecoveryManager.RestartClientProcessAsync: processName is empty - skipping");
                return;
            }

            string? exePath = null;
            try
            {
                var processes = Process.GetProcessesByName(processName);
                try
                {
                    try
                    {
                        exePath = processes.FirstOrDefault()?.MainModule?.FileName;
                    }
                    catch (Win32Exception ex)
                    {
                        // MainModule access can throw on 32/64-bit mismatch or access denial.
                        // Leave exePath null so the registry fallback path is used below.
                        LogManager.Instance.LogDebug($"AutoRecoveryManager.RestartClientProcessAsync: Could not read exe path for '{processName}': {ex.Message}");
                    }
                    foreach (var p in processes)
                    {
                        try
                        {
                            if (!AppConstants.KillProcess(p))
                                LogManager.Instance.LogMessage($"Client process '{processName}' (PID {p.Id}) still running after kill attempts", LogLevel.Warn);
                        }
                        catch (Exception ex) { LogManager.Instance.LogDebug($"AutoRecoveryManager.RestartClientProcessAsync: Kill '{processName}': {ex.Message}"); }
                    }
                    if (exePath is not null)
                        LogManager.Instance.LogMessage($"Killed client process '{processName}'", LogLevel.Info);
                    else
                        LogManager.Instance.LogDebug($"AutoRecoveryManager.RestartClientProcessAsync: Client process '{processName}' was not running");
                }
                finally
                {
                    foreach (var p in processes) p.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to kill client process '{processName}': {ex.Message}", LogLevel.Warn);
            }

            // Fall back to registry-derived exe path if the process was not running
            if (exePath is null)
            {
                exePath = getInstalledExePath?.Invoke();
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
    }
}

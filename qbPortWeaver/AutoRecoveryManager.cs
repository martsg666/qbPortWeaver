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
        private const int ClientRestartDelayMs    = 2000;
        // Must exceed the helper service's full stop→delay→start cycle (5s stop wait + 5s restart
        // delay + service startup time) so the VPN service is running before the client process
        // is relaunched. See AutoRecovery.ServiceRestartDelayMs in HelperService.
        private const int ServiceHeadStartDelayMs = 20000;

        // Maps a provider token to the client process that must be restarted alongside the service.
        // GetInstalledExePath resolves the exe path from the service registry entry when the process is not running.
        private static readonly (string ProviderKeyword, Func<string> GetClientProcessName, Func<string?>? GetInstalledExePath, Func<string?> FindServiceName)[] _clientProcessMap =
        [
            (RegistrySettingsManager.VpnProviderProtonVpn, ProtonVpnManager.Config.GetClientProcessName, ProtonVpnManager.Config.GetClientExePath, ProtonVpnManager.Config.FindServiceName),
            (RegistrySettingsManager.VpnProviderPia,       PiaVpnManager.Config.GetClientProcessName,    PiaVpnManager.Config.GetClientExePath,    PiaVpnManager.Config.FindServiceName),
        ];

        /// <summary>
        /// Sends a recovery request to the helper service. For "restart" actions, also
        /// restarts the matching client process in the current user session after a delay
        /// to let the service come up first. For "cycle-adapter" actions, only the adapter
        /// is cycled - no client process is involved.
        /// </summary>
        internal static async Task TriggerRecoveryAsync(string action, string target, CancellationToken cancellationToken = default)
        {
            if (action != HelperServiceClient.ActionRestart)
            {
                await HelperServiceClient.SendCycleAdapterAsync(target).ConfigureAwait(false);
                return;
            }

            var entry = _clientProcessMap.FirstOrDefault(e => e.ProviderKeyword.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (entry.FindServiceName is null)
            {
                LogManager.Instance.LogMessage($"Unknown VPN provider '{target}' - skipping recovery", LogLevel.Warn);
                return;
            }

            string? serviceName = entry.FindServiceName.Invoke();
            if (serviceName is null)
            {
                LogManager.Instance.LogMessage($"Could not find Windows service for '{target}' - skipping recovery", LogLevel.Warn);
                return;
            }

            await HelperServiceClient.SendRestartAsync(serviceName).ConfigureAwait(false);

            // Give the helper service time to stop/restart the VPN service before
            // we kill and relaunch the client process - the client should come up
            // after the service is running, not before.
            await Task.Delay(ServiceHeadStartDelayMs, cancellationToken).ConfigureAwait(false);
            await RestartClientProcessAsync(entry.GetClientProcessName(), entry.GetInstalledExePath, cancellationToken).ConfigureAwait(false);
        }

        // Kills all instances of the named client process (capturing the exe path first),
        // waits briefly, then relaunches it. Runs in the main app's user session - no
        // elevation or WTS token manipulation needed.
        // If the process is not running, falls back to the registry-derived exe path via getInstalledExePath.
        private static async Task RestartClientProcessAsync(string processName, Func<string?>? getInstalledExePath, CancellationToken cancellationToken = default)
        {
            string? exePath = null;
            try
            {
                var processes = Process.GetProcessesByName(processName);
                try
                {
                    exePath = processes.FirstOrDefault()?.MainModule?.FileName;
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

using System.Collections.Concurrent;
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
        private const int ServiceHeadStartDelayMs = 20000;

        // Caches the EXE path for each client process name so recovery works even when
        // the process was killed externally before we could inspect it.
        private static readonly ConcurrentDictionary<string, string> _cachedClientExePaths = new(StringComparer.OrdinalIgnoreCase);

        // Maps a provider token to the client process that must be restarted alongside the service.
        private static readonly (string ProviderKeyword, string ClientProcessName)[] _clientProcessMap =
        [
            (RegistrySettingsManager.VpnProviderProtonVpn, ProtonVpnManager.ClientProcessName),
            (RegistrySettingsManager.VpnProviderPia,       PiaVpnManager.ClientProcessName),
        ];

        /// <summary>
        /// Sends a recovery request to the helper service. For "restart" actions, also
        /// restarts the matching client process in the current user session after a delay
        /// to let the service come up first. For "cycle-adapter" actions, only the adapter
        /// is cycled - no client process is involved.
        /// </summary>
        internal static async Task TriggerRecoveryAsync(string action, string target)
        {
            if (action != HelperServiceClient.ActionRestart)
            {
                await HelperServiceClient.SendCycleAdapterAsync(target).ConfigureAwait(false);
                return;
            }

            await HelperServiceClient.SendRestartAsync(target).ConfigureAwait(false);

            string? clientProcessName = FindClientProcessName(target);
            if (clientProcessName is not null)
            {
                // Give the helper service time to stop/restart the VPN service before
                // we kill and relaunch the client process - the client should come up
                // after the service is running, not before.
                await Task.Delay(ServiceHeadStartDelayMs).ConfigureAwait(false);
                await RestartClientProcessAsync(clientProcessName).ConfigureAwait(false);
            }
            else
                LogManager.Instance.LogMessage($"No client process matches '{target}' - skipping client restart", LogLevel.Warn);
        }

        /// <summary>
        /// Proactively discovers and caches EXE paths for all known client processes.
        /// Called during normal sync cycles so the path is available if the client is
        /// later killed externally before recovery runs.
        /// </summary>
        internal static void CacheRunningClientExePaths()
        {
            foreach (var processName in _clientProcessMap
                         .Select(e => e.ClientProcessName)
                         .Where(name => !_cachedClientExePaths.ContainsKey(name)))
            {
                try
                {
                    var processes = Process.GetProcessesByName(processName);
                    try
                    {
                        string? exePath = processes.FirstOrDefault()?.MainModule?.FileName;
                        if (exePath is not null)
                        {
                            _cachedClientExePaths[processName] = exePath;
                            LogManager.Instance.LogDebug($"AutoRecoveryManager.CacheRunningClientExePaths: Cached '{processName}' -> {exePath}");
                        }
                    }
                    finally
                    {
                        foreach (var p in processes) p.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Instance.LogDebug($"AutoRecoveryManager.CacheRunningClientExePaths: '{processName}': {ex.Message}");
                }
            }
        }

        // Matches the provider token (e.g. "ProtonVPN", "PIA") to the client process name.
        private static string? FindClientProcessName(string target) =>
            _clientProcessMap
                .Where(e => e.ProviderKeyword.Equals(target, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.ClientProcessName)
                .FirstOrDefault();

        // Kills all instances of the named client process (capturing the exe path first),
        // waits briefly, then relaunches it. Runs in the main app's user session - no
        // elevation or WTS token manipulation needed.
        // If the process is already dead (e.g. killed externally), falls back to a cached
        // EXE path from a previous successful discovery.
        private static async Task RestartClientProcessAsync(string processName)
        {
            string? exePath = null;
            try
            {
                var processes = Process.GetProcessesByName(processName);
                try
                {
                    exePath = processes.FirstOrDefault()?.MainModule?.FileName;
                    if (exePath is not null)
                        _cachedClientExePaths[processName] = exePath;
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

            // Fall back to cached path if the process was already dead
            if (exePath is null && _cachedClientExePaths.TryGetValue(processName, out string? cached))
            {
                exePath = cached;
                LogManager.Instance.LogDebug($"AutoRecoveryManager.RestartClientProcessAsync: Using cached EXE path for '{processName}': {exePath}");
            }

            if (exePath is null)
            {
                LogManager.Instance.LogMessage($"No EXE path available for '{processName}' - cannot restart client process", LogLevel.Warn);
                return;
            }

            await Task.Delay(ClientRestartDelayMs).ConfigureAwait(false);
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

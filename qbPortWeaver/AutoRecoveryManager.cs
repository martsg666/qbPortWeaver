using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace qbPortWeaver
{
    // Handles auto-recovery from the user session.
    // Privileged operations (service restart, adapter cycling) are delegated to the helper
    // service via named pipe. Client process restart runs directly in the user session.
    internal static class AutoRecoveryManager
    {
        internal const string ActionRestart      = "restart";
        internal const string ActionCycleAdapter = "cycle-adapter";

        private const int ClientRestartDelayMs       = 2000;
        private const int PipeConnectTimeoutMs       = 5000;
        private const int ServiceHeadStartDelayMs    = 20000;

        // Caches the EXE path for each client process name so recovery works even when
        // the process was killed externally before we could inspect it.
        private static readonly ConcurrentDictionary<string, string> CachedClientExePaths = new(StringComparer.OrdinalIgnoreCase);

        // Maps a provider token to the client process that must be restarted alongside the service.
        private static readonly (string ProviderKeyword, string ClientProcessName)[] ClientProcessMap =
        [
            (RegistrySettingsManager.VpnProviderProtonVpn, ProtonVpnManager.ClientProcessName),
            (RegistrySettingsManager.VpnProviderPia,       PiaVpnManager.ClientProcessName),
        ];

        // Sends a recovery request to the helper service. For "restart" actions, also
        // restarts the matching client process in the current user session after a delay
        // to let the service come up first. For "cycle-adapter" (generic NAT-PMP gateways),
        // only the adapter is cycled - no client process is involved.
        internal static async Task TriggerRecoveryAsync(string action, string target)
        {
            await SendToHelperServiceAsync(action, target).ConfigureAwait(false);

            if (action != ActionRestart)
                return;

            string? clientProcessName = FindClientProcessName(target);
            if (clientProcessName != null)
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

        // Proactively discovers and caches EXE paths for all known client processes.
        // Called during normal sync cycles (when the VPN is connected) so the path is
        // available if the client is later killed externally before recovery runs.
        internal static void CacheRunningClientExePaths()
        {
            foreach (var processName in ClientProcessMap
                         .Select(e => e.ClientProcessName)
                         .Where(name => !CachedClientExePaths.ContainsKey(name)))
            {
                try
                {
                    var processes = Process.GetProcessesByName(processName);
                    try
                    {
                        string? exePath = processes.FirstOrDefault()?.MainModule?.FileName;
                        if (exePath != null)
                        {
                            CachedClientExePaths[processName] = exePath;
                            LogManager.Instance.LogDebug($"AutoRecoveryManager.CacheRunningClientExePaths: cached '{processName}' → {exePath}");
                        }
                    }
                    finally
                    {
                        foreach (var p in processes) p.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Instance.LogDebug($"AutoRecoveryManager.CacheRunningClientExePaths: '{processName}' - {ex.Message}");
                }
            }
        }

        // Sends a recovery request to the SYSTEM helper service via named pipe.
        // Protocol: <action>:<target>:<logFilePath>
        private static async Task SendToHelperServiceAsync(string action, string target)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", AppConstants.HelperServicePipeName, PipeDirection.Out);
                await pipe.ConnectAsync(PipeConnectTimeoutMs).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync($"{action}:{target}:{AppConstants.GetLogFilePath()}").ConfigureAwait(false);
                LogManager.Instance.LogMessage($"Sent '{action}' request for '{target}'", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to reach helper service: {ex.Message}", LogLevel.Warn);
            }
        }

        // Matches the provider token (e.g. "ProtonVPN", "PIA") to the client process name.
        private static string? FindClientProcessName(string target) =>
            ClientProcessMap
                .Where(e => target.Contains(e.ProviderKeyword, StringComparison.OrdinalIgnoreCase))
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
                    if (exePath != null)
                        CachedClientExePaths[processName] = exePath;
                    foreach (var p in processes)
                    {
                        try
                        {
                            if (!AppConstants.KillProcess(p))
                                LogManager.Instance.LogMessage($"Client process '{processName}' (PID {p.Id}) still running after kill attempts", LogLevel.Warn);
                        }
                        catch (Exception ex) { LogManager.Instance.LogDebug($"AutoRecoveryManager.RestartClientProcessAsync: Kill '{processName}' - {ex.Message} - ignored"); }
                    }
                    LogManager.Instance.LogMessage(exePath != null
                        ? $"Killed client process '{processName}'"
                        : $"Client process '{processName}' was not running", LogLevel.Info);
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
            if (exePath == null && CachedClientExePaths.TryGetValue(processName, out string? cached))
            {
                exePath = cached;
                LogManager.Instance.LogMessage($"Using cached EXE path for '{processName}': {exePath}", LogLevel.Info);
            }

            if (exePath == null)
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
                LogManager.Instance.LogMessage($"Failed to restart client process '{processName}': {ex.Message}", LogLevel.Warn);
            }
        }
    }
}

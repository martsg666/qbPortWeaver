using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace qbPortWeaver
{
    // Handles VPN auto-recovery from the user session.
    // Service restart (requires SYSTEM) is delegated to the helper service via named pipe.
    // VPN client process restart (e.g. ProtonVPN.Client, pia-client) runs directly in the user session — no elevation needed.
    internal static class VpnAutoRecoveryManager
    {
        private const int ClientRestartDelayMs = 2000;
        private const int PipeConnectTimeoutMs = 5000;

        // Caches the EXE path for each client process name so recovery works even when
        // the process was killed externally before we could inspect it.
        private static readonly ConcurrentDictionary<string, string> CachedClientExePaths = new(StringComparer.OrdinalIgnoreCase);

        // Maps a VPN service name to the client process that must be restarted alongside it.
        // The client holds connection state and triggers auto-connect on startup —
        // restarting the service alone is not sufficient to reconnect.
        private static readonly (string ServiceName, string ClientProcessName)[] ClientProcessMap =
        [
            (ProtonVpnManager.VpnServiceName, ProtonVpnManager.ClientProcessName),
            (PiaVpnManager.VpnServiceName,    PiaVpnManager.ClientProcessName),
        ];

        // Sends a service restart request to the helper service and restarts the VPN client
        // process in the current user session.
        internal static async Task TriggerRecoveryAsync(string serviceName)
        {
            await SendToHelperServiceAsync(serviceName).ConfigureAwait(false);

            string? clientProcessName = ResolveClientProcessName(serviceName);
            if (clientProcessName != null)
                await RestartClientProcessAsync(clientProcessName).ConfigureAwait(false);
        }

        // Proactively discovers and caches EXE paths for all known VPN client processes.
        // Called during normal sync cycles (when the VPN is connected) so the path is
        // available if the client is later killed externally before recovery runs.
        internal static void CacheRunningClientExePaths()
        {
            foreach (var (_, clientProcessName) in ClientProcessMap)
            {
                if (CachedClientExePaths.ContainsKey(clientProcessName))
                    continue;

                try
                {
                    var processes = Process.GetProcessesByName(clientProcessName);
                    try
                    {
                        string? exePath = processes.FirstOrDefault()?.MainModule?.FileName;
                        if (exePath != null)
                        {
                            CachedClientExePaths[clientProcessName] = exePath;
                            LogManager.Instance.LogDebug($"VpnAutoRecoveryManager.CacheRunningClientExePaths: cached '{clientProcessName}' → {exePath}");
                        }
                    }
                    finally
                    {
                        foreach (var p in processes) p.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Instance.LogDebug($"VpnAutoRecoveryManager.CacheRunningClientExePaths: '{clientProcessName}' — {ex.Message}");
                }
            }
        }

        // Sends a restart request for the given service to the SYSTEM helper service via named pipe.
        private static async Task SendToHelperServiceAsync(string serviceName)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", AppConstants.HelperServicePipeName, PipeDirection.Out);
                await pipe.ConnectAsync(PipeConnectTimeoutMs).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync($"restart:{serviceName}:{AppConstants.GetLogFilePath()}").ConfigureAwait(false);
                LogManager.Instance.LogMessage($"VPN auto-recovery triggered for service '{serviceName}'", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"VPN auto-recovery: failed to reach helper service: {ex.Message}", LogLevel.Warn);
            }
        }

        private static string? ResolveClientProcessName(string serviceName)
        {
            foreach (var (service, clientProcess) in ClientProcessMap)
            {
                if (serviceName.Equals(service, StringComparison.OrdinalIgnoreCase))
                    return clientProcess;
            }
            LogManager.Instance.LogMessage($"No client process restart configured for service '{serviceName}' — skipping", LogLevel.Info);
            return null;
        }

        // Kills all instances of the named client process (capturing the exe path first),
        // waits briefly, then relaunches it. Runs in the main app's user session — no
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
                        try { p.Kill(entireProcessTree: true); } catch (Exception ex) { LogManager.Instance.LogDebug($"VpnAutoRecoveryManager.RestartClientProcessAsync: Kill '{processName}' — {ex.Message} — ignored"); }
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
                LogManager.Instance.LogMessage($"VPN auto-recovery: no EXE path available for '{processName}' — cannot restart client", LogLevel.Warn);
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

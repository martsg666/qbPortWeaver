using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace qbPortWeaver
{
    // Handles VPN auto-recovery from the user session.
    // Service restart (requires SYSTEM) is delegated to the helper service via named pipe.
    // VPN client process restart (e.g. ProtonVPN.Client, pia-client) runs directly in the user session - no elevation needed.
    internal static class VpnAutoRecoveryManager
    {
        private const int ClientRestartDelayMs = 2000;
        private const int PipeConnectTimeoutMs = 5000;

        // Caches the EXE path for each client process name so recovery works even when
        // the process was killed externally before we could inspect it.
        private static readonly ConcurrentDictionary<string, string> CachedClientExePaths = new(StringComparer.OrdinalIgnoreCase);

        // Maps a provider token to the client process that must be restarted alongside the service.
        // The token is sent over the pipe — the helper holds the only copy of the token→service
        // name mapping, so Windows service names never appear in this project.
        // The client holds connection state and triggers auto-connect on startup -
        // restarting the service alone is not sufficient to reconnect.
        private static readonly (string ProviderToken, string ClientProcessName)[] ClientProcessMap =
        [
            (RegistrySettingsManager.VpnProviderProtonVpn, ProtonVpnManager.ClientProcessName),
            (RegistrySettingsManager.VpnProviderPia,       PiaVpnManager.ClientProcessName),
        ];

        // Sends a service restart request to the helper service and restarts the VPN client
        // process in the current user session.
        internal static async Task TriggerRecoveryAsync(string providerToken)
        {
            await SendToHelperServiceAsync(providerToken).ConfigureAwait(false);

            string? clientProcessName = FindClientProcessName(providerToken);
            if (clientProcessName != null)
                await RestartClientProcessAsync(clientProcessName).ConfigureAwait(false);
            else
                LogManager.Instance.LogMessage($"VPN auto-recovery: no client process configured for provider '{providerToken}' - skipping client restart", LogLevel.Warn);
        }

        // Proactively discovers and caches EXE paths for all known VPN client processes.
        // Called during normal sync cycles (when the VPN is connected) so the path is
        // available if the client is later killed externally before recovery runs.
        internal static void CacheRunningClientExePaths()
        {
            foreach (var entry in ClientProcessMap)
            {
                if (CachedClientExePaths.ContainsKey(entry.ClientProcessName))
                    continue;

                try
                {
                    var processes = Process.GetProcessesByName(entry.ClientProcessName);
                    try
                    {
                        string? exePath = processes.FirstOrDefault()?.MainModule?.FileName;
                        if (exePath != null)
                        {
                            CachedClientExePaths[entry.ClientProcessName] = exePath;
                            LogManager.Instance.LogDebug($"VpnAutoRecoveryManager.CacheRunningClientExePaths: cached '{entry.ClientProcessName}' → {exePath}");
                        }
                    }
                    finally
                    {
                        foreach (var p in processes) p.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Instance.LogDebug($"VpnAutoRecoveryManager.CacheRunningClientExePaths: '{entry.ClientProcessName}' - {ex.Message}");
                }
            }
        }

        // Sends a restart request to the SYSTEM helper service via named pipe.
        // The provider token (e.g. "ProtonVPN", "PIA") is sent instead of the service name —
        // the helper holds the only copy of the token→service name mapping.
        private static async Task SendToHelperServiceAsync(string providerToken)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", AppConstants.HelperServicePipeName, PipeDirection.Out);
                await pipe.ConnectAsync(PipeConnectTimeoutMs).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync($"restart:{providerToken}:{AppConstants.GetLogFilePath()}").ConfigureAwait(false);
                LogManager.Instance.LogMessage($"VPN auto-recovery: sent restart request for provider '{providerToken}'", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"VPN auto-recovery: failed to reach helper service: {ex.Message}", LogLevel.Warn);
            }
        }

        private static string? FindClientProcessName(string providerToken)
        {
            foreach (var entry in ClientProcessMap)
            {
                if (providerToken.Equals(entry.ProviderToken, StringComparison.OrdinalIgnoreCase))
                    return entry.ClientProcessName;
            }
            return null;
        }

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
                            if (!AppConstants.KillAndWait(p))
                                LogManager.Instance.LogMessage($"VPN client process '{processName}' (PID {p.Id}) still running after two kill attempts", LogLevel.Warn);
                        }
                        catch (Exception ex) { LogManager.Instance.LogDebug($"VpnAutoRecoveryManager.RestartClientProcessAsync: Kill '{processName}' - {ex.Message} - ignored"); }
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
                LogManager.Instance.LogMessage($"VPN auto-recovery: no EXE path available for '{processName}' - cannot restart client", LogLevel.Warn);
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

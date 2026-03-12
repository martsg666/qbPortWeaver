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
        private static async Task RestartClientProcessAsync(string processName)
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
                        try { p.Kill(entireProcessTree: true); } catch (Exception ex) { LogManager.Instance.LogDebug($"VpnAutoRecoveryManager.TryTriggerRecoveryAsync: Kill '{processName}' — {ex.Message} — ignored"); }
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

            if (exePath == null) return;

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

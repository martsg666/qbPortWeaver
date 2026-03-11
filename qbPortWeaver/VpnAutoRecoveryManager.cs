using System.Diagnostics;
using System.IO.Pipes;

namespace qbPortWeaver
{
    // Handles VPN auto-recovery from the user session.
    // Service restart (requires SYSTEM) is delegated to the helper service via named pipe.
    // VPN client process restart runs directly in the user session — no elevation needed.
    internal static class VpnAutoRecoveryManager
    {
        private const int ClientRestartDelayMs = 2000;
        private const int PipeConnectTimeoutMs = 5000;

        // Maps a VPN service name fragment to the client process name that must be restarted
        // alongside the service. The client holds the connection state and triggers auto-connect
        // on startup; restarting the service alone is not sufficient to reconnect.
        private static readonly (string ServiceFragment, string ClientProcessName)[] ClientProcessMap =
        [
            ("ProtonVPN",             "ProtonVPN.Client"),
            ("PrivateInternetAccess", "pia-client"),
        ];

        // Sends a service restart request to the helper service and restarts the VPN client
        // process in the current user session.
        internal static void TriggerRecovery(string serviceName)
        {
            SendToHelperService(serviceName);

            string? clientProcessName = ResolveClientProcessName(serviceName);
            if (clientProcessName != null)
                RestartClientProcess(clientProcessName);
        }

        // Sends a restart request for the given service to the SYSTEM helper service via named pipe.
        private static void SendToHelperService(string serviceName)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", AppConstants.HelperServicePipeName, PipeDirection.Out);
                pipe.Connect(PipeConnectTimeoutMs);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine($"restart:{serviceName}:{AppConstants.GetLogFilePath()}");
                LogManager.Instance.LogMessage($"VPN auto-recovery triggered for service '{serviceName}'", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"VPN auto-recovery: failed to reach helper service: {ex.Message}", LogLevel.Warn);
            }
        }

        private static string? ResolveClientProcessName(string serviceName)
        {
            foreach (var (fragment, clientProcess) in ClientProcessMap)
            {
                if (serviceName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return clientProcess;
            }
            LogManager.Instance.LogMessage($"No client process mapping for service '{serviceName}' — skipping client restart", LogLevel.Info);
            return null;
        }

        // Kills all instances of the named client process (capturing the exe path first),
        // waits briefly, then relaunches it. Runs in the main app's user session — no
        // elevation or WTS token manipulation needed.
        private static void RestartClientProcess(string processName)
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
                        try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
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

            Thread.Sleep(ClientRestartDelayMs);
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

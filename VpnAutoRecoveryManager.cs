using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace qbPortWeaver
{
    // Manages VPN auto-recovery. The scheduled task is created/removed by the MSI installer.
    // The task runs as SYSTEM and is triggered by an Application log event (EventID 1001).
    // Using an event trigger allows the non-admin app process to fire the task without elevation:
    // writing to a pre-registered event source requires no admin rights, whereas schtasks /Run
    // on a SYSTEM task does.
    //
    // Recovery is split into two parts that run concurrently:
    //   TriggerRecovery (main app, user context) — fires the SYSTEM task and restarts the VPN
    //     client process. Client restart requires no elevation since it runs as the same user.
    //   ExecuteRecovery (SYSTEM task)            — stops and starts the VPN service, which
    //     requires elevation and cannot be done from the non-admin main app.
    internal static class VpnAutoRecoveryManager
    {
        private const int ServiceRestartDelayMs     = 5000;
        private const int ClientRestartDelayMs      = 2000;
        private const int ServiceOperationTimeoutMs = 30000;

        // Maps a VPN service name fragment to the client process name that must be restarted
        // alongside the service. The client holds the connection state and triggers auto-connect
        // on startup; restarting the service alone is not sufficient to reconnect.
        private static readonly (string ServiceFragment, string ClientProcessName)[] ClientProcessMap =
        [
            ("ProtonVPN",             "ProtonVPN.Client"),
            ("PrivateInternetAccess", "pia-client"),
        ];

        // Fires the SYSTEM scheduled task via an Application log event and restarts the VPN client
        // process in the current user session. The service name is written as the event message so
        // the task's ValueQueries binding can extract it and pass it as a command-line argument —
        // no trigger file needed.
        internal static void TriggerRecovery(string serviceName)
        {
            try
            {
                // Write just the service name as the event message so the task's ValueQueries
                // binding can extract it from Event/EventData/Data and pass it as an argument.
                EventLog.WriteEntry(
                    AppConstants.AppName,
                    serviceName,
                    EventLogEntryType.Information,
                    AppConstants.VpnAutoRecoveryEventId);

                LogManager.Instance.LogMessage($"VPN auto-recovery triggered for service '{serviceName}'", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to trigger VPN auto-recovery: {ex.Message}", LogLevel.Warn);
                return;
            }

            string? clientProcessName = ResolveClientProcessName(serviceName);
            if (clientProcessName != null)
                RestartClientProcess(clientProcessName);
        }

        // Entry point when launched by the scheduled task (--vpn-autorecovery <serviceName>).
        // LogManager is initialized by Program.Main before calling this.
        // Runs as SYSTEM via the scheduled task.
        internal static void ExecuteRecovery(string serviceName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(serviceName))
                {
                    LogManager.Instance.LogMessage("VPN auto-recovery: service name argument is empty — nothing to restart", LogLevel.Warn);
                    return;
                }

                LogManager.Instance.LogMessage($"VPN auto-recovery restarting service '{serviceName}'", LogLevel.Info);
                try { StopService(serviceName); }
                catch (Exception ex) { LogManager.Instance.LogMessage($"VPN service '{serviceName}' stop failed: {ex.Message}", LogLevel.Warn); }

                Thread.Sleep(ServiceRestartDelayMs);

                try { StartService(serviceName); }
                catch (Exception ex) { LogManager.Instance.LogMessage($"VPN service '{serviceName}' start failed: {ex.Message}", LogLevel.Warn); return; }

                LogManager.Instance.LogMessage($"VPN auto-recovery service restart complete for service '{serviceName}'", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"VPN auto-recovery failed: {ex.Message}", LogLevel.Error);
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

        private static void StopService(string serviceName)
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                LogManager.Instance.LogMessage($"VPN service '{serviceName}' is already stopped", LogLevel.Info);
                return;
            }
            sc.Stop();
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs));
                LogManager.Instance.LogMessage($"VPN service '{serviceName}' stopped", LogLevel.Info);
            }
            catch (System.TimeoutException)
            {
                // Service did not respond to the SCM stop command in time.
                // Kill the hosting process directly so it cannot remain stuck in StopPending.
                LogManager.Instance.LogMessage($"VPN service '{serviceName}' stop timed out — force-killing process", LogLevel.Warn);
                KillServiceProcess(sc);
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs));
                LogManager.Instance.LogMessage($"VPN service '{serviceName}' force-stopped", LogLevel.Info);
            }
        }

        private static void StartService(string serviceName)
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running)
            {
                // The service may have auto-restarted after being killed — that is fine.
                LogManager.Instance.LogMessage($"VPN service '{serviceName}' is already running", LogLevel.Info);
                return;
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs));
            LogManager.Instance.LogMessage($"VPN service '{serviceName}' started", LogLevel.Info);
        }

        // Obtains the PID of the process hosting the service via QueryServiceStatusEx and kills it.
        // This is the only reliable way to force-stop a service that ignores SCM stop commands.
        private static void KillServiceProcess(ServiceController sc)
        {
            try
            {
                int    bufSize = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
                IntPtr buf    = Marshal.AllocHGlobal(bufSize);
                try
                {
                    if (!QueryServiceStatusEx(sc.ServiceHandle.DangerousGetHandle(),
                            SC_STATUS_PROCESS_INFO, buf, bufSize, out _))
                        return;

                    int pid = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buf).dwProcessId;
                    if (pid <= 0) return;

                    using var process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    LogManager.Instance.LogMessage($"Killed service process PID {pid}", LogLevel.Info);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to kill service process: {ex.Message}", LogLevel.Warn);
            }
        }

        private const int  SC_STATUS_PROCESS_INFO    = 0;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(
            IntPtr hService, int infoLevel, IntPtr buffer, int bufSize, out int bytesNeeded);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public int dwServiceType, dwCurrentState, dwControlsAccepted,
                       dwWin32ExitCode, dwServiceSpecificExitCode,
                       dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
        }
    }
}

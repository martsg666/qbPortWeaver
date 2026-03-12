using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace qbPortWeaver.HelperService;

// Executes VPN service recovery inside the SYSTEM Windows service.
// Called by HelperPipeServer when the user-session tray app signals a VPN disconnect.
internal static class VpnAutoRecovery
{
    private const int ServiceRestartDelayMs     = 5000;
    private const int ServiceOperationTimeoutMs = 30000;

    internal static async Task RestartServiceAsync(string serviceName, HelperLogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                logger.LogWarn("VPN auto-recovery: service name is empty - nothing to restart");
                return;
            }

            logger.LogInfo($"VPN auto-recovery: restarting service '{serviceName}'");

            try { await StopServiceAsync(serviceName, logger).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarn($"VPN service '{serviceName}' stop failed: {ex.Message}"); }

            await Task.Delay(ServiceRestartDelayMs).ConfigureAwait(false);

            try { await StartServiceAsync(serviceName, logger).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogWarn($"VPN service '{serviceName}' start failed: {ex.Message}");
                return;
            }

            logger.LogInfo($"VPN auto-recovery: service '{serviceName}' restarted successfully");
        }
        catch (Exception ex)
        {
            logger.LogError($"VPN auto-recovery failed: {ex.Message}");
        }
    }

    // ServiceController.WaitForStatus has no async overload - wrap in Task.Run to avoid
    // blocking the BackgroundService thread pool thread for up to ServiceOperationTimeoutMs.
    private static async Task StopServiceAsync(string serviceName, HelperLogger logger)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            logger.LogInfo($"VPN service '{serviceName}' is already stopped");
            return;
        }

        sc.Stop();
        try
        {
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            logger.LogInfo($"VPN service '{serviceName}' stopped");
        }
        catch (System.TimeoutException)
        {
            logger.LogWarn($"VPN service '{serviceName}' stop timed out - force-killing process");
            KillServiceProcess(sc, logger);
            try
            {
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                    TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
                logger.LogInfo($"VPN service '{serviceName}' force-stopped");
            }
            catch (System.TimeoutException)
            {
                logger.LogWarn($"VPN service '{serviceName}' still not stopped after force-kill - proceeding with start anyway");
            }
        }
    }

    private static async Task StartServiceAsync(string serviceName, HelperLogger logger)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running)
        {
            logger.LogInfo($"VPN service '{serviceName}' is already running");
            return;
        }

        // The service may still be in StopPending (e.g. the VPN client process was killed
        // concurrently and the service is tearing down). Wait for it to finish stopping
        // before attempting to start, otherwise sc.Start() throws.
        if (sc.Status == ServiceControllerStatus.StopPending)
        {
            logger.LogInfo($"VPN service '{serviceName}' is still stopping - waiting");
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
        }

        sc.Start();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running,
            TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
        logger.LogInfo($"VPN service '{serviceName}' started");
    }

    // Obtains the PID of the process hosting the service via QueryServiceStatusEx and kills it.
    // This is the only reliable way to force-stop a service that ignores SCM stop commands.
    private static void KillServiceProcess(ServiceController sc, HelperLogger logger)
    {
        try
        {
            int    bufSize = Marshal.SizeOf<ServiceStatusProcess>();
            IntPtr buf     = Marshal.AllocHGlobal(bufSize);
            try
            {
                if (!QueryServiceStatusEx(sc.ServiceHandle.DangerousGetHandle(),
                        ScStatusProcessInfo, buf, bufSize, out _))
                    return;

                int pid = Marshal.PtrToStructure<ServiceStatusProcess>(buf).dwProcessId;
                if (pid <= 0) return;

                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                logger.LogInfo($"Killed service process PID {pid}");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarn($"Failed to kill service process: {ex.Message}");
        }
    }

    private const int ScStatusProcessInfo = 0; // SC_STATUS_PROCESS_INFO - only valid infoLevel for QueryServiceStatusEx

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr hService, int infoLevel, IntPtr buffer, int bufSize, out int bytesNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
        public int dwProcessId;
        public int dwServiceFlags;
    }
}

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

    internal static void RestartService(string serviceName, HelperLogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                logger.LogWarn("VPN auto-recovery: service name is empty — nothing to restart");
                return;
            }

            logger.LogInfo($"VPN auto-recovery: restarting service '{serviceName}'");

            try { StopService(serviceName, logger); }
            catch (Exception ex) { logger.LogWarn($"VPN service '{serviceName}' stop failed: {ex.Message}"); }

            Thread.Sleep(ServiceRestartDelayMs);

            try { StartService(serviceName, logger); }
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

    private static void StopService(string serviceName, HelperLogger logger)
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
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs));
            logger.LogInfo($"VPN service '{serviceName}' stopped");
        }
        catch (System.TimeoutException)
        {
            logger.LogWarn($"VPN service '{serviceName}' stop timed out — force-killing process");
            KillServiceProcess(sc, logger);
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs));
                logger.LogInfo($"VPN service '{serviceName}' force-stopped");
            }
            catch (System.TimeoutException)
            {
                logger.LogWarn($"VPN service '{serviceName}' still not stopped after force-kill — proceeding with start anyway");
            }
        }
    }

    private static void StartService(string serviceName, HelperLogger logger)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running)
        {
            logger.LogInfo($"VPN service '{serviceName}' is already running");
            return;
        }

        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs));
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

    private const int ScStatusProcessInfo = 0;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr hService, int infoLevel, IntPtr buffer, int bufSize, out int bytesNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted,
                   dwWin32ExitCode, dwServiceSpecificExitCode,
                   dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }
}

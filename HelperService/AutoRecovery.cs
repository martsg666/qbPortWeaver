using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace qbPortWeaver.HelperService;

/// <summary>
/// Executes privileged recovery actions inside the SYSTEM Windows service.
/// Called by HelperPipeServer when the user-session tray app signals a recovery request.
/// Supported actions: restart (stop/start a Windows service by name) and
/// cycle-adapter (disable/enable a network adapter via netsh).
/// </summary>
internal static class AutoRecovery
{
    private const int ProcessKillTimeoutMs      = 5000;
    private const int ServiceRestartDelayMs     = 5000;
    private const int ServiceOperationTimeoutMs = 15000;
    private const int AdapterCycleDelayMs       = 3000;
    private const int NetshTimeoutMs            = 15000;

    // Maps provider keywords to the Windows service to restart.
    // Used by HelperPipeServer for the "restart" action (exact token lookup via FindServiceForToken).
    private static readonly Dictionary<string, string> _providerServiceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProtonVPN"] = "ProtonVPN Service",
        ["PIA"]       = "PrivateInternetAccessService",
    };

    internal static async Task RestartServiceAsync(string serviceName, HelperLogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                logger.LogWarn("Service name is empty - nothing to restart");
                return;
            }

            logger.LogInfo($"Restarting service '{serviceName}'");

            try { await StopServiceAsync(serviceName, logger).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarn($"Failed to stop service '{serviceName}': {ex.Message}"); }

            await Task.Delay(ServiceRestartDelayMs).ConfigureAwait(false);

            try { await StartServiceAsync(serviceName, logger).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogWarn($"Failed to start service '{serviceName}': {ex.Message}");
                return;
            }

            logger.LogInfo($"Restarted service '{serviceName}'");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to restart service: {ex.Message}");
        }
    }

    // Cycles a network adapter by disabling and re-enabling it via netsh.
    // Used for generic NAT-PMP gateways where no known VPN service is involved.
    // For known providers (ProtonVPN, PIA), the main app sends "restart" instead.
    internal static async Task CycleAdapterAsync(string adapterName, HelperLogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(adapterName))
            {
                logger.LogWarn("Adapter name is empty - nothing to cycle");
                return;
            }

            logger.LogInfo($"Cycling adapter '{adapterName}'");

            if (!await RunNetshAsync($"interface set interface \"{adapterName}\" admin=disable", logger).ConfigureAwait(false))
            {
                logger.LogWarn($"Failed to disable adapter '{adapterName}'");
                return;
            }
            logger.LogInfo($"Adapter '{adapterName}' disabled");

            await Task.Delay(AdapterCycleDelayMs).ConfigureAwait(false);

            if (!await RunNetshAsync($"interface set interface \"{adapterName}\" admin=enable", logger).ConfigureAwait(false))
            {
                logger.LogWarn($"Failed to re-enable adapter '{adapterName}'");
                return;
            }
            logger.LogInfo($"Re-enabled adapter '{adapterName}'");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to cycle adapter: {ex.Message}");
        }
    }

    // Exact-match lookup used by HelperPipeServer for the "restart" action.
    internal static string? FindServiceForToken(string providerToken) =>
        _providerServiceMap.TryGetValue(providerToken, out string? serviceName) ? serviceName : null;

    // Stops a service cleanly via the SCM, with escalating force if it doesn't respond.
    // Escalation: SCM stop → wait → KillServiceProcess (3-stage: Process.Kill → taskkill /F /T → retry).
    // ServiceController.WaitForStatus has no async overload - wrap in Task.Run to avoid
    // blocking the BackgroundService thread pool thread.
    //
    // IMPORTANT: WaitForStatus throws System.ServiceProcess.TimeoutException, NOT
    // System.TimeoutException - they are sibling classes (both inherit SystemException).
    private static async Task StopServiceAsync(string serviceName, HelperLogger logger)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            logger.LogInfo($"Service '{serviceName}' is already stopped");
            return;
        }

        // The service may already be stopping (e.g. the VPN client crashed and the SCM is
        // tearing the service down). Wait for it to finish instead of calling Stop() again
        // which would throw InvalidOperationException.
        if (sc.Status == ServiceControllerStatus.StopPending)
        {
            logger.LogInfo($"Service '{serviceName}' is already stopping - waiting");
            try
            {
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                    TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
                logger.LogInfo($"Service '{serviceName}' stopped");
                return;
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogWarn($"Service '{serviceName}' StopPending timed out - force-killing process");
                KillServiceProcess(sc, logger);
                await WaitForStoppedOrWarnAsync(sc, serviceName, logger).ConfigureAwait(false);
                return;
            }
        }

        // The service may be mid-startup (e.g. SCM auto-recovery kicked in). Wait for it
        // to finish starting before issuing a stop, otherwise sc.Stop() may throw or behave
        // unpredictably on a partially-initialized service.
        if (sc.Status == ServiceControllerStatus.StartPending)
        {
            logger.LogInfo($"Service '{serviceName}' is starting - waiting before stopping");
            try
            {
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running,
                    TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogWarn($"Service '{serviceName}' StartPending timed out - force-killing process");
                KillServiceProcess(sc, logger);
                await WaitForStoppedOrWarnAsync(sc, serviceName, logger).ConfigureAwait(false);
                return;
            }
        }

        try { sc.Stop(); }
        catch (Exception ex)
        {
            // sc.Stop() can throw if the service doesn't accept stop controls or is in
            // a transient state. Fall through to force-kill.
            logger.LogWarn($"Failed to stop service '{serviceName}' via SCM: {ex.Message} - force-killing process");
            KillServiceProcess(sc, logger);
            await WaitForStoppedOrWarnAsync(sc, serviceName, logger).ConfigureAwait(false);
            return;
        }

        try
        {
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            logger.LogInfo($"Service '{serviceName}' stopped");
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            logger.LogWarn($"Service '{serviceName}' stop timed out - force-killing process");
            KillServiceProcess(sc, logger);
            await WaitForStoppedOrWarnAsync(sc, serviceName, logger).ConfigureAwait(false);
        }
    }

    // Waits for a service to reach Stopped after a force-kill attempt, logging the outcome.
    private static async Task WaitForStoppedOrWarnAsync(ServiceController sc, string serviceName, HelperLogger logger)
    {
        try
        {
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            logger.LogInfo($"Service '{serviceName}' force-stopped");
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            logger.LogWarn($"Service '{serviceName}' still not stopped after force-kill - proceeding with start anyway");
        }
    }

    private static async Task StartServiceAsync(string serviceName, HelperLogger logger)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running)
        {
            logger.LogInfo($"Service '{serviceName}' is already running");
            return;
        }

        // The SCM's recovery policy may have already triggered a restart (e.g. VPN services
        // are typically configured to restart automatically on failure). If the service is
        // already starting, just wait for it instead of calling Start() which would throw.
        if (sc.Status == ServiceControllerStatus.StartPending)
        {
            logger.LogInfo($"Service '{serviceName}' is already starting (likely SCM auto-recovery) - waiting");
            try
            {
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running,
                    TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
                logger.LogInfo($"Service '{serviceName}' started (by SCM)");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogWarn($"Service '{serviceName}' stuck in StartPending after {ServiceOperationTimeoutMs}ms");
                throw;
            }
            return;
        }

        // The service may still be in StopPending (e.g. the client process was killed
        // concurrently and the service is tearing down). Wait for it to finish stopping
        // before attempting to start, otherwise sc.Start() throws.
        if (sc.Status == ServiceControllerStatus.StopPending)
        {
            logger.LogInfo($"Service '{serviceName}' is still stopping - waiting");
            try
            {
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                    TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogWarn($"Service '{serviceName}' StopPending timed out during start - proceeding anyway");
            }
        }

        sc.Start();
        try
        {
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            logger.LogInfo($"Service '{serviceName}' started");
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            logger.LogWarn($"Service '{serviceName}' start timed out - service may still be starting");
        }
    }

    // Called by StopServiceAsync when the service doesn't respond to a clean stop
    // or is stuck in a pending state. Resolves the service's host PID via
    // QueryServiceStatusEx, then escalates: Process.Kill → wait → taskkill /F /T → retry Process.Kill.
    // Mirrors the escalation logic in AppConstants.KillProcess (main app project).
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

                // Stage 1: Process.Kill
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { return; } // already exited

                if (process.WaitForExit(ProcessKillTimeoutMs))
                {
                    logger.LogWarn($"Service '{sc.ServiceName}' process force-killed (PID {pid})");
                    return;
                }

                // Stage 2: taskkill /F /T
                try
                {
                    using var taskkill = Process.Start(new ProcessStartInfo(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                        $"/F /T /PID {pid}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    });
                    taskkill?.WaitForExit(ProcessKillTimeoutMs);
                }
                catch (Exception ex)
                {
                    logger.LogWarn($"Failed to kill service '{sc.ServiceName}' via taskkill (PID {pid}): {ex.Message}");
                }
                if (process.WaitForExit(ProcessKillTimeoutMs))
                {
                    logger.LogWarn($"Service '{sc.ServiceName}' process force-killed via taskkill (PID {pid})");
                    return;
                }

                // Stage 3: retry Process.Kill after taskkill may have weakened the process tree
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException)
                {
                    logger.LogWarn($"Service '{sc.ServiceName}' process force-killed (PID {pid})");
                    return;
                }

                if (process.WaitForExit(ProcessKillTimeoutMs))
                    logger.LogWarn($"Service '{sc.ServiceName}' process force-killed (PID {pid})");
                else
                    logger.LogWarn($"Service '{sc.ServiceName}' process (PID {pid}) still running after all kill attempts");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarn($"Failed to force-kill service '{sc.ServiceName}': {ex.Message}");
        }
    }

    // Runs a netsh command and returns true if it exits with code 0.
    private static async Task<bool> RunNetshAsync(string arguments, HelperLogger logger)
    {
        try
        {
            string netshPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
            var startInfo = new ProcessStartInfo(netshPath, arguments)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogWarn($"Failed to start netsh");
                return false;
            }

            // Read stdout/stderr before WaitForExit to avoid deadlock on full buffers
            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

            bool exited = await Task.Run(() => process.WaitForExit(NetshTimeoutMs)).ConfigureAwait(false);
            if (!exited)
            {
                // netsh is a short-lived system utility that always responds to Process.Kill -
                // no taskkill fallback needed here.
                process.Kill(entireProcessTree: true);
                logger.LogWarn("netsh timed out and was killed");
                return false;
            }

            if (process.ExitCode != 0)
            {
                string output = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                logger.LogWarn($"netsh exited with code {process.ExitCode}: {output}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarn($"Failed to run netsh: {ex.Message}");
            return false;
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

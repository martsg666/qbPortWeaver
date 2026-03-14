using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace qbPortWeaver.HelperService;

// Executes privileged recovery actions inside the SYSTEM Windows service.
// Called by HelperPipeServer when the user-session tray app signals a recovery request.
//
// Supported actions:
//   restart        — stop/start a Windows service by name
//   cycle-adapter  — disable/enable a network adapter via netsh; if the adapter name
//                    matches a known provider (ProtonVPN, PIA), restart its service after
//                    the adapter is re-enabled so the VPN re-initialises on a clean adapter
internal static class AutoRecovery
{
    private const int ServiceRestartDelayMs     = 5000;
    private const int ServiceOperationTimeoutMs = 30000;
    private const int AdapterCycleDelayMs       = 3000;
    private const int NetshTimeoutMs            = 15000;

    // Maps provider keywords to the Windows service to restart. Used by:
    //   - HelperPipeServer for the "restart" action (exact token lookup via FindServiceForToken)
    //   - CycleAdapterAsync for the "cycle-adapter" action (adapter name contains keyword)
    internal static readonly Dictionary<string, string> ProviderServiceMap = new(StringComparer.OrdinalIgnoreCase)
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
                logger.LogWarn("Auto-recovery: service name is empty - nothing to restart");
                return;
            }

            logger.LogInfo($"Auto-recovery: restarting service '{serviceName}'");

            try { await StopServiceAsync(serviceName, logger).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarn($"Service '{serviceName}' stop failed: {ex.Message}"); }

            await Task.Delay(ServiceRestartDelayMs).ConfigureAwait(false);

            try { await StartServiceAsync(serviceName, logger).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogWarn($"Service '{serviceName}' start failed: {ex.Message}");
                return;
            }

            logger.LogInfo($"Auto-recovery: service '{serviceName}' restarted successfully");
        }
        catch (Exception ex)
        {
            logger.LogError($"Auto-recovery: service restart failed: {ex.Message}");
        }
    }

    // Cycles a network adapter by disabling and re-enabling it via netsh. If the adapter
    // name matches a known provider, the adapter is cycled first (to clear stale state),
    // then the corresponding Windows service is restarted so it re-initialises cleanly.
    internal static async Task CycleAdapterAsync(string adapterName, HelperLogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(adapterName))
            {
                logger.LogWarn("Auto-recovery: adapter name is empty - nothing to cycle");
                return;
            }

            logger.LogInfo($"Auto-recovery: cycling adapter '{adapterName}'");

            // Attempt adapter disable/enable to clear stale state. If the adapter is already
            // gone (e.g. VPN removed the TUN device on disconnect), the netsh calls will fail
            // — that's fine, we still proceed to the service restart which is the critical step.
            bool adapterCycled = false;
            if (await RunNetshAsync($"interface set interface \"{adapterName}\" admin=disable", logger).ConfigureAwait(false))
            {
                logger.LogInfo($"Auto-recovery: adapter '{adapterName}' disabled");
                await Task.Delay(AdapterCycleDelayMs).ConfigureAwait(false);

                if (await RunNetshAsync($"interface set interface \"{adapterName}\" admin=enable", logger).ConfigureAwait(false))
                {
                    logger.LogInfo($"Auto-recovery: adapter '{adapterName}' re-enabled successfully");
                    adapterCycled = true;
                }
                else
                {
                    logger.LogWarn($"Auto-recovery: failed to re-enable adapter '{adapterName}'");
                }
            }
            else
            {
                logger.LogWarn($"Auto-recovery: failed to disable adapter '{adapterName}' (adapter may already be down)");
            }

            // If the adapter belongs to a known provider, restart its service regardless of
            // whether the adapter cycle succeeded — the service restart is what actually
            // re-establishes the VPN tunnel and recreates the adapter.
            string? matchedService = FindServiceForAdapter(adapterName);
            if (matchedService is not null)
            {
                logger.LogInfo($"Auto-recovery: adapter '{adapterName}' matches provider service '{matchedService}' - restarting service{(adapterCycled ? " after adapter cycle" : " (adapter cycle skipped)")}");
                await RestartServiceAsync(matchedService, logger).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"Auto-recovery: adapter cycle failed: {ex.Message}");
        }
    }

    // Exact-match lookup used by HelperPipeServer for the "restart" action.
    internal static string? FindServiceForToken(string providerToken) =>
        ProviderServiceMap.TryGetValue(providerToken, out string? serviceName) ? serviceName : null;

    // Matches an adapter name against known provider keywords to find the associated service.
    private static string? FindServiceForAdapter(string adapterName)
    {
        foreach (var (keyword, serviceName) in ProviderServiceMap)
        {
            if (adapterName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return serviceName;
        }
        return null;
    }

    // Stops a service cleanly via the SCM, with escalating force if it doesn't respond.
    // Escalation: SCM stop → wait → Process.Kill → wait → taskkill /F /T (last resort).
    // ServiceController.WaitForStatus has no async overload — wrap in Task.Run to avoid
    // blocking the BackgroundService thread pool thread for up to ServiceOperationTimeoutMs.
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
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            logger.LogInfo($"Service '{serviceName}' stopped");
            return;
        }

        // The service may be mid-startup (e.g. SCM auto-recovery kicked in). Wait for it
        // to finish starting before issuing a stop, otherwise sc.Stop() may throw or behave
        // unpredictably on a partially-initialized service.
        if (sc.Status == ServiceControllerStatus.StartPending)
        {
            logger.LogInfo($"Service '{serviceName}' is starting - waiting before stopping");
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
        }

        sc.Stop();
        try
        {
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            logger.LogInfo($"Service '{serviceName}' stopped");
        }
        catch (System.TimeoutException)
        {
            logger.LogWarn($"Service '{serviceName}' stop timed out - force-killing process");
            KillServiceProcess(sc, logger);
            try
            {
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                    TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
                logger.LogInfo($"Service '{serviceName}' force-stopped");
            }
            catch (System.TimeoutException)
            {
                logger.LogWarn($"Service '{serviceName}' still not stopped after force-kill - proceeding with start anyway");
            }
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
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
            logger.LogInfo($"Service '{serviceName}' started (by SCM)");
            return;
        }

        // The service may still be in StopPending (e.g. the client process was killed
        // concurrently and the service is tearing down). Wait for it to finish stopping
        // before attempting to start, otherwise sc.Start() throws.
        if (sc.Status == ServiceControllerStatus.StopPending)
        {
            logger.LogInfo($"Service '{serviceName}' is still stopping - waiting");
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped,
                TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
        }

        sc.Start();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running,
            TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs))).ConfigureAwait(false);
        logger.LogInfo($"Service '{serviceName}' started");
    }

    // Called by StopServiceAsync after a clean SCM stop has already timed out.
    // Resolves the service's host PID via QueryServiceStatusEx, then escalates:
    // Process.Kill → wait → taskkill /F /T as last resort.
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

                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { return; } // already exited

                if (process.WaitForExit(2000))
                {
                    logger.LogInfo($"Service '{sc.ServiceName}' process force-killed (PID {pid})");
                    return;
                }

                // Process.Kill failed to terminate in time — fall back to taskkill /F /T
                try
                {
                    using var taskkill = Process.Start(new ProcessStartInfo(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                        $"/F /T /PID {pid}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    });
                    taskkill?.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    logger.LogWarn($"Service '{sc.ServiceName}' taskkill fallback failed (PID {pid}): {ex.Message}");
                }

                logger.LogInfo($"Service '{sc.ServiceName}' process force-killed via taskkill (PID {pid})");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarn($"Service '{sc.ServiceName}' force-kill failed: {ex.Message}");
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
                logger.LogWarn($"Auto-recovery: failed to start netsh");
                return false;
            }

            // Read stdout/stderr before WaitForExit to avoid deadlock on full buffers
            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

            bool exited = await Task.Run(() => process.WaitForExit(NetshTimeoutMs)).ConfigureAwait(false);
            if (!exited)
            {
                // netsh is a short-lived system utility that always responds to Process.Kill —
                // no taskkill fallback needed here.
                process.Kill(entireProcessTree: true);
                logger.LogWarn($"Auto-recovery: netsh timed out and was killed");
                return false;
            }

            if (process.ExitCode != 0)
            {
                string output = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                logger.LogWarn($"Auto-recovery: netsh exited with code {process.ExitCode}: {output}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarn($"Auto-recovery: netsh failed: {ex.Message}");
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

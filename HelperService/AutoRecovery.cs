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
internal static partial class AutoRecovery
{
    private const int ProcessKillTimeoutMs = 5000;
    // Delay between stop and start to let the service host fully release its handles
    // and listening sockets before we ask the SCM to bring it back up.
    private const int ServiceRestartDelayMs = 5000;
    private const int ServiceOperationTimeoutMs = 15000;
    private const int AdapterCycleDelayMs = 3000;
    private const int NetshTimeoutMs = 15000;

    // First positional argument to "netsh interface set interface <name> admin=...".
    private const string NetshInterface = "interface";

    // P/Invoke - used by KillServiceProcess to resolve a service's host process ID
    private const int ScStatusProcessInfo = 0; // SC_STATUS_PROCESS_INFO - only valid infoLevel for QueryServiceStatusEx

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        SafeHandle hService, int infoLevel, IntPtr buffer, int bufSize, out int bytesNeeded);

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

    internal static async Task RestartServiceAsync(string serviceName, HelperLogger logger, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            logger.LogMessage("Service name is empty - nothing to restart", LogLevel.Warn);
            return;
        }

        logger.LogMessage($"Restarting service '{serviceName}'", LogLevel.Info);

        try { await StopServiceAsync(serviceName, logger, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogMessage($"Failed to stop service '{serviceName}': {ex.Message}", LogLevel.Warn); }

        // Brief pause to allow the OS to fully release service resources (handles, sockets)
        // before the start is issued - avoids a race where SCM reports stopped but the
        // underlying process has not yet exited and freed its ports.
        await Task.Delay(ServiceRestartDelayMs, cancellationToken).ConfigureAwait(false);

        try { await StartServiceAsync(serviceName, logger, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogMessage($"Failed to start service '{serviceName}': {ex.Message}", LogLevel.Error);
            return;
        }

        logger.LogMessage($"Restarted service '{serviceName}'", LogLevel.Info);
    }

    // Cycles a network adapter by disabling and re-enabling it via netsh.
    // Used for generic NAT-PMP gateways where no known VPN service is involved.
    // For known providers (ProtonVPN, PIA), the main app sends "restart" instead.
    internal static async Task CycleAdapterAsync(string adapterName, HelperLogger logger, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(adapterName))
            {
                logger.LogMessage("Adapter name is empty - nothing to cycle", LogLevel.Warn);
                return;
            }

            // Defensive: ProcessStartInfo.ArgumentList handles quoting safely (we no longer build
            // a command-line string by hand), but rejecting quote characters in adapter names
            // remains a sanity check since real Windows NICs never contain them.
            if (adapterName.Contains('"'))
            {
                logger.LogMessage("Rejected adapter name containing invalid characters", LogLevel.Warn);
                return;
            }

            logger.LogMessage($"Cycling adapter '{adapterName}'", LogLevel.Info);

            if (!await RunNetshAsync([NetshInterface, "set", NetshInterface, adapterName, "admin=disable"], logger, cancellationToken).ConfigureAwait(false))
            {
                logger.LogMessage($"Failed to disable adapter '{adapterName}'", LogLevel.Warn);
                return;
            }
            logger.LogMessage($"Adapter '{adapterName}' disabled", LogLevel.Info);

            // Past this point the adapter is administratively DOWN, and that state persists across
            // reboots. Neither shutdown nor an unexpected error may leave it there, so the re-enable
            // runs from a finally block and deliberately ignores the cancellation token: a few
            // seconds of delayed service stop is a far better outcome than a machine that boots
            // with no network on this adapter, which the user would have to fix by hand.
            try
            {
                await Task.Delay(AdapterCycleDelayMs, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (await RunNetshAsync([NetshInterface, "set", NetshInterface, adapterName, "admin=enable"],
                        logger, CancellationToken.None).ConfigureAwait(false))
                    logger.LogMessage($"Re-enabled adapter '{adapterName}'", LogLevel.Info);
                else
                    logger.LogMessage($"Failed to re-enable adapter '{adapterName}'", LogLevel.Warn);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogMessage($"Failed to cycle adapter: {ex.Message}", LogLevel.Error);
        }
    }

    // Stops a service cleanly via the SCM, with escalating force if it doesn't respond.
    // Escalation: SCM stop → wait → KillServiceProcess (3-stage: Process.Kill → taskkill /F /T → retry).
    // ServiceController.WaitForStatus has no async overload - wrap in Task.Run to avoid
    // blocking the BackgroundService thread pool thread.
    //
    // IMPORTANT: WaitForStatus throws System.ServiceProcess.TimeoutException, NOT
    // System.TimeoutException - they are sibling classes (both inherit SystemException).
    private static async Task StopServiceAsync(string serviceName, HelperLogger logger, CancellationToken cancellationToken = default)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            logger.LogMessage($"Service '{serviceName}' is already stopped", LogLevel.Info);
            return;
        }

        // The service may already be stopping (e.g. the VPN client crashed and the SCM is
        // tearing the service down). Wait for it to finish instead of calling Stop() again
        // which would throw InvalidOperationException.
        if (sc.Status == ServiceControllerStatus.StopPending)
        {
            logger.LogMessage($"Service '{serviceName}' is already stopping - waiting", LogLevel.Info);
            try
            {
                await WaitForStatusAsync(sc, ServiceControllerStatus.Stopped, cancellationToken).ConfigureAwait(false);
                logger.LogMessage($"Service '{serviceName}' stopped", LogLevel.Info);
                return;
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogMessage($"Service '{serviceName}' StopPending timed out - force-killing process", LogLevel.Warn);
                KillServiceProcess(sc, logger);
                await WaitForStoppedOrWarnAsync(sc, serviceName, logger, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // The service may be mid-startup (e.g. SCM auto-recovery kicked in). Wait for it
        // to finish starting before issuing a stop, otherwise sc.Stop() may throw or behave
        // unpredictably on a partially-initialized service.
        if (sc.Status == ServiceControllerStatus.StartPending)
        {
            logger.LogMessage($"Service '{serviceName}' is starting - waiting before stopping", LogLevel.Info);
            try
            {
                await WaitForStatusAsync(sc, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogMessage($"Service '{serviceName}' StartPending timed out - force-killing process", LogLevel.Warn);
                KillServiceProcess(sc, logger);
                await WaitForStoppedOrWarnAsync(sc, serviceName, logger, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        try { sc.Stop(); }
        catch (Exception ex)
        {
            // sc.Stop() can throw if the service doesn't accept stop controls or is in
            // a transient state. Fall through to force-kill.
            logger.LogMessage($"Failed to stop service '{serviceName}' via SCM: {ex.Message} - force-killing process", LogLevel.Warn);
            KillServiceProcess(sc, logger);
            await WaitForStoppedOrWarnAsync(sc, serviceName, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await WaitForStatusAsync(sc, ServiceControllerStatus.Stopped, cancellationToken).ConfigureAwait(false);
            logger.LogMessage($"Service '{serviceName}' stopped", LogLevel.Info);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            logger.LogMessage($"Service '{serviceName}' stop timed out - force-killing process", LogLevel.Warn);
            KillServiceProcess(sc, logger);
            await WaitForStoppedOrWarnAsync(sc, serviceName, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    // Waits for a service to reach Stopped after a force-kill attempt, logging the outcome.
    private static async Task WaitForStoppedOrWarnAsync(ServiceController sc, string serviceName, HelperLogger logger, CancellationToken cancellationToken = default)
    {
        try
        {
            await WaitForStatusAsync(sc, ServiceControllerStatus.Stopped, cancellationToken).ConfigureAwait(false);
            logger.LogMessage($"Service '{serviceName}' force-stopped", LogLevel.Info);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            logger.LogMessage($"Service '{serviceName}' still not stopped after force-kill - proceeding with start anyway", LogLevel.Warn);
        }
    }

    // Called by StopServiceAsync when the service doesn't respond to a clean stop or is stuck
    // in a pending state. Resolves the service's host PID via QueryServiceStatusEx, then
    // delegates the kill escalation (Process.Kill -> taskkill /F /T -> retry) to
    // ProcessKillHelper in qbPortWeaver.Shared so the main app and the helper service share
    // the same logic. The per-outcome logging stays here because it embeds service-name and PID
    // context that the shared helper does not know about.
    //
    // Intentionally synchronous: the kill escalation is sequential by design (each stage must
    // complete before the next begins), and this runs only on the exceptional "service not
    // responding" path. Worst-case blocking is 4 x ProcessKillTimeoutMs (20s) on the caller's
    // thread-pool thread - four waits, not three, because stage 2 waits for the taskkill
    // subprocess and then again for the target. Still well inside the pipe client's 120s response
    // timeout, and acceptable given how rarely this path is reached.
    private static void KillServiceProcess(ServiceController sc, HelperLogger logger)
    {
        try
        {
            if (sc.ServiceHandle.IsInvalid) return;

            int bufSize = Marshal.SizeOf<ServiceStatusProcess>();
            IntPtr buf = Marshal.AllocHGlobal(bufSize);
            try
            {
                // Pass the SafeHandle directly - the [LibraryImport] marshaller AddRef/Releases
                // it for the duration of the call so the handle stays valid even if the
                // ServiceController is finalized concurrently. Avoids DangerousGetHandle.
                if (!QueryServiceStatusEx(sc.ServiceHandle,
                        ScStatusProcessInfo, buf, bufSize, out _))
                    return;

                int pid = Marshal.PtrToStructure<ServiceStatusProcess>(buf).dwProcessId;
                if (pid <= 0) return;

                // Known residual risk: between QueryServiceStatusEx returning the PID and
                // GetProcessById opening it, the service process could exit cleanly and Windows
                // could recycle the PID for an unrelated process. We would then force-kill the
                // wrong process as SYSTEM. The window is microseconds and PID reuse on Windows
                // typically takes much longer, so the probability is negligible in practice.
                Process process;
                try { process = Process.GetProcessById(pid); }
                catch (ArgumentException) { return; } // already exited between SCM query and here

                using (process)
                {
                    var result = ProcessKillHelper.KillProcessTreeWithEscalation(process, ProcessKillTimeoutMs);
                    LogKillOutcome(sc.ServiceName, pid, result, logger);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            logger.LogMessage($"Failed to force-kill service '{sc.ServiceName}': {ex.Message}", LogLevel.Warn);
        }
    }

    // Renders a ProcessKillHelper outcome as service-context-aware log entries. Centralised here
    // so KillServiceProcess stays focused on PID resolution and the shared helper stays
    // service-agnostic.
    private static void LogKillOutcome(string serviceName, int pid, ProcessKillResult result, HelperLogger logger)
    {
        if (result.TaskkillError is not null)
            logger.LogMessage($"Failed to kill service '{serviceName}' via taskkill (PID {pid}): {result.TaskkillError.Message}", LogLevel.Warn);

        switch (result.Outcome)
        {
            case ProcessKillOutcome.AlreadyExited:
                logger.LogMessage($"Service '{serviceName}' process (PID {pid}) already exited", LogLevel.Warn);
                break;
            case ProcessKillOutcome.AccessDenied:
                logger.LogMessage($"Service '{serviceName}' process (PID {pid}) could not be killed - access denied or process protected", LogLevel.Warn);
                break;
            case ProcessKillOutcome.KilledByProcessKill:
                logger.LogMessage($"Service '{serviceName}' process force-killed via Process.Kill (PID {pid})", LogLevel.Info);
                break;
            case ProcessKillOutcome.KilledByTaskkill:
                logger.LogMessage($"Service '{serviceName}' process force-killed via taskkill (PID {pid})", LogLevel.Info);
                break;
            case ProcessKillOutcome.KilledByProcessKillRetry:
                logger.LogMessage($"Service '{serviceName}' process force-killed via Process.Kill retry (PID {pid})", LogLevel.Info);
                break;
            case ProcessKillOutcome.StillRunning:
                logger.LogMessage($"Service '{serviceName}' process (PID {pid}) still running after all kill attempts", LogLevel.Warn);
                break;
        }
    }

    private static async Task StartServiceAsync(string serviceName, HelperLogger logger, CancellationToken cancellationToken = default)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running)
        {
            logger.LogMessage($"Service '{serviceName}' is already running", LogLevel.Info);
            return;
        }

        // The SCM's recovery policy may have already triggered a restart (e.g. VPN services
        // are typically configured to restart automatically on failure). If the service is
        // already starting, just wait for it instead of calling Start() which would throw.
        if (sc.Status == ServiceControllerStatus.StartPending)
        {
            logger.LogMessage($"Service '{serviceName}' is already starting (likely SCM auto-recovery) - waiting", LogLevel.Info);
            try
            {
                await WaitForStatusAsync(sc, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false);
                logger.LogMessage($"Service '{serviceName}' started (by SCM)", LogLevel.Info);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogMessage($"Service '{serviceName}' stuck in StartPending after {ServiceOperationTimeoutMs}ms", LogLevel.Warn);
                throw;
            }
            return;
        }

        // The service may still be in StopPending (e.g. the client process was killed
        // concurrently and the service is tearing down). Wait for it to finish stopping
        // before attempting to start, otherwise sc.Start() throws.
        if (sc.Status == ServiceControllerStatus.StopPending)
        {
            logger.LogMessage($"Service '{serviceName}' is still stopping - waiting", LogLevel.Info);
            try
            {
                await WaitForStatusAsync(sc, ServiceControllerStatus.Stopped, cancellationToken).ConfigureAwait(false);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogMessage($"Service '{serviceName}' StopPending timed out during start - proceeding anyway", LogLevel.Warn);
            }
        }

        try { sc.Start(); }
        catch (InvalidOperationException)
        {
            // SCM auto-recovery may have started the service between our StopPending wait and this
            // call. Refresh and treat Running/StartPending as success; rethrow anything else.
            sc.Refresh();
            if (sc.Status is not ServiceControllerStatus.Running and not ServiceControllerStatus.StartPending)
                throw;
            logger.LogMessage($"Service '{serviceName}' is already starting (likely SCM auto-recovery) - waiting", LogLevel.Info);
            try
            {
                await WaitForStatusAsync(sc, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false);
                logger.LogMessage($"Service '{serviceName}' started (by SCM)", LogLevel.Info);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                logger.LogMessage($"Service '{serviceName}' stuck in StartPending after {ServiceOperationTimeoutMs}ms", LogLevel.Warn);
                throw;
            }
            return;
        }

        try
        {
            await WaitForStatusAsync(sc, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false);
            logger.LogMessage($"Service '{serviceName}' started", LogLevel.Info);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            logger.LogMessage($"Service '{serviceName}' start timed out - service may still be starting", LogLevel.Warn);
        }
    }

    // Wraps the synchronous ServiceController.WaitForStatus in Task.Run so it doesn't block a BackgroundService thread.
    // Note: WaitForStatus has no cancellation support - the cancellationToken is checked before scheduling and on entry,
    // but cannot interrupt a WaitForStatus already in progress. The timeout (ServiceOperationTimeoutMs) is the hard bound.
    private static Task WaitForStatusAsync(ServiceController sc, ServiceControllerStatus status, CancellationToken cancellationToken = default) =>
        Task.Run(() => sc.WaitForStatus(status, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs)), cancellationToken);

    // Runs a netsh command and returns true if it exits with code 0.
    // Arguments are passed via ProcessStartInfo.ArgumentList so .NET handles escaping/quoting,
    // avoiding manual quote-escaping pitfalls for adapter names containing spaces or trailing backslashes.
    private static async Task<bool> RunNetshAsync(IReadOnlyList<string> arguments, HelperLogger logger, CancellationToken cancellationToken = default)
    {
        try
        {
            string netshPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
            var startInfo = new ProcessStartInfo(netshPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in arguments)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogMessage("Failed to start netsh", LogLevel.Warn);
                return false;
            }

            // Start drain tasks concurrently so a full pipe buffer cannot deadlock the child against WaitForExit.
            // Drain tasks are awaited only after WaitForExit returns - awaiting them first would let a hung
            // child block indefinitely (ReadToEndAsync only completes on stream close), defeating the timeout.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            bool exited = await Task.Run(() => process.WaitForExit(NetshTimeoutMs), cancellationToken).ConfigureAwait(false);
            if (!exited)
            {
                // netsh is a short-lived system utility that always responds to Process.Kill -
                // no taskkill fallback needed here. Kill triggers EOF on stdout/stderr so the drain
                // tasks complete naturally; we abandon them since their output is no longer useful.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(ProcessKillTimeoutMs);
                logger.LogMessage("netsh timed out and was killed", LogLevel.Warn);
                return false;
            }

            // Process has exited - streams are closed so drain tasks complete promptly.
            // Re-throw if shutdown was requested so the caller sees OCE, not a spurious Warn.
            cancellationToken.ThrowIfCancellationRequested();
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string output = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                logger.LogMessage($"netsh exited with code {process.ExitCode}: {output}", LogLevel.Warn);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogMessage($"Failed to run netsh: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }
}

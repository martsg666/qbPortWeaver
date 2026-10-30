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
    // These timeouts add up to the helper's worst-case restart time - roughly 100-115s across the SCM
    // stop, the escalating force-kill, the pause, the start and the post-start verification - which
    // HelperServiceClient.ResponseTimeoutMs (120s, in the main app) must stay above. Raising any of them
    // means re-deriving that sum and raising the client's bound to match.
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
    private const int SC_STATUS_PROCESS_INFO = 0; // the only valid infoLevel for QueryServiceStatusEx
    private const int SERVICE_WIN32_SHARE_PROCESS = 0x20;   // dwServiceType bit
    private const int SERVICE_RUNS_IN_SYSTEM_PROCESS = 0x1; // dwServiceFlags bit

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

    /// <remarks>
    /// Cancellation between the stop and the start deliberately leaves the service stopped, unlike
    /// <see cref="CycleAdapterAsync"/>, which re-enables its adapter from a <c>finally</c>. The
    /// asymmetry is a decision, not an oversight:
    /// <para>A stopped service does not persist the way an administratively disabled adapter does.
    /// VPN services are <c>Start=auto</c>, so a reboot restores one, and short of a reboot the sync
    /// loop sees the VPN down, accumulates failures and dispatches another recovery that starts it.
    /// Both routes self-correct; a disabled adapter has neither.</para>
    /// <para>A <c>finally</c>-start would also have to run during the helper's own shutdown, on
    /// <see cref="CancellationToken.None"/> to be of any use, and starting a service can take
    /// seconds - <see cref="StartServiceAsync"/> waits up to <see cref="ServiceOperationTimeoutMs"/>.
    /// That delay lands inside the SCM's stop timeout, which kills the service if it overruns, and an
    /// MSI upgrade (<c>ServiceControl Stop="both"</c>) is exactly when that would happen. Trading a
    /// self-correcting outage for a killed service during upgrades is the worse bargain.
    /// <c>netsh admin=enable</c> takes about a second, which is why the adapter path can afford it.</para>
    /// </remarks>
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

        // Verified, not assumed. StartServiceAsync deliberately swallows a start timeout - the service
        // may genuinely still be coming up - so reaching this point does not mean it is running, and
        // "Restarted service 'X'" would then be a false reassurance about the one thing that matters.
        // It matters more than a wrong log line usually would: this method has just stopped a VPN
        // service, and with a killswitch enabled a service that never comes back leaves the machine
        // with no network at all.
        await VerifyServiceRunningAsync(serviceName, logger, cancellationToken).ConfigureAwait(false);
    }

    // Reports the service's real state after a restart attempt, replacing the unconditional success
    // line. Logged at Error when it is not running: the helper's WARN/ERROR counts travel back to the
    // tray app over the pipe and raise its alert, and a VPN service that failed to come back is exactly
    // what the user needs told - staying silent leaves them to discover it as "the internet is broken".
    private static async Task VerifyServiceRunningAsync(string serviceName, HelperLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();

            // Still coming up is not yet a failure. Give it one more bounded wait rather than reporting
            // an error for a service that starts a second later - that false alarm would be its own bug.
            if (sc.Status == ServiceControllerStatus.StartPending)
            {
                try { await WaitForStatusAsync(sc, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false); }
                catch (System.ServiceProcess.TimeoutException) { } // NOSONAR S108 - the refreshed status below is the report
                sc.Refresh();
            }

            if (sc.Status == ServiceControllerStatus.Running)
            {
                logger.LogMessage($"Restarted service '{serviceName}'", LogLevel.Info);
                return;
            }

            logger.LogMessage(
                $"Service '{serviceName}' is {sc.Status} after the restart attempt, not Running - the VPN cannot reconnect until it starts. " +
                "If a VPN killswitch is enabled, this machine may have no network access until then.",
                LogLevel.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Could not read the state at all. Reported rather than swallowed, but only as a Warn: an
            // unverifiable restart is not evidence the restart failed.
            logger.LogMessage($"Could not verify service '{serviceName}' after the restart attempt: {ex.Message}", LogLevel.Warn);
        }
    }

    // Log levels around the kill escalation, so the force-kill announcements do not drift back to Warn:
    // an *announcement* that we are escalating to a force-kill is Info, because escalating
    // is the designed fallback and its outcome is logged separately. Only the *outcome* carries severity -
    // Info when a stage succeeds ("force-killed via ..."), Warn when one fails ("Failed to ...", "could
    // not be killed", "still running"). The distinction matters beyond tidiness: the helper's Warn count
    // travels back over the pipe and raises the tray warning badge, so a Warn on the success path made
    // every routine recovery look like a fault. ProtonVPN's service never accepts an SCM stop while its
    // tunnel is up, so for that provider the force-kill path is not the exception - it is every time.
    // The main app follows the same rule: ProcessControl logs only kill outcomes, AutoRecoveryManager
    // logs a successful kill at Info.

    // Cycles a network adapter by disabling and re-enabling it via netsh.
    // Used when the adapter belongs to no known provider: a generic NAT-PMP gateway, or a Static
    // port adapter the registry does not recognise. For an adapter of a known provider (ProtonVPN,
    // PIA) the main app sends "restart" instead, but only when that provider's service is installed;
    // otherwise, e.g. a WireGuard tunnel named after the provider, it cycles the adapter here too.
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

            // From here the adapter may be administratively DOWN, and that state persists across
            // reboots. Neither shutdown nor an unexpected error may leave it there, so the re-enable
            // runs from a finally block and deliberately ignores the cancellation token: a few
            // seconds of delayed service stop is a far better outcome than a machine that boots
            // with no network on this adapter, which the user would have to fix by hand.
            //
            // The try opens *before* the disable, not after it. RunNetshAsync lets
            // OperationCanceledException propagate by design (so a shutdown is not misreported as a
            // netsh failure), and it can only throw once netsh has already been started - at which
            // point the adapter may be down. Opening the try after the disable left that window
            // uncovered: a stop signalled there escaped past the catch below, which also excludes
            // OCE, and the adapter stayed disabled.
            //
            // adapterMayBeDown starts true and is cleared only when netsh *explicitly reports*
            // failure, because that is the one outcome proving the adapter is still up. A throw, or a
            // netsh killed after its timeout, leaves it unknown - the disable may have taken effect
            // before netsh hung - so the flag stays set and the re-enable runs anyway: re-enabling an
            // adapter that was never disabled is a no-op, failing to re-enable one that was is not.
            bool adapterMayBeDown = true;
            try
            {
                var disable = await RunNetshAsync([NetshInterface, "set", NetshInterface, adapterName, "admin=disable"], logger, cancellationToken).ConfigureAwait(false);
                if (disable == NetshResult.Failed)
                {
                    adapterMayBeDown = false;
                    logger.LogMessage($"Failed to disable adapter '{adapterName}'", LogLevel.Warn);
                    return;
                }
                if (disable == NetshResult.Unknown)
                    logger.LogMessage($"netsh did not confirm that adapter '{adapterName}' was disabled - re-enabling it anyway", LogLevel.Warn);
                else
                    logger.LogMessage($"Adapter '{adapterName}' disabled", LogLevel.Info);

                await Task.Delay(AdapterCycleDelayMs, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (adapterMayBeDown)
                {
                    var enable = await RunNetshAsync([NetshInterface, "set", NetshInterface, adapterName, "admin=enable"],
                        logger, CancellationToken.None).ConfigureAwait(false);
                    if (enable == NetshResult.Succeeded)
                        logger.LogMessage($"Re-enabled adapter '{adapterName}'", LogLevel.Info);
                    else if (enable == NetshResult.Unknown)
                        logger.LogMessage($"netsh did not confirm that adapter '{adapterName}' was re-enabled - check it in Network Connections", LogLevel.Warn);
                    else
                        logger.LogMessage($"Failed to re-enable adapter '{adapterName}'", LogLevel.Warn);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogMessage($"Failed to cycle adapter: {ex.Message}", LogLevel.Error);
        }
    }

    // Stops a service cleanly via the SCM, with escalating force if it doesn't respond.
    // Escalation: SCM stop -> wait -> KillServiceProcess (3-stage: Process.Kill -> taskkill /F /T -> retry).
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
                await ForceKillAndWaitAsync(sc, serviceName,
                    $"Service '{serviceName}' StopPending timed out", logger, cancellationToken).ConfigureAwait(false);
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
                await ForceKillAndWaitAsync(sc, serviceName,
                    $"Service '{serviceName}' StartPending timed out", logger, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        try { sc.Stop(); }
        catch (Exception ex)
        {
            // sc.Stop() can throw if the service doesn't accept stop controls or is in
            // a transient state. Fall through to force-kill.
            await ForceKillAndWaitAsync(sc, serviceName,
                $"Failed to stop service '{serviceName}' via SCM: {ex.Message}", logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await WaitForStatusAsync(sc, ServiceControllerStatus.Stopped, cancellationToken).ConfigureAwait(false);
            logger.LogMessage($"Service '{serviceName}' stopped", LogLevel.Info);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            await ForceKillAndWaitAsync(sc, serviceName,
                $"Service '{serviceName}' stop timed out", logger, cancellationToken).ConfigureAwait(false);
        }
    }

    // The escalation the stop path falls back to whenever SCM cannot bring the service down: say why,
    // kill the process, then wait for Stopped. Every route that gives up on SCM comes through here -
    // StopPending timing out, StartPending timing out, sc.Stop() throwing, and the ordinary stop
    // timing out - each having run the identical three steps and differed only in the reason. That
    // last one is the common case and was the one missed when this was first extracted, which is the
    // drift in miniature. Keeping them as one sequence is what stops a later change (a
    // different wait, an extra log line) reaching some routes and not the rest.
    //
    // The caller still returns after this: the escalation is terminal for the stop attempt, and making
    // that explicit at each site reads better than a bool nobody branches on.
    private static async Task ForceKillAndWaitAsync(ServiceController sc, string serviceName, string reason,
        HelperLogger logger, CancellationToken cancellationToken)
    {
        logger.LogMessage($"{reason} - attempting to force-kill the process", LogLevel.Info);
        KillServiceProcess(sc, logger);
        await WaitForStoppedOrWarnAsync(sc, serviceName, logger, cancellationToken).ConfigureAwait(false);
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
            // Worded for every route here, not only a kill that ran: the shared-host refusal and a failed or
            // denied kill also arrive, and each has already logged why the process was not killed.
            logger.LogMessage($"Service '{serviceName}' still not stopped - proceeding with start anyway", LogLevel.Warn);
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
            // Every early return logs its reason: StopServiceAsync's "still not stopped" line that follows
            // relies on each route having said why the process was not killed.
            if (sc.ServiceHandle.IsInvalid)
            {
                logger.LogMessage($"Service '{sc.ServiceName}' could not be force-killed - its service handle is not valid", LogLevel.Warn);
                return;
            }

            int bufSize = Marshal.SizeOf<ServiceStatusProcess>();
            IntPtr buf = Marshal.AllocHGlobal(bufSize);
            try
            {
                // Pass the SafeHandle directly - the [LibraryImport] marshaller AddRef/Releases
                // it for the duration of the call so the handle stays valid even if the
                // ServiceController is finalized concurrently. Avoids DangerousGetHandle.
                if (!QueryServiceStatusEx(sc.ServiceHandle,
                        SC_STATUS_PROCESS_INFO, buf, bufSize, out _))
                {
                    logger.LogMessage($"Service '{sc.ServiceName}' could not be force-killed - its process ID could not be read " +
                        $"(error {Marshal.GetLastPInvokeError()})", LogLevel.Warn);
                    return;
                }

                var status = Marshal.PtrToStructure<ServiceStatusProcess>(buf);

                // A shared host (svchost) runs other services too, and a system process is Windows
                // itself: killing either takes down far more than the service asked for. The VPN
                // services this exists for each run in their own process, so refusing here never
                // blocks a real recovery.
                if ((status.dwServiceType & SERVICE_WIN32_SHARE_PROCESS) != 0 ||
                    (status.dwServiceFlags & SERVICE_RUNS_IN_SYSTEM_PROCESS) != 0)
                {
                    logger.LogMessage($"Service '{sc.ServiceName}' shares its process with other services - not force-killed", LogLevel.Warn);
                    return;
                }

                int pid = status.dwProcessId;
                if (pid <= 0)
                {
                    // No process: the service stopped on its own after the stop timed out.
                    logger.LogMessage($"Service '{sc.ServiceName}' has no process left to force-kill", LogLevel.Info);
                    return;
                }

                // Known residual risk: between QueryServiceStatusEx returning the PID and
                // GetProcessById opening it, the service process could exit cleanly and Windows
                // could recycle the PID for an unrelated process. We would then force-kill the
                // wrong process as SYSTEM. The window is microseconds and PID reuse on Windows
                // typically takes much longer, so the probability is negligible in practice.
                Process process;
                try { process = Process.GetProcessById(pid); }
                catch (ArgumentException)
                {
                    // Already exited between the SCM query and here.
                    logger.LogMessage($"Service '{sc.ServiceName}' process (PID {pid}) exited before it could be force-killed", LogLevel.Info);
                    return;
                }

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
                logger.LogMessage($"Service '{serviceName}' process (PID {pid}) had already exited when the force-kill reached it", LogLevel.Info);
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

    // Waits for a start the SCM itself is carrying out - its recovery policy restarting the service -
    // rather than calling Start(), which would throw while one is pending. Shared by the two places
    // StartServiceAsync finds the service already starting.
    private static async Task AwaitScmStartAsync(ServiceController sc, string serviceName, HelperLogger logger, CancellationToken cancellationToken)
    {
        logger.LogMessage($"Service '{serviceName}' is already starting (likely SCM auto-recovery) - waiting", LogLevel.Info);
        try
        {
            await WaitForStatusAsync(sc, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false);
            logger.LogMessage($"Service '{serviceName}' started (by SCM)", LogLevel.Info);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            // Not a failure yet: the verification after StartServiceAsync gives the verdict, as for its own
            // start timeout, and a throw would skip it and report Error for a service that may be Running a
            // moment later.
            logger.LogMessage($"Service '{serviceName}' stuck in StartPending after {ServiceOperationTimeoutMs}ms", LogLevel.Info);
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
            await AwaitScmStartAsync(sc, serviceName, logger, cancellationToken).ConfigureAwait(false);
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
                logger.LogMessage($"Service '{serviceName}' StopPending timed out during start - proceeding anyway", LogLevel.Info);
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
            await AwaitScmStartAsync(sc, serviceName, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await WaitForStatusAsync(sc, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false);
            logger.LogMessage($"Service '{serviceName}' started", LogLevel.Info);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            // Info, like the other wait timeouts in this method: VerifyServiceRunningAsync gives the
            // verdict and logs Error if the service never comes up. A Warn here travels back to the
            // tray and badges a recovery whose service started a few seconds late.
            logger.LogMessage($"Service '{serviceName}' start timed out - service may still be starting", LogLevel.Info);
        }
    }

    // Wraps the synchronous ServiceController.WaitForStatus in Task.Run so it doesn't block a BackgroundService thread.
    // Note: WaitForStatus has no cancellation support - the cancellationToken is checked before scheduling and on entry,
    // but cannot interrupt a WaitForStatus already in progress. The timeout (ServiceOperationTimeoutMs) is the hard bound.
    private static Task WaitForStatusAsync(ServiceController sc, ServiceControllerStatus status, CancellationToken cancellationToken = default) =>
        Task.Run(() => sc.WaitForStatus(status, TimeSpan.FromMilliseconds(ServiceOperationTimeoutMs)), cancellationToken);

    // Failed means netsh reported failure (or never started), so the command did not take effect.
    // Unknown means netsh started but its outcome was never read - killed after its timeout, or an
    // error after start - so the command may or may not have taken effect.
    private enum NetshResult { Succeeded, Failed, Unknown }

    // Runs a netsh command: Succeeded when it exits with code 0, otherwise see NetshResult.
    // Arguments are passed via ProcessStartInfo.ArgumentList so .NET handles escaping/quoting,
    // avoiding manual quote-escaping pitfalls for adapter names containing spaces or trailing backslashes.
    private static async Task<NetshResult> RunNetshAsync(IReadOnlyList<string> arguments, HelperLogger logger, CancellationToken cancellationToken = default)
    {
        bool started = false;
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
                return NetshResult.Failed;
            }
            started = true;

            // Start drain tasks concurrently so a full pipe buffer cannot deadlock the child against WaitForExit.
            // Drain tasks are awaited only after WaitForExit returns - awaiting them first would let a hung
            // child block indefinitely (ReadToEndAsync only completes on stream close), defeating the timeout.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Not cancellable: a token cancelled before Task.Run schedules would skip the wait while netsh
            // is still running, and the caller's re-enable would then race this command - a disable that
            // finished last would leave the adapter down. The wait is bounded by NetshTimeoutMs, and a
            // shutdown is still reported just below, once netsh has exited.
            bool exited = await Task.Run(() => process.WaitForExit(NetshTimeoutMs), CancellationToken.None).ConfigureAwait(false);
            if (!exited)
            {
                // netsh is a short-lived system utility that always responds to Process.Kill -
                // no taskkill fallback needed here. Kill triggers EOF on stdout/stderr so the drain
                // tasks complete naturally; we abandon them since their output is no longer useful.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(ProcessKillTimeoutMs);
                logger.LogMessage("netsh timed out and was killed", LogLevel.Warn);
                return NetshResult.Unknown;
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
                return NetshResult.Failed;
            }

            return NetshResult.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogMessage($"Failed to run netsh: {ex.Message}", LogLevel.Warn);
            return started ? NetshResult.Unknown : NetshResult.Failed;
        }
    }
}

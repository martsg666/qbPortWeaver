using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>Outcome of a port sync cycle, used to drive the tray icon color and tooltip.</summary>
public enum SyncState
{
    /// <summary>Port was successfully detected and applied to the BitTorrent client.</summary>
    Synced,
    /// <summary>VPN is not connected; no port is available to sync.</summary>
    VpnDisconnected,
    /// <summary>Sync is paused because the BitTorrent client or VPN provider is not configured.</summary>
    Disabled,
    /// <summary>An error occurred during the sync cycle (e.g. client unreachable, port update failed).</summary>
    Error,
    /// <summary>Sync cycles are temporarily paused by the user via the tray menu. Not persisted - a restart always resumes.</summary>
    Paused,
    /// <summary>Within the startup grace window the VPN is not up yet; sync is held quietly until it connects.</summary>
    WaitingForVpn,
}

/// <summary>Snapshot of the tray icon state after a sync cycle, raised via <see cref="PortSyncService.SyncCompleted"/>.</summary>
public sealed record TrayStatus(SyncState State, int? Port, string Message);

/// <summary>Background service that syncs the BitTorrent client's listening port with the VPN-assigned port on each cycle.</summary>
public sealed class PortSyncService
{
    // Connection status value returned by clients that support GetConnectionStatusAsync
    private const string ClientDisconnectedStatus = "disconnected";

    /// <summary>Raised when a sync cycle completes (success or failure) with the resulting tray status.</summary>
    public event Action<TrayStatus>? SyncCompleted;

    /// <summary>Raised when the BitTorrent client's network interface does not match the configured VPN provider.</summary>
    public event Action<string>? InterfaceMismatchDetected;

    /// <summary>Raised when the BitTorrent client's listening port is successfully updated to a new value.</summary>
    public event Action<string>? PortUpdated;

    /// <summary>Raised once when the forwarded port is confirmed unreachable from outside (two consecutive failed checks). Transition-only - it re-fires only after the port has tested open again.</summary>
    public event Action<string>? PortVerificationFailed;

    // Consecutive sync cycles in which the VPN was disconnected or port detection failed.
    // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
    private int _consecutiveFailedCycles;
    // Wall-clock time the current failure streak began (re-stamped on every 0 -> 1 transition
    // of the counter, so it always describes the streak in progress). Auto-recovery gates on
    // this in addition to the cycle count so a burst of early wakes (network-change re-syncs
    // interrupting the inter-cycle delay) cannot fast-track a heavy recovery action during a
    // transient outage that would self-heal. Only meaningful while the counter is non-zero.
    private DateTime _failureStreakStartedUtc;
    // Tracks the last interface-mismatch message shown as a balloon tip to suppress repeat invocations
    // for the same persistent mismatch. Cleared when the mismatch resolves so the balloon re-fires if it returns.
    // Thread-safety: only read/written inside CheckInterfaceMatch via EnsureRunningAndUpdatePortAsync,
    // serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles and _lastKnownNatPmpManager).
    private string? _lastInterfaceMismatchMessage;

    // Port verification throttle: full reachability tests run at most every N cycles because
    // Transmission's and Deluge's tests contact their projects' online check services.
    private const int VerifyEveryNCycles = 5;

    // Startup grace window: right after the app starts the VPN is often still connecting (boot/login).
    // For this long, a not-yet-connected VPN is held quietly - no failure, recovery, default-port
    // fallback, or orange state - and re-checked every StartupGracePollSeconds so the first sync lands
    // promptly once it is up, instead of waiting a full (possibly multi-minute) update interval.
    private const int StartupGracePeriodSeconds = 90;
    private const int StartupGracePollSeconds = 15;
    // Started once at construction (the sync service is created once at app startup). Monotonic, so a
    // wall-clock/NTP correction during the grace window - likely just after boot/login - cannot shift it.
    private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();
    // Set when the current cycle is a quiet startup wait (VPN not up within the grace window), so
    // RaiseSyncCompleted maps it to the neutral WaitingForVpn tray state instead of orange
    // VpnDisconnected. Reset at the start of every cycle; serialised by MainForm._updateSemaphore.
    private bool _waitingForVpnThisCycle;
    // Latches across cycles while the app is holding for the VPN during startup, so the one-time
    // "grace period ended" marker fires exactly once when the window elapses (or the VPN comes up).
    // Unlike _waitingForVpnThisCycle (per-cycle), this persists. Serialised by MainForm._updateSemaphore.
    private bool _graceHoldActive;

    // Port verification state. Serialised by MainForm._updateSemaphore (same guarantee as
    // _consecutiveFailedCycles). Deliberately not reset on a port change: the condition being
    // tracked is "incoming connections unreachable", which survives a new port assignment.
    // Initialised above the threshold (VerifyEveryNCycles) so the first increment in
    // ShouldVerifyThisCycle brings it above the "< VerifyEveryNCycles" guard, triggering a
    // verification on the first eligible cycle after startup. A stale mapping is most likely
    // right after a restart, and "ports match" alone cannot see it.
    private int _cyclesSinceVerify = VerifyEveryNCycles;
    private bool _portCheckPendingConfirmation; // one unconfirmed closed result seen
    private bool _portConfirmedClosed;          // closed confirmed by two consecutive checks

    // Port-closed recovery state (serialised by MainForm._updateSemaphore like the rest).
    // The armed flag implements one-shot recovery: a persistent false "closed" (e.g. qBittorrent's
    // idle-firewalled state, which can last indefinitely on a client with no active transfers)
    // causes at most one recovery action - re-armed only after a verification reports the port open.
    private int _confirmedClosedCount;
    private bool _portClosedRecoveryArmed = true;

    // Set when a recovery action is dispatched (automatic or manual test) and consumed by the
    // next successful cycle: a port change in that cycle gets the "after recovery" history
    // annotation. Static because DispatchRecoveryAsync is shared by the sync loop's instance
    // paths and the static manual-test entry point (TestRecoveryAsync); the app runs a single
    // sync service, and volatile covers the manual test setting it from the UI thread.
    private static volatile bool _recoveryDispatched;

    // Snapshot of _recoveryDispatched taken at cycle start, so a recovery dispatched mid-cycle
    // (port-closed trigger fires after the port update step) is never consumed by the same
    // cycle that dispatched it. Serialised by MainForm._updateSemaphore.
    private bool _recoveryPendingThisCycle;

    // Whether the running cycle was triggered by a network change (passed in by MainForm's
    // debounced NetworkChange handler). Drives the "after network change" history annotation.
    // Overwritten at the start of every cycle; serialised by MainForm._updateSemaphore.
    private bool _networkChangeCycle;

    // Fallback for when TryCreateForAdapterAsync cannot reach the configured adapter (e.g. VPN is
    // between disconnect and reconnect) - returned so IsVpnConnected() reports false and
    // RunCoreAsync handles disconnection gracefully. Cleared when the adapter name changes in settings.
    // Thread-safety: only accessed inside RunCoreAsync, serialised by MainForm._updateSemaphore.
    private NatPmpManager? _lastKnownNatPmpManager;

    // All values read from the registry for a single sync cycle. Only the active client's connection
    // settings are read, into a single Client block (see ReadConfig); the other clients' sections are
    // not touched. Adding another client: add one entry to ClientRegistry (its keys + factory)
    // plus its Settings UI - ReadConfig, CreateManagedClient, and LogConfigDebug are all driven from
    // that table and pick it up with no change here.
    // Per-client behaviour flags stay at the top level: restart-on-disconnect is qBittorrent-only,
    // and the interface-mismatch warning applies only to the clients that report an adapter name.
    private sealed record AppConfig(
        string VpnProvider,
        string NatPmpAdapterName,
        int UpdateInterval,
        string ClientName,
        ClientConfig Client,
        bool QBittorrentWarnOnInterfaceMismatch,
        bool QBittorrentRestartOnDisconnect,
        bool NicotineWarnOnInterfaceMismatch,
        string PostUpdateCommand,
        bool VpnAutoRecoveryEnabled,
        int VpnAutoRecoveryTriggerCycles,
        bool NotifyOnPortUpdate,
        bool VerifyPortAfterSync,
        bool PortClosedRecoveryEnabled,
        int PortClosedRecoveryTriggerChecks,
        bool WaitForVpnOnStartup
    );

    // Groups client behaviour settings passed to EnsureRunningAndUpdatePortAsync
    private sealed record SyncConfig(
        bool ForceStart,
        bool Restart,
        string PostUpdateCommand,
        IVpnManager? VpnManager,
        bool WarnOnInterfaceMismatch,
        bool RestartOnDisconnect,
        bool NotifyOnPortUpdate,
        bool VerifyPort,
        bool PortClosedRecoveryEnabled,
        int PortClosedRecoveryTriggerChecks
    );

    // Compile-time-safe keys and values for the status dictionary written to the JSON status file.
    private static class StatusKeys
    {
        // Keys
        public const string AppVersion = "appVersion";
        public const string Timestamp = "timestamp";
        public const string VpnProvider = "vpnProvider";
        public const string VpnConnected = "vpnConnected";
        public const string VpnPort = "vpnPort";
        public const string Client = "client";
        public const string ClientRunning = "clientRunning";
        public const string ClientPreviousPort = "clientPreviousPort";
        public const string ClientPort = "clientPort";
        public const string PortChanged = "portChanged";
        public const string PortVerified = "portVerified";
        public const string UpdateIntervalSeconds = "updateIntervalSeconds";
        public const string Status = "status";
        public const string Message = "message";
        public const string WaitingForVpn = "waitingForVpn";

        // Values for the Status key live in the public SyncStatusValues (shared with the Status panel).
    }

    /// <summary>Runs one port sync cycle and returns the configured update interval in seconds.
    /// <paramref name="networkChangeTriggered"/> marks a cycle started by the network-change
    /// re-sync; a port change it detects is annotated accordingly in the port history.</summary>
    public async Task<int> RunAsync(bool networkChangeTriggered = false, CancellationToken cancellationToken = default)
    {
        _networkChangeCycle = networkChangeTriggered;
        _recoveryPendingThisCycle = _recoveryDispatched;
        _waitingForVpnThisCycle = false;
        // Initialize status with default values. This is written to the status file at the end of the method (in finally)
        // so it captures the final state even if an exception occurs.
        // The RunCoreAsync method updates this dictionary as it progresses.
        var status = new Dictionary<string, object?>
        {
            [StatusKeys.AppVersion] = AppConstants.AppVersion,
            [StatusKeys.Timestamp] = DateTimeOffset.Now,
            [StatusKeys.VpnProvider] = null,
            [StatusKeys.VpnConnected] = false,
            [StatusKeys.VpnPort] = null,
            [StatusKeys.Client] = null,
            [StatusKeys.ClientRunning] = false,
            [StatusKeys.ClientPreviousPort] = null,
            [StatusKeys.ClientPort] = null,
            [StatusKeys.PortChanged] = false,
            [StatusKeys.PortVerified] = null,
            [StatusKeys.UpdateIntervalSeconds] = AppConstants.DefaultUpdateIntervalSeconds,
            [StatusKeys.Status] = SyncStatusValues.Error,
            [StatusKeys.Message] = null
        };

        try
        {
            return await RunCoreAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            SetSyncResult(status, false, $"An unexpected error occurred: {ex.Message}");
            return AppConstants.DefaultUpdateIntervalSeconds;
        }
        finally
        {
            // Skip status write and tray update on clean shutdown - the cycle was cancelled,
            // not failed. Writing an error/disconnected state here would flicker the tray icon
            // and leave a misleading error JSON file on every exit.
            if (!cancellationToken.IsCancellationRequested)
            {
                StatusManager.Write(status);
                string? outcome = status[StatusKeys.Status] as string;
                LogCycleOutcome(outcome);
                if (outcome != SyncStatusValues.Skipped)
                    SessionStats.RecordSync(outcome == SyncStatusValues.Success);
                // A successful cycle concludes a pending recovery whether or not the port
                // changed (the annotation, if due, was applied during the cycle). Only the
                // cycle-start snapshot is cleared, so a recovery dispatched mid-cycle
                // stays pending for the next one.
                if (outcome == SyncStatusValues.Success && _recoveryPendingThisCycle)
                    _recoveryDispatched = false; // NOSONAR S2696 - the app runs a single sync service; the field is static only because the manual-test dispatch path is static
                LaunchPostUpdateCommandIfChanged(status);
                RaiseSyncCompleted(status);
            }
        }
    }

    // Launches the post-update command after the status file has been written, so a script that reads
    // the status file (e.g. an email notifier) sees this cycle's result rather than the previous cycle's.
    // Fires only on a successful port change. Read fresh from the registry since the cycle's SyncConfig
    // is not in scope in RunAsync's finally.
    private static void LaunchPostUpdateCommandIfChanged(Dictionary<string, object?> status)
    {
        if (status[StatusKeys.Status] as string != SyncStatusValues.Success || status[StatusKeys.PortChanged] is not true)
            return;
        string postUpdateCmd = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd);
        if (!string.IsNullOrWhiteSpace(postUpdateCmd))
            RunPostUpdateCommand(postUpdateCmd);
    }

    // Maps the finalized status dictionary to a tray SyncState and raises the SyncCompleted event.
    private void RaiseSyncCompleted(Dictionary<string, object?> status)
    {
        bool success = status[StatusKeys.Status] as string == SyncStatusValues.Success;
        bool vpnConnected = status[StatusKeys.VpnConnected] is true;
        int? port = status[StatusKeys.ClientPort] as int?;
        string message = status[StatusKeys.Message] as string ?? string.Empty;
        string? provider = status[StatusKeys.VpnProvider] as string;
        bool isDisabled = string.Equals(provider, RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);
        // An unrecognized provider value (only reachable via a manual registry edit) is a
        // configuration error, not a disconnection - surface it as Error so the tray shows
        // red with the "not recognized" message rather than orange "VPN not connected".
        bool isKnownProvider = isDisabled || VpnProviderRegistry.IsRecognizedProvider(provider);

        SyncState state;
        if (isDisabled) state = SyncState.Disabled;
        else if (!isKnownProvider) state = SyncState.Error;
        else if (_waitingForVpnThisCycle) state = SyncState.WaitingForVpn;
        else if (!vpnConnected) state = SyncState.VpnDisconnected;
        else if (success) state = SyncState.Synced;
        else state = SyncState.Error;

        try { SyncCompleted?.Invoke(new TrayStatus(state, port, message)); }
        catch (Exception ex) { LogManager.Instance.LogMessage($"SyncCompleted handler failed: {ex.Message}", LogLevel.Warn); }
    }

    // True while the startup grace window is open and the wait-for-VPN setting is on. During this
    // window a not-yet-connected VPN is expected (still establishing after boot/login) and is handled
    // as a quiet wait rather than a failure.
    private bool ShouldWaitForVpnStartup(bool waitEnabled) =>
        waitEnabled && _uptime.Elapsed.TotalSeconds < StartupGracePeriodSeconds;

    // Interval to wait after a grace-window check: the fast poll, but never slower than the user's
    // configured interval (a sub-15s interval would otherwise be slowed down during startup).
    private static int GraceStartupInterval(int updateInterval) => Math.Min(updateInterval, StartupGracePollSeconds);

    // Records a quiet "waiting for VPN" outcome for the current cycle: a neutral tray state (via
    // _waitingForVpnThisCycle, read in RaiseSyncCompleted), an informational log line (not Warn), and a
    // Skipped status. No failure is registered, so no recovery runs and no default port is applied.
    private void MarkWaitingForVpn(Dictionary<string, object?> status, string message)
    {
        _waitingForVpnThisCycle = true;
        _graceHoldActive = true;
        status[StatusKeys.Status] = SyncStatusValues.Skipped;
        // Consistent "startup grace period" wording across the tray tooltip, the Status panel, and the
        // log, so the user can tell this quiet wait is the startup grace period rather than a normal outage.
        status[StatusKeys.Message] = $"{message} (startup grace period)";
        status[StatusKeys.WaitingForVpn] = true;
        // The log adds the remaining time so repeated checks read as a countdown to the window's end.
        int secondsLeft = Math.Max(0, StartupGracePeriodSeconds - (int)_uptime.Elapsed.TotalSeconds);
        LogManager.Instance.LogMessage($"{message} (startup grace period, ~{secondsLeft}s left)", LogLevel.Info);
    }

    // Core logic separated so the outer method handles status writing via finally
    private async Task<int> RunCoreAsync(Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        // Set debug mode as early as possible (reads fresh from registry each loop)
        LogManager.Instance.DebugMode = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode);

        var (cfg, activeSection) = ReadConfig();
        int defaultPort = GetDefaultPort(cfg);
        LogConfigDebug(cfg, activeSection);
        status[StatusKeys.VpnProvider] = cfg.VpnProvider;
        status[StatusKeys.UpdateIntervalSeconds] = cfg.UpdateInterval;

        // If we were holding for the VPN during startup and the grace window has now elapsed (or the
        // setting was turned off), note the transition once so the log explains why quiet "waiting"
        // lines give way to normal handling (a still-disconnected VPN now warns and can trigger recovery).
        if (_graceHoldActive && !ShouldWaitForVpnStartup(cfg.WaitForVpnOnStartup))
        {
            _graceHoldActive = false;
            LogManager.Instance.LogMessage("Startup grace period ended - resuming normal handling", LogLevel.Info);
        }

        // Instantiate VPN manager based on configured provider
        IVpnManager? vpnManager = await CreateVpnManagerAsync(cfg, status, cancellationToken).ConfigureAwait(false);
        if (vpnManager is null)
            // A null from a startup wait (e.g. NAT-PMP adapter not up yet) re-checks on the fast grace poll.
            return _waitingForVpnThisCycle ? GraceStartupInterval(cfg.UpdateInterval) : cfg.UpdateInterval;

        var (forceStart, restart, restartOnDisconnect, warnOnInterfaceMismatch) = GetClientBehaviorConfig(cfg, activeSection);

        // Resolve which port to sync (or whether to stop this cycle) from the VPN state. Split into
        // per-state helpers so this method's control flow stays flat.
        TargetPortResult resolved = vpnManager.IsVpnConnected()
            ? await HandleVpnConnectedAsync(vpnManager, cfg, status, cancellationToken).ConfigureAwait(false)
            : await HandleVpnDisconnectedAsync(vpnManager, cfg, defaultPort, status, cancellationToken).ConfigureAwait(false);
        if (resolved.EarlyInterval is int earlyInterval)
            return earlyInterval;

        int targetPort = resolved.TargetPort;
        IVpnManager? syncVpnManager = resolved.SyncVpnManager;

        using var manager = CreateManagedClient(cfg);
        status[StatusKeys.Client] = manager.ClientName;

        await EnsureRunningAndUpdatePortAsync(manager, targetPort,
            new SyncConfig(
                ForceStart: forceStart,
                Restart: restart,
                PostUpdateCommand: cfg.PostUpdateCommand,
                VpnManager: syncVpnManager,
                WarnOnInterfaceMismatch: warnOnInterfaceMismatch,
                RestartOnDisconnect: restartOnDisconnect,
                NotifyOnPortUpdate: cfg.NotifyOnPortUpdate,
                VerifyPort: cfg.VerifyPortAfterSync,
                PortClosedRecoveryEnabled: cfg.PortClosedRecoveryEnabled,
                PortClosedRecoveryTriggerChecks: cfg.PortClosedRecoveryTriggerChecks),
            status,
            cancellationToken).ConfigureAwait(false);

        return cfg.UpdateInterval;
    }

    // Outcome of resolving which port to sync this cycle. EarlyInterval set => nothing to sync, return it
    // now; otherwise sync TargetPort (SyncVpnManager is the connected manager, or null for the default-port
    // fallback so outside-reachability verification is skipped).
    private readonly record struct TargetPortResult(int? EarlyInterval, int TargetPort, IVpnManager? SyncVpnManager);

    // VPN not connected: hold quietly during the startup grace window, otherwise register the failure
    // (which may trigger recovery) and either fall back to the configured default port or skip the cycle.
    private async Task<TargetPortResult> HandleVpnDisconnectedAsync(IVpnManager vpnManager, AppConfig cfg, int defaultPort, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        // During the startup grace window, a not-yet-connected VPN is expected. Hold quietly and re-check
        // soon rather than registering a failure or applying the default-port fallback.
        if (ShouldWaitForVpnStartup(cfg.WaitForVpnOnStartup))
        {
            MarkWaitingForVpn(status, $"Waiting for {vpnManager.ProviderName} to connect");
            return new TargetPortResult(GraceStartupInterval(cfg.UpdateInterval), 0, null);
        }

        string disconnectedMsg = $"{vpnManager.ProviderName} is not connected";
        await RegisterFailureAndTryRecoveryAsync(
            disconnectedMsg, LogLevel.Info,
            vpnManager.GetRecoveryAction(), vpnManager.GetRecoveryTarget(), vpnManager.ProviderName,
            cfg, cancellationToken).ConfigureAwait(false);

        if (defaultPort == 0)
        {
            status[StatusKeys.Status] = SyncStatusValues.Skipped;
            status[StatusKeys.Message] = disconnectedMsg;
            LogManager.Instance.LogMessage($"{vpnManager.ProviderName} default port is 0 - skipping port update", LogLevel.Info);
            return new TargetPortResult(cfg.UpdateInterval, 0, null);
        }
        LogManager.Instance.LogMessage($"{vpnManager.ProviderName} default port is {defaultPort} - applying to {cfg.ClientName}", LogLevel.Info);
        return new TargetPortResult(null, defaultPort, null); // fall back to the default port (no tunnel to verify)
    }

    // VPN connected: read the assigned port. Hold quietly during the grace window if none is assigned yet,
    // otherwise treat a missing port as a failure. On success, sync the VPN-assigned port.
    private async Task<TargetPortResult> HandleVpnConnectedAsync(IVpnManager vpnManager, AppConfig cfg, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        // Counter is only reset after a successful port detection (see below) so that port detection
        // failures also accumulate toward the auto-recovery threshold.
        status[StatusKeys.VpnConnected] = true;
        LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is connected", LogLevel.Info);

        int? vpnPort = await vpnManager.GetVpnPortAsync(cancellationToken).ConfigureAwait(false);
        if (!vpnPort.HasValue)
        {
            // Connected but no port yet (e.g. NAT-PMP mapping still establishing). Within the startup
            // grace window, wait quietly instead of warning.
            if (ShouldWaitForVpnStartup(cfg.WaitForVpnOnStartup))
            {
                MarkWaitingForVpn(status, $"Waiting for {vpnManager.ProviderName} to assign a port");
                return new TargetPortResult(GraceStartupInterval(cfg.UpdateInterval), 0, null);
            }
            await HandlePortDetectionFailureAsync(vpnManager, cfg, cancellationToken).ConfigureAwait(false);
            SetSyncResult(status, false, $"Failed to determine {vpnManager.ProviderName} port", LogLevel.Warn);
            return new TargetPortResult(cfg.UpdateInterval, 0, null);
        }

        ResetFailureStreak(); // Reset only after a successful port fetch
        _graceHoldActive = false; // VPN is up within the grace window - clear the hold without a marker
        status[StatusKeys.VpnPort] = vpnPort.Value;
        LogManager.Instance.LogMessage($"{vpnManager.ProviderName} port found: {vpnPort.Value}", LogLevel.Info);
        WarnIfNatPmpLeaseTooShort(vpnManager, cfg);
        return new TargetPortResult(null, vpnPort.Value, vpnManager);
    }

    // Warns if a NAT-PMP lease will expire before the next sync cycle renews it.
    private static void WarnIfNatPmpLeaseTooShort(IVpnManager vpnManager, AppConfig cfg)
    {
        if (vpnManager is NatPmpManager natPmp &&
            natPmp.LastGrantedLifetime > 0 &&
            cfg.UpdateInterval > natPmp.LastGrantedLifetime)
            LogManager.Instance.LogMessage(
                $"NAT-PMP sync interval ({cfg.UpdateInterval}s) exceeds lease lifetime ({natPmp.LastGrantedLifetime}s) - port mapping will expire before the next sync cycle",
                LogLevel.Warn);
    }

    // Reads all configuration values from the registry into a single AppConfig record
    private static (AppConfig Config, string ActiveSection) ReadConfig()
    {
        int updateInterval = RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
        if (updateInterval < AppConstants.MinUpdateIntervalSeconds) updateInterval = AppConstants.DefaultUpdateIntervalSeconds;

        int vpnAutoRecoveryTriggerCycles = Math.Max(1, RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryTriggerCycles));

        string clientName = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyClient);
        var activeClient = ClientRegistry.Resolve(clientName);

        // Only the active client's section is read. UserNameKey is null for clients without a username
        // (Deluge); the password is DPAPI-decrypted via GetEncryptedValue (same as the per-client
        // GetXxxPassword helpers). DefaultPort shares one key across all clients.
        var clientConfig = new ClientConfig(
            Url: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.UrlKey),
            UserName: activeClient.UserNameKey is null ? string.Empty : RegistrySettingsManager.GetValue(activeClient.Section, activeClient.UserNameKey),
            Password: RegistrySettingsManager.GetEncryptedValue(activeClient.Section, activeClient.PasswordKey),
            ProcessName: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.ProcessNameKey),
            ExePath: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.ExePathKey),
            Restart: RegistrySettingsManager.GetBool(activeClient.Section, activeClient.RestartKey),
            ForceStart: RegistrySettingsManager.GetBool(activeClient.Section, activeClient.ForceStartKey),
            DefaultPort: RegistrySettingsManager.GetInt(activeClient.Section, RegistrySettingsManager.KeyDefaultPort));

        return (new AppConfig(
            VpnProvider: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider),
            NatPmpAdapterName: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName),
            UpdateInterval: updateInterval,
            ClientName: clientName,
            Client: clientConfig,
            QBittorrentWarnOnInterfaceMismatch: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch),
            QBittorrentRestartOnDisconnect: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect),
            NicotineWarnOnInterfaceMismatch: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineWarnOnInterfaceMismatch),
            PostUpdateCommand: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd),
            VpnAutoRecoveryEnabled: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryEnabled),
            VpnAutoRecoveryTriggerCycles: vpnAutoRecoveryTriggerCycles,
            NotifyOnPortUpdate: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNotifyOnPortUpdate),
            VerifyPortAfterSync: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVerifyPortAfterSync),
            PortClosedRecoveryEnabled: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryEnabled),
            PortClosedRecoveryTriggerChecks: Math.Max(1, RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryTriggerChecks)),
            WaitForVpnOnStartup: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyWaitForVpnOnStartup)
        ), activeClient.Section);
    }

    // Dumps the active AppConfig to the log file when debug mode is enabled.
    // Three lines (general / active client / extra) keep each section independently greppable.
    // The client line is built from the active client's ClientRegistry key names, so it stays in
    // step with whatever ReadConfig read - no per-client branch.
    private static void LogConfigDebug(AppConfig cfg, string activeSection)
    {
        if (!LogManager.Instance.DebugMode) return;

        LogManager.Instance.LogDebug(
            $"PortSyncService.RunCoreAsync [general]: {RegistrySettingsManager.KeyVpnProvider}={cfg.VpnProvider}, " +
            $"{RegistrySettingsManager.KeyNatPmpAdapterName}={cfg.NatPmpAdapterName}, " +
            $"{RegistrySettingsManager.KeyUpdateIntervalSeconds}={cfg.UpdateInterval}s, " +
            $"{RegistrySettingsManager.KeyVpnAutoRecoveryEnabled}={cfg.VpnAutoRecoveryEnabled}, " +
            $"{RegistrySettingsManager.KeyVpnAutoRecoveryTriggerCycles}={cfg.VpnAutoRecoveryTriggerCycles}, " +
            $"{RegistrySettingsManager.KeyClient}={cfg.ClientName}, " +
            $"{RegistrySettingsManager.KeyVerifyPortAfterSync}={cfg.VerifyPortAfterSync}, " +
            $"{RegistrySettingsManager.KeyPortClosedRecoveryEnabled}={cfg.PortClosedRecoveryEnabled}, " +
            $"{RegistrySettingsManager.KeyPortClosedRecoveryTriggerChecks}={cfg.PortClosedRecoveryTriggerChecks}");

        var ci = ClientRegistry.Resolve(cfg.ClientName);
        string clientLine =
            $"PortSyncService.RunCoreAsync [{ci.Name}]: {ci.UrlKey}={cfg.Client.Url}, " +
            (ci.UserNameKey is not null ? $"{ci.UserNameKey}={cfg.Client.UserName}, " : string.Empty) +
            $"{ci.PasswordKey}=***, " + // NOSONAR S2068 - value is masked, not a real credential
            $"{ci.ProcessNameKey}={cfg.Client.ProcessName}, " +
            $"{ci.ExePathKey}={cfg.Client.ExePath}, " +
            $"{ci.RestartKey}={cfg.Client.Restart}, " +
            $"{ci.ForceStartKey}={cfg.Client.ForceStart}, " +
            $"{RegistrySettingsManager.KeyDefaultPort}={cfg.Client.DefaultPort}";
        // qBittorrent exposes two extra RPC-backed flags; append them only for that client.
        if (activeSection == RegistrySettingsManager.SectionQBittorrent)
            clientLine +=
                $", {RegistrySettingsManager.KeyWarnOnInterfaceMismatch}={cfg.QBittorrentWarnOnInterfaceMismatch}" +
                $", {RegistrySettingsManager.KeyRestartOnDisconnect}={cfg.QBittorrentRestartOnDisconnect}";
        LogManager.Instance.LogDebug(clientLine);

        LogManager.Instance.LogDebug(
            $"PortSyncService.RunCoreAsync [extra]: {RegistrySettingsManager.KeyPostUpdateCmd}={cfg.PostUpdateCommand}, " +
            $"{RegistrySettingsManager.KeyDebugMode}={LogManager.Instance.DebugMode}");
    }

    // Instantiates the appropriate VPN manager for the configured provider.
    // Returns null (with status already set) if the provider is disabled or cannot be initialised.
    // PIA and ProtonVPN are stateless and adapter-independent, so the sync loop and the read-only
    // diagnostics path build them identically. NAT-PMP differs between the two (sticky fallback vs.
    // plain probe) and stays with each caller. Returns null for any other provider value.
    private static IVpnManager? CreateStatelessVpnManager(string provider)
    {
        if (provider.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase))
            return new PiaVpnManager();
        if (provider.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase))
            return new ProtonVpnManager(AppConstants.GetProtonVpnLogFilePath());
        return null;
    }

    // Adding a new VPN provider: add a VpnProvider* constant in RegistrySettingsManager; if it is
    // stateless (like PIA/ProtonVPN) add an arm in CreateStatelessVpnManager (shared by the sync loop
    // and diagnostics), otherwise add an arm in both this method and BuildActiveVpnManagerAsync (as
    // NAT-PMP does); then add the keyword in VpnProviderRegistry.IsRecognizedProvider, an entry in
    // VpnProviderRegistry.KnownProviders (when service-restart recovery applies), and the value in
    // SettingsForm's cboVpnProvider list.
    private async Task<IVpnManager?> CreateVpnManagerAsync(AppConfig cfg, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase))
        {
            LogManager.Instance.LogMessage("Port sync disabled", LogLevel.Info);
            status[StatusKeys.Status] = SyncStatusValues.Skipped;
            status[StatusKeys.Message] = "Port sync disabled";
            return null;
        }

        if (CreateStatelessVpnManager(cfg.VpnProvider) is { } manager)
            return manager;

        if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
            return await CreateNatPmpVpnManagerAsync(cfg, status, cancellationToken).ConfigureAwait(false);

        LogManager.Instance.LogMessage($"VPN provider '{cfg.VpnProvider}' is not recognized - check Settings", LogLevel.Warn);
        status[StatusKeys.Status] = SyncStatusValues.Error;
        status[StatusKeys.Message] = $"VPN provider '{cfg.VpnProvider}' is not recognized";
        return null;
    }

    // Resolves the NAT-PMP VPN manager for the configured adapter, handling the disconnected
    // fallback cases and auto-recovery triggering when no adapter is reachable.
    private async Task<IVpnManager?> CreateNatPmpVpnManagerAsync(AppConfig cfg, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cfg.NatPmpAdapterName))
        {
            SetSyncResult(status, false, "No NAT-PMP adapter configured - open Settings and select an adapter");
            return null;
        }

        // Discard the fallback if the adapter name changed in settings
        if (_lastKnownNatPmpManager is not null &&
            !_lastKnownNatPmpManager.ProviderName.Equals(cfg.NatPmpAdapterName, StringComparison.OrdinalIgnoreCase))
            _lastKnownNatPmpManager = null;

        var selected = await NatPmpManager.TryCreateForAdapterAsync(cfg.NatPmpAdapterName, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (selected is not null)
        {
            // Transfer renewal state from the previous instance so port renewal works correctly
            // when TryCreateForAdapterAsync() returns a fresh NatPmpManager instance each cycle.
            if (_lastKnownNatPmpManager is not null)
                selected.CopyRenewalStateFrom(_lastKnownNatPmpManager);
            _lastKnownNatPmpManager = selected;
            return selected;
        }

        // Adapter not found - likely down between disconnect and reconnect.
        // Return the last known manager so IsVpnConnected() reports false and
        // RunCoreAsync handles disconnection gracefully (apply default port or skip).
        if (_lastKnownNatPmpManager is not null)
        {
            LogManager.Instance.LogDebug("PortSyncService.CreateNatPmpVpnManagerAsync: Adapter not discoverable, using last known manager for disconnection handling");
            return _lastKnownNatPmpManager;
        }

        // No previous knowledge of this adapter - VPN likely just disconnected for the first time.
        // No IVpnManager instance is available here (adapter not found, no fallback manager),
        // so we resolve the recovery action and target directly instead of going through the interface.
        string adapterName = cfg.NatPmpAdapterName;
        // Startup grace: the adapter is likely still coming up after boot/login. Wait quietly instead
        // of registering a failure. The null return re-checks on the fast grace poll (see RunCoreAsync).
        if (ShouldWaitForVpnStartup(cfg.WaitForVpnOnStartup))
        {
            MarkWaitingForVpn(status, $"Waiting for VPN adapter '{adapterName}' to come up");
            return null;
        }
        string? providerToken = NatPmpManager.FindProviderToken(adapterName);
        string disconnectedMsg = $"NAT-PMP adapter '{adapterName}' not found - VPN may be disconnected";
        await RegisterFailureAndTryRecoveryAsync(
            disconnectedMsg, LogLevel.Info,
            providerToken is not null ? HelperProtocol.ActionRestart : HelperProtocol.ActionCycleAdapter,
            providerToken ?? adapterName,
            $"NAT-PMP adapter '{adapterName}'",
            cfg, cancellationToken).ConfigureAwait(false);

        status[StatusKeys.Status] = SyncStatusValues.Skipped;
        status[StatusKeys.Message] = disconnectedMsg;
        return null;
    }

    // Creates the active IManagedClient via its ClientRegistry factory from the config block
    // ReadConfig built for the active client. Resolve defaults to qBittorrent when the value is
    // unrecognized (matching ReadConfig).
    private static IManagedClient CreateManagedClient(AppConfig cfg) =>
        ClientRegistry.Resolve(cfg.ClientName).Factory(cfg.Client);

    /// <summary>
    /// Tests on demand whether the currently configured client's listening port is reachable from
    /// outside, independently of the sync loop. Builds a fresh client from the saved settings, so it
    /// is safe to call while a cycle is running. Returns <see langword="true"/> when open,
    /// <see langword="false"/> when closed, or <see langword="null"/> when it cannot be determined
    /// (client unreachable, no internet, or the port-test service is unavailable). Never throws
    /// except for <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public static async Task<bool?> TestActivePortAsync(CancellationToken cancellationToken = default)
    {
        using var client = BuildActiveClient();
        try
        {
            return await client.TestListeningPortAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Manual port test failed: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    /// <summary>
    /// Builds the currently-configured BitTorrent client from the saved settings. Read-only and
    /// side-effect free, so it is safe to call while a sync cycle is running (fresh instance, no
    /// shared state). The caller owns disposal. Shared by <see cref="TestActivePortAsync"/> and
    /// <see cref="DiagnosticsService"/> so client construction stays single-source.
    /// </summary>
    internal static IManagedClient BuildActiveClient()
    {
        var (cfg, _) = ReadConfig();
        return CreateManagedClient(cfg);
    }

    /// <summary>
    /// Builds the currently-configured VPN manager fresh for read-only callers (e.g.
    /// <see cref="DiagnosticsService"/>), without the sync loop's status side effects or NAT-PMP
    /// fallback state. Returns <see langword="null"/> when the provider is Disabled/unrecognized, the
    /// NAT-PMP adapter is unset, or the adapter cannot currently be reached. Mirrors the provider
    /// dispatch in <see cref="CreateVpnManagerAsync"/>: stateless providers come from the shared
    /// <see cref="CreateStatelessVpnManager"/>; only the NAT-PMP arm is per-path (plain probe here, sticky
    /// fallback there) - keep that arm in step when adding a non-stateless provider.
    /// </summary>
    internal static Task<IVpnManager?> BuildActiveVpnManagerAsync(CancellationToken cancellationToken = default)
    {
        string provider = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider);
        string adapter = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);
        return BuildVpnManagerForAsync(provider, adapter, cancellationToken);
    }

    // Core of BuildActiveVpnManagerAsync with the provider selection passed in, so the Settings
    // form's recovery test can build from its in-form (possibly unsaved) selection - the same
    // convention the client Test buttons follow.
    private static async Task<IVpnManager?> BuildVpnManagerForAsync(string provider, string? natPmpAdapterName, CancellationToken cancellationToken)
    {
        if (CreateStatelessVpnManager(provider) is { } manager)
            return manager;

        if (provider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(natPmpAdapterName)) return null;
            return await NatPmpManager.TryCreateForAdapterAsync(natPmpAdapterName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return null; // Disabled or unrecognized
    }

    /// <summary>
    /// Dispatches the recovery action for the given provider selection on demand (the Settings
    /// form's Auto-recovery Test button). Runs the exact same action as automatic recovery (VPN
    /// service restart, or adapter cycle for NAT-PMP) so the recovery chain - helper service,
    /// service discovery, VPN client relaunch - can be verified before a real failure needs it.
    /// Returns <see langword="false"/> when nothing could be dispatched (provider disabled or
    /// unrecognized, adapter unreachable, or no recovery target); the outcome of a dispatched
    /// action is reported through the log exactly like an automatic recovery.
    /// </summary>
    public static async Task<bool> TestRecoveryAsync(string vpnProvider, string? natPmpAdapterName, CancellationToken cancellationToken = default)
    {
        IVpnManager? vpnManager = await BuildVpnManagerForAsync(vpnProvider, natPmpAdapterName, cancellationToken).ConfigureAwait(false);
        if (vpnManager is null)
        {
            LogManager.Instance.LogMessage($"Recovery test: no VPN manager available for provider '{vpnProvider}' - nothing to test", LogLevel.Warn);
            return false;
        }

        string? target = vpnManager.GetRecoveryTarget();
        if (target is null)
        {
            LogManager.Instance.LogMessage($"Recovery test: no recovery target found for '{vpnManager.ProviderName}'", LogLevel.Warn);
            return false;
        }

        // Bookend the test in the log so its start and end stand out from automatic recovery
        // entries - the dispatched action's own entries land between the two lines.
        LogManager.Instance.LogMessage($"Recovery test started manually for '{vpnManager.ProviderName}'", LogLevel.Info);
        await DispatchRecoveryAsync(vpnManager.GetRecoveryAction(), target, vpnManager.ProviderName, cancellationToken, manualTest: true).ConfigureAwait(false);
        LogManager.Instance.LogMessage($"Recovery test completed for '{vpnManager.ProviderName}' - see the entries above for the outcome", LogLevel.Info);
        return true;
    }

    // Ensures the BitTorrent client is running, then updates its port if it differs from the target port
    private async Task EnsureRunningAndUpdatePortAsync(IManagedClient manager, int targetPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        if (!await EnsureClientRunningAsync(manager, config, status, cancellationToken).ConfigureAwait(false))
            return;
        status[StatusKeys.ClientRunning] = true;

        // Get current preferences (listening port and network interface) in a single request
        var (currentPort, currentInterfaceName) = await manager.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (!currentPort.HasValue)
        {
            SetSyncResult(status, false, $"Failed to determine {manager.ClientName} port");
            return;
        }
        status[StatusKeys.ClientPreviousPort] = currentPort.Value;
        LogManager.Instance.LogMessage($"{manager.ClientName} port found: {currentPort.Value}", LogLevel.Info);

        // Warn if the client's network interface doesn't match the configured VPN provider
        if (config.VpnManager is not null && config.WarnOnInterfaceMismatch && manager.SupportsInterfaceMismatchWarning)
            CheckInterfaceMatch(manager.ClientName, currentInterfaceName, config.VpnManager);

        if (currentPort.Value == targetPort)
        {
            status[StatusKeys.ClientPort] = currentPort.Value;
            LogManager.Instance.LogMessage($"{manager.ClientName} ports match - no update needed", LogLevel.Info);
        }
        else if (!await UpdatePortAndNotifyAsync(manager, targetPort, currentPort.Value, config, status, cancellationToken).ConfigureAwait(false))
        {
            return; // port update failed - skip RestartOnDisconnect check; next cycle will retry
        }

        // Check connection status and restart if offline - skip if a restart was already performed
        // by ApplyPortUpdateAsync (port changed + restart enabled) to avoid a redundant cycle.
        bool restartAttemptedThisCycle = config.Restart && status[StatusKeys.PortChanged] is true;
        if (config.RestartOnDisconnect && !restartAttemptedThisCycle)
            await CheckAndRestartIfDisconnectedAsync(manager, cancellationToken).ConfigureAwait(false);

        // Verify outside reachability of the synced port. Skipped when the VPN is disconnected
        // (VpnManager is null): the default-port fallback has no working tunnel for incoming
        // connections, so a closed result would be expected noise.
        if (config.VerifyPort && config.VpnManager is not null)
            await VerifyPortAsync(manager, targetPort, config, status, cancellationToken).ConfigureAwait(false);

        SetSyncResult(status, true, "Sync cycle completed");
    }

    // Applies the port update, records it in the port history (with the cause: VPN-assigned or
    // default-port fallback), and raises the optional tray notification. Returns false when the
    // update failed - the caller skips the cycle's remaining steps and the next cycle retries.
    private async Task<bool> UpdatePortAndNotifyAsync(IManagedClient manager, int targetPort, int previousPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        if (!await ApplyPortUpdateAsync(manager, targetPort, config, status, cancellationToken).ConfigureAwait(false))
            return false;
        // Cause annotation: recovery takes precedence over network change (a recovery usually
        // produces a network change too, and the recovery is the root cause).
        string cause = string.Empty;
        if (_recoveryPendingThisCycle) cause = " - after recovery";
        else if (_networkChangeCycle) cause = " - after network change";
        PortHistoryManager.Append(PortHistoryKind.PortChanged, targetPort,
            (config.VpnManager is null
                ? $"Default port applied (was {previousPort})"
                : $"VPN assigned new port (was {previousPort})") + cause);
        if (config.NotifyOnPortUpdate)
            NotifyPortUpdated(manager.ClientName, targetPort);
        return true;
    }

    // Throttles the reachability test: Transmission's and Deluge's tests contact their projects'
    // online check services, so testing every cycle would be wasteful. Tests run when the port
    // changed this cycle, every cycle while a result awaits confirmation, every cycle while
    // confirmed-closed AND port-closed recovery is enabled and still armed (so the recovery counter
    // advances each cycle up to the trigger), and otherwise every VerifyEveryNCycles cycles. A
    // confirmed-closed port falls through to the throttle when recovery is off OR has already fired
    // (disarmed) - throttled tests still detect a reopen (which re-arms) without hammering the
    // online check services every cycle for a port that may stay closed indefinitely.
    private bool ShouldVerifyThisCycle(bool portChanged, bool portClosedRecoveryEnabled)
    {
        if (portChanged || _portCheckPendingConfirmation || (_portConfirmedClosed && portClosedRecoveryEnabled && _portClosedRecoveryArmed))
        {
            _cyclesSinceVerify = 0;
            return true;
        }
        _cyclesSinceVerify++;
        if (_cyclesSinceVerify < VerifyEveryNCycles) return false;
        _cyclesSinceVerify = 0;
        return true;
    }

    // Verifies the forwarded port is reachable from outside after a successful sync. A single
    // closed result is logged at Info and re-tested next cycle (absorbs qBittorrent's
    // idle-firewalled false positive and transient check-service glitches); the second
    // consecutive closed result is confirmed - see HandlePortClosedResult. Null results
    // (client unreachable, test service unavailable) leave the verification state unchanged.
    private async Task VerifyPortAsync(IManagedClient manager, int port, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        if (!ShouldVerifyThisCycle(status[StatusKeys.PortChanged] is true, config.PortClosedRecoveryEnabled)) return;

        bool? open = await manager.TestListeningPortAsync(cancellationToken).ConfigureAwait(false);
        if (open is null)
        {
            LogManager.Instance.LogDebug($"PortSyncService.VerifyPortAsync: {manager.ClientName} port reachability could not be determined");
            return;
        }
        status[StatusKeys.PortVerified] = open.Value;

        if (open.Value)
        {
            HandlePortOpenResult(manager.ClientName, port);
        }
        else
        {
            HandlePortClosedResult(manager.ClientName, port, config);
            await MaybeTriggerPortClosedRecoveryAsync(config, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandlePortOpenResult(string clientName, int port)
    {
        if (_portConfirmedClosed)
            LogManager.Instance.LogMessage($"{clientName} port {port} is reachable from outside again", LogLevel.Info);
        else
            LogManager.Instance.LogDebug($"PortSyncService.VerifyPortAsync: {clientName} port {port} verified open");
        _portCheckPendingConfirmation = false;
        _portConfirmedClosed = false;
        _confirmedClosedCount = 0;
        if (!_portClosedRecoveryArmed)
        {
            _portClosedRecoveryArmed = true;
            LogManager.Instance.LogDebug("PortSyncService.HandlePortOpenResult: Port-closed recovery re-armed");
        }
    }

    // Confirmed-closed logs at Warn every cycle so the alert badge tracks the persistent
    // condition (same pattern as the interface mismatch check); the PortVerificationFailed
    // balloon fires only on the transition into the confirmed state.
    private void HandlePortClosedResult(string clientName, int port, SyncConfig config)
    {
        if (_portConfirmedClosed)
        {
            _confirmedClosedCount++;
            string closedSuffix = BuildPortClosedRecoverySuffix(config);
            LogManager.Instance.LogMessage($"{clientName} port {port} is still not reachable from outside{closedSuffix}", LogLevel.Warn);
            return;
        }
        if (!_portCheckPendingConfirmation)
        {
            _portCheckPendingConfirmation = true;
            LogManager.Instance.LogMessage($"{clientName} port {port} test reports closed - confirming on the next check", LogLevel.Info);
            return;
        }

        _portCheckPendingConfirmation = false;
        _portConfirmedClosed = true;
        _confirmedClosedCount = 1;
        // History records the transition into confirmed-closed only (like the balloon), not the
        // per-cycle re-confirmations - the persistent condition is one event, not many.
        PortHistoryManager.Append(PortHistoryKind.PortClosed, port, "Port confirmed closed from outside");
        string confirmedSuffix = BuildPortClosedRecoverySuffix(config);
        LogManager.Instance.LogMessage($"{clientName} port {port} is not reachable from outside (confirmed by two checks){confirmedSuffix}", LogLevel.Warn);
        try { PortVerificationFailed?.Invoke($"{clientName} port {port} is not reachable from outside."); }
        catch (Exception ex) { LogManager.Instance.LogMessage($"PortVerificationFailed handler failed: {ex.Message}", LogLevel.Warn); }
    }

    // Builds the recovery-progress suffix for the port-closed Warn messages, mirroring
    // BuildCycleCountMessage's structure (counted in checks, not cycles) so it reads consistently
    // with the failed-cycle recovery logs.
    // Shown only while recovery is enabled AND still armed - it tracks progress toward the
    // threshold. With recovery off the count is zeroed each cycle; once recovery has fired
    // (disarmed) the count no longer drives a trigger, so a climbing count would mislead.
    private string BuildPortClosedRecoverySuffix(SyncConfig config)
    {
        if (!config.PortClosedRecoveryEnabled || !_portClosedRecoveryArmed)
            return string.Empty;
        string checks = _confirmedClosedCount == 1 ? "closed check" : "closed checks";
        return $" ({_confirmedClosedCount} consecutive {checks}, recovery triggers after {config.PortClosedRecoveryTriggerChecks} consecutive closed checks)";
    }

    // Opt-in: when the port has been confirmed closed for the configured number of checks,
    // dispatches the provider's recovery action once. Independent of the failed-sync recovery
    // trigger - the two share the action, not the gate. One-shot arming: after firing, recovery
    // stays disarmed until a verification reports the port open again (see HandlePortOpenResult),
    // so a persistently false "closed" can never cause a recovery loop.
    private async Task MaybeTriggerPortClosedRecoveryAsync(SyncConfig config, CancellationToken cancellationToken)
    {
        if (!config.PortClosedRecoveryEnabled)
        {
            _confirmedClosedCount = 0;
            return;
        }
        if (!_portClosedRecoveryArmed || _confirmedClosedCount < config.PortClosedRecoveryTriggerChecks) return;

        _portClosedRecoveryArmed = false;
        _confirmedClosedCount = 0;

        IVpnManager vpnManager = config.VpnManager!; // non-null: verification only runs while the VPN is connected
        string? target = vpnManager.GetRecoveryTarget();
        if (target is null)
        {
            LogManager.Instance.LogMessage($"No recovery target found for '{vpnManager.ProviderName}' - skipping port-closed recovery", LogLevel.Warn);
            return;
        }

        string action = vpnManager.GetRecoveryAction();
        LogManager.Instance.LogMessage(
            $"Triggering '{action}' for '{vpnManager.ProviderName}' after {config.PortClosedRecoveryTriggerChecks} consecutive closed {(config.PortClosedRecoveryTriggerChecks == 1 ? "check" : "checks")}",
            LogLevel.Info);
        await DispatchRecoveryAsync(action, target, vpnManager.ProviderName, cancellationToken).ConfigureAwait(false);
    }

    // Returns true if the BitTorrent client is running (or was successfully force-started), false otherwise
    private static async Task<bool> EnsureClientRunningAsync(IManagedClient manager, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        if (manager.IsRunning())
        {
            LogManager.Instance.LogMessage($"{manager.ClientName} is running", LogLevel.Info);
            return true;
        }

        if (!config.ForceStart)
        {
            SetSyncResult(status, false, $"{manager.ClientName} is not running", LogLevel.Warn);
            return false;
        }

        LogManager.Instance.LogMessage($"{manager.ClientName} is not running - attempting to force-start", LogLevel.Info);
        if (!await manager.ForceStartAsync(cancellationToken).ConfigureAwait(false))
        {
            SetSyncResult(status, false, $"Failed to force start {manager.ClientName}");
            return false;
        }
        LogManager.Instance.LogMessage($"Force-started {manager.ClientName}", LogLevel.Info);
        return true;
    }

    // Checks if the client's network interface matches the expected VPN provider and logs a warning if not.
    // The warn log fires every cycle so the log alert badge tracks the persistent condition.
    // The InterfaceMismatchDetected balloon fires only on transition (new or changed mismatch) to avoid
    // spamming the user each cycle; it re-fires if the mismatch clears and then returns.
    private void CheckInterfaceMatch(string clientName, string? interfaceName, IVpnManager vpnManager)
    {
        if (interfaceName is null)
        {
            LogManager.Instance.LogDebug($"PortSyncService.CheckInterfaceMatch: {clientName} did not return an interface name, skipping check");
            return;
        }

        string? balloonMessage = null;

        if (interfaceName.Length == 0)
        {
            LogManager.Instance.LogMessage($"{clientName} is bound to all network interfaces - traffic may leak outside the VPN", LogLevel.Warn);
            balloonMessage = $"{clientName}: no VPN interface bound - traffic may leak.";
        }
        else if (!vpnManager.IsAdapterMatch(interfaceName))
        {
            LogManager.Instance.LogMessage($"{clientName} network interface '{interfaceName}' does not match '{vpnManager.ProviderName}'", LogLevel.Warn);
            balloonMessage = $"{clientName} interface mismatch - '{interfaceName}' is not a {vpnManager.ProviderName} adapter.";
        }
        else
        {
            LogManager.Instance.LogDebug($"PortSyncService.CheckInterfaceMatch: {clientName} interface '{interfaceName}' matches '{vpnManager.ProviderName}'");
        }

        if (balloonMessage is null)
        {
            _lastInterfaceMismatchMessage = null;
            return;
        }

        if (balloonMessage == _lastInterfaceMismatchMessage) return;
        _lastInterfaceMismatchMessage = balloonMessage;
        try { InterfaceMismatchDetected?.Invoke(balloonMessage); }
        catch (Exception ex) { LogManager.Instance.LogMessage($"InterfaceMismatchDetected handler failed: {ex.Message}", LogLevel.Warn); }
    }

    private void NotifyPortUpdated(string clientName, int port)
    {
        try { PortUpdated?.Invoke($"{clientName} port updated to {port}"); }
        catch (Exception ex) { LogManager.Instance.LogMessage($"PortUpdated handler failed: {ex.Message}", LogLevel.Warn); }
    }

    // Sets the listening port and optionally restarts the client. Returns false if any step fails.
    // The post-update command is launched later (in RunAsync's finally, after the status file write).
    private static async Task<bool> ApplyPortUpdateAsync(IManagedClient manager, int targetPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        LogManager.Instance.LogMessage($"Ports do not match - updating {manager.ClientName} port to {targetPort}", LogLevel.Info);
        if (!await manager.SetListeningPortAsync(targetPort, cancellationToken).ConfigureAwait(false))
        {
            SetSyncResult(status, false, $"Failed to set {manager.ClientName} port to {targetPort}");
            return false;
        }
        LogManager.Instance.LogMessage($"{manager.ClientName} port set to {targetPort}", LogLevel.Info);

        status[StatusKeys.ClientPort] = targetPort;
        status[StatusKeys.PortChanged] = true;

        if (config.Restart)
        {
            LogManager.Instance.LogMessage($"Attempting to restart {manager.ClientName}", LogLevel.Info);
            if (!await manager.RestartAsync(cancellationToken).ConfigureAwait(false))
            {
                SetSyncResult(status, false, $"Failed to restart {manager.ClientName}");
                return false;
            }
            LogManager.Instance.LogMessage($"Restarted {manager.ClientName}", LogLevel.Info);
        }

        // The post-update command is launched by the outer finally in RunAsync, after the status file
        // is written, so a script that reads that file sees this cycle's result (not the prior cycle's).
        return true;
    }

    // Launches the post-update shell command (fire-and-forget).
    // The command string is passed through directly without sanitisation - this is intentional.
    // It is a user-configured value (stored in the registry under HKCU) so the user already
    // controls execution in their own context; no external or untrusted input reaches this path.
    private static void RunPostUpdateCommand(string cmd)
    {
        LogManager.Instance.LogDebug($"PortSyncService.RunPostUpdateCommand: {cmd}");
        try
        {
            string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            Process.Start(ProcessHelpers.CreateHiddenStartInfo(cmdExe, $"/C \"{cmd}\""))?.Dispose(); // NOSONAR S4721 - cmd is a user-configured registry value; execution of arbitrary commands is the intended behaviour
            LogManager.Instance.LogMessage("Post-update command launched", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to run post-update command: {ex.Message}", LogLevel.Warn);
        }
    }

    // Checks connection status and restarts the client if it reports as disconnected.
    // Clients that do not support connection status (GetConnectionStatusAsync returns null) are skipped.
    private static async Task CheckAndRestartIfDisconnectedAsync(IManagedClient manager, CancellationToken cancellationToken)
    {
        string? connectionStatus = await manager.GetConnectionStatusAsync(cancellationToken).ConfigureAwait(false);
        if (connectionStatus is null)
            return;

        LogManager.Instance.LogDebug($"PortSyncService.CheckAndRestartIfDisconnectedAsync: {manager.ClientName} connection status: {connectionStatus}");

        if (!connectionStatus.Equals(ClientDisconnectedStatus, StringComparison.OrdinalIgnoreCase))
            return;

        LogManager.Instance.LogMessage($"{manager.ClientName} connection status is disconnected - restarting", LogLevel.Warn);
        if (!await manager.RestartAsync(cancellationToken).ConfigureAwait(false))
            LogManager.Instance.LogMessage($"Failed to restart {manager.ClientName} after connection disconnect", LogLevel.Error);
        else
            LogManager.Instance.LogMessage($"Restarted {manager.ClientName} after connection disconnect", LogLevel.Info);
    }

    // Builds a failure log message with cycle count and optional recovery trigger suffix
    private static string BuildCycleCountMessage(string prefix, int count, AppConfig cfg)
    {
        string cycles = count == 1 ? "failed cycle" : "failed cycles";
        string recoverySuffix = cfg.VpnAutoRecoveryEnabled
            ? $", recovery may trigger after {cfg.VpnAutoRecoveryTriggerCycles} consecutive failed cycles"
            : string.Empty;
        return $"{prefix} ({count} consecutive {cycles}{recoverySuffix})";
    }

    // Port detection failed despite the VPN being connected. Logs at Warn (the other two
    // failure paths use Info because they correspond to expected disconnection states).
    private Task HandlePortDetectionFailureAsync(IVpnManager vpnManager, AppConfig cfg, CancellationToken cancellationToken) => // NOSONAR S2325 - calls the instance method RegisterFailureAndTryRecoveryAsync, so it cannot be static
        RegisterFailureAndTryRecoveryAsync(
            $"Port detection failed on '{vpnManager.ProviderName}'", LogLevel.Warn,
            vpnManager.GetRecoveryAction(), vpnManager.GetRecoveryTarget(), vpnManager.ProviderName,
            cfg, cancellationToken);

    // Single increment site for _consecutiveFailedCycles. Every failure path that contributes
    // to the auto-recovery threshold flows through here: VPN disconnected, port detection
    // failed, and NAT-PMP adapter not found. Stamps the streak start time on the first failure
    // (for the recovery time gate), logs the cycle count message, then dispatches recovery
    // (which may no-op if the cycle-count or time threshold has not been reached).
    private async Task RegisterFailureAndTryRecoveryAsync(
        string reason, LogLevel logLevel,
        string recoveryAction, string? recoveryTarget, string displayName,
        AppConfig cfg, CancellationToken cancellationToken)
    {
        if (_consecutiveFailedCycles == 0) _failureStreakStartedUtc = DateTime.UtcNow;
        _consecutiveFailedCycles++;
        int count = _consecutiveFailedCycles;
        LogManager.Instance.LogMessage(BuildCycleCountMessage(reason, count, cfg), logLevel);
        await TryTriggerRecoveryAsync(recoveryAction, recoveryTarget, displayName, cfg, cancellationToken).ConfigureAwait(false);
    }

    // Resets the failure streak counter. The start timestamp is deliberately left alone: it is
    // re-stamped on the next streak's first failure (see RegisterFailureAndTryRecoveryAsync),
    // and the time gate in TryTriggerRecoveryAsync only reads it while the counter is non-zero,
    // so a stale value can never be observed.
    private void ResetFailureStreak() => _consecutiveFailedCycles = 0;

    // Triggers auto-recovery if enabled and both gates are cleared: the failure cycle threshold
    // AND enough wall-clock time has elapsed since the streak began. The time gate (derived from
    // the normal cycle cadence) prevents a burst of early wakes from fast-tracking recovery during
    // a transient outage. Resets the counter before the target check so the warning does not fire
    // every cycle when no recovery target is found.
    private async Task TryTriggerRecoveryAsync(string action, string? recoveryTarget, string displayName, AppConfig cfg, CancellationToken cancellationToken)
    {
        if (!cfg.VpnAutoRecoveryEnabled)
        {
            ResetFailureStreak();
            return;
        }
        if (_consecutiveFailedCycles < cfg.VpnAutoRecoveryTriggerCycles) return;

        // Sustained-failure floor: the elapsed time recovery would have taken under normal
        // scheduled cycling ((TriggerCycles - 1) intervals between the first and last failure).
        // A streak driven faster than this by early wakes is held until the time also clears.
        TimeSpan minSustainedFailure = TimeSpan.FromSeconds((cfg.VpnAutoRecoveryTriggerCycles - 1) * cfg.UpdateInterval);
        TimeSpan elapsed = DateTime.UtcNow - _failureStreakStartedUtc;
        if (elapsed < minSustainedFailure)
        {
            LogManager.Instance.LogMessage(
                $"Holding recovery - failures started only {elapsed.TotalSeconds:F0}s ago " +
                $"(recovery waits until {minSustainedFailure.TotalSeconds:F0}s to ignore brief network blips)",
                LogLevel.Info);
            return;
        }

        int count = _consecutiveFailedCycles;

        if (recoveryTarget is null)
        {
            ResetFailureStreak();
            LogManager.Instance.LogMessage($"No recovery target found for '{displayName}' - skipping recovery", LogLevel.Warn);
            return;
        }

        ResetFailureStreak();

        LogManager.Instance.LogMessage(
            $"Triggering '{action}' for '{displayName}' after {count} consecutive failed {(count == 1 ? "cycle" : "cycles")}",
            LogLevel.Info);
        await DispatchRecoveryAsync(action, recoveryTarget, displayName, cancellationToken).ConfigureAwait(false);
    }

    // Dispatches a recovery action to the helper service. Shared by the failed-cycle trigger
    // (TryTriggerRecoveryAsync), the port-closed trigger (MaybeTriggerPortClosedRecoveryAsync),
    // and the on-demand recovery test (TestRecoveryAsync, manualTest = true). A manual test is
    // not counted in the session's auto-recovery statistic (the label says "Auto-recoveries")
    // but is recorded in the history and arms the "after recovery" annotation like a real one.
    private static async Task DispatchRecoveryAsync(string action, string recoveryTarget, string displayName, CancellationToken cancellationToken, bool manualTest = false)
    {
        string trigger = manualTest ? "Recovery test" : "Auto-recovery";
        if (action == HelperProtocol.ActionRestart)
        {
            // "triggered", not "restarted": the entry is recorded at dispatch, before the
            // helper reports the outcome - the log file carries the actual result.
            PortHistoryManager.Append(PortHistoryKind.Recovery, null, $"{trigger} triggered for '{displayName}' (service restart)");
            if (!manualTest) SessionStats.RecordRecovery();
            _recoveryDispatched = true;
            await AutoRecoveryManager.TriggerRestartAsync(recoveryTarget, cancellationToken).ConfigureAwait(false);
        }
        else if (action == HelperProtocol.ActionCycleAdapter)
        {
            PortHistoryManager.Append(PortHistoryKind.Recovery, null, $"{trigger} triggered for '{displayName}' (adapter cycle)");
            if (!manualTest) SessionStats.RecordRecovery();
            _recoveryDispatched = true;
            await AutoRecoveryManager.TriggerCycleAdapterAsync(recoveryTarget, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LogManager.Instance.LogMessage($"Unknown recovery action '{action}' for '{displayName}' - skipping", LogLevel.Warn);
        }
    }

    private static (bool ForceStart, bool Restart, bool RestartOnDisconnect, bool WarnOnInterfaceMismatch) GetClientBehaviorConfig(AppConfig cfg, string activeSection)
    {
        // RestartOnDisconnect is qBittorrent-only: it is the only client where restarting is both
        // possible and the right response to a dropped connection. Nicotine+ reconnects itself,
        // and restarting it would discard its configuration.
        // WarnOnInterfaceMismatch needs a named adapter, which qBittorrent and Nicotine+ both
        // report; Transmission and Deluge expose only a bind address, which the VPN rotates.
        bool isQBittorrent = activeSection == RegistrySettingsManager.SectionQBittorrent;
        bool isNicotine = activeSection == RegistrySettingsManager.SectionNicotine;
        return (
            cfg.Client.ForceStart,
            cfg.Client.Restart,
            isQBittorrent && cfg.QBittorrentRestartOnDisconnect,
            (isQBittorrent && cfg.QBittorrentWarnOnInterfaceMismatch) ||
            (isNicotine && cfg.NicotineWarnOnInterfaceMismatch));
    }

    private static int GetDefaultPort(AppConfig cfg) => cfg.Client.DefaultPort;

    // Sets the cycle status and message in the status dict, logs the message, and adds a closing bookend on failure.
    // Pass an explicit level to override the default (Info on success, Error on failure).
    // The bookend uses the same effective level so a Warn-level soft failure does not escalate to Error.
    private static void SetSyncResult(Dictionary<string, object?> status, bool success, string message, LogLevel? level = null)
    {
        status[StatusKeys.Status] = success ? SyncStatusValues.Success : SyncStatusValues.Error;
        status[StatusKeys.Message] = message;
        // Log the specific reason on failure (at its own severity); the uniform terminal line is
        // emitted once per cycle by LogCycleOutcome. A successful cycle needs no reason line - its
        // terminal marker says it all.
        if (!success)
            LogManager.Instance.LogMessage(message, level ?? LogLevel.Error);
    }

    // Emits exactly one terminal line per cycle so every cycle closes with a clear outcome - completed,
    // skipped, or failed - regardless of which branch it exited through. Called once from the finally
    // in RunAsync after the status is finalized.
    private static void LogCycleOutcome(string? status) => LogManager.Instance.LogMessage(
        status switch
        {
            SyncStatusValues.Success => "Sync cycle completed",
            SyncStatusValues.Skipped => "Sync cycle skipped",
            _ => "Sync cycle failed",
        },
        status == SyncStatusValues.Error ? LogLevel.Error : LogLevel.Info);
}

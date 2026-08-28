using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>Outcome of a port sync cycle, used to drive the tray icon color and tooltip.</summary>
public enum SyncState
{
    /// <summary>Port was successfully detected and applied to the client.</summary>
    Synced,
    /// <summary>VPN is not connected; no port is available to sync.</summary>
    VpnDisconnected,
    /// <summary>The VPN provider is set to Disabled, so port sync is skipped entirely. An
    /// unrecognized provider is <see cref="Error"/>, not this.</summary>
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

/// <summary>Background service that syncs the client's listening port with the VPN-assigned port on each cycle.</summary>
public sealed class PortSyncService
{
    // Connection status value returned by clients that support GetConnectionStatusAsync
    private const string ClientDisconnectedStatus = "disconnected";

    // LogManager.LogStateChange keys for the three conditions in this cycle that persist until the
    // user acts, so each is logged on the transition rather than on every cycle. The two restart and
    // settings-conflict warnings nearby already do this with their own fields; these use the shared
    // mechanism because they have no other reason to carry state. Every Warn also bumps the tray's
    // unviewed-warning count, so repeating one costs more than a duplicated log line.
    private const string NatPmpLeaseStateKey = "vpn.natpmpLeaseTooShort";
    private const string InterfaceMatchStateKey = "client.interfaceMatch";
    private const string BindingStaleStateKey = "client.bindingStale";
    private const string VpnProviderStateKey = "vpn.providerUnrecognized";
    private const string DefaultPortStateKey = "client.defaultPortUnusable";
    private const string NatPmpAdapterStateKey = "vpn.natpmpAdapterUnconfigured";
    private const string PortForwardingUnavailableStateKey = "vpn.portForwardingUnavailable";
    private const string BindingAddressStateKey = "client.bindingAddressStale";
    private const string RecoveryCapStateKey = "vpn.recoveryCapReached";

    /// <summary>Raised when a sync cycle completes (success or failure) with the resulting tray status.</summary>
    public event Action<TrayStatus>? SyncCompleted;

    /// <summary>Raised when the client's network interface does not match the configured VPN provider.</summary>
    public event Action<string>? InterfaceMismatchDetected;

    /// <summary>Raised when the client's listening port is successfully updated to a new value.</summary>
    public event Action<string>? PortUpdated;

    /// <summary>Raised once when the forwarded port is confirmed unreachable from outside (two consecutive failed checks). Transition-only - it re-fires only after the port has tested open again.</summary>
    public event Action<string>? PortVerificationFailed;

    /// <summary>Raised when the client's own settings are found working against the synchronized port. Transition-only - it re-fires only after the settings have tested clean again.</summary>
    public event Action<string>? ClientSettingsConflictDetected;

    // Consecutive sync cycles in which the VPN was disconnected or port detection failed.
    // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
    private int _consecutiveFailedCycles;
    // Uptime reading (from _uptime) at which the current failure streak began - re-stamped on every
    // 0 -> 1 transition of the counter, so it always describes the streak in progress. Auto-recovery
    // gates on this in addition to the cycle count so a burst of early wakes (network-change re-syncs
    // interrupting the inter-cycle delay) cannot fast-track a heavy recovery action during a
    // transient outage that would self-heal. Only meaningful while the counter is non-zero.
    // Monotonic rather than wall-clock: a VPN reconnect frequently triggers an NTP correction, so a
    // clock jump is *correlated* with the failure streak this gate measures. A backward jump would
    // hold recovery off indefinitely; a forward jump would fire it on the first failure, which is
    // precisely the transient-blip restart the floor exists to prevent.
    private TimeSpan _failureStreakStarted;
    // Consecutive restarts issued because the client reported itself disconnected, reset the moment it
    // reports anything else. Restarting only helps when the cause is the client's own state; when the
    // cause is persisted configuration - a stale network-interface binding, say - every restart re-reads
    // the same value and reports disconnected again, so an uncapped loop restarts once per cycle forever
    // while being structurally unable to fix anything. Each restart also interrupts transfers and can
    // trigger rechecks, so the loop costs the user more than doing nothing would.
    // Serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles).
    private int _consecutiveDisconnectRestarts;
    // Auto-recoveries dispatched on the failed-cycle path since the last successful port read. Capped at
    // MaxConsecutiveRecoveries for the same reason _consecutiveDisconnectRestarts is capped: a remedy that
    // has not worked three times running is not addressing the cause, and repeating it on a timer costs
    // the user a torn-down tunnel and interrupted transfers each time. The existing gates do not cover
    // this - the cycle count and sustained-failure floor both reset with the streak, and the offline
    // limiter only engages when the machine cannot reach the internet at all.
    // Serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles).
    private int _consecutiveRecoveries;
    // True once a stale interface binding has been re-applied for the current stale streak. Cleared as
    // soon as the binding reads healthy, so a later drift is repaired again - but a write that does not
    // stick is not retried every cycle, which would be the same unbounded-remedy loop as the restarts.
    // Serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles).
    private bool _interfaceBindingRepairAttempted;
    // Addresses the bound adapter carried on the previous cycle, or null before the first successful
    // read. Lives here rather than on the client because the client is constructed and disposed once
    // per cycle (see the `using var manager` in RunCoreAsync), so it cannot remember anything, and the
    // question this answers is precisely "did this change since last time".
    // Serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles).
    private IReadOnlyList<string>? _lastKnownInterfaceAddresses;
    // Set when the bound adapter's addresses move while the client is bound to *all* addresses on it -
    // the case where a listener can survive on the previous address with nothing else in the cycle able
    // to see it. Consumed by the port-closed escalation, which is the point at which the symptom has
    // actually been confirmed; cleared after one rebind attempt so a failed attempt escalates to the VPN
    // restart rather than repeating itself. Not a fault on its own: an ordinary reconnect sets it too.
    // Serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles).
    private bool _interfaceAddressChangedSinceRebind;
    // What the client's bind address was when the change above was recorded, and therefore what the
    // rebind must leave behind. Data rather than a constant: the arm is set in one cycle and spent in
    // a later one, so the value has to travel with it.
    // Serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles).
    private string _rebindReleaseAddress = string.Empty;
    // One repair per stale-pin streak, mirroring _interfaceBindingRepairAttempted: if the write lands and
    // the pin is still wrong next cycle, something else is writing it and repeating would be its own loop.
    private bool _interfaceAddressRepairAttempted;
    // One latch per condition, each owned by exactly one method. They must not be shared:
    // CheckInterfaceMatch clears its latch on every healthy cycle, which would defeat the dedupe for
    // any other condition writing to the same field - a persistent binding warning would then balloon
    // on every cycle instead of once.
    private string? _lastBindingWarningMessage;
    // Tracks the last interface-mismatch message shown as a balloon tip to suppress repeat invocations
    // for the same persistent mismatch. Cleared when the mismatch resolves so the balloon re-fires if it returns.
    // Thread-safety: only read/written inside CheckInterfaceMatch via EnsureRunningAndUpdatePortAsync,
    // serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles and _lastKnownNatPmpManager).
    private string? _lastInterfaceMismatchMessage;

    // Port verification throttle: full reachability tests run at most every N cycles because
    // Transmission's and Deluge's tests contact their projects' online check services.
    private const int VerifyEveryNCycles = 5;
    // Conflicting-client-setting throttle. One extra local call, so cost is not the reason to throttle:
    // a setting the user just toggled needs catching before their client next restarts, not within one
    // cycle, and checking every cycle would only add noise to the debug log.
    private const int ConflictCheckEveryNCycles = 5;
    // How many consecutive undetermined results are tolerated while a closed result awaits
    // confirmation before the pending state is dropped and the normal throttle resumes. Three keeps
    // a genuine confirmation reachable across a couple of transient glitches without polling an
    // unavailable check service every cycle for as long as it stays down.
    private const int MaxPendingUndetermined = 3;

    // Startup grace window: right after the app starts the VPN is often still connecting (boot/login).
    // For this long, a not-yet-connected VPN is held quietly - no failure, recovery, default-port
    // fallback, or orange state - and re-checked every StartupGracePollSeconds so the first sync lands
    // promptly once it is up, instead of waiting a full (possibly multi-minute) update interval.
    private const int StartupGracePeriodSeconds = 90;
    private const int StartupGracePollSeconds = 15;
    // How many times a disconnected client is restarted before the attempts are suspended. Matches the
    // auto-recovery trigger default: enough for a genuinely transient client fault to clear, few enough
    // that an unfixable cause stops costing the user interrupted transfers.
    private const int MaxDisconnectRestarts = 3;
    // How many auto-recoveries may run back to back on the failed-cycle path without a single successful
    // port read in between. A backstop, not a tuning knob: if three service restarts have not produced a
    // port, the cause is not something a fourth will fix, and each one costs the user a torn-down tunnel.
    // Cleared by any successful port fetch, so a genuinely intermittent VPN is never permanently
    // suspended - only an unbroken run of futile recoveries is. Applies to the failed-cycle trigger
    // alone: the port-closed trigger runs after a successful fetch (which has already cleared the
    // count), and a recovery the user asked for by hand is never withheld.
    private const int MaxConsecutiveRecoveries = 3;
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

    // Conflicting-client-setting check state. Serialised by MainForm._updateSemaphore like the rest.
    // Initialised at the threshold so the first eligible cycle checks immediately, as _cyclesSinceVerify
    // does - a conflict that was already present when the app started should not wait five cycles.
    private int _cyclesSinceConflictCheck = ConflictCheckEveryNCycles;
    // Latches while a conflict is being reported, so the warning fires on the transition rather than on
    // every check. The condition persists until the user acts on it, which could be days.
    private bool _clientSettingsConflictActive;
    private bool _portCheckPendingConfirmation; // one unconfirmed closed result seen
    private bool _portConfirmedClosed;          // closed confirmed by two consecutive checks
    // Consecutive undetermined results while awaiting confirmation. An undetermined result cannot
    // resolve the pending state, but pending forces a check every cycle - so a checker outage would
    // otherwise be polled at full rate indefinitely, by every install at once. Counts only while
    // pending; any definite result clears it.
    private int _pendingUndeterminedCount;

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

    // How long recovery waits between attempts while the machine has no internet connection. The first
    // attempt of a streak is not delayed at all; these are the waits before the second, third, and
    // every later attempt. Capped at the last entry, so recovery keeps retrying at a steady 15 minutes
    // for as long as the condition lasts rather than stopping - see TryTakeRecoverySlotAsync for why
    // never stopping is the point.
    private static readonly TimeSpan[] OfflineRetryBackoff =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
    ];

    // Rate-limiter state for recovery attempted while offline: how many attempts this streak has made
    // (0 = none, so the next one runs immediately) and the Environment.TickCount64 reading of the last
    // one. Reset when the connectivity probe next succeeds, or when a cycle fetches a port. Instance
    // fields, serialised by MainForm._updateSemaphore like the rest of the cycle's state - unlike
    // _recoveryDispatched above, which is static and volatile because the manual test really does set
    // it from the UI thread. The gate these belong to runs only from TriggerRecoveryIfDueAsync.
    private int _offlineRecoveryAttempts;
    private long _lastOfflineRecoveryMs;
    // Latches while a wait window is being served, so the "holding recovery" explanation is logged once
    // per window instead of once per re-evaluated cycle.
    private bool _recoveryHoldLogged;

    // When the sustained-failure floor will clear, or null when it is holding nothing back. Published
    // for the Status panel: this is the second of the two independent holds, and the one that fires
    // during ordinary fast-cycling failure - the offline limiter needs a real outage as well. Set
    // where the gate is evaluated, because that is the only place _failureStreakStarted and the
    // configured cadence are both in scope. Absolute rather than remaining, for the same reason as
    // RecoveryHoldUntil: it is read a cycle later than it is computed.
    private DateTimeOffset? _recoverySustainedUntil;

    // Snapshot of _recoveryDispatched taken at cycle start, so a recovery dispatched mid-cycle
    // (port-closed trigger fires after the port update step) is never consumed by the same
    // cycle that dispatched it. Serialised by MainForm._updateSemaphore.
    private bool _recoveryPendingThisCycle;

    // Whether the running cycle was triggered by a network change (passed in by MainForm's
    // debounced NetworkChange handler). Drives the "after network change" history annotation.
    // Overwritten at the start of every cycle; serialised by MainForm._updateSemaphore.
    private bool _networkChangeTriggered;

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
        bool QBittorrentFixInterfaceBinding,
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
        bool FixInterfaceBinding,
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
        // When the next cycle is due. An absolute instant for the same reason RecoveryHoldUntil is
        // one: the wait starts when the cycle ends, while Timestamp is stamped at the start, so
        // deriving it as Timestamp + UpdateIntervalSeconds reads the duration against the wrong
        // origin and runs out early by the cycle's length - up to the 30s a client restart takes,
        // or the 120s an auto-recovery round trip can. Consumers that predate this key can still
        // derive the old estimate; nothing here replaces UpdateIntervalSeconds.
        public const string NextSyncAt = "nextSyncAt";
        public const string Status = "status";
        public const string Message = "message";
        public const string WaitingForVpn = "waitingForVpn";
        // When auto-recovery may next attempt, while it is being held back because connectivity could
        // not be confirmed. Null whenever nothing is being held. An absolute instant rather than a
        // duration: it is computed at the end of the cycle while Timestamp is stamped at the start, so
        // a duration would be read against the wrong origin and run out early by the cycle's length.
        public const string RecoveryHoldUntil = "recoveryHoldUntil";
        // Whether auto-recovery is switched on, how many consecutive failed cycles have accumulated,
        // and how many are needed to trigger. Together these let the Status panel show recovery
        // approaching, not just recovery already held back - the failure streak is the state a user
        // sees during ordinary VPN trouble, whereas a hold needs an outage as well.
        public const string RecoveryEnabled = "recoveryEnabled";
        public const string RecoveryFailedCycles = "recoveryFailedCycles";
        public const string RecoveryTriggerCycles = "recoveryTriggerCycles";
        // When the sustained-failure floor clears, while it is holding recovery back; null otherwise.
        // A separate key from RecoveryHoldUntil because the two holds have different causes and the
        // panel names the cause - collapsing them would make the row say "no internet connection"
        // during an ordinary blip.
        public const string RecoverySustainedUntil = "recoverySustainedUntil";
        // True while the consecutive-recovery cap is suspending the failed-cycle trigger. Published for
        // the same reason the two holds above are: this is a third gate that stops recovery firing, and
        // without it the panel would show the failure streak climbing past the trigger threshold with
        // nothing to say why nothing happens - the exact gap the sustained-failure key was added to close.
        // A flag rather than an instant, because this hold has no deadline: it ends on a successful port
        // read, which no countdown can predict.
        public const string RecoverySuspended = "recoverySuspended";
        // The port-closed recovery trigger, which is independent of the failed-cycle one above: its
        // own setting, its own threshold, counted in confirmed-closed checks rather than in cycles.
        // Published for the same reason the failed-cycle counters are - without them the Status panel
        // can only describe one of the two triggers, so a user whose port is closed sees a row that
        // says nothing about the recovery actually approaching (or, once Armed goes false, about the
        // recovery that has already run and will not run again until the port reopens).
        public const string PortClosedRecoveryEnabled = "portClosedRecoveryEnabled";
        public const string PortClosedRecoveryChecks = "portClosedRecoveryChecks";
        public const string PortClosedRecoveryTriggerChecks = "portClosedRecoveryTriggerChecks";
        // False once the one-shot trigger has fired, until a verification reports the port open again.
        public const string PortClosedRecoveryArmed = "portClosedRecoveryArmed";

        // Values for the Status key live in the public SyncStatusValues (shared with the Status panel).
    }

    /// <summary>Runs one port sync cycle and returns the configured update interval in seconds.
    /// <paramref name="networkChangeTriggered"/> marks a cycle started by the network-change
    /// re-sync; a port change it detects is annotated accordingly in the port history.</summary>
    public async Task<int> RunAsync(bool networkChangeTriggered = false, CancellationToken cancellationToken = default)
    {
        _networkChangeTriggered = networkChangeTriggered;
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
            [StatusKeys.NextSyncAt] = null,
            [StatusKeys.Status] = SyncStatusValues.Error,
            [StatusKeys.Message] = null,
            [StatusKeys.RecoveryHoldUntil] = null,
            [StatusKeys.RecoveryEnabled] = false,
            [StatusKeys.RecoveryFailedCycles] = 0,
            [StatusKeys.RecoveryTriggerCycles] = 0,
            [StatusKeys.RecoverySustainedUntil] = null,
            [StatusKeys.RecoverySuspended] = false,
            [StatusKeys.PortClosedRecoveryEnabled] = false,
            [StatusKeys.PortClosedRecoveryChecks] = 0,
            [StatusKeys.PortClosedRecoveryTriggerChecks] = 0,
            // Initialised to match the field rather than to false: armed is the resting state,
            // and the finally overwrites it with the real value on every cycle that gets that far.
            [StatusKeys.PortClosedRecoveryArmed] = true
        };

        // Captured so the finally can publish NextSyncAt against the wait this cycle actually
        // returned - including the shortened startup-grace poll - rather than re-reading the
        // configured interval. Stays at the default when RunCoreAsync throws, which is what the
        // catch returns anyway.
        int nextWaitSeconds = AppConstants.DefaultUpdateIntervalSeconds;
        try
        {
            nextWaitSeconds = await RunCoreAsync(status, cancellationToken).ConfigureAwait(false);
            return nextWaitSeconds;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            SetSyncResult(status, false, $"An unexpected error occurred: {ex.Message}");
            return nextWaitSeconds;
        }
        finally
        {
            // Skip status write and tray update on clean shutdown - the cycle was cancelled,
            // not failed. Writing an error/disconnected state here would flicker the tray icon
            // and leave a misleading error JSON file on every exit.
            if (!cancellationToken.IsCancellationRequested)
            {
                // Stamped here rather than at cycle start because the wait begins when the cycle
                // ends. MainForm shortens this to ManualSyncWaitSeconds after a manual sync or a
                // network-change re-check, which happens after this write, so those two cases still
                // publish the full interval.
                status[StatusKeys.NextSyncAt] = DateTimeOffset.Now.AddSeconds(nextWaitSeconds);
                status[StatusKeys.RecoveryHoldUntil] = GetRecoveryHoldUntil();
                // Read here rather than mid-cycle: the streak is incremented by the failure paths
                // inside RunCoreAsync, so only the finally sees this cycle's final count.
                status[StatusKeys.RecoveryFailedCycles] = _consecutiveFailedCycles;
                status[StatusKeys.RecoverySustainedUntil] = _recoverySustainedUntil;
                // Read here with the streak, for the same reason: the dispatch path inside RunCoreAsync
                // is what advances the count, so only the finally sees this cycle's value.
                status[StatusKeys.RecoverySuspended] = _consecutiveRecoveries >= MaxConsecutiveRecoveries;
                // Read here for the same reason as the failure streak above: both are mutated by the
                // verification path inside RunCoreAsync, so only the finally sees this cycle's values.
                status[StatusKeys.PortClosedRecoveryChecks] = _confirmedClosedCount;
                status[StatusKeys.PortClosedRecoveryArmed] = _portClosedRecoveryArmed;
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
        status[StatusKeys.RecoveryEnabled] = cfg.VpnAutoRecoveryEnabled;
        status[StatusKeys.RecoveryTriggerCycles] = cfg.VpnAutoRecoveryTriggerCycles;
        // The effective value, not the stored one. The port-closed trigger runs inside port
        // verification, so with verification off it is inert however its own checkbox was left - and
        // Settings only greys that checkbox out, it never clears it. Publishing the raw setting made
        // the panel report Idle for a trigger that could never fire.
        status[StatusKeys.PortClosedRecoveryEnabled] = cfg.PortClosedRecoveryEnabled && cfg.VerifyPortAfterSync;
        status[StatusKeys.PortClosedRecoveryTriggerChecks] = cfg.PortClosedRecoveryTriggerChecks;

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
                FixInterfaceBinding: cfg.QBittorrentFixInterfaceBinding,
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

        // The same usable-port rule HandleVpnConnectedAsync applies to a provider-reported port. The
        // fallback reaches the client through the same SetListeningPortAsync call, so a value the loop
        // would refuse from a provider must not get through just because it came from settings. Only a
        // hand-edited registry value can be out of range (the Settings spinner caps it, and re-saving
        // clamps it back), so this is a floor under that rather than the primary validation. Zeroing
        // falls through to the branch below, which already skips the update and reports it.
        if (defaultPort != 0 && !AppConstants.IsUsablePort(defaultPort))
        {
            // Transition-only: the value comes from the registry and stays unusable until the user
            // edits it, and this path runs on every cycle for as long as the VPN is down. The message
            // carries the port, so correcting it to another unusable value still reports.
            LogManager.Instance.LogStateChange(DefaultPortStateKey,
                $"Configured default port ({defaultPort}) is not usable - ignoring it", LogLevel.Warn);
            defaultPort = 0;
        }
        else
        {
            LogManager.Instance.ClearLogState(DefaultPortStateKey);
        }

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

        // A provider can report a value outside the usable range - ProtonVPN's log carries a port
        // pair while a mapping is being torn down, and NAT-PMP/PIA each guard this case themselves.
        // Guarding here covers every provider at the point the value becomes the client's config:
        // applying 0 makes most clients pick a random port, quietly undoing the forwarding this app
        // maintains while the cycle still reports success. Falls through to the no-port branch, so
        // the grace window, the failure streak and auto-recovery all behave as they already do.
        if (vpnPort is int reportedPort && !AppConstants.IsUsablePort(reportedPort))
        {
            LogManager.Instance.LogMessage(
                $"{vpnManager.ProviderName} reported an unusable port ({reportedPort}) - ignoring it", LogLevel.Warn);
            vpnPort = null;
        }

        if (!vpnPort.HasValue)
        {
            // Connected but no port yet (e.g. NAT-PMP mapping still establishing). Within the startup
            // grace window, wait quietly instead of warning.
            if (ShouldWaitForVpnStartup(cfg.WaitForVpnOnStartup))
            {
                MarkWaitingForVpn(status, $"Waiting for {vpnManager.ProviderName} to assign a port");
                return new TargetPortResult(GraceStartupInterval(cfg.UpdateInterval), 0, null);
            }
            // The provider has told us there is no forwarded port to be had until the user changes
            // something. That is a configuration state, not a fault, so it is reported and re-checked
            // on the normal cadence but never counted toward auto-recovery: a service restart cannot
            // create a forward the region or the account settings do not offer, and dispatching one
            // every few cycles tears the tunnel down repeatedly for no possible benefit.
            if (vpnManager.PortForwardingUnavailable)
            {
                ResetFailureStreak();
                SetSyncResult(status, false,
                    $"{vpnManager.ProviderName} reports port forwarding is unavailable - check that port forwarding " +
                    "is enabled in the VPN client and that the connected region supports it (auto-recovery does not run for this)",
                    LogLevel.Warn, PortForwardingUnavailableStateKey);
                return new TargetPortResult(cfg.UpdateInterval, 0, null);
            }

            await HandlePortDetectionFailureAsync(vpnManager, cfg, cancellationToken).ConfigureAwait(false);
            SetSyncResult(status, false, $"Failed to determine {vpnManager.ProviderName} port", LogLevel.Warn);
            return new TargetPortResult(cfg.UpdateInterval, 0, null);
        }

        ResetFailureStreak(); // Reset only after a successful port fetch
        ResetOfflineRecoveryBackoff(); // A working cycle ends any offline streak, so the next one starts fresh
        ResetRecoveryCap();            // and ends any run of consecutive recoveries
        LogManager.Instance.ClearLogState(PortForwardingUnavailableStateKey);
        _graceHoldActive = false; // VPN is up within the grace window - clear the hold without a marker
        status[StatusKeys.VpnPort] = vpnPort.Value;
        LogManager.Instance.LogMessage($"{vpnManager.ProviderName} port found: {vpnPort.Value}", LogLevel.Info);
        WarnIfNatPmpLeaseTooShort(vpnManager, cfg);
        return new TargetPortResult(null, vpnPort.Value, vpnManager);
    }

    // Warns if a NAT-PMP lease will expire before the next sync cycle renews it.
    private static void WarnIfNatPmpLeaseTooShort(IVpnManager vpnManager, AppConfig cfg)
    {
        if (vpnManager is not NatPmpManager natPmp || natPmp.LastGrantedLifetime == 0) return;

        // Transition-only. Both sides are stable - the interval is a setting and the lifetime is what
        // this gateway grants - so the line is identical on every cycle and says nothing new after the
        // first. The else re-arms it, so widening the interval and narrowing it again warns afresh.
        if (cfg.UpdateInterval > natPmp.LastGrantedLifetime)
            LogManager.Instance.LogStateChange(NatPmpLeaseStateKey,
                $"NAT-PMP sync interval ({cfg.UpdateInterval}s) exceeds lease lifetime ({natPmp.LastGrantedLifetime}s) - port mapping will expire before the next sync cycle",
                LogLevel.Warn);
        else
            LogManager.Instance.ClearLogState(NatPmpLeaseStateKey);
    }

    // Reads all configuration values from the registry into a single AppConfig record
    private static (AppConfig Config, string ActiveSection) ReadConfig()
    {
        int updateInterval = RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
        if (updateInterval < AppConstants.MinUpdateIntervalSeconds) updateInterval = AppConstants.DefaultUpdateIntervalSeconds;

        int vpnAutoRecoveryTriggerCycles = Math.Max(1, RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryTriggerCycles));

        string clientName = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyClient);
        var activeClient = ClientRegistry.Resolve(clientName);

        // Only the active client's section is read; every client section uses the same key names, so
        // the section is the only thing that varies here. HasUserName/HasRestart cover the two keys a
        // client may not have at all (Deluge and Nicotine+ have no user name; Nicotine+ is never
        // restarted). The password is DPAPI-decrypted via GetEncryptedValue, same as the per-client
        // GetXxxPassword helpers.
        var clientConfig = new ClientConfig(
            Url: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.UrlKey),
            UserName: activeClient.UserNameKey is not null ? RegistrySettingsManager.GetValue(activeClient.Section, activeClient.UserNameKey) : string.Empty,
            Password: RegistrySettingsManager.GetEncryptedValue(activeClient.Section, activeClient.PasswordKey),
            ProcessName: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.ProcessNameKey),
            ExePath: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.ExePathKey),
            Restart: activeClient.RestartKey is not null && RegistrySettingsManager.GetBool(activeClient.Section, activeClient.RestartKey),
            ForceStart: RegistrySettingsManager.GetBool(activeClient.Section, activeClient.ForceStartKey),
            DefaultPort: RegistrySettingsManager.GetInt(activeClient.Section, activeClient.DefaultPortKey));

        return (new AppConfig(
            VpnProvider: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider),
            NatPmpAdapterName: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName),
            UpdateInterval: updateInterval,
            ClientName: clientName,
            Client: clientConfig,
            QBittorrentWarnOnInterfaceMismatch: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentWarnOnInterfaceMismatch),
            QBittorrentRestartOnDisconnect: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentRestartOnDisconnect),
            QBittorrentFixInterfaceBinding: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentFixInterfaceBinding),
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
            (ci.RestartKey is not null ? $"{ci.RestartKey}={cfg.Client.Restart}, " : string.Empty) +
            $"{ci.ForceStartKey}={cfg.Client.ForceStart}, " +
            $"{ci.DefaultPortKey}={cfg.Client.DefaultPort}";
        // qBittorrent exposes two extra RPC-backed flags; append them only for that client.
        if (activeSection == RegistrySettingsManager.SectionQBittorrent)
            clientLine +=
                $", {RegistrySettingsManager.KeyQBittorrentWarnOnInterfaceMismatch}={cfg.QBittorrentWarnOnInterfaceMismatch}" +
                $", {RegistrySettingsManager.KeyQBittorrentRestartOnDisconnect}={cfg.QBittorrentRestartOnDisconnect}" +
                $", {RegistrySettingsManager.KeyQBittorrentFixInterfaceBinding}={cfg.QBittorrentFixInterfaceBinding}";
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
            return new ProtonVpnManager(AppFiles.GetProtonVpnLogFilePath());
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

        // Re-arms the warning below whenever the provider is one we know, so correcting Settings and
        // then mistyping it again reports afresh. Gated on the registry rather than on the two
        // branches beneath it so there is one definition of "recognized", and placed before them so
        // the unknown-provider fallthrough keeps its order: a keyword added to the registry without a
        // matching factory arm still reaches the warning rather than being routed to NAT-PMP.
        if (VpnProviderRegistry.IsRecognizedProvider(cfg.VpnProvider))
            LogManager.Instance.ClearLogState(VpnProviderStateKey);

        if (CreateStatelessVpnManager(cfg.VpnProvider) is { } manager)
            return manager;

        if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
            return await CreateNatPmpVpnManagerAsync(cfg, status, cancellationToken).ConfigureAwait(false);

        // Transition-only: the provider name comes from the registry and stays wrong until Settings is
        // corrected, so the same line every cycle adds nothing. The status below still reports Error on
        // every cycle, which is the signal that should persist.
        LogManager.Instance.LogStateChange(VpnProviderStateKey,
            $"VPN provider '{cfg.VpnProvider}' is not recognized - check Settings", LogLevel.Warn);
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
            // Transition-only: an unset adapter stays unset until the user opens Settings, so the
            // identical line every cycle says nothing new. The status above still reports the error
            // on every cycle, which is the part that should persist. See SetSyncResult's stateKey.
            SetSyncResult(status, false, "No NAT-PMP adapter configured - open Settings and select an adapter",
                stateKey: NatPmpAdapterStateKey);
            return null;
        }
        LogManager.Instance.ClearLogState(NatPmpAdapterStateKey);

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
        // Construction inside the try as well: this method's contract is "null on failure", so no
        // statement of its own should be able to throw past its catch to an async void caller.
        IManagedClient? client = null;
        try
        {
            client = BuildActiveClient();
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
        finally
        {
            // Replaces the `using` the try-scoping displaced: null when construction itself failed.
            client?.Dispose();
        }
    }

    /// <summary>
    /// Builds the currently-configured client from the saved settings. Read-only and
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

    // Ensures the client is running, then updates its port if it differs from the target port
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

        // The name check above cannot see a stale interface *token*, which is a qBittorrent-only
        // concern. Deliberately not gated on the two conditions above: a binding can go stale with
        // no VPN configured at all, and the client test is narrower than SupportsInterfaceMismatchWarning.
        // WarnOnInterfaceMismatch is a preference rather than a capability, so it is applied inside,
        // to the warning only - the repair runs either way.
        if (manager is QBittorrentClient qbClient)
        {
            await CheckAndRepairInterfaceBindingAsync(qbClient, currentInterfaceName, config, cancellationToken).ConfigureAwait(false);
            // And the half neither check above can see: the address under a binding whose name and
            // token are both still correct - see CheckInterfaceAddressAsync.
            await CheckInterfaceAddressAsync(qbClient, currentInterfaceName, config, cancellationToken).ConfigureAwait(false);
        }

        await CheckClientSettingsConflictsAsync(manager, cancellationToken).ConfigureAwait(false);

        if (currentPort.Value == targetPort)
        {
            status[StatusKeys.ClientPort] = currentPort.Value;
            LogManager.Instance.LogMessage($"{manager.ClientName} port {currentPort.Value} already matches the target port - no update needed", LogLevel.Info);
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
        // A port write rebuilds the client's listen sockets from a fresh enumeration of the adapter,
        // exactly as an address write does - measured, not assumed. So it has already done what the
        // rebind would do, and any listener stranded on a previous address is gone. Clearing the arm
        // here keeps it honest: without this, an ordinary reconnect (new address *and* a new forwarded
        // port, which is the common case) would stay armed after the port write had already fixed it,
        // and spend that arm later on a closed port it could not possibly explain.
        _interfaceAddressChangedSinceRebind = false;
        // Cause annotation: recovery takes precedence over network change (a recovery usually
        // produces a network change too, and the recovery is the root cause).
        string cause = string.Empty;
        if (_recoveryPendingThisCycle) cause = " - after recovery";
        else if (_networkChangeTriggered) cause = " - after network change";
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
            // Only pending forces a check every cycle, so only pending needs bounding. Give up
            // waiting for a confirmation the check service is not able to supply and fall back to
            // the normal throttle; a later definite closed result simply starts the sequence again.
            if (_portCheckPendingConfirmation && ++_pendingUndeterminedCount >= MaxPendingUndetermined)
            {
                _portCheckPendingConfirmation = false;
                _pendingUndeterminedCount = 0;
                LogManager.Instance.LogMessage(
                    $"{manager.ClientName} port reachability stayed undetermined for {MaxPendingUndetermined} checks - " +
                    "the earlier closed result could not be confirmed, resuming the normal check interval",
                    LogLevel.Info);
            }
            return;
        }
        _pendingUndeterminedCount = 0;
        status[StatusKeys.PortVerified] = open.Value;

        if (open.Value)
        {
            HandlePortOpenResult(manager.ClientName, port, config);
        }
        else
        {
            HandlePortClosedResult(manager.ClientName, port, config);
            await MaybeTriggerPortClosedRecoveryAsync(manager, config, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandlePortOpenResult(string clientName, int port, SyncConfig config)
    {
        if (_portConfirmedClosed)
            LogManager.Instance.LogMessage($"{clientName} port {port} is reachable from outside again", LogLevel.Info);
        else
            LogManager.Instance.LogDebug($"PortSyncService.HandlePortOpenResult: {clientName} port {port} verified open");
        _portCheckPendingConfirmation = false;
        _portConfirmedClosed = false;
        _confirmedClosedCount = 0;
        // A port verified reachable proves the client is listening, so whatever the adapter's address
        // did earlier, it did not strand the listener. Spending the arm here matters: without it a
        // change recorded during a healthy reconnect would sit armed indefinitely and spend itself on
        // the next unrelated closed port, rebinding for a reason that stopped applying long before.
        _interfaceAddressChangedSinceRebind = false;
        if (!_portClosedRecoveryArmed)
        {
            // Re-armed unconditionally, and announced only when the trigger could actually use it.
            // The field is internal arming state; whether the trigger can fire also depends on the
            // setting, and the two were conflated here - so a user who switched the trigger off after
            // it fired was told it "can trigger again", which every other site reporting this state
            // (BuildPortClosedRecoverySuffix, MaybeTriggerPortClosedRecoveryAsync,
            // StatusForm.DescribePortClosedRecovery) already gates on. The re-arm itself must stay
            // unconditional: gating it too would leave the trigger permanently disarmed for anyone who
            // switched the setting off and later back on.
            _portClosedRecoveryArmed = true;

            // Info, not Debug: the disarmed state it clears is reported to the user in the port-closed
            // Warn and on the Status panel, so the point at which recovery becomes available again has
            // to be visible at the same level. Reached only after a recovery actually fired, so it
            // cannot become routine noise.
            if (config.PortClosedRecoveryEnabled)
                LogManager.Instance.LogMessage("Port-closed recovery re-armed - it can trigger again if the port closes", LogLevel.Info);
        }
    }

    // Confirmed-closed logs at Warn on every confirming cycle, deliberately, and is the counterpart to
    // the standing conditions that report only on their transition: this line's text carries a count
    // that advances toward the trigger, so each repetition tells the user something the last one did
    // not. The test is whether hearing it again could change what they do. (The interface-mismatch
    // check used to be cited here as the same pattern; it is now transition-only, being fixed text.)
    // The PortVerificationFailed balloon still fires only on the transition into the confirmed state.
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
    // With recovery off there is nothing to report and the count is zeroed each cycle, so the suffix
    // is empty. Once recovery has fired (disarmed) the count no longer drives a trigger, so reporting
    // it would mislead - but reporting nothing misleads more: this Warn repeats every cycle for as
    // long as the port stays closed, and dropping the suffix at exactly the moment the trigger stops
    // firing leaves the user watching a warning repeat with no sign that the app has already acted
    // and is now waiting. So the disarmed state names itself instead of going quiet.
    private string BuildPortClosedRecoverySuffix(SyncConfig config)
    {
        if (!config.PortClosedRecoveryEnabled)
            return string.Empty;
        if (!_portClosedRecoveryArmed)
            return " (recovery has already run for this outage - it will not run again until a scheduled check reports the port open)";
        string checks = TextFormat.PluralizeNoun(_confirmedClosedCount, "closed check");
        return $" ({_confirmedClosedCount} consecutive {checks}, recovery triggers after {config.PortClosedRecoveryTriggerChecks} consecutive closed checks)";
    }

    // Opt-in: when the port has been confirmed closed for the configured number of checks,
    // dispatches the provider's recovery action once. Independent of the failed-sync recovery
    // trigger - the two share the action, not the gate. One-shot arming: after firing, recovery
    // stays disarmed until a verification reports the port open again (see HandlePortOpenResult),
    // so a persistently false "closed" can never cause a recovery loop.
    // Forces the client to rebuild its listen sockets when a confirmed-closed port coincides with the
    // bound adapter having changed address. Returns true when a rebind was actually attempted, which is
    // what tells the caller to hold the VPN restart back for one more round of confirmation.
    // One attempt per address change: the flag is cleared whether or not the write succeeded, so a rebind
    // that does not help escalates to the VPN restart instead of repeating.
    private async Task<bool> TryRebindClientAddressAsync(IManagedClient manager, SyncConfig config, CancellationToken cancellationToken)
    {
        if (!_interfaceAddressChangedSinceRebind || !config.FixInterfaceBinding) return false;
        if (manager is not QBittorrentClient client) return false;
        if (_lastKnownInterfaceAddresses is not { Count: > 0 } live) return false;
        if (SelectBindAddress(live) is not string pinAddress) return false;

        _interfaceAddressChangedSinceRebind = false;

        LogManager.Instance.LogMessage(
            $"The forwarded port is closed and the bound adapter changed address, so {client.ClientName} may still be " +
            $"listening on the previous one. Rebinding it via {pinAddress} before restarting the VPN",
            LogLevel.Warn);

        // Pin a live address, then release back to whatever was configured when the change was seen.
        // Both writes are real changes, which is what makes the client rebuild its sockets; the end
        // state is the user's own value, read from the client rather than assumed here.
        if (await client.ForceInterfaceRebindAsync(pinAddress, _rebindReleaseAddress, cancellationToken).ConfigureAwait(false))
        {
            LogManager.Instance.LogMessage(
                $"Rebound {client.ClientName} to all addresses on its adapter - the next port check will show whether it is listening again",
                LogLevel.Info);
        }
        else
        {
            LogManager.Instance.LogMessage(
                $"Could not rebind {client.ClientName} - the next confirmed closed check will restart the VPN instead",
                LogLevel.Warn);
        }

        // True either way: the attempt happened, so the caller holds the restart back for one round.
        return true;
    }

    private async Task MaybeTriggerPortClosedRecoveryAsync(IManagedClient manager, SyncConfig config, CancellationToken cancellationToken)
    {
        if (!config.PortClosedRecoveryEnabled)
        {
            _confirmedClosedCount = 0;
            return;
        }
        if (!_portClosedRecoveryArmed || _confirmedClosedCount < config.PortClosedRecoveryTriggerChecks) return;

        // A cheaper and more likely remedy first, when the evidence points at it: the port is confirmed
        // closed *and* the bound adapter's address moved since the client last bound. Restarting the VPN
        // cannot fix a listener stranded on the old address, and costs the user the tunnel to find out.
        // Deliberately leaves the trigger armed - if this does not help, the next confirmed-closed round
        // escalates to the restart as it always did.
        if (await TryRebindClientAddressAsync(manager, config, cancellationToken).ConfigureAwait(false))
        {
            _confirmedClosedCount = 0;   // the next escalation needs fresh confirmation, not this one
            return;
        }

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
        await DispatchRecoveryAsync(action, target, vpnManager.ProviderName, cancellationToken,
            triggerLogMessage: $"Triggering '{action}' for '{vpnManager.ProviderName}' after {config.PortClosedRecoveryTriggerChecks} consecutive closed {TextFormat.PluralizeNoun(config.PortClosedRecoveryTriggerChecks, "check")}").ConfigureAwait(false);
    }

    // Returns true if the client is running (or was successfully force-started), false otherwise
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
            SetSyncResult(status, false, $"Failed to force-start {manager.ClientName}");
            return false;
        }
        LogManager.Instance.LogMessage($"Force-started {manager.ClientName}", LogLevel.Info);
        return true;
    }

    // Checks if the client's network interface matches the expected VPN provider and logs a warning if not.
    // Both the warn log and the InterfaceMismatchDetected balloon report only on the transition, by
    // separate mechanisms: the log through LogStateChange (keyed on InterfaceMatchStateKey), the balloon
    // through the _lastInterfaceMismatchMessage latch. A binding stays wrong until the user re-selects
    // an interface, so repeating either one adds nothing and the Warn would keep pushing the tray's
    // unviewed count up for a condition the user cannot clear by acting on it. Both carry the interface
    // name, so drifting from one wrong adapter to another still reports; both re-arm when it matches.
    private void CheckInterfaceMatch(string clientName, string? interfaceName, IVpnManager vpnManager)
    {
        if (interfaceName is null)
        {
            LogManager.Instance.LogDebug($"PortSyncService.CheckInterfaceMatch: {clientName} did not return an interface name, skipping check");
            return;
        }

        string? balloonMessage = null;

        // Both warnings are transition-only, matching the balloon beside them: the binding persists
        // until the user re-selects an interface, so the same line every cycle adds nothing and keeps
        // bumping the tray's unviewed-warning count. The message carries the interface name, so
        // drifting from one wrong adapter to another still reports, which is a real change.
        if (interfaceName.Length == 0)
        {
            LogManager.Instance.LogStateChange(InterfaceMatchStateKey,
                $"{clientName} is bound to all network interfaces - traffic may leak outside the VPN", LogLevel.Warn);
            balloonMessage = $"{clientName}: no VPN interface bound - traffic may leak.";
        }
        else if (!vpnManager.IsAdapterMatch(interfaceName))
        {
            LogManager.Instance.LogStateChange(InterfaceMatchStateKey,
                $"{clientName} network interface '{interfaceName}' does not match '{vpnManager.ProviderName}'", LogLevel.Warn);
            balloonMessage = $"{clientName} interface mismatch - '{interfaceName}' is not a {vpnManager.ProviderName} adapter.";
        }
        else
        {
            LogManager.Instance.LogDebug($"PortSyncService.CheckInterfaceMatch: {clientName} interface '{interfaceName}' matches '{vpnManager.ProviderName}'");
        }

        if (balloonMessage is null)
        {
            // Healthy: re-arm both latches so a later mismatch warns and notifies again.
            LogManager.Instance.ClearLogState(InterfaceMatchStateKey);
            _lastInterfaceMismatchMessage = null;
            return;
        }

        _lastInterfaceMismatchMessage = RaiseInterfaceBalloonIfNew(balloonMessage, _lastInterfaceMismatchMessage);
    }

    // Detects a stale qBittorrent interface binding and re-points it at the adapter qBittorrent
    // already names. Detection always runs: the stored token can drift to a different live adapter
    // while the client still reports itself connected, which no other check in the cycle would
    // notice and which is the case that can carry traffic outside the tunnel.
    // Two independent settings govern what follows - fixInterfaceBinding decides whether the binding
    // is repaired, warnOnInterfaceMismatch decides whether an unrepaired one is reported.
    private async Task CheckAndRepairInterfaceBindingAsync(QBittorrentClient client, string? interfaceName, SyncConfig config, CancellationToken cancellationToken)
    {
        var (stale, expectedToken) = await client.CheckInterfaceBindingAsync(interfaceName, cancellationToken).ConfigureAwait(false);

        if (!stale || expectedToken is null || interfaceName is null)
        {
            // Healthy, or nothing to say. Re-arm so a later drift gets a fresh repair attempt, and
            // clear this condition's own balloon and log latches so a future stale binding reports again.
            _interfaceBindingRepairAttempted = false;
            _lastBindingWarningMessage = null;
            LogManager.Instance.ClearLogState(BindingStaleStateKey);
            return;
        }

        string warning =
            $"{client.ClientName} is bound to '{interfaceName}' by a stale identifier, so it is not listening on that adapter - " +
            "restarting cannot fix this because the value is stored in its configuration";

        if (!config.FixInterfaceBinding)
        {
            // Only the warning honours WarnOnInterfaceMismatch. Detection above is a capability gate
            // and has to run either way - the repair must work whether or not the user wants to be
            // told - but this branch produces exactly the kind of notification that setting turns
            // off, through the same event and balloon as the adapter-name warning beside it. Warning
            // here while "bound to all interfaces - traffic may leak" stays silent would be the
            // stricter of the two, which is backwards.
            if (config.WarnOnInterfaceMismatch)
            {
                // Transition-only, like the balloon below it and the repair branch further down, which
                // gates on _interfaceBindingRepairAttempted for the same reason: a stale binding lives
                // in the client's own configuration and persists until the user acts.
                LogManager.Instance.LogStateChange(BindingStaleStateKey,
                    $"{warning}. Re-select the network interface in {client.ClientName}, or turn on \"Fix the network interface binding when it goes stale\" in Settings",
                    LogLevel.Warn);
                _lastBindingWarningMessage = RaiseInterfaceBalloonIfNew(
                    $"{client.ClientName}: network interface binding is stale - re-select it or enable the automatic fix.",
                    _lastBindingWarningMessage);
            }
            else
            {
                LogManager.Instance.LogDebug(
                    $"PortSyncService.CheckAndRepairInterfaceBindingAsync: {client.ClientName} binding is stale, but both the fix and the interface warning are off");
            }
            return;
        }

        // One attempt per stale streak: if the write lands but the binding is still wrong next cycle,
        // something else is overwriting it and repeating the write every cycle would be its own loop.
        if (_interfaceBindingRepairAttempted)
        {
            LogManager.Instance.LogDebug(
                $"PortSyncService.CheckAndRepairInterfaceBindingAsync: {client.ClientName} binding still stale after a repair - not retrying until it is healthy");
            return;
        }

        _interfaceBindingRepairAttempted = true;
        LogManager.Instance.LogMessage($"{warning}. Re-applying it", LogLevel.Warn);

        if (await client.RepairInterfaceBindingAsync(interfaceName, expectedToken, cancellationToken).ConfigureAwait(false))
            LogManager.Instance.LogMessage($"Re-applied the {client.ClientName} network interface binding to '{interfaceName}'", LogLevel.Info);
        else
            LogManager.Instance.LogMessage($"Could not re-apply the {client.ClientName} network interface binding - re-select it in {client.ClientName}", LogLevel.Error);
    }

    // Checks the address side of the binding, which is the half the name and token checks cannot see.
    // Two conditions, distinguished because the evidence for them differs and so does the response:
    //  - a pinned address the adapter no longer carries: provably broken, so it is repaired here and now,
    //    exactly as a stale token is
    //  - the adapter's addresses moving while the binding is "all addresses": what an ordinary VPN
    //    reconnect produces. The client may have coped, or may have left a listener on the old address,
    //    and nothing observable here tells the two apart. So this only *arms* the port-closed escalation
    //    (TryRebindClientAddressAsync), which acts once the port is confirmed closed as well. Rebinding
    //    on every reconnect would write to the user's client configuration for a problem that may not
    //    exist; waiting for the symptom costs a few cycles and spends nothing on a healthy client.
    private async Task CheckInterfaceAddressAsync(QBittorrentClient client, string? interfaceName, SyncConfig config, CancellationToken cancellationToken)
    {
        var (live, pinned) = await client.GetInterfaceAddressStateAsync(interfaceName, cancellationToken).ConfigureAwait(false);

        // Unknown rather than healthy: an older qBittorrent, an unreachable API, or no bound interface.
        // Leaving the remembered addresses alone means the next readable cycle compares against the last
        // real observation rather than against a gap, so a change spanning the gap is still reported.
        if (live is null) return;

        if (!IsWildcardBindAddress(pinned) && !live.Contains(pinned, StringComparer.OrdinalIgnoreCase))
        {
            // Provably broken rather than suspected: the client is bound to an address the adapter does
            // not have, so it cannot be listening. Repaired on detection like the stale token beside it,
            // not deferred to the port-closed escalation, which is for the case that is only a suspicion.
            await RepairPinnedAddressAsync(client, interfaceName, pinned, live, config, cancellationToken).ConfigureAwait(false);
        }
        else if (IsWildcardBindAddress(pinned) &&
                 _lastKnownInterfaceAddresses is { } previous && !previous.SequenceEqual(live, StringComparer.OrdinalIgnoreCase))
        {
            // The wildcard test is load-bearing, not a tidy-up. The first branch is false in *two*
            // cases - no pin, and a pin that is present and correct - and only the first belongs here.
            // Without this test a user pinned to one address whose adapter merely gained or lost
            // another address was armed, and the rebind then released to "all addresses", silently
            // widening a binding they chose. A valid pin means the listener is on an address that
            // still exists, which is healthy, so it correctly falls through to the else below.
            //
            // Arms the port-closed escalation. Not acted on here: on its own an address change is a
            // normal reconnect, and forcing a rebind on every one would write to the user's client
            // configuration for a problem that may not exist.
            _interfaceAddressChangedSinceRebind = true;
            // Captured rather than assumed. The rebind releases back to whatever was configured when
            // the change was seen, so the end state is the user's own value even if this branch is
            // ever widened to admit a non-empty one. The previous code hardcoded string.Empty here,
            // which was only correct because of the very condition that was missing above.
            _rebindReleaseAddress = pinned ?? string.Empty;
            // Info, not Warn: on its own this is a normal VPN reconnect, not a fault. The connection
            // status is read here rather than every cycle - this branch is a transition, so the extra
            // call is rare - and it answers whether the existing restart-on-disconnect path could ever
            // see this state. The line states what happens next, because something does: leaving it at
            // "if connections stop, the listener is on the old address" would ask the user to draw a
            // conclusion the app now acts on by itself.
            string? connectionStatus = await client.GetConnectionStatusAsync(cancellationToken).ConfigureAwait(false);
            LogManager.Instance.LogMessage(
                $"The address on '{interfaceName}' changed from {FormatAddressList(previous)} to {FormatAddressList(live)} " +
                $"while {client.ClientName} is bound to all addresses on it ({client.ClientName} reports " +
                $"'{connectionStatus ?? "no status"}'). If the forwarded port stops answering, {client.ClientName} will be " +
                "rebound before the VPN is restarted",
                LogLevel.Info);
        }
        else
        {
            LogManager.Instance.ClearLogState(BindingAddressStateKey);
            _interfaceAddressRepairAttempted = false;   // healthy: re-arm for a later drift
        }

        _lastKnownInterfaceAddresses = live;
    }

    // Corrects a pin that names an address the adapter no longer has. Writing the live address is a real
    // change, so it also forces the rebind; the pin stays because it was the user's own setting and only
    // its value was wrong. Warn-only when the fix is switched off, matching the stale-token repair.
    private async Task RepairPinnedAddressAsync(
        QBittorrentClient client, string? interfaceName, string pinned, IReadOnlyList<string> live,
        SyncConfig config, CancellationToken cancellationToken)
    {
        string detail =
            $"{client.ClientName} is bound to address {pinned} on '{interfaceName}', which that adapter no longer " +
            $"has (it now has {FormatAddressList(live)}), so it cannot accept incoming connections";

        string? replacement = SelectBindAddress(live);
        if (replacement is null || !config.FixInterfaceBinding)
        {
            // No usable address covers an adapter mid-negotiation, where the right move is to wait for
            // the next cycle rather than write something that is about to be wrong again.
            string remedy = replacement is null
                ? "waiting for the adapter to report a usable address"
                : $"correct it in {client.ClientName}, or turn on \"Fix the network interface binding when it goes stale\" in Settings";
            LogManager.Instance.LogStateChange(BindingAddressStateKey, $"{detail} - {remedy}", LogLevel.Warn);
            return;
        }

        if (_interfaceAddressRepairAttempted)
        {
            LogManager.Instance.LogDebug(
                $"PortSyncService.RepairPinnedAddressAsync: {client.ClientName} address pin still stale after a repair - not retrying until it is healthy");
            return;
        }

        _interfaceAddressRepairAttempted = true;
        LogManager.Instance.LogMessage($"{detail}. Re-pointing it at {replacement}", LogLevel.Warn);

        // finalAddress equals pinAddress: the client was pinned by choice and stays pinned, only to an
        // address that exists. One write, and no release step.
        if (await client.ForceInterfaceRebindAsync(replacement, replacement, cancellationToken).ConfigureAwait(false))
            LogManager.Instance.LogMessage($"Re-pointed the {client.ClientName} network interface address at {replacement}", LogLevel.Info);
        else
            LogManager.Instance.LogMessage($"Could not set the {client.ClientName} network interface address - correct it in {client.ClientName}", LogLevel.Error);
    }

    // qBittorrent's bind-address field holds either a concrete address or one of three wildcards, and
    // the dropdown offers all three: "" (all addresses), 0.0.0.0 (all IPv4) and :: (all IPv6). None of
    // them ever appears in the adapter's address list, so none can be judged stale against it - the
    // check that only knew about "" reported "All IPv4 addresses" as broken and, with the repair on,
    // rewrote a wildcard the user chose into one concrete address, which then genuinely went stale on
    // every reconnect. Verified live for 0.0.0.0; :: is included on the strength of the dropdown
    // offering it, and costs nothing if it never occurs.
    //
    // One predicate, used by both branches of the check, because they are the two halves of a
    // partition: a value either can be judged stale or is a wildcard that arms the escalation instead.
    // Two inline tests could drift apart and leave a value in neither branch or in both.
    private static bool IsWildcardBindAddress(string? address) =>
        string.IsNullOrEmpty(address) || address == "0.0.0.0" || address == "::";

    // The address to bind to, out of everything the adapter reports. IPv4 first because that is what a
    // forwarded port is granted for by every provider this app supports; link-local is never usable for
    // incoming connections, so it is excluded rather than picked as a last resort.
    private static string? SelectBindAddress(IReadOnlyList<string> addresses)
    {
        var usable = addresses.Where(a => !IsLinkLocal(a)).ToList();
        return usable.FirstOrDefault(a => !a.Contains(':')) ?? usable.FirstOrDefault();
    }

    private static bool IsLinkLocal(string address) =>
        address.StartsWith("169.254.", StringComparison.Ordinal) ||
        address.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase);

    // Addresses as a readable list for one log line. qBittorrent reports IPv4 and IPv6 together and
    // the whole set is the evidence, so none is filtered out - a v6-only change is still a change.
    private static string FormatAddressList(IReadOnlyList<string> addresses) =>
        addresses.Count == 0 ? "no addresses" : string.Join(", ", addresses);

    // Balloon dispatch for interface problems, deduplicated so a persistent condition notifies once
    // rather than every cycle. Returns the latch value the caller should store, so each condition owns
    // its own latch and no caller can clear another's.
    private string? RaiseInterfaceBalloonIfNew(string balloonMessage, string? lastMessage)
    {
        if (balloonMessage == lastMessage) return lastMessage;
        try { InterfaceMismatchDetected?.Invoke(balloonMessage); }
        catch (Exception ex) { LogManager.Instance.LogMessage($"InterfaceMismatchDetected handler failed: {ex.Message}", LogLevel.Warn); }
        return balloonMessage;
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
        LogManager.Instance.LogMessage($"{manager.ClientName} port does not match the target port - updating to {targetPort}", LogLevel.Info);
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
    private async Task CheckAndRestartIfDisconnectedAsync(IManagedClient manager, CancellationToken cancellationToken)
    {
        string? connectionStatus = await manager.GetConnectionStatusAsync(cancellationToken).ConfigureAwait(false);
        if (connectionStatus is null)
            return;

        LogManager.Instance.LogDebug($"PortSyncService.CheckAndRestartIfDisconnectedAsync: {manager.ClientName} connection status: {connectionStatus}");

        if (!connectionStatus.Equals(ClientDisconnectedStatus, StringComparison.OrdinalIgnoreCase))
        {
            // Any non-disconnected status re-arms the allowance, so a later unrelated disconnect gets
            // the full number of attempts rather than inheriting an exhausted counter.
            if (_consecutiveDisconnectRestarts > 0)
                LogManager.Instance.LogDebug(
                    $"PortSyncService.CheckAndRestartIfDisconnectedAsync: {manager.ClientName} is no longer disconnected - restart attempts re-armed");
            _consecutiveDisconnectRestarts = 0;
            return;
        }

        if (_consecutiveDisconnectRestarts >= MaxDisconnectRestarts)
        {
            LogManager.Instance.LogDebug(
                $"PortSyncService.CheckAndRestartIfDisconnectedAsync: {manager.ClientName} still disconnected, restarts remain suspended");
            return;
        }

        _consecutiveDisconnectRestarts++;
        LogManager.Instance.LogMessage(
            $"{manager.ClientName} connection status is disconnected - restarting (attempt {_consecutiveDisconnectRestarts} of {MaxDisconnectRestarts})",
            LogLevel.Warn);

        if (!await manager.RestartAsync(cancellationToken).ConfigureAwait(false))
            LogManager.Instance.LogMessage($"Failed to restart {manager.ClientName} after connection disconnect", LogLevel.Error);
        else
            LogManager.Instance.LogMessage($"Restarted {manager.ClientName} after connection disconnect", LogLevel.Info);

        // Logged once, on the transition to the cap, so a persistent cause does not repeat this every
        // cycle. The hint names the most common unfixable-by-restart cause: a binding the client keeps
        // in its own configuration, which a restart re-reads unchanged.
        if (_consecutiveDisconnectRestarts == MaxDisconnectRestarts)
            LogManager.Instance.LogMessage(
                $"Restarting has not cleared {manager.ClientName}'s disconnected status after {MaxDisconnectRestarts} attempts - " +
                $"no further restarts until it reconnects. If it is bound to a VPN adapter, re-select the network interface in {manager.ClientName}'s settings",
                LogLevel.Warn);
    }

    // Warns when the client's own settings are working against the synchronized port - a randomised
    // listening port, or the client's built-in UPnP/NAT-PMP mapping.
    //
    // Runs on the sync loop and not only from Diagnostics because on Transmission and Nicotine+ these
    // settings produce no symptom at all: the client keeps reporting the correct port, so nothing
    // mismatches, no port write is triggered, and the user has no reason to open Diagnostics before
    // their next client restart moves the port off the forwarded one. (On qBittorrent and Deluge the
    // condition does surface as a port mismatch and the next write corrects it, so there the warning is
    // just earlier notice.) Throttled to every ConflictCheckEveryNCycles cycles and logged on the
    // transition, because the condition persists until the user acts - warning on every check would
    // repeat the same line for as long as the setting stays on.
    private async Task CheckClientSettingsConflictsAsync(IManagedClient manager, CancellationToken cancellationToken)
    {
        if (++_cyclesSinceConflictCheck < ConflictCheckEveryNCycles) return;
        _cyclesSinceConflictCheck = 0;

        var conflicts = await manager.GetConflictingSettingsAsync(cancellationToken).ConfigureAwait(false);
        // Null is "could not read", which is neither a conflict nor proof of its absence. Leave the
        // latch untouched so an unreadable check cannot silently clear a warning the user has not fixed.
        if (conflicts is null) return;

        if (conflicts.Count == 0)
        {
            if (_clientSettingsConflictActive)
            {
                _clientSettingsConflictActive = false;
                LogManager.Instance.LogMessage(
                    $"{manager.ClientName} settings no longer work against the forwarded port", LogLevel.Info);
            }
            return;
        }

        if (_clientSettingsConflictActive) return;
        _clientSettingsConflictActive = true;

        string names = string.Join(", ", conflicts.Select(c => $"\"{c.SettingName}\""));
        string pronoun = conflicts.Count == 1 ? "it" : "them";
        LogManager.Instance.LogMessage(
            $"{manager.ClientName} has {TextFormat.Pluralize(conflicts.Count, "setting")} working against the forwarded port: " +
            $"{names} - turn {pronoun} off in {manager.ClientName}'s settings",
            LogLevel.Warn);

        // A balloon as well as the log line, matching the interface-mismatch warning this most
        // resembles. It is not redundant with the generic "warnings were logged" balloon: on
        // Transmission and Nicotine+ this condition has no symptom, so a user with no reason to
        // suspect anything has no reason to open the log viewer either. Guarded like the other
        // raisers - a handler that throws must not take down the sync cycle.
        try
        {
            ClientSettingsConflictDetected?.Invoke(
                $"{manager.ClientName} has {TextFormat.Pluralize(conflicts.Count, "setting")} working against the forwarded port. See the log for details.");
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"ClientSettingsConflictDetected handler failed: {ex.Message}", LogLevel.Warn);
        }
    }

    // Builds a failure log message with cycle count and optional recovery trigger suffix
    // The suffix has to track the cap as well as the setting. While the cap is suspending recovery the
    // streak keeps climbing (deliberately - the failures are real), so a suffix that only knew about the
    // setting promised "recovery may trigger after N cycles" on every one of those cycles, for a recovery
    // that could not run. The log then contradicted both the suspension warning and the Status panel.
    private string BuildCycleCountMessage(string prefix, int count, AppConfig cfg)
    {
        string cycles = TextFormat.PluralizeNoun(count, "failed cycle");
        string recoverySuffix;
        if (!cfg.VpnAutoRecoveryEnabled)
            recoverySuffix = string.Empty;
        else if (_consecutiveRecoveries >= MaxConsecutiveRecoveries)
            recoverySuffix = $", auto-recovery is suspended after {MaxConsecutiveRecoveries} attempts that did not restore a port";
        else
            recoverySuffix = $", recovery may trigger after {cfg.VpnAutoRecoveryTriggerCycles} consecutive failed cycles";
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
        if (_consecutiveFailedCycles == 0) _failureStreakStarted = _uptime.Elapsed;
        _consecutiveFailedCycles++;
        int count = _consecutiveFailedCycles;
        LogManager.Instance.LogMessage(BuildCycleCountMessage(reason, count, cfg), logLevel);
        await TriggerRecoveryIfDueAsync(recoveryAction, recoveryTarget, displayName, cfg, cancellationToken).ConfigureAwait(false);
    }

    // Resets the failure streak counter. The start timestamp is deliberately left alone: it is
    // re-stamped on the next streak's first failure (see RegisterFailureAndTryRecoveryAsync),
    // and the time gate in TriggerRecoveryIfDueAsync only reads it while the counter is non-zero,
    // so a stale value can never be observed.
    private void ResetFailureStreak()
    {
        _consecutiveFailedCycles = 0;
        _recoverySustainedUntil = null;   // the floor is measured from a streak that no longer exists
    }

    // Ends a run of consecutive recoveries. Called from the successful-port path and when auto-recovery
    // is switched off, and deliberately not from ResetFailureStreak: the run has to be broken by recovery
    // actually working, not merely by the failure counter being reset - the dispatch path resets that
    // streak every time it fires, so clearing the count there would mean the cap was never reached.
    private void ResetRecoveryCap()
    {
        _consecutiveRecoveries = 0;
        LogManager.Instance.ClearLogState(RecoveryCapStateKey);
    }

    // Triggers auto-recovery if enabled and both gates are cleared: the failure cycle threshold
    // AND enough monotonic time has elapsed since the streak began. The time gate (derived from
    // the normal cycle cadence) prevents a burst of early wakes from fast-tracking recovery during
    // a transient outage. Resets the counter before the target check so the warning does not fire
    // every cycle when no recovery target is found.
    private async Task TriggerRecoveryIfDueAsync(string action, string? recoveryTarget, string displayName, AppConfig cfg, CancellationToken cancellationToken)
    {
        if (!cfg.VpnAutoRecoveryEnabled)
        {
            ResetFailureStreak();
            // Switched off, so nothing can be accumulating: clear the cap too, matching the existing
            // rule that a disabled feature leaves no stale counters to surprise the user on re-enable.
            ResetRecoveryCap();
            return;
        }
        if (_consecutiveFailedCycles < cfg.VpnAutoRecoveryTriggerCycles) return;

        // Backstop: stop dispatching once MaxConsecutiveRecoveries have run without a single successful
        // port read between them. The failure streak is deliberately left running rather than reset - the
        // failures really are continuing, and the count is what the log line and the Status panel report.
        // Every later cycle re-enters here and returns on this same check; LogStateChange keeps that to
        // one line. Cleared by ResetRecoveryCap on the next successful port read.
        if (_consecutiveRecoveries >= MaxConsecutiveRecoveries)
        {
            LogManager.Instance.LogStateChange(RecoveryCapStateKey,
                $"Suspending auto-recovery for '{displayName}' - {MaxConsecutiveRecoveries} consecutive recoveries " +
                "did not restore a forwarded port, so repeating it would only keep interrupting the connection. " +
                "Recovery resumes automatically once a port is read successfully.",
                LogLevel.Warn);
            return;
        }

        // Sustained-failure floor: the elapsed time recovery would have taken under normal
        // scheduled cycling ((TriggerCycles - 1) intervals between the first and last failure).
        // A streak driven faster than this by early wakes is held until the time also clears.
        TimeSpan minSustainedFailure = TimeSpan.FromSeconds((cfg.VpnAutoRecoveryTriggerCycles - 1) * cfg.UpdateInterval);
        TimeSpan elapsed = _uptime.Elapsed - _failureStreakStarted;
        if (elapsed < minSustainedFailure)
        {
            _recoverySustainedUntil = DateTimeOffset.Now.Add(minSustainedFailure - elapsed);
            LogManager.Instance.LogMessage(
                $"Holding recovery - failures started only {elapsed.TotalSeconds:F0}s ago " +
                $"(recovery waits until {minSustainedFailure.TotalSeconds:F0}s to ignore brief network blips)",
                // Info, while the offline limiter logs its hold at Warn, and the asymmetry is
                // deliberate rather than an oversight: both lines mean "recovery is not running right
                // now", but this floor clears by itself within a cycle or two and is reached during
                // any ordinary blip, whereas an offline hold lasts 5 to 15 minutes and means something
                // is actually wrong. Warn here would badge the tray on every transient failure.
                LogLevel.Info);
            return;
        }

        _recoverySustainedUntil = null;   // floor cleared: it is no longer holding anything back
        int count = _consecutiveFailedCycles;

        if (recoveryTarget is null)
        {
            ResetFailureStreak();
            LogManager.Instance.LogMessage($"No recovery target found for '{displayName}' - skipping recovery", LogLevel.Warn);
            return;
        }

        // Rate-limits recovery while the machine cannot reach the internet. Deliberately gates this
        // trigger only: the port-closed trigger runs after a successful port fetch, so that path has
        // already proven it has connectivity and the probe there could only ever be wrong (a machine
        // that filters ICMP would be told its internet is down moments after a clean sync).
        //
        // Placed above ResetFailureStreak so a held attempt leaves the streak hot. The next attempt is
        // then governed purely by the backoff, rather than also having to rebuild the streak and clear
        // the sustained-failure floor again - which would make the real spacing longer than the 5, 10
        // and 15 minutes documented, by an amount that depends on the configured interval.
        var slot = await TryTakeRecoverySlotAsync(displayName, cancellationToken).ConfigureAwait(false);
        if (!slot.Allowed) return;

        ResetFailureStreak();
        // Counted here, past every gate, so it records recoveries that are actually dispatched. Counting
        // at the trigger sites instead would let held attempts consume the cap without a restart running.
        //
        // Online attempts only, and that exemption is load-bearing rather than a nicety. The cap is a
        // hard stop, and the connectivity limiter exists precisely because a hard stop is wrong while the
        // machine cannot reach the internet: a killswitch blocks the probe itself, so a stuck VPN and a
        // dead upstream are indistinguishable, and refusing to retry leaves the killswitch up with no way
        // out. Counting offline attempts would put that deadlock back after three tries. Offline
        // recoveries are already bounded, by the 5/10/15 minute backoff, so they need no second bound.
        if (slot.Online) _consecutiveRecoveries++;

        await DispatchRecoveryAsync(action, recoveryTarget, displayName, cancellationToken,
            triggerLogMessage: $"Triggering '{action}' for '{displayName}' after {count} consecutive failed {TextFormat.PluralizeNoun(count, "cycle")}").ConfigureAwait(false);
    }

    // When the offline rate limiter will allow the next recovery attempt, or null when
    // nothing is being held back. Read once per cycle for the status file so the Status panel can say
    // "Holding - no internet connection, retry in ~15m" - without it the user sees a disconnected VPN and no sign that
    // the app is deliberately waiting rather than idle, which is the whole point of the limiter.
    // Computed rather than stored: the deadline is the last attempt plus its backoff step, and both
    // are already tracked. Returns null once the wait has elapsed - at that point the next due cycle
    // will attempt recovery, so there is nothing left to count down.
    private DateTimeOffset? GetRecoveryHoldUntil()
    {
        if (_offlineRecoveryAttempts <= 0) return null;
        TimeSpan required = OfflineRetryBackoff[Math.Min(_offlineRecoveryAttempts - 1, OfflineRetryBackoff.Length - 1)];
        TimeSpan waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastOfflineRecoveryMs);
        TimeSpan remaining = required - waited;
        return remaining > TimeSpan.Zero ? DateTimeOffset.Now.Add(remaining) : null;
    }

    // Clears the offline rate-limiter so the next offline streak starts with an immediate attempt.
    // Called from both ends: the probe succeeding here, and a successful port fetch in the sync cycle.
    // Both are needed - once the VPN is healthy again recovery is never dispatched, so the probe branch
    // alone would leave a stale count behind and needlessly delay the first attempt of a later outage.
    private void ResetOfflineRecoveryBackoff()
    {
        _offlineRecoveryAttempts = 0;
        _lastOfflineRecoveryMs = 0;
        _recoveryHoldLogged = false;
    }

    // Decides whether an automatic recovery may run now, given whether the machine can reach the
    // internet. The probe rate-limits recovery, it never vetoes it outright, and that distinction is
    // load-bearing: a VPN killswitch blocks the probe itself while the tunnel is down, so a hard veto
    // would suppress recovery exactly when a restart is the fix, leaving the killswitch up, the probe
    // failing, and the machine deadlocked with no way out. Backing off instead bounds the damage of a
    // real outage while guaranteeing a stuck VPN still gets retried.
    //
    // Online: recover immediately, and reset the backoff.
    // Offline: the first recovery of a streak still runs (it is the one most likely to help), then
    // successive attempts wait OfflineRetryBackoff - 5, 10, then 15 minutes for every attempt after.
    private async Task<(bool Allowed, bool Online)> TryTakeRecoverySlotAsync(string displayName, CancellationToken cancellationToken)
    {
        if (await InternetConnectivityProbe.IsInternetReachableAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_offlineRecoveryAttempts > 0)
                LogManager.Instance.LogMessage("Internet connection confirmed - recovery resumed at the normal rate", LogLevel.Info);
            ResetOfflineRecoveryBackoff();
            return (true, Online: true);
        }

        // Monotonic, which is what matters here: a machine coming back from an outage often corrects
        // its clock by NTP, and a backward jump must not unblock every held attempt at once. The
        // instance _uptime stopwatch is equally monotonic and equally reachable from here, so either
        // would serve - this path uses TickCount64 because the limiter already stores its state as a
        // raw tick count in _lastOfflineRecoveryMs, and GetRecoveryHoldUntil reads it back the same way.
        long nowMs = Environment.TickCount64;

        if (_offlineRecoveryAttempts > 0)
        {
            TimeSpan required = OfflineRetryBackoff[Math.Min(_offlineRecoveryAttempts - 1, OfflineRetryBackoff.Length - 1)];
            TimeSpan waited = TimeSpan.FromMilliseconds(nowMs - _lastOfflineRecoveryMs);
            if (waited < required)
            {
                // Logged once per wait window rather than per attempt: recovery is re-evaluated every
                // few cycles, so logging each held attempt would repeat the same line for the length of
                // the outage. Cleared when an attempt is allowed, so each new window reports its wait.
                if (!_recoveryHoldLogged)
                {
                    _recoveryHoldLogged = true;
                    LogManager.Instance.LogMessage(
                        $"Could not confirm an internet connection - holding recovery for '{displayName}' for {required.TotalMinutes:F0} minutes " +
                        "(restarting the VPN cannot restore a connection that is down upstream)",
                        LogLevel.Warn);
                }
                return (false, Online: false);
            }
        }

        _lastOfflineRecoveryMs = nowMs;
        _offlineRecoveryAttempts++;
        _recoveryHoldLogged = false;
        LogManager.Instance.LogMessage(
            $"Could not confirm an internet connection - trying recovery for '{displayName}' anyway (attempt {_offlineRecoveryAttempts}), " +
            "in case it is being blocked locally rather than being down upstream",
            // Info, unlike the hold above, and the asymmetry is deliberate: holding means recovery is
            // *not* happening and the VPN stays broken for the whole window, which is worth surfacing;
            // this line announces that recovery *is* proceeding, and its outcome reports itself (the
            // helper logs Error if the service does not come back). Warn here badged the tray on every
            // recovery for anyone whose network filters ICMP, for an app doing exactly its job. A real
            // outage is still surfaced independently by the per-cycle port-detection warning.
            LogLevel.Info);
        return (true, Online: false);
    }

    // Dispatches a recovery action to the helper service. Shared by the failed-cycle trigger
    // (TriggerRecoveryIfDueAsync), the port-closed trigger (MaybeTriggerPortClosedRecoveryAsync),
    // and the on-demand recovery test (TestRecoveryAsync, manualTest = true). A manual test is
    // not counted in the session statistic, whose label reads "Auto-recoveries (session)" precisely
    // so that exclusion is correct by construction - a test the user ran by hand is not the app
    // recovering itself, and counting it would inflate a health figure. It is still recorded in the
    // history and arms the "after recovery" annotation like a real one.
    private static async Task DispatchRecoveryAsync(string action, string recoveryTarget, string displayName, CancellationToken cancellationToken, bool manualTest = false, string? triggerLogMessage = null)
    {
        // Announced here rather than at the trigger sites, because only this side knows whether the
        // action survived the gate above. Logging "Triggering ..." before the call would assert a
        // recovery that was then held, with no history entry or statistic to contradict it - the log
        // is the only account of what happened during an outage, so it has to stay literally true.
        if (triggerLogMessage is not null)
            LogManager.Instance.LogMessage(triggerLogMessage, LogLevel.Info);

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
        // WarnOnInterfaceMismatch needs a named adapter, which qBittorrent and Nicotine+ both report;
        // Transmission and Deluge report a bind address instead, so there is no name to compare. That
        // is about the name check alone - see IManagedClient.SupportsInterfaceMismatchWarning for what
        // each of them actually binds, which is not the same question and does not have the same answer.
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
    /// <summary>Records this cycle's outcome in the status dictionary and, on failure, logs the reason.</summary>
    /// <param name="status">The cycle's status dictionary, written to the status file in RunAsync's finally.</param>
    /// <param name="success">Whether the cycle succeeded; false writes the error status and logs the reason.</param>
    /// <param name="message">The reason, used both as the status message and as the logged line.</param>
    /// <param name="level">Severity for the logged reason. Defaults to <see cref="LogLevel.Error"/>.</param>
    /// <param name="stateKey">
    /// When set, the reason is logged through <see cref="LogManager.LogStateChange"/> under this key
    /// instead of on every failing cycle.
    /// <para>The test for which to use: <b>can the user do anything differently if we say it again?</b>
    /// A misconfiguration only they can fix - no NAT-PMP adapter selected, an unrecognised provider -
    /// produces the identical line forever, so it is reported on the transition and the status field
    /// below still carries it every cycle. Observed runtime state is the opposite: "{client} is not
    /// running" changes on its own, so each cycle is a fresh observation rather than a repeat and
    /// belongs in the default path. Repeating a fixed configuration error also climbs the tray's
    /// unviewed-warning count indefinitely, which the status field does not.</para>
    /// <para>Pair every key with a <see cref="LogManager.ClearLogState"/> on the path where the
    /// condition clears, or it is reported once per process rather than once per occurrence.</para>
    /// </param>
    private static void SetSyncResult(Dictionary<string, object?> status, bool success, string message,
        LogLevel? level = null, string? stateKey = null)
    {
        status[StatusKeys.Status] = success ? SyncStatusValues.Success : SyncStatusValues.Error;
        status[StatusKeys.Message] = message;
        // Log the specific reason on failure (at its own severity); the uniform terminal line is
        // emitted once per cycle by LogCycleOutcome. A successful cycle needs no reason line - its
        // terminal marker says it all.
        if (!success)
        {
            if (stateKey is not null)
                LogManager.Instance.LogStateChange(stateKey, message, level ?? LogLevel.Error);
            else
                LogManager.Instance.LogMessage(message, level ?? LogLevel.Error);
        }
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

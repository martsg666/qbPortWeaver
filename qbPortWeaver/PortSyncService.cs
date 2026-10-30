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

    // LogManager.LogStateChange keys for the conditions in this cycle that persist until the
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
    private const string StaticPortStateKey = "vpn.staticPortUnconfigured";
    private const string PortForwardingUnavailableStateKey = "vpn.portForwardingUnavailable";
    private const string BindingAddressStateKey = "client.bindingAddressStale";
    private const string VpnExitMismatchStateKey = "vpn.exitAddressMismatch";
    private const string VpnExitNoTargetStateKey = "vpn.exitAddressNoRecoveryTarget";
    private const string UnexpectedErrorDetailsStateKey = "sync.unexpectedErrorDetails";

    /// <summary>Raised when a sync cycle completes (success or failure) with the resulting tray status.</summary>
    public event Action<TrayStatus>? SyncCompleted;

    /// <summary>Raised when the client's network interface does not match the configured VPN provider.</summary>
    public event Action<string>? InterfaceMismatchDetected;

    /// <summary>Raised when the client's listening port is successfully updated to a new value.</summary>
    public event Action<string>? PortUpdated;

    /// <summary>Raised for the port-reachability warnings, each once per condition: when the forwarded port is
    /// confirmed unreachable from outside (two consecutive failed checks; re-fires only after the port has
    /// tested open again), when an exit-address mismatch is first found, and when the exit-address
    /// reconnects run out.</summary>
    public event Action<string>? PortVerificationFailed;

    /// <summary>Raised when the client's own settings are found working against the synchronized port. Transition-only - it re-fires only after the settings have tested clean again.</summary>
    public event Action<string>? ClientSettingsConflictDetected;

    // The failed-cycle auto-recovery trigger: its failure streak, sustained-failure floor, offline rate
    // limiter and consecutive-recovery cap. Serialised by MainForm._updateSemaphore (same guarantee as
    // _lastKnownNatPmpManager), like every field below.
    private readonly RecoveryGate _recoveryGate;
    // The provider and adapter the gate's current streak belongs to; null before the first cycle.
    private string? _recoveryContext;
    // Consecutive restarts issued because the client reported itself disconnected, reset the moment it
    // reports anything else. Restarting only helps when the cause is the client's own state; when the
    // cause is persisted configuration - a stale network-interface binding, say - every restart re-reads
    // the same value and reports disconnected again, so an uncapped loop restarts once per cycle forever
    // while being structurally unable to fix anything. Each restart also interrupts transfers and can
    // trigger rechecks, so the loop costs the user more than doing nothing would.
    // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
    private int _consecutiveDisconnectRestarts;
    // True once a stale interface binding has been re-applied for the current stale streak. Cleared as
    // soon as the binding reads healthy, so a later drift is repaired again - but a write that does not
    // stick is not retried every cycle, which would be the same unbounded-remedy loop as the restarts.
    // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
    private bool _interfaceBindingRepairAttempted;
    // Addresses the bound adapter carried on the previous cycle, or null before the first successful
    // read. Lives here rather than on the client because the client is constructed and disposed once
    // per cycle (see the `using var manager` in RunCoreAsync), so it cannot remember anything, and the
    // question this answers is precisely "did this change since last time".
    // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
    // Keyed by the interface the addresses were read from. Without the key, a cycle that follows a
    // change of bound adapter - the user re-selecting one, or RepairInterfaceBindingAsync re-pointing
    // the token - compares one adapter's addresses against another's. That differs by construction, so
    // it reported a change that never happened and armed a rebind on the strength of it.
    // Name rather than token: the name is what the log line quotes, and the token can change while the
    // adapter stays the same, which is the stale-token case the neighbouring check already owns.
    private (string Interface, IReadOnlyList<string> Addresses)? _lastKnownInterfaceAddresses;
    // Set when the bound adapter's addresses move while the client is bound to *all* addresses on it -
    // the case where a listener can survive on the previous address with nothing else in the cycle able
    // to see it. Consumed by the port-closed escalation, which is the point at which the symptom has
    // actually been confirmed; cleared after one rebind attempt so a failed attempt escalates to the VPN
    // restart rather than repeating itself. Not a fault on its own: an ordinary reconnect sets it too.
    // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
    private bool _interfaceAddressChangedSinceRebind;
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
    // serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
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
    // How many times the VPN is reconnected for one exit-address mismatch before the user is asked to
    // change server instead. The exit address can change from one connection to the next while the
    // forward stays on a fixed one, so a reconnect may fix it, but each one drops the tunnel, and a
    // server that rarely lands on the forward address must not loop.
    private const int MaxVpnExitReconnects = 3;
    // The least time between two exit-address reconnects. A reconnect makes the next cycle start at once
    // (the recovery is a network change) and ProtonVPN reconnects on its own through 2-3 connections, which
    // took about 40 seconds when measured, so without this the next lookup can judge a connection that is
    // about to be replaced and spend a reconnect on it. Lookups still run meanwhile, but a match inside the
    // window ends only the hold, not the episode (see EndVpnExitHold).
    private static readonly TimeSpan MinVpnExitReconnectSpacing = TimeSpan.FromMinutes(2);
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
    // _lastKnownNatPmpManager). Deliberately not reset on a port change: the condition being
    // tracked is "incoming connections unreachable", which survives a new port assignment.
    // Initialised at the threshold (VerifyEveryNCycles) so the first increment in
    // ShouldVerifyThisCycle carries it past the "< VerifyEveryNCycles" guard, triggering a
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
    // The client the two fields above describe. A switch in Settings starts them over, so a warning
    // latched for one client cannot silence, or be reported as cleared by, the next one.
    private string? _conflictClientName;
    private bool _portCheckPendingConfirmation; // one unconfirmed closed result seen
    private TimeSpan? _portClosedPendingAt;     // uptime reading at that result, so the confirmation cannot follow too soon
    private bool _portConfirmedClosed;          // closed confirmed by two consecutive checks
    // Whether a confirmed-closed Warn has gone out and no open result has followed it. Outlives
    // _portConfirmedClosed, which RestartClosedPortSequence clears mid-outage, so the "reachable again"
    // line still closes every warning it answers.
    private bool _portClosedAnnounced;
    // Whether this outage already has its "Port confirmed closed" history entry. Its own flag because an
    // outage can be confirmed more than once (an exit-address hold ending restarts the sequence) and can
    // be announced before any confirmation (the mismatch balloon), so neither flag above fits.
    private bool _portClosedRecorded;
    // Uptime reading at which the current outage was first confirmed closed; null while the port is not
    // known closed. The port-closed trigger's time floor is measured from it (see
    // MaybeTriggerPortClosedRecoveryAsync). Cleared when the port verifies open or the closed-port
    // sequence restarts (RestartClosedPortSequence).
    private TimeSpan? _portClosedConfirmedAt;
    // Uptime reading at which a hold on the port-closed trigger (PortClosedRecoveryHeldBack) ends, set by
    // a cycle that held the trigger back with the count already at it, so the status file can say the
    // trigger is waiting rather than show a count past its target. Kept across cycles that do not reach
    // the trigger (an undetermined test, the VPN down), and published only while the count still stands
    // at the trigger, armed and with no mismatch holding it.
    private TimeSpan? _portClosedHoldEndsAt;
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

    // VPN exit-address check (see CheckVpnExitAddressAsync), for a provider that reports the address it
    // forwards the port on. Looked up at each confirming closed check, at any closed result while a
    // mismatch is held, and at each undetermined result; the addresses are the last ones
    // seen, kept for the status file. _vpnExitMismatch is true only when both are known and differ, and
    // while it is, the closed port is explained: port-closed recovery is held and the checks it
    // explains are not counted towards the trigger. Serialised by MainForm._updateSemaphore.
    private System.Net.IPAddress? _vpnForwardAddress;
    private System.Net.IPAddress? _vpnExitAddress;
    private bool _vpnExitMismatch;
    // Whether the last lookup ran over ProtonVPN's OpenVPN tunnel, where the messages suggest switching
    // protocol (see IVpnManager.IsProtonVpnOverOpenVpn).
    private bool _vpnProtonOverOpenVpn;
    // Reconnects made for the current mismatch (see ReconnectForExitAddressAsync), and whether the user
    // has been told they ran out. Reset by a matching lookup outside the settle window (including the one an
    // open result makes while they are spent), a mismatch on a new forward after a give-up
    // (StartNewExitEpisodeIfForwardChanged), a change of provider or adapter, a switch to a provider with no
    // forward address, or verification switched off - not by an open result alone or a failed lookup.
    private int _vpnExitReconnects;
    private bool _vpnExitGiveUpNotified;
    // The forward address the give-up was for, so a mismatch on another one is told apart as a new server
    // (see StartNewExitEpisodeIfForwardChanged).
    private System.Net.IPAddress? _vpnExitGiveUpForward;
    // Uptime reading at the last exit-address reconnect, for MinVpnExitReconnectSpacing; null before the first.
    private TimeSpan? _vpnExitLastReconnectAt;
    // Whether the current mismatch episode has had its balloon. Outlives a failed lookup, which ends the
    // hold but not the episode; cleared by a matching lookup, an open port outside the settle window, a new
    // forward after a give-up, a change of provider or adapter, or a provider with no forward.
    private bool _vpnExitAnnounced;
    // Whether the "reachable again" line has gone out for the current exit-address announcement: an open
    // result inside the settle window keeps the announcement, and the checks after it would otherwise
    // repeat the line for the same outage. Cleared whenever the announcement is made.
    private bool _exitReachableReported;
    // The provider the last successful forward-address read came from, so a provider that cannot read its
    // forward right now is told apart from a switch to one that reports none (see CheckVpnExitAddressAsync).
    private string? _vpnExitProvider;

    // Incremented each time a recovery action is dispatched (automatic or manual test). A recovery
    // is pending while this is ahead of _recoveryGenerationConsumed, and the next successful cycle
    // consumes it: a port change in that cycle gets the "after recovery" history annotation. A
    // counter rather than a flag so a cycle consumes only the recoveries dispatched before it
    // started - clearing a flag at cycle end would also wipe one dispatched mid-cycle. Static
    // because DispatchRecoveryAsync is shared by the sync loop's instance paths and the static
    // manual-test entry point (TestRecoveryAsync); the app runs a single sync service, and
    // Interlocked/Volatile cover the manual test bumping it from the UI thread.
    private static int _recoveryGeneration;
    // Environment.TickCount64 at the last recovery dispatch of any kind, stamped before and after the
    // helper call; 0 before the first. A ProtonVPN service restart goes through 2-3 connections of its own
    // whichever trigger sent it, so the exit-address reconnects wait on any recent recovery, not only on
    // their own (see ReconnectForExitAddressAsync). Static for the same reason as _recoveryGeneration.
    private static long _lastRecoveryDispatchTick;

    // _recoveryGeneration as read at cycle start, the generation the last successful cycle consumed,
    // and whether the two differed. Taken at cycle start so a recovery dispatched mid-cycle
    // (port-closed trigger fires after the port update step) is never consumed by the same
    // cycle that dispatched it. Serialised by MainForm._updateSemaphore. Instance fields beside a
    // static counter are safe because MainForm creates this service once, in a readonly field.
    private int _recoveryGenerationAtStart;
    private int _recoveryGenerationConsumed;
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
    // Per-client behaviour flags (interface-mismatch warning, restart-on-disconnect, binding repair)
    // live in the Client block as well, read from nullable keys on the ClientRegistry entry so a
    // client that does not carry the setting reads false without a special case here.
    private sealed record AppConfig(
        string VpnProvider,
        string VpnAdapterName,
        int StaticPort,
        int UpdateInterval,
        string ClientName,
        ClientConfig Client,
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
        IVpnManager? VpnManager,
        bool WarnOnInterfaceMismatch,
        bool RestartOnDisconnect,
        bool FixInterfaceBinding,
        bool NotifyOnPortUpdate,
        bool VerifyPort,
        bool PortClosedRecoveryEnabled,
        int PortClosedRecoveryTriggerChecks,
        int UpdateIntervalSeconds
    )
    {
        /// <summary>
        /// Whether a rebind can happen from this cycle at all, and therefore whether it is honest to
        /// mention one. <c>fixInterfaceBinding</c> alone is not enough: the only route to
        /// <see cref="TryRebindClientAddressAsync"/> is VerifyPortAsync then
        /// MaybeTriggerPortClosedRecoveryAsync, and that route is closed by any of four things - the
        /// repair being off, <see cref="VerifyPort"/> being off, <see cref="PortClosedRecoveryEnabled"/>
        /// being off, or <see cref="VpnManager"/> being <see langword="null"/>, which is the default-port
        /// fallback where verification is skipped because a disconnected tunnel would read closed as a
        /// matter of course. With any of them true the port is never tested, the rebind is unreachable,
        /// and a line promising or cancelling one is false.
        /// <para>Deliberately one predicate rather than a condition repeated at each message site. The
        /// two sites each spelled it out separately and drifted: the promise was corrected twice before
        /// the third condition was noticed, and the fourth only after that. A guard duplicated at two
        /// call sites will drift; a named one cannot.</para>
        /// </summary>
        public bool RebindReachable =>
            VpnManager is not null && FixInterfaceBinding && VerifyPort && PortClosedRecoveryEnabled;
    }

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
        // origin and runs out early by the cycle's length - up to about a minute a client restart takes,
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
        // panel names the cause - collapsing them would make the row say "cannot confirm internet"
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
        // When the trigger's count has reached its threshold but a time hold keeps it back (the port has
        // not stayed closed long enough, or an exit-address reconnect is still settling): the instant
        // that hold ends. Null otherwise.
        public const string PortClosedRecoveryHeldUntil = "portClosedRecoveryHeldUntil";
        // The VPN exit-address check: the address the port is forwarded on, the address traffic was
        // last seen leaving from, and whether they differ. The addresses are the last ones looked up
        // (null until a closed or undetermined port test prompts a lookup, and for providers that
        // report no forward address); Mismatch is true only while both are known and differ, which is also when
        // port-closed recovery is held.
        public const string VpnForwardAddress = "vpnForwardAddress";
        public const string VpnExitAddress = "vpnExitAddress";
        public const string VpnExitAddressMismatch = "vpnExitAddressMismatch";
        // Reconnects made for the current mismatch, and the most that will be made before the user is
        // asked to change server. Published as a pair so a reader never hardcodes the limit.
        public const string VpnExitReconnects = "vpnExitReconnects";
        public const string VpnExitReconnectsMax = "vpnExitReconnectsMax";
        // True once the last reconnect's session was looked at and still did not match, so the user has
        // been asked to change server. A separate flag because the count reaches the limit as soon as
        // the last reconnect is dispatched, before its result is known.
        public const string VpnExitReconnectGaveUp = "vpnExitReconnectGaveUp";
        // True when the last lookup ran over ProtonVPN's OpenVPN tunnel, where mismatches were seen and
        // the advice is to switch ProtonVPN to Proton WireGuard (UDP).
        public const string VpnProtonOverOpenVpn = "vpnProtonOverOpenVpn";

        // Values for the Status key live in the public SyncStatusValues (shared with the Status panel).
    }

    /// <summary>Creates the sync service. MainForm makes one, at startup, for the life of the app.</summary>
    public PortSyncService()
    {
        // In the constructor rather than at the field: the gate measures its sustained-failure floor on
        // this service's uptime clock, and a field initializer cannot read another instance field.
        _recoveryGate = new RecoveryGate(_uptime);
    }

    /// <summary>Runs one port sync cycle and returns the configured update interval in seconds.
    /// <paramref name="networkChangeTriggered"/> marks a cycle started by the network-change
    /// re-sync; a port change it detects is annotated accordingly in the port history.</summary>
    public async Task<int> RunAsync(bool networkChangeTriggered = false, CancellationToken cancellationToken = default)
    {
        _networkChangeTriggered = networkChangeTriggered;
        _recoveryGenerationAtStart = Volatile.Read(ref _recoveryGeneration);
        _recoveryPendingThisCycle = _recoveryGenerationAtStart != _recoveryGenerationConsumed;
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
            [StatusKeys.PortClosedRecoveryArmed] = true,
            [StatusKeys.PortClosedRecoveryHeldUntil] = null,
            [StatusKeys.VpnForwardAddress] = null,
            [StatusKeys.VpnExitAddress] = null,
            [StatusKeys.VpnExitAddressMismatch] = false,
            [StatusKeys.VpnExitReconnects] = 0,
            [StatusKeys.VpnExitReconnectsMax] = MaxVpnExitReconnects,
            [StatusKeys.VpnExitReconnectGaveUp] = false,
            [StatusKeys.VpnProtonOverOpenVpn] = false
        };

        // Captured so the finally can publish NextSyncAt against the wait this cycle actually
        // returned - including the shortened startup-grace poll - rather than re-reading the
        // configured interval. When RunCoreAsync throws, the catch falls back to the configured
        // interval so the retry cadence the user chose still holds; the registry read itself
        // cannot throw (the getters return defaults), so the catch cannot throw either.
        int nextWaitSeconds = AppConstants.DefaultUpdateIntervalSeconds;
        try
        {
            nextWaitSeconds = await RunCoreAsync(status, cancellationToken).ConfigureAwait(false);
            LogManager.Instance.ClearLogState(UnexpectedErrorDetailsStateKey);
            return nextWaitSeconds;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The status message stays one short line because the tray tooltip shows it; the full exception
            // goes to the log as Info, since an exception reaching here is a bug and its type and stack are
            // what a report needs. Info rather than Error: the line above already counts once towards the badge.
            // Once per distinct exception: a persistent bug would otherwise repeat the whole stack every
            // cycle. The key is cleared by the next cycle that completes, so a recurrence is logged again.
            SetSyncResult(status, false, $"An unexpected error occurred: {ex.Message}");
            LogManager.Instance.LogStateChange(UnexpectedErrorDetailsStateKey, $"Details of the unexpected error: {ex}", LogLevel.Info);
            nextWaitSeconds = RegistrySettingsManager.GetClampedUpdateIntervalSeconds();
            status[StatusKeys.UpdateIntervalSeconds] = nextWaitSeconds;
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
                status[StatusKeys.RecoveryHoldUntil] = _recoveryGate.GetRecoveryHoldUntil();
                // Read here rather than mid-cycle: the streak is incremented by the failure paths
                // inside RunCoreAsync, so only the finally sees this cycle's final count.
                status[StatusKeys.RecoveryFailedCycles] = _recoveryGate.FailedCycles;
                status[StatusKeys.RecoverySustainedUntil] = _recoveryGate.SustainedUntil;
                // Read here with the streak, for the same reason: the dispatch path inside RunCoreAsync
                // is what advances the count, so only the finally sees this cycle's value.
                status[StatusKeys.RecoverySuspended] = _recoveryGate.Suspended;
                // Read here for the same reason as the failure streak above: both are mutated by the
                // verification path inside RunCoreAsync, so only the finally sees this cycle's values.
                status[StatusKeys.PortClosedRecoveryChecks] = _confirmedClosedCount;
                status[StatusKeys.PortClosedRecoveryArmed] = _portClosedRecoveryArmed;
                // An absolute instant, like RecoverySustainedUntil, so the panel can count it down.
                status[StatusKeys.PortClosedRecoveryHeldUntil] = PortClosedHoldStillApplies(status) && _portClosedHoldEndsAt is { } holdEnd
                    ? DateTimeOffset.Now + (holdEnd - _uptime.Elapsed)
                    : null;
                status[StatusKeys.VpnForwardAddress] = _vpnForwardAddress?.ToString();
                status[StatusKeys.VpnExitAddress] = _vpnExitAddress?.ToString();
                status[StatusKeys.VpnExitAddressMismatch] = _vpnExitMismatch;
                status[StatusKeys.VpnExitReconnects] = _vpnExitReconnects;
                status[StatusKeys.VpnExitReconnectGaveUp] = _vpnExitGiveUpNotified;
                status[StatusKeys.VpnProtonOverOpenVpn] = _vpnProtonOverOpenVpn;
                StatusManager.Write(status);
                string? outcome = status[StatusKeys.Status] as string;
                LogCycleOutcome(outcome);
                if (outcome != SyncStatusValues.Skipped)
                    SessionStats.RecordSync(outcome == SyncStatusValues.Success);
                // A successful cycle concludes a pending recovery whether or not the port
                // changed (the annotation, if due, was applied during the cycle). Only the
                // generation read at cycle start is consumed, so a recovery dispatched mid-cycle
                // stays pending for the next one.
                if (outcome == SyncStatusValues.Success && _recoveryPendingThisCycle)
                    _recoveryGenerationConsumed = _recoveryGenerationAtStart;
                LaunchPostUpdateCommandIfChanged(status);
                RaiseSyncCompleted(status);
            }
        }
    }

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
        var cfg = ReadConfig();
        return CreateManagedClient(cfg);
    }

    /// <summary>
    /// Builds the currently-configured VPN manager fresh for read-only callers (e.g.
    /// <see cref="DiagnosticsService"/>), without the sync loop's status side effects or NAT-PMP
    /// fallback state. Returns <see langword="null"/> when the provider is Disabled/unrecognized, the
    /// NAT-PMP or Static port adapter is unset, or the NAT-PMP adapter cannot currently be reached.
    /// Mirrors the provider dispatch in <see cref="CreateVpnManagerAsync"/>: stateless providers come
    /// from the shared <see cref="CreateStatelessVpnManager"/>; the NAT-PMP and Static port arms are
    /// per-path (plain probe here, sticky fallback there for NAT-PMP; no status reporting here for
    /// Static port) - keep them in step when adding a non-stateless provider.
    /// </summary>
    internal static Task<IVpnManager?> BuildActiveVpnManagerAsync(CancellationToken cancellationToken = default)
    {
        string provider = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider);
        string adapter = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAdapterName);
        return BuildVpnManagerForAsync(provider, adapter, cancellationToken);
    }

    // Core of BuildActiveVpnManagerAsync with the provider selection passed in, so the Settings
    // form's recovery test can build from its in-form (possibly unsaved) selection - the same
    // convention the client Test buttons follow.
    private static async Task<IVpnManager?> BuildVpnManagerForAsync(string provider, string? vpnAdapterName, CancellationToken cancellationToken)
    {
        if (CreateStatelessVpnManager(provider) is { } manager)
            return manager;

        if (provider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(vpnAdapterName)) return null;
            return await NatPmpManager.TryCreateForAdapterAsync(vpnAdapterName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (provider.Equals(RegistrySettingsManager.VpnProviderStaticPort, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(vpnAdapterName)) return null;
            // The saved port, also for the Settings recovery test: recovery acts on the adapter only,
            // so an unsaved port edit on screen cannot change what the test does.
            return new StaticPortManager(vpnAdapterName,
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyStaticPort));
        }
        return null; // Disabled or unrecognized
    }

    /// <summary>
    /// Dispatches the recovery action for the given provider selection on demand (the Settings
    /// form's Auto-recovery Test button). Runs the exact same action as automatic recovery (VPN
    /// service restart for ProtonVPN, PIA or a NAT-PMP / Static port adapter that belongs to one of them
    /// with its service installed,
    /// adapter cycle for any other adapter) so the recovery chain - helper service,
    /// service discovery, VPN client relaunch - can be verified before a real failure needs it.
    /// Returns <see langword="false"/> when nothing could be dispatched (provider disabled or
    /// unrecognized, adapter unreachable, or no recovery target); the outcome of a dispatched
    /// action is reported through the log exactly like an automatic recovery.
    /// </summary>
    public static async Task<bool> TestRecoveryAsync(string vpnProvider, string? vpnAdapterName, CancellationToken cancellationToken = default)
    {
        IVpnManager? vpnManager = await BuildVpnManagerForAsync(vpnProvider, vpnAdapterName, cancellationToken).ConfigureAwait(false);
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

    // Launches the post-update command after the status file has been written, so a script that reads
    // the status file (e.g. an email notifier) sees this cycle's result rather than the previous cycle's.
    // Fires whenever the port was changed this cycle, including when the client restart after the write
    // failed: the change is real, the next cycle reads it as already matching so this is the only chance,
    // and the status file the script reads carries the failure. Read fresh from the registry since the
    // cycle's SyncConfig is not in scope in RunAsync's finally.
    private static void LaunchPostUpdateCommandIfChanged(Dictionary<string, object?> status)
    {
        if (status[StatusKeys.PortChanged] is not true)
            return;
        string postUpdateCmd = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd);
        if (!string.IsNullOrWhiteSpace(postUpdateCmd))
            RunPostUpdateCommand(postUpdateCmd);
    }

    // Maps the finalized status dictionary to a tray SyncState and raises the SyncCompleted event.
    private void RaiseSyncCompleted(Dictionary<string, object?> status)
    {
        string outcome = status[StatusKeys.Status] as string ?? SyncStatusValues.Error;
        bool vpnConnected = status[StatusKeys.VpnConnected] is true;
        int? port = status[StatusKeys.ClientPort] as int?;
        string message = status[StatusKeys.Message] as string ?? string.Empty;
        string? provider = status[StatusKeys.VpnProvider] as string;
        bool isDisabled = string.Equals(provider, RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);
        // An unrecognized provider value (only reachable via a manual registry edit) is a
        // configuration error, not a disconnection - surface it as Error so the tray shows
        // red with the "not recognized" message rather than orange "VPN not connected".
        bool isKnownProvider = isDisabled || VpnProviderRegistry.IsRecognizedProvider(provider);

        // The cycle outcome decides before the VPN state does. VpnConnected is only set once the
        // provider check has run, so an error raised earlier (no adapter or port configured for
        // Static port or NAT-PMP, an unexpected exception) would otherwise read as a disconnection:
        // orange "VPN not connected" with the reason visible nowhere but the log. The same holds
        // when the VPN is down and the default-port fallback itself fails (client not running, port
        // write refused): the fallback the user configured did not work, which is red, not orange.
        // Skipped outcomes (VPN down with no default port, startup grace, disabled) are unchanged.
        SyncState state;
        if (isDisabled) state = SyncState.Disabled;
        else if (!isKnownProvider || outcome == SyncStatusValues.Error) state = SyncState.Error;
        else if (_waitingForVpnThisCycle) state = SyncState.WaitingForVpn;
        else if (!vpnConnected) state = SyncState.VpnDisconnected;
        else state = SyncState.Synced;

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

        var cfg = ReadConfig();
        int defaultPort = GetDefaultPort(cfg);
        LogConfigDebug(cfg);
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
            // A null means the outcome is already set: sync disabled, a missing setting or an unknown provider.
            return cfg.UpdateInterval;

        var (forceStart, restart, restartOnDisconnect, warnOnInterfaceMismatch) = GetClientBehaviorConfig(cfg);

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
                VpnManager: syncVpnManager,
                WarnOnInterfaceMismatch: warnOnInterfaceMismatch,
                RestartOnDisconnect: restartOnDisconnect,
                FixInterfaceBinding: cfg.Client.FixInterfaceBinding,
                NotifyOnPortUpdate: cfg.NotifyOnPortUpdate,
                VerifyPort: cfg.VerifyPortAfterSync,
                PortClosedRecoveryEnabled: cfg.PortClosedRecoveryEnabled,
                PortClosedRecoveryTriggerChecks: cfg.PortClosedRecoveryTriggerChecks,
                UpdateIntervalSeconds: cfg.UpdateInterval),
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

        // A down tunnel verifies nothing, and the first check after it comes back is usually the client still
        // reconnecting: start the closed-port sequence (and its time floor) again, as an exit-address hold
        // ending does, so that check cannot complete a count from before the outage and fire a restart.
        ForceVerifyAfterOutage();
        RestartClosedPortSequence();
        string disconnectedMsg = $"{vpnManager.ProviderName} is not connected";
        await _recoveryGate.RegisterFailureAndTryRecoveryAsync(
            disconnectedMsg, LogLevel.Info,
            vpnManager.GetRecoveryAction(), vpnManager.GetRecoveryTarget(), vpnManager.ProviderName,
            RecoverySettingsOf(cfg), cancellationToken).ConfigureAwait(false);

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
            // No port to verify either, and the connection is likely bouncing (PIA still assigning one):
            // the closed-port sequence starts again, as when the VPN is found down.
            ForceVerifyAfterOutage();
            RestartClosedPortSequence();
            // The provider has told us there is no forwarded port to be had until the user changes
            // something. That is a configuration state, not a fault, so it is reported and re-checked
            // on the normal cadence but never counted toward auto-recovery: a service restart cannot
            // create a forward the region or the account settings do not offer, and dispatching one
            // every few cycles tears the tunnel down repeatedly for no possible benefit.
            if (vpnManager.PortForwardingUnavailable)
            {
                // The streak is neither advanced nor cleared here, and the difference matters. Not
                // advancing is what keeps this condition from driving recovery, which was the whole
                // requirement. Clearing it - which this did at first - is a stronger claim than the
                // reasoning supports, and PIA gives a reason to care: it reports `Inactive` both for
                // "port forwarding is off" and for "PIA is disconnected", and the connected check and
                // the port read are two separate piactl invocations. A tunnel dropping between them
                // lands here while genuinely broken, and clearing the streak on that reading would
                // discard evidence gathered from real failures, so a flapping tunnel could keep
                // resetting its way below the threshold and never be recovered.
                SetSyncResult(status, false,
                    $"{vpnManager.ProviderName} reports port forwarding is unavailable - check that port forwarding " +
                    "is enabled in the VPN client and that the connected region supports it (auto-recovery does not run for this)",
                    LogLevel.Warn, PortForwardingUnavailableStateKey);
                return new TargetPortResult(cfg.UpdateInterval, 0, null);
            }

            await HandlePortDetectionFailureAsync(vpnManager, cfg, cancellationToken).ConfigureAwait(false);
            // Status only, no second log line. HandlePortDetectionFailureAsync has already reported this
            // condition at Warn, and its line is the better of the two - it carries the streak count and
            // the trigger threshold, which this message lacks. Logging both doubled the tray's
            // unviewed-warning count for every failed cycle, and this file's own rule is that repeating
            // a Warn costs more than a duplicated line because of exactly that.
            //
            // It also read as a false verdict on the recovery cycle. SetSyncResult runs after the
            // awaited recovery returns, so the line landed after "Recovery completed" and looked like
            // the recovery had failed, when it was only this cycle's outcome surfacing late.
            //
            // Set directly rather than through SetSyncResult, matching the sibling failure path
            // above (VPN disconnected) which already does this. The
            // message still reaches the tray tooltip and the Status panel through the status file;
            // only the duplicate log entry goes. LogCycleOutcome still emits the terminal Error line
            // from the status value set here.
            status[StatusKeys.Status] = SyncStatusValues.Error;
            status[StatusKeys.Message] = $"Failed to determine {vpnManager.ProviderName} port";
            return new TargetPortResult(cfg.UpdateInterval, 0, null);
        }

        _recoveryGate.ResetFailureStreak(); // Reset only after a successful port fetch
        _recoveryGate.ResetOfflineRecoveryBackoff(); // A working cycle ends any offline streak, so the next one starts fresh
        _recoveryGate.ResetRecoveryCap();          // and ends any run of consecutive recoveries
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

    // Reads all configuration values from the registry into a single AppConfig record.
    // The active client's section is no longer returned: nothing outside this method needs it now
    // that the three exclusive settings are read here through the ClientRegistry row like the rest.
    private static AppConfig ReadConfig()
    {
        // Shared with the Status panel's fallback due-time derivation - see
        // GetClampedUpdateIntervalSeconds for why the clamp lives in one place. The upper bound is
        // load-bearing here: MainForm's two delay paths multiply this by MillisecondsPerSecond, which
        // overflows int above ~24.8 days and produces a negative delay that kills the main loop.
        int updateInterval = RegistrySettingsManager.GetClampedUpdateIntervalSeconds();

        int vpnAutoRecoveryTriggerCycles = Math.Max(1, RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryTriggerCycles));

        string clientName = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyClient);
        var activeClient = ClientRegistry.Resolve(clientName);

        // Only the active client's section is read; every client section uses the same key names, so
        // the section is the only thing that varies here. A nullable key on the ClientRegistry entry
        // marks a setting a client may not have at all (Deluge and Nicotine+ have no user name;
        // Nicotine+ is never restarted), and a null key reads as the setting's off value. The password
        // is DPAPI-decrypted via GetEncryptedValue, same as the per-client GetXxxPassword helpers.
        var clientConfig = new ClientConfig(
            Url: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.UrlKey),
            UserName: activeClient.UserNameKey is not null ? RegistrySettingsManager.GetValue(activeClient.Section, activeClient.UserNameKey) : string.Empty,
            Password: RegistrySettingsManager.GetEncryptedValue(activeClient.Section, activeClient.PasswordKey),
            ProcessName: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.ProcessNameKey),
            ExePath: RegistrySettingsManager.GetValue(activeClient.Section, activeClient.ExePathKey),
            Restart: activeClient.RestartKey is not null && RegistrySettingsManager.GetBool(activeClient.Section, activeClient.RestartKey),
            ForceStart: RegistrySettingsManager.GetBool(activeClient.Section, activeClient.ForceStartKey),
            DefaultPort: RegistrySettingsManager.GetInt(activeClient.Section, activeClient.DefaultPortKey),
            // Same null-key idiom as Restart above: a client without the key does not have the setting,
            // so it reads false rather than borrowing another client's value from a hardcoded section.
            WarnOnInterfaceMismatch: activeClient.WarnOnInterfaceMismatchKey is { } warnKey
                && RegistrySettingsManager.GetBool(activeClient.Section, warnKey),
            RestartOnDisconnect: activeClient.RestartOnDisconnectKey is { } restartOnDisconnectKey
                && RegistrySettingsManager.GetBool(activeClient.Section, restartOnDisconnectKey),
            FixInterfaceBinding: activeClient.FixInterfaceBindingKey is { } bindingKey
                && RegistrySettingsManager.GetBool(activeClient.Section, bindingKey));

        return new AppConfig(
            VpnProvider: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider),
            VpnAdapterName: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAdapterName),
            StaticPort: RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyStaticPort),
            UpdateInterval: updateInterval,
            ClientName: clientName,
            Client: clientConfig,
            PostUpdateCommand: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd),
            VpnAutoRecoveryEnabled: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryEnabled),
            VpnAutoRecoveryTriggerCycles: vpnAutoRecoveryTriggerCycles,
            NotifyOnPortUpdate: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNotifyOnPortUpdate),
            VerifyPortAfterSync: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVerifyPortAfterSync),
            PortClosedRecoveryEnabled: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryEnabled),
            PortClosedRecoveryTriggerChecks: Math.Max(1, RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryTriggerChecks)),
            WaitForVpnOnStartup: RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyWaitForVpnOnStartup)
        );
    }

    // Dumps the active AppConfig to the log file when debug mode is enabled.
    // Three lines (general / active client / extra) keep each section independently greppable.
    // The client line is built from the active client's ClientRegistry key names, so it stays in
    // step with whatever ReadConfig read - no per-client branch.
    private static void LogConfigDebug(AppConfig cfg)
    {
        if (!LogManager.Instance.DebugMode) return;

        LogManager.Instance.LogDebug(
            $"PortSyncService.RunCoreAsync [general]: {RegistrySettingsManager.KeyVpnProvider}={cfg.VpnProvider}, " +
            $"{RegistrySettingsManager.KeyVpnAdapterName}={cfg.VpnAdapterName}, " +
            $"{RegistrySettingsManager.KeyStaticPort}={cfg.StaticPort}, " +
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
            $"{ci.DefaultPortKey}={cfg.Client.DefaultPort}" +
            // The three exclusive settings, in the same null-key idiom as UserNameKey and RestartKey
            // above rather than a per-client branch. The branch that used to sit here named the
            // clients twice - once to read the value and once to print it - and the two could drift:
            // Nicotine+'s warning was honoured but never printed until 2.6.8. Now a client prints
            // exactly the settings its ClientRegistry row declares, so the dump cannot disagree with
            // what GetClientBehaviorConfig acted on.
            (ci.WarnOnInterfaceMismatchKey is not null
                ? $", {ci.WarnOnInterfaceMismatchKey}={cfg.Client.WarnOnInterfaceMismatch}" : string.Empty) +
            (ci.RestartOnDisconnectKey is not null
                ? $", {ci.RestartOnDisconnectKey}={cfg.Client.RestartOnDisconnect}" : string.Empty) +
            (ci.FixInterfaceBindingKey is not null
                ? $", {ci.FixInterfaceBindingKey}={cfg.Client.FixInterfaceBinding}" : string.Empty);
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
    // NAT-PMP and Static port do); then add the keyword in VpnProviderRegistry.IsRecognizedProvider, an entry in
    // VpnProviderRegistry.KnownProviders (when service-restart recovery applies), and the value in
    // SettingsForm's cboVpnProvider list.
    // A failure streak describes one provider and adapter: switching either (or to Disabled) starts the
    // failed-cycle trigger afresh, rather than letting the new setup inherit a streak whose start lies far
    // in the past and fire recovery on its first failure.
    private void TrackRecoveryContext(AppConfig cfg)
    {
        string recoveryContext = $"{cfg.VpnProvider}|{cfg.VpnAdapterName}";
        if (_recoveryContext is not null && !string.Equals(_recoveryContext, recoveryContext, StringComparison.OrdinalIgnoreCase))
        {
            _recoveryGate.ResetForNewContext();
            // The closed-port sequence starts afresh too: a confirmation, count or time floor from the old
            // setup would let the new one's first closed check (its client still rebinding) trigger recovery.
            RestartClosedPortSequence();
            // And the port-closed trigger is armed again: a recovery that ran for the old setup's outage says
            // nothing about the new one, which would otherwise be told recovery "has already run".
            _portClosedRecoveryArmed = true;
            // So does an exit-address episode: its reconnects, give-up and announcement were for the old
            // setup, and two setups can report the same provider name (a NAT-PMP adapter and Static port on
            // it), so the provider check in EndVpnExitHoldWithoutForward cannot tell them apart.
            ClearVpnExitMismatch();
        }
        _recoveryContext = recoveryContext;
    }

    private async Task<IVpnManager?> CreateVpnManagerAsync(AppConfig cfg, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        TrackRecoveryContext(cfg);

        // Re-arms each provider's standing warnings (a missing setting, the NAT-PMP lease being shorter
        // than the interval) while another provider is selected, so switching away and back with the
        // condition still present logs it again. Placed before
        // the Disabled return so a round trip through Disabled re-arms them too.
        if (!cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderStaticPort, StringComparison.OrdinalIgnoreCase))
            LogManager.Instance.ClearLogState(StaticPortStateKey);
        if (!cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
        {
            LogManager.Instance.ClearLogState(NatPmpAdapterStateKey);
            LogManager.Instance.ClearLogState(NatPmpLeaseStateKey);
        }

        if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase))
        {
            // Nothing fails while sync is disabled, so no streak survives an hour of it either.
            _recoveryGate.ResetForNewContext();
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

        if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderStaticPort, StringComparison.OrdinalIgnoreCase))
            return CreateStaticPortVpnManager(cfg, status);

        // Transition-only: the provider name comes from the registry and stays wrong until Settings is
        // corrected, so the same line every cycle adds nothing. The status below still reports Error on
        // every cycle, which is the signal that should persist.
        LogManager.Instance.LogStateChange(VpnProviderStateKey,
            $"VPN provider '{cfg.VpnProvider}' is not recognized - check Settings", LogLevel.Warn);
        status[StatusKeys.Status] = SyncStatusValues.Error;
        status[StatusKeys.Message] = $"VPN provider '{cfg.VpnProvider}' is not recognized";
        return null;
    }

    // Builds the Static port manager, or reports what is missing. Refused here rather than left to
    // the usable-port guard in the cycle: that guard falls through to the no-port branch, which counts
    // toward auto-recovery, and restarting the VPN cannot fix a setting the user has not entered.
    private static StaticPortManager? CreateStaticPortVpnManager(AppConfig cfg, Dictionary<string, object?> status)
    {
        string? missing = null;
        if (string.IsNullOrWhiteSpace(cfg.VpnAdapterName))
            missing = "No VPN adapter configured";
        else if (!AppConstants.IsUsablePort(cfg.StaticPort))
            missing = "No forwarded port configured";
        if (missing is not null)
        {
            // Transition-only, like the unset NAT-PMP adapter: it stays unset until Settings changes.
            SetSyncResult(status, false, $"{missing} for {RegistrySettingsManager.VpnProviderStaticPort} - open Settings and set it",
                stateKey: StaticPortStateKey);
            return null;
        }
        LogManager.Instance.ClearLogState(StaticPortStateKey);
        return new StaticPortManager(cfg.VpnAdapterName, cfg.StaticPort);
    }

    // Resolves the NAT-PMP VPN manager for the configured adapter, handling the disconnected
    // fallback cases and auto-recovery triggering when no adapter is reachable.
    private async Task<IVpnManager?> CreateNatPmpVpnManagerAsync(AppConfig cfg, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cfg.VpnAdapterName))
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
            !_lastKnownNatPmpManager.ProviderName.Equals(cfg.VpnAdapterName, StringComparison.OrdinalIgnoreCase))
            _lastKnownNatPmpManager = null;

        var selected = await NatPmpManager.TryCreateForAdapterAsync(cfg.VpnAdapterName, cancellationToken: cancellationToken).ConfigureAwait(false);

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
            // Its forward address is from an earlier cycle: the probe that would refresh it just failed,
            // and the adapter can still be up (a reconnect to another server behind the same gateway).
            _lastKnownNatPmpManager.ForgetForwardAddress();
            return _lastKnownNatPmpManager;
        }

        // No previous knowledge of this adapter: no gateway has answered since the app started (a boot with
        // the VPN down), or the adapter setting just changed. A stand-in that reads the adapter's real state
        // sends the cycle through the same handling as the cached manager: disconnected when the adapter is
        // down (the startup grace hold, the failure count and recovery, the default-port fallback), and a
        // connected VPN with no port when it is up but its gateway is missing or silent.
        LogManager.Instance.LogDebug("PortSyncService.CreateNatPmpVpnManagerAsync: No gateway answered and none seen before - using a stand-in manager");
        return new MissingNatPmpAdapter(cfg.VpnAdapterName);
    }

    // The NAT-PMP manager for an adapter whose gateway has not answered in this run, so there is none to
    // build a real one from. Reports the adapter's real state, never a port, and carries its recovery
    // action; never kept as _lastKnownNatPmpManager, which must be a real manager with renewal state.
    private sealed class MissingNatPmpAdapter(string adapterName) : IVpnManager
    {
        private (string Target, string Action)? _recovery;
        private (string Target, string Action) Recovery => _recovery ??= VpnAdapter.GetRecovery(adapterName);

        public string ProviderName => adapterName;
        public bool IsVpnConnected() => VpnAdapter.IsUp(adapterName, "MissingNatPmpAdapter.IsVpnConnected");
        public Task<int?> GetVpnPortAsync(CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public string? GetRecoveryTarget() => Recovery.Target;
        public string GetRecoveryAction() => Recovery.Action;
        public bool IsAdapterMatch(string interfaceName) => VpnRegistryConfig.AdapterNamesMatchExactly(adapterName, interfaceName);
    }

    // Creates the active IManagedClient via its ClientRegistry factory from the config block
    // ReadConfig built for the active client. Resolve defaults to qBittorrent when the value is
    // unrecognized (matching ReadConfig).
    private static IManagedClient CreateManagedClient(AppConfig cfg) =>
        ClientRegistry.Resolve(cfg.ClientName).Factory(cfg.Client);

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
        //
        // The address check that follows is the half neither check above can see: the address under a
        // binding whose name and token are both still correct - see CheckInterfaceAddressAsync. It is
        // an && rather than a second statement because "the token is correct" is a precondition of
        // reading the address, not a description of the usual case: under a stale token qBittorrent
        // reports the adapter as having no addresses at all, which the address check would read as a
        // real loss. The gate is what the comment on that method has always claimed and never enforced.
        if (manager is QBittorrentClient qbClient
            && await CheckAndRepairInterfaceBindingAsync(qbClient, currentInterfaceName, config, cancellationToken).ConfigureAwait(false))
        {
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
            // The write or the restart after it failed - skip the RestartOnDisconnect check. A failed write
            // is retried next cycle; a failed restart is not, since the port then already matches.
            return;
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
        // With verification off nothing refreshes the exit-address state, so a mismatch found earlier would
        // stay in the status file indefinitely. Not on a disconnected cycle: the exit-address reconnects
        // cause those, and clearing there would restart their count on every reconnect.
        else if (!config.VerifyPort)
            ClearVpnExitMismatch();

        SetSyncResult(status, true, "Sync cycle completed");
    }

    // Applies the port update, records it in the port history (with the cause: VPN-assigned, the
    // configured Static port, or the default-port fallback), and raises the optional tray notification.
    // Returns false when the update failed and the caller skips the cycle's remaining steps: a failed
    // write, which the next cycle retries, or a failed restart after a successful write, which it does
    // not, since it reads the port as already matching (see below).
    private async Task<bool> UpdatePortAndNotifyAsync(IManagedClient manager, int targetPort, int previousPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
    {
        // A restart that fails after a successful write still leaves the port changed - and the next cycle
        // reads it as already matching - so the history entry and balloon below are recorded whenever the
        // port was written; only a failed write skips them. The cycle still ends as an error either way.
        bool applied = await ApplyPortUpdateAsync(manager, targetPort, config, status, cancellationToken).ConfigureAwait(false);
        if (!applied && status[StatusKeys.PortChanged] is not true)
            return false;
        // A port write rebuilds the client's listen sockets from a fresh enumeration of the adapter,
        // exactly as an address write does - measured, not assumed. So it has already done what the
        // rebind would do, and any listener stranded on a previous address is gone. Clearing the arm
        // here keeps it honest: without this, an ordinary reconnect (new address *and* a new forwarded
        // port, which is the common case) would stay armed after the port write had already fixed it,
        // and spend that arm later on a closed port it could not possibly explain.
        //
        // Announced when it actually cancels something, because the address-change line logged earlier
        // this cycle told the user a rebind was coming. Without this the log showed that expectation
        // being set and then a VPN restart with no rebind and no reason - which reads as the feature
        // failing, not as it correctly standing down. Only logged when an arm was really spent, so a
        // routine port change on a client whose address never moved stays quiet.
        //
        // Gated on the same facts RebindClause promises the rebind on - reachable by settings, and
        // port-closed recovery still armed - not on the arm alone. CheckInterfaceAddressAsync sets the
        // arm whatever fixInterfaceBinding says, and whether or not recovery for this outage has
        // already run, so in either case this cycle would log "will not be rebound" and then, seconds
        // later, "the pending rebind is no longer needed" - announcing the cancellation of something
        // that was never pending. The client test matters for a narrower reason: only the qBittorrent
        // path sets the arm and nothing clears it when the active client changes, so a switch to
        // another client between the address change and the next port write would name that client
        // in a message about a rebind that was never pending for it.
        if (_interfaceAddressChangedSinceRebind && config.RebindReachable && _portClosedRecoveryArmed && manager is QBittorrentClient)
        {
            LogManager.Instance.LogMessage(
                $"The port write rebuilt {manager.ClientName}'s listen sockets, so the pending rebind for the " +
                "adapter's address change is no longer needed",
                LogLevel.Info);
        }
        _interfaceAddressChangedSinceRebind = false;
        // Cause annotation: recovery takes precedence over network change (a recovery usually
        // produces a network change too, and the recovery is the root cause).
        string cause = string.Empty;
        if (_recoveryPendingThisCycle) cause = " - after recovery";
        else if (_networkChangeTriggered) cause = " - after network change";
        // A Static port is entered by the user, not assigned by the VPN at runtime, so the history
        // must not credit the VPN with a change the user made.
        string reason = config.VpnManager switch
        {
            null => $"Default port applied (was {previousPort})",
            StaticPortManager => $"Configured port applied (was {previousPort})",
            _ => $"VPN assigned new port (was {previousPort})"
        };
        PortHistoryManager.Append(PortHistoryKind.PortChanged, targetPort, reason + cause);
        if (config.NotifyOnPortUpdate)
            NotifyPortUpdated(manager.ClientName, targetPort);
        return applied;
    }

    // Throttles the reachability test: Transmission's and Deluge's tests contact their projects'
    // online check services, so testing every cycle would be wasteful. Tests run when the port
    // changed this cycle, every cycle while a result awaits confirmation, every cycle while
    // confirmed-closed AND port-closed recovery is enabled and still armed (so the recovery counter
    // advances each cycle up to the trigger), every cycle while an exit-address mismatch has reconnects
    // left or its final connection after a reconnect is still to be looked at (see below), and otherwise
    // every VerifyEveryNCycles cycles. A
    // confirmed-closed port falls through to the throttle when recovery is off OR has already fired
    // (disarmed) - throttled tests still detect a reopen (which re-arms) without hammering the
    // online check services every cycle for a port that may stay closed indefinitely.
    private bool ShouldVerifyThisCycle(bool portChanged, bool portClosedRecoveryEnabled)
    {
        // A closed port the exit-address mismatch explains is checked every cycle while reconnects for it
        // remain - each one may give a matching address, and a reconnect does not always bring a new port
        // to force the check - then falls to the throttle like a disarmed one once they run out.
        // Keyed on the give-up, not on the count: the count reaches the limit when the last reconnect is
        // dispatched, before its session has been looked at, and that session still needs checking.
        bool exitReconnectPending = _vpnExitMismatch && portClosedRecoveryEnabled && !_vpnExitGiveUpNotified
            && !VpnExitMismatchBehindAnotherNat;
        // Also while an episode that has had reconnects is still open without a mismatch held: a match on
        // one of the provider's own intermediate connections ends the hold (and disarms the trigger), and the
        // final connection still has to be looked at, which nothing else would force until a port change or
        // the throttle. Bounded in time as well as by the episode or the give-up: a lookup that never
        // completes (a log that rotated the connection line away, a STUN that keeps failing) would otherwise
        // keep the episode open, and the check every cycle, indefinitely.
        bool exitEpisodeOpen = _vpnExitAnnounced && _vpnExitReconnects > 0 && !_vpnExitGiveUpNotified
            && portClosedRecoveryEnabled
            && _vpnExitLastReconnectAt is { } lastReconnect && _uptime.Elapsed - lastReconnect < 2 * MinVpnExitReconnectSpacing;
        if (portChanged || _portCheckPendingConfirmation || exitReconnectPending || exitEpisodeOpen ||
            (_portConfirmedClosed && portClosedRecoveryEnabled && _portClosedRecoveryArmed && !_vpnExitMismatch))
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
    // (client unreachable, test service unavailable) leave the verification state unchanged, but
    // still run the exit-address check - see CheckVpnExitAddressForUndeterminedAsync.
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
            await CheckVpnExitAddressForUndeterminedAsync(port, config, cancellationToken).ConfigureAwait(false);
            return;
        }
        _pendingUndeterminedCount = 0;
        status[StatusKeys.PortVerified] = open.Value;

        if (open.Value)
        {
            // Read before it is cleared: a mismatch announced on undetermined results said the port could
            // not be reached without setting _portClosedAnnounced, and its Warn still wants the
            // "reachable again" line that closes it.
            bool exitAnnounced = _vpnExitAnnounced && !_exitReachableReported;
            ClearVpnExitMismatch(keepReconnects: true);
            await ResetSpentExitReconnectsIfMatchedAsync(config.VpnManager, cancellationToken).ConfigureAwait(false);
            HandlePortOpenResult(manager.ClientName, port, config, exitAnnounced);
        }
        else
        {
            // At the confirming check, not the first closed result: that one, right after a port change,
            // is usually the client still reconnecting, and is tested again next cycle anyway. Before
            // HandlePortClosedResult, so its Warn and balloon can give the reason. Also on any closed result
            // while a mismatch is held - one found on an undetermined result has neither flag set - so a
            // reconnect is never made for a session nobody looked at, or for a provider switched away from.
            if (_portCheckPendingConfirmation || _portConfirmedClosed || _vpnExitMismatch)
                await CheckVpnExitAddressAsync(config.VpnManager, port, fromClosedResult: true, cancellationToken).ConfigureAwait(false);
            HandlePortClosedResult(manager.ClientName, port, config);
            await MaybeTriggerPortClosedRecoveryAsync(manager, config, cancellationToken).ConfigureAwait(false);
        }
    }

    // The exit-address check for a client whose own test gave no answer - Deluge's check service filters
    // automated requests, and any check service can be down - which would otherwise never reach the
    // check, since that runs on a closed result. A mismatch needs no confirmation from the client: peers
    // are given the exit address, so the forward is unreachable whatever its test would have said. So it
    // is handled like a closed port the mismatch explains: the lookup's one Warn and balloon (raised by
    // CheckVpnExitAddressAsync on entering it), and the reconnects while port-closed recovery is on. A
    // match or a failed lookup shows nothing.
    private async Task CheckVpnExitAddressForUndeterminedAsync(int port, SyncConfig config, CancellationToken cancellationToken)
    {
        await CheckVpnExitAddressAsync(config.VpnManager, port, fromClosedResult: false, cancellationToken).ConfigureAwait(false);
        if (_vpnExitMismatch && config.PortClosedRecoveryEnabled)
            await ReconnectForExitAddressAsync(config, cancellationToken).ConfigureAwait(false);
    }

    // An open result keeps the exit-address reconnects spent (see ClearVpnExitMismatch), so a client whose
    // test flips to open under a mismatch cannot buy fresh ones - but an open result is also how a working
    // fix normally shows up, and no lookup runs on one, so without this the count would never reset after
    // a success and later outages on the same server would get fewer reconnects, or none after a give-up.
    // One direct lookup settles it, only while reconnects are spent: equal addresses end the episode for
    // good. Not through CheckVpnExitAddressAsync, which would announce a mismatch on an open port.
    // Not while the last reconnect is settling, for the reason EndVpnExitHold gives: an open result on one
    // of the provider's own intermediate connections would refund the reconnects, and a final connection
    // that mismatches again would start a fresh round, so the limit would never be reached.
    private async Task ResetSpentExitReconnectsIfMatchedAsync(IVpnManager? vpnManager, CancellationToken cancellationToken)
    {
        if (_vpnExitReconnects == 0 && !_vpnExitGiveUpNotified) return;
        if (ExitEpisodeSettling) return;
        System.Net.IPAddress? forward = vpnManager?.ForwardAddress;
        System.Net.IPAddress? local = vpnManager?.TunnelAddress;
        if (forward is null || local is null) return;
        System.Net.IPAddress? exit = await VpnExitAddress.QueryAsync(local, cancellationToken).ConfigureAwait(false);
        if (exit is null || !exit.Equals(forward)) return;
        LogManager.Instance.LogMessage(
            $"Traffic now leaves from {exit}, the address the port is forwarded on - the exit-address reconnect count starts again",
            LogLevel.Info);
        _vpnExitReconnects = 0;
        _vpnExitGiveUpNotified = false;
        _vpnExitGiveUpForward = null;
    }

    // Raises the PortVerificationFailed balloon event. A throwing handler must not break the cycle.
    private void RaisePortVerificationFailed(string message)
    {
        try { PortVerificationFailed?.Invoke(message); }
        catch (Exception ex) { LogManager.Instance.LogMessage($"PortVerificationFailed handler failed: {ex.Message}", LogLevel.Warn); }
    }

    // Compares the address the provider forwards the port on with the address traffic actually leaves
    // from. Peers learn a client's address from what other hosts observe, so when the two differ the
    // forward is unreachable whatever the client does - a VPN server that forwards on one address and
    // sends traffic out on another, which some ProtonVPN servers do. Looked up afresh on every call,
    // never cached: a reconnect can keep both the forward address and the adapter address while the
    // exit address changes. Fails open: an exit address that cannot be determined is no evidence, so
    // nothing is held and nothing is logged.
    private async Task CheckVpnExitAddressAsync(IVpnManager? vpnManager, int port, bool fromClosedResult, CancellationToken cancellationToken)
    {
        System.Net.IPAddress? forward = vpnManager?.ForwardAddress;
        if (vpnManager is null || forward is null)
        {
            EndVpnExitHoldWithoutForward(vpnManager);
            return;
        }
        _vpnExitProvider = vpnManager.ProviderName;

        System.Net.IPAddress? local = vpnManager.TunnelAddress;
        System.Net.IPAddress? exit = local is null
            ? null
            : await VpnExitAddress.QueryAsync(local, cancellationToken).ConfigureAwait(false);
        _vpnForwardAddress = forward;
        _vpnProtonOverOpenVpn = vpnManager.IsProtonVpnOverOpenVpn;

        if (exit is not null && !exit.Equals(forward))
        {
            _vpnExitAddress = exit;
            // Entering the hold: the closed checks this explains must not count towards the trigger, or
            // the count would sit past it and fire on the first closed check after the user reconnects
            // to a good server - usually the client still reconnecting.
            bool entering = StartNewExitEpisodeIfForwardChanged(forward) || !_vpnExitMismatch;
            if (entering) _confirmedClosedCount = 0;
            _vpnExitMismatch = true;
            LogManager.Instance.LogStateChange(VpnExitMismatchStateKey,
                VpnExitAddress.DescribeMismatch(vpnManager.ForwardSource, port, forward, exit, _vpnProtonOverOpenVpn), LogLevel.Warn);
            // The mismatch balloon is raised here and only here, once per mismatch, whichever path found
            // it: the closed-port transition balloon stands aside while a mismatch is held, so the two can
            // neither double up nor leave a mismatch that starts mid-outage unannounced.
            // Not again for a mismatch already announced: a failed lookup in between ends the hold (fail
            // open) but not the episode, and is likeliest right after a reconnect, so without this every
            // reconnect would bring the same balloon back.
            if (entering && !_vpnExitAnnounced)
                RaisePortVerificationFailed(VpnExitAddress.DescribeMismatchShort(vpnManager.ForwardSource, port, forward, exit, _vpnProtonOverOpenVpn));
            _vpnExitAnnounced = true;
            _exitReachableReported = false;
            // The balloon stands in for the closed-port one, so a later open result logs "reachable again".
            // Only for a mismatch found on a closed result: one found on undetermined results stands in for
            // no closed-port announcement, and a client whose test is never definite (Deluge) never produces
            // the open result that would clear the flag, which would then suppress the first balloon of a
            // later real outage.
            if (fromClosedResult) _portClosedAnnounced = true;
            return;
        }

        EndVpnExitHold(exit, fromClosedResult);
    }

    // No forward address to compare. The same provider failing to read it right now - ProtonVPN once its log
    // has rotated the connection line away - is no evidence either way, so it is handled as a failed lookup:
    // the hold ends but the episode, its reconnects and its give-up stay, or the next fresh log line would
    // start a whole new round of reconnects hours after a give-up. The exit address goes, so the status file
    // does not show the old differing pair beside a cleared mismatch. After a give-up the hold stays instead
    // (see below). Only a different provider (a switch to one that reports none) ends the episode outright.
    // Whenever the hold ends, the closed-port sequence restarts, for the same reason a matching lookup does.
    private void EndVpnExitHoldWithoutForward(IVpnManager? vpnManager)
    {
        if (vpnManager is not null && string.Equals(vpnManager.ProviderName, _vpnExitProvider, StringComparison.OrdinalIgnoreCase))
        {
            // After a give-up the hold stays: a forward address the provider has merely stopped reporting
            // (ProtonVPN once its log has rotated the line away) means no new connection since the mismatch
            // was seen, so that evidence still stands, and ending the hold would let the generic trigger
            // restart the VPN an hour after the user was told to change server.
            if (_vpnExitMismatch && _vpnExitGiveUpNotified)
            {
                LogManager.Instance.LogDebug("PortSyncService.EndVpnExitHoldWithoutForward: forward address not reported after the give-up - the hold stays");
                return;
            }
            _vpnExitAddress = null;
            EndVpnExitHold(null, fromClosedResult: false);
            return;
        }
        if (_vpnExitMismatch) RestartClosedPortSequence();
        ClearVpnExitMismatch();
    }

    // After a give-up, a mismatch on a different forward address is a new server - usually the one the
    // give-up told the user to move to - so it gets its own reconnects and balloon instead of inheriting the
    // spent ones. Safe from looping: after a give-up the app reconnects no more, so the forward can only
    // have changed from outside, and before one a changed forward resets nothing, so the app's own
    // reconnects landing on another server cannot stretch the limit. Compared with the give-up's forward,
    // not the last one seen, so an open result in between (which clears the addresses) buys nothing.
    private bool StartNewExitEpisodeIfForwardChanged(System.Net.IPAddress forward)
    {
        if (!_vpnExitGiveUpNotified || forward.Equals(_vpnExitGiveUpForward)) return false;
        _vpnExitReconnects = 0;
        _vpnExitGiveUpNotified = false;
        _vpnExitGiveUpForward = null;
        _vpnExitAnnounced = false;
        return true;
    }

    // The no-mismatch outcome of a lookup: the addresses are equal, or the exit address could not be
    // determined (null). Split out of CheckVpnExitAddressAsync to keep it under the complexity gate.
    // fromClosedResult: the lookup was prompted by a closed result (not an undetermined one) - see EndVpnExitEpisode.
    private void EndVpnExitHold(System.Net.IPAddress? exit, bool fromClosedResult)
    {
        // A match while the last exit reconnect is still settling is treated like a failed lookup: it ends
        // the hold but not the episode. One reconnect passes through 2-3 connections of the provider's own
        // in about 40 seconds, and a passing match on one of them would otherwise refund all the reconnects
        // and, when the final connection mismatches again, announce the same mismatch a second time.
        bool settling = exit is not null && ExitEpisodeSettling;
        bool episodeEnds = exit is not null && !settling;
        // Keyed on the episode as well as the hold: a failed lookup in between ends the hold but not the
        // episode, and the match that finally ends it must still be reported.
        if (episodeEnds && (_vpnExitMismatch || _vpnExitAnnounced))
            LogExitAddressMatched(exit!);
        else if (exit is null)
            LogManager.Instance.LogDebug("PortSyncService.CheckVpnExitAddressAsync: exit address could not be determined");
        else if (settling)
            LogManager.Instance.LogDebug("PortSyncService.EndVpnExitHold: the addresses match, but a recovery is still settling - the episode stays open");

        // Ending a hold restarts the closed-port sequence from the beginning: this result becomes a first,
        // unconfirmed closed result, not a confirmed one. The confirmed state survives a port change by
        // design, so without this the first check after a reconnect to a good server - usually the
        // client still reconnecting - would be counted as confirmed, and with a trigger of one would
        // fire recovery on the very server the user just moved to.
        // After a give-up a lookup that could not complete keeps the hold, as a forward address that is no
        // longer reported does (EndVpnExitHoldWithoutForward): no new connection has been made, and ending
        // it would let the generic trigger restart the VPN after the user was told to change server. Only
        // on the server the give-up was for: a new forward address is the user having moved, as advised,
        // and holding there would report the old server's give-up against the new one. The last known
        // pair stays in the status file, which is why the exit address is assigned only below.
        if (exit is null && _vpnExitMismatch && _vpnExitGiveUpNotified && Equals(_vpnForwardAddress, _vpnExitGiveUpForward))
        {
            LogManager.Instance.LogDebug("PortSyncService.EndVpnExitHold: exit address not determined after the give-up - the hold stays");
            return;
        }
        _vpnExitAddress = exit;
        if (_vpnExitMismatch) RestartClosedPortSequence();
        _vpnExitMismatch = false;
        // A match at a closed check, after reconnects, counts them as this outage's recovery: the session they
        // produced is the good one, and the generic trigger, still armed because exit reconnects never spend
        // it, would otherwise restart the VPN a few checks later on the client's post-reconnect closed
        // readings (qBittorrent stays firewalled for minutes) and throw that session away. Settling or not: a
        // settling match keeps the episode open, and without the disarm the closed checks after it would
        // rebuild the count, so one failed lookup once the window ends would fire the restart. If the final
        // connection mismatches again, the mismatch hold owns the outage and the arm does not matter. It
        // re-arms on the next open result, as after any recovery - which is why only a closed result
        // disarms: a client whose test is never definite (Deluge) never produces that open result. Keyed on
        // the episode, not the hold, which is already cleared (and may have ended at a failed lookup).
        if (exit is not null && fromClosedResult && _vpnExitAnnounced && _vpnExitReconnects > 0) _portClosedRecoveryArmed = false;
        // Only a lookup that found the addresses equal, outside the settle window, ends the episode. One
        // that could not complete ends the hold (fail open) but keeps the reconnects already spent, so a
        // lookup that keeps failing between mismatches cannot add up to more than MaxVpnExitReconnects. It
        // also keeps the episode announced and the Warn latched, so the same mismatch found again is not
        // reported twice.
        if (episodeEnds) EndVpnExitEpisode();
    }

    // The Info line for a match that ends an episode. Credits the reconnects only before a give-up: after it
    // the app reconnected no more, so a match then is the user's doing (another server, or the switch to
    // Proton WireGuard), not the reconnects'.
    private void LogExitAddressMatched(System.Net.IPAddress exit) =>
        LogManager.Instance.LogMessage(
            $"Traffic now leaves from {exit}, the address the port is forwarded on" +
            (_vpnExitReconnects > 0 && !_vpnExitGiveUpNotified ? $" - matched after {TextFormat.Pluralize(_vpnExitReconnects, "reconnect")}" : string.Empty),
            LogLevel.Info);

    // Whether the connection may still be one of the provider's own intermediate ones: an exit reconnect, or
    // any other recovery (the failed-cycle or port-closed trigger restarts the service just the same), less
    // than MinVpnExitReconnectSpacing ago. What the episode rules wait on: a match ends only the hold, an
    // open result keeps the announcement and refunds nothing, and no reconnect or give-up is made. The
    // port-closed trigger's own hold stays on ExitReconnectSettling, since its end is computed from the
    // last exit reconnect.
    private bool ExitEpisodeSettling => ExitReconnectSettling || RecentRecoveryDispatched;

    // Whether any recovery was dispatched less than MinVpnExitReconnectSpacing ago.
    private static bool RecentRecoveryDispatched =>
        Volatile.Read(ref _lastRecoveryDispatchTick) is var tick and not 0
        && Environment.TickCount64 - tick < (long)MinVpnExitReconnectSpacing.TotalMilliseconds;

    // Whether the last exit-address reconnect is less than MinVpnExitReconnectSpacing old.
    private bool ExitReconnectSettling =>
        _vpnExitLastReconnectAt is { } last && _uptime.Elapsed - last < MinVpnExitReconnectSpacing;

    // A matching lookup ends the mismatch episode: the reconnects, the give-up and the announcement all start
    // again for the next one.
    private void EndVpnExitEpisode()
    {
        _vpnExitReconnects = 0;
        _vpnExitGiveUpNotified = false;
        _vpnExitGiveUpForward = null;
        _vpnExitAnnounced = false;
        LogManager.Instance.ClearLogState(VpnExitMismatchStateKey);
        LogManager.Instance.ClearLogState(VpnExitNoTargetStateKey);
    }

    // A port known closed or pending before the VPN went down is re-tested on the first connected cycle,
    // not after the throttle: RestartClosedPortSequence below clears the flags that force a check, and a
    // tunnel that comes back on the same port (Static port, a NAT-PMP renewal) brings no port change to
    // force one either.
    private void ForceVerifyAfterOutage()
    {
        if (_portConfirmedClosed || _portCheckPendingConfirmation) _cyclesSinceVerify = VerifyEveryNCycles;
    }

    // Makes the next closed result a first, unconfirmed one again. Used when an exit-address hold ends at
    // a closed check, when the VPN is found down, on a change of provider or adapter, and on a switch to a
    // provider with no forward address:
    // the confirmed state survives a port change by design, so without this the first check after a
    // reconnect - usually the client still reconnecting - would count as confirmed. The
    // outage itself stays announced (_portClosedAnnounced), so a later reopen is still reported. The time
    // floor starts again too, from the next confirmation: measured from the first one, it would already
    // have passed, and give no cover to the burst of closed checks that follows the reconnect.
    private void RestartClosedPortSequence()
    {
        _portConfirmedClosed = false;
        _portCheckPendingConfirmation = false;
        _confirmedClosedCount = 0;
        _portClosedConfirmedAt = null;
    }

    // A mismatch whose forward address is private or carrier-grade NAT: the gateway is a home router behind
    // another NAT, where the port cannot be reached from the Internet and no reconnect can change that, so
    // the reconnects are skipped and the messages give that cause instead of VPN advice.
    private bool VpnExitMismatchBehindAnotherNat =>
        _vpnExitMismatch && _vpnForwardAddress is not null && VpnExitAddress.IsBehindAnotherNat(_vpnForwardAddress);

    // Ends a hold the mismatch was causing - the port is open, or a switch to a provider that reports no
    // forward address - without announcing anything: the open-port line or the provider change says what happened.
    // The addresses go too, so the status file does not show a stale pair beside a cleared mismatch, or
    // a NAT-PMP pair after a switch to a provider that reports none.
    // keepReconnects (the open-port case): the reconnects already made stay spent, so a client whose test
    // flips to open in between cannot buy a fresh MaxVpnExitReconnects each time - only a lookup that finds
    // the addresses equal (EndVpnExitHold, or ResetSpentExitReconnectsIfMatchedAsync on an open result), a
    // new forward after a give-up (StartNewExitEpisodeIfForwardChanged), a change of provider or adapter, a
    // switch to a provider with no forward address, or verification switched off starts the count again.
    private void ClearVpnExitMismatch(bool keepReconnects = false)
    {
        _vpnExitMismatch = false;
        if (!keepReconnects)
        {
            _vpnExitReconnects = 0;
            _vpnExitGiveUpNotified = false;
            _vpnExitGiveUpForward = null;
        }
        _vpnForwardAddress = null;
        _vpnExitAddress = null;
        _vpnProtonOverOpenVpn = false;
        // Not on an open result while the last reconnect is settling: that open may be one of the provider's
        // own intermediate connections, and a final connection that mismatches again is the same episode,
        // which must not raise its balloon a second time.
        if (!keepReconnects || !ExitEpisodeSettling) _vpnExitAnnounced = false;
        // The Warn latch goes even while the announcement is kept: a final connection that mismatches with the
        // same text logs the Warn again (the port was seen open in between), but raises no second balloon.
        LogManager.Instance.ClearLogState(VpnExitMismatchStateKey);
        LogManager.Instance.ClearLogState(VpnExitNoTargetStateKey);
    }

    private void HandlePortOpenResult(string clientName, int port, SyncConfig config, bool exitAnnounced)
    {
        if (_portConfirmedClosed || _portClosedAnnounced || exitAnnounced)
        {
            LogManager.Instance.LogMessage($"{clientName} port {port} is reachable from outside again", LogLevel.Info);
            _exitReachableReported = true;
        }
        else
            LogManager.Instance.LogDebug($"PortSyncService.HandlePortOpenResult: {clientName} port {port} verified open");
        _portCheckPendingConfirmation = false;
        _portConfirmedClosed = false;
        _portClosedAnnounced = false;
        _portClosedRecorded = false;
        _portClosedConfirmedAt = null;
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
            // Not counted while the exit-address mismatch explains the closed port - see CheckVpnExitAddressAsync.
            if (!_vpnExitMismatch) _confirmedClosedCount++;
            string closedSuffix = BuildPortClosedRecoverySuffix(config);
            LogManager.Instance.LogMessage($"{clientName} port {port} is still not reachable from outside{closedSuffix}", LogLevel.Warn);
            return;
        }
        if (!_portCheckPendingConfirmation)
        {
            _portCheckPendingConfirmation = true;
            _portClosedPendingAt = _uptime.Elapsed;
            LogManager.Instance.LogMessage($"{clientName} port {port} test reports closed - confirming on the next check", LogLevel.Info);
            return;
        }
        // The confirming check has to come at least half an interval later: the quick re-check after a
        // network change or Sync Now runs about 10 seconds on, and two closed readings that close together
        // are usually one reconnect still settling (qBittorrent stays firewalled for minutes), which would
        // then be warned and announced as an outage. Still pending, so the next cycle checks again.
        if (_portClosedPendingAt is { } pendingSince
            && _uptime.Elapsed - pendingSince < TimeSpan.FromSeconds(config.UpdateIntervalSeconds / 2.0))
        {
            LogManager.Instance.LogDebug("PortSyncService.HandlePortClosedResult: closed again too soon after the first closed result to confirm it - waiting for the next cycle");
            return;
        }

        _portCheckPendingConfirmation = false;
        _portConfirmedClosed = true;
        // Read before it is set: a restarted sequence (an exit-address hold ending) confirms the same
        // outage a second time, and it was already announced - by this balloon or by the mismatch's.
        bool firstAnnouncement = !_portClosedAnnounced;
        _portClosedAnnounced = true;
        // When this confirmed sequence began, for the port-closed trigger's time floor. Cleared by
        // RestartClosedPortSequence, so a restarted sequence measures the floor afresh.
        _portClosedConfirmedAt = _uptime.Elapsed;
        _confirmedClosedCount = _vpnExitMismatch ? 0 : 1;
        // History records the transition into confirmed-closed only (like the balloon), not the
        // per-cycle re-confirmations - the persistent condition is one event, not many - and once per
        // outage, not again when a restarted sequence confirms it a second time.
        if (!_portClosedRecorded)
            PortHistoryManager.Append(PortHistoryKind.PortClosed, port, "Port confirmed closed from outside");
        _portClosedRecorded = true;
        string confirmedSuffix = BuildPortClosedRecoverySuffix(config);
        LogManager.Instance.LogMessage($"{clientName} port {port} is not reachable from outside (confirmed by two checks){confirmedSuffix}", LogLevel.Warn);
        // With an exit-address mismatch held, its own balloon (raised when it was found, see
        // CheckVpnExitAddressAsync) already named the cause and the fix, and a second one saying only
        // "not reachable" would send the user looking at the client, which is not where the problem is.
        // And once per outage: re-fires only after the port has tested open again, as the event promises.
        if (!_vpnExitMismatch && firstAnnouncement)
            RaisePortVerificationFailed($"{clientName} port {port} is not reachable from outside.");
    }

    // Builds the recovery-progress suffix for the port-closed Warn messages, mirroring
    // RecoveryGate.BuildCycleCountMessage's structure (counted in checks, not cycles) so it reads consistently
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
        // Ahead of the disarmed case: the mismatch is the more useful thing to say, and while it holds,
        // the count below is not advancing, so quoting it would read as a trigger about to fire.
        if (VpnExitMismatchBehindAnotherNat)
            return " (the gateway is itself behind another NAT, so the port cannot be reached from the Internet - recovery is held)";
        if (_vpnExitMismatch)
            return _vpnExitReconnects < MaxVpnExitReconnects
                ? $" (traffic leaves from a different address than the port is forwarded on - reconnecting the VPN for a matching one, {_vpnExitReconnects} of {MaxVpnExitReconnects} reconnects so far)"
                : $" (traffic leaves from a different address than the port is forwarded on - still so after {MaxVpnExitReconnects} reconnect attempts, so recovery is held)";
        if (!_portClosedRecoveryArmed)
            return " (recovery has already run for this outage - it will not run again until a scheduled check reports the port open)";
        string checks = TextFormat.PluralizeNoun(_confirmedClosedCount, "closed check");
        // Past the count but held back (PortClosedRecoveryHeldBack): say it is waiting, or the line would read
        // "4 consecutive closed checks, recovery triggers after 3".
        if (_confirmedClosedCount >= config.PortClosedRecoveryTriggerChecks)
        {
            if (PortClosedFloorPending(config))
                return $" ({_confirmedClosedCount} consecutive {checks} - recovery waits until the port has stayed closed for " +
                       $"{config.PortClosedRecoveryTriggerChecks - 1} sync {TextFormat.PluralizeNoun(config.PortClosedRecoveryTriggerChecks - 1, "interval")})";
            if (ExitReconnectSettling)
                return $" ({_confirmedClosedCount} consecutive {checks} - recovery waits for the last exit-address reconnect to settle)";
            return $" ({_confirmedClosedCount} consecutive {checks} - recovery is due now)";
        }
        return $" ({_confirmedClosedCount} consecutive {checks}, recovery triggers after {config.PortClosedRecoveryTriggerChecks} consecutive {TextFormat.PluralizeNoun(config.PortClosedRecoveryTriggerChecks, "closed check")})";
    }

    // Forces the client to rebuild its listen sockets when a confirmed-closed port coincides with the
    // bound adapter having changed address. Returns true when a rebind was actually attempted, which is
    // what tells the caller to hold recovery back for one more round of confirmation.
    // One attempt per address change: the flag is cleared whether or not the write succeeded, so a rebind
    // that does not help escalates to recovery instead of repeating.
    //
    // "recovery" rather than "the VPN restart" throughout, matching the messages below: the action is a
    // service restart for ProtonVPN and PIA, but for NAT-PMP and Static port VpnAdapter.GetRecovery
    // returns cycle-adapter when the adapter belongs to no known provider, or to one whose service is not
    // installed, where no VPN is restarted at all.
    private async Task<bool> TryRebindClientAddressAsync(IManagedClient manager, SyncConfig config, CancellationToken cancellationToken)
    {
        if (!_interfaceAddressChangedSinceRebind || !config.FixInterfaceBinding) return false;
        if (manager is not QBittorrentClient client) return false;
        // No baseline guard here any more: it meant "addresses were never read", which the live read
        // below covers, and holding the baseline was what let observation-time state reach this action.
        // _lastKnownInterfaceAddresses is now used solely to answer "did this change since last time".

        // Read fresh rather than pinning from the baseline. The baseline answers "did this change since
        // last time", a question about history; the pin needs "what is on the adapter right now". Using
        // one for the other is why a cycle whose address read failed could pin an address the adapter no
        // longer carries - self-correcting, since the release restores the configured value either way,
        // but it wrote a nonexistent address and logged it as live. Reading here is correct by
        // construction and bounds nothing on how old the baseline is. The cost is one request, on a path
        // that only runs once the port-closed trigger's count is reached.
        var (live, pinned) = await client.GetInterfaceAddressStateAsync(cancellationToken).ConfigureAwait(false);

        // Arm deliberately left set, for the same reason the SelectBindAddress case below leaves it: the
        // change really was observed, and failing to read the adapter now is not evidence that it stopped
        // mattering. Kept separate from the pin test below rather than sharing its ||, because that test
        // spends the arm and none of its reasoning is about a failed read. This also covers an empty
        // token (bound to all interfaces, so there is no adapter to inspect), where a surviving arm costs
        // nothing: CheckInterfaceAddressAsync returns early on that same condition, so the arm just sits
        // until a readable cycle either uses or clears it.
        //
        // Logged, not silent: the address-change line has already told the user a rebind is coming if
        // the port stops answering, and the port has now stopped answering. Returning here without a
        // word leaves them watching recovery run when the promise said a rebind would come first.
        // Worded for every cause and logged at Info, not Warn. A null list is not only a failed read,
        // and the benign causes are the common ones. GetInterfaceAddressStateAsync returns null for an
        // empty interface token, which means "bound to all interfaces", and for a token that resolves
        // to no adapter, which is the ordinary VPN-disconnected state. In both of those nothing was
        // attempted and nothing failed, exactly as the comment above says. Asserting a read failure
        // there was wrong, and Warn badged the tray for a benign state that recurs on every later
        // closed streak because the arm is deliberately left set.
        // A genuine read failure is not lost: GetInterfaceAddressesAsync logs it at Debug itself.
        //
        // Keep this list in step with GetInterfaceAddressStateAsync's null contract. It has grown once
        // already: the absent-adapter case was added there without this line or its message being
        // updated, so the user was told their client was bound to all interfaces while its adapter was
        // simply gone.
        if (live is null)
        {
            LogManager.Instance.LogMessage(
                $"No adapter address list to rebind {client.ClientName} against - it is bound to all " +
                "interfaces, its adapter is not present, or the list could not be read - escalating to recovery",
                LogLevel.Info);
            return false;
        }

        // The pin is re-checked at the point of use, never trusted from when the arm was set. The user
        // can change the bind address between those two moments and the arm survives that, so a stored
        // release value could widen a pin they made deliberately in the meantime - the exact harm this
        // whole feature exists to avoid. A non-wildcard pin also means the ambiguous "bound to all
        // addresses" case no longer applies, so the premise for rebinding is gone: spend the arm and
        // let the caller escalate rather than leaving it set to re-enter here every closed round.
        if (!QBittorrentClient.IsWildcardBindAddress(pinned))
        {
            _interfaceAddressChangedSinceRebind = false;
            // Info, not Warn, and that difference is meaning rather than drift: the two refusals above
            // are "we meant to rebind and could not", which is an anomaly; this one is "there is
            // correctly nothing to rebind", because the user pinned a specific address themselves.
            // Logged all the same - the address-change line promised a rebind, and this path both
            // declines it and spends the arm, so the next confirmed-closed round escalates with
            // nothing else said. Same closing clause as its two siblings: all three return false, and
            // false falls straight through to DispatchRecoveryAsync in this very cycle.
            LogManager.Instance.LogMessage(
                $"{client.ClientName} is now bound to '{pinned}' rather than all addresses, so there is " +
                "nothing to rebind - escalating to recovery",
                LogLevel.Info);
            return false;
        }
        string releaseAddress = pinned ?? string.Empty;
        // Arm deliberately left set: the change really was observed, and failing to read the adapter now
        // is not evidence that it stopped mattering. No attempt was made, so "one attempt per address
        // change" is still honest, and the next eligible cycle tries again.
        //
        // Logged for the same reason as the failed read above, and this one is the likelier of the two:
        // every address being link-local is exactly what a reconnecting or disconnected VPN leaves
        // behind, which is the same state DiagnosticsService treats as a Warn rather than a Fail.
        if (QBittorrentClient.SelectBindAddress(live) is not string pinAddress)
        {
            LogManager.Instance.LogMessage(
                $"{client.ClientName}'s adapter has no routable address to rebind to ({QBittorrentClient.FormatAddressList(live)}) " +
                "- escalating to recovery",
                LogLevel.Warn);
            return false;
        }

        _interfaceAddressChangedSinceRebind = false;

        LogManager.Instance.LogMessage(
            $"The forwarded port is closed and the bound adapter changed address, so {client.ClientName} may still be " +
            $"listening on the previous one. Rebinding it via {pinAddress} before recovery runs",
            LogLevel.Warn);

        // Pin a live address, then release back to whatever was configured when the change was seen.
        // Both writes are real changes, which is what makes the client rebuild its sockets; the end
        // state is the user's own value, read from the client rather than assumed here.
        if (await client.ForceInterfaceRebindAsync(pinAddress, releaseAddress, cancellationToken).ConfigureAwait(false))
        {
            // Names the value actually restored rather than saying "all addresses": since the wildcard
            // fix the release can be 0.0.0.0 or ::, and a line that describes the wrong end state is the
            // one thing someone diagnosing this has to go on.
            LogManager.Instance.LogMessage(
                $"Rebound {client.ClientName} to '{(releaseAddress.Length == 0 ? "all addresses" : releaseAddress)}' " +
                "on its adapter - the next port check will show whether it is listening again",
                LogLevel.Info);
        }
        else
        {
            LogManager.Instance.LogMessage(
                // The caller resets the closed count after this attempt, so recovery needs a full fresh round.
                $"Could not rebind {client.ClientName} - recovery runs instead if the port is still confirmed closed " +
                $"after another {TextFormat.Pluralize(config.PortClosedRecoveryTriggerChecks, "check")}",
                LogLevel.Warn);
        }

        // True either way: the attempt happened, so the caller holds the restart back for one round.
        return true;
    }

    // Time floors on the port-closed trigger, the counterpart of the failed-cycle trigger's
    // sustained-failure floor. Closed checks are counted, and after any reconnect they arrive in a burst -
    // the cycle that follows a network change runs at once, and a quick re-check comes about 10 seconds
    // later - so the count can reach the trigger within a minute while a client such as qBittorrent is only
    // still firewalled from the reconnect. Held back, logged at Debug, while either applies:
    // - the outage has been confirmed for less than the time the checks would take on the normal
    //   schedule, (trigger - 1) sync intervals, so a sustained closed port still fires when it did before;
    // - an exit-address reconnect is less than MinVpnExitReconnectSpacing old, so the session it produced
    //   is not restarted before it has settled.
    private bool PortClosedRecoveryHeldBack(SyncConfig config)
    {
        if (PortClosedFloorPending(config))
        {
            LogManager.Instance.LogDebug("PortSyncService.PortClosedRecoveryHeldBack: the port has not yet stayed closed for the trigger's time floor");
            return true;
        }
        if (ExitReconnectSettling)
        {
            LogManager.Instance.LogDebug("PortSyncService.PortClosedRecoveryHeldBack: an exit-address reconnect is still settling");
            return true;
        }
        return false;
    }

    // Whether the last recorded port-closed hold still describes the trigger: the count still at the
    // threshold of an armed trigger that no mismatch holds instead. The threshold is this cycle's, as
    // published in the status. An instant already past is still published: the panel then reads "Will
    // trigger on the next closed check" rather than a count past its target, on a cycle whose test did
    // not reach the trigger.
    private bool PortClosedHoldStillApplies(Dictionary<string, object?> status) =>
        _portClosedHoldEndsAt is not null
        && _portClosedRecoveryArmed && !_vpnExitMismatch
        && status[StatusKeys.PortClosedRecoveryTriggerChecks] is int trigger && trigger > 0 && _confirmedClosedCount >= trigger;

    // When the holds PortClosedRecoveryHeldBack applies end, the later of the two; null when neither applies.
    private TimeSpan? PortClosedHoldEnd(SyncConfig config)
    {
        TimeSpan? end = null;
        if (PortClosedFloorPending(config) && _portClosedConfirmedAt is { } since)
            end = since + TimeSpan.FromSeconds((config.PortClosedRecoveryTriggerChecks - 1) * config.UpdateIntervalSeconds);
        if (ExitReconnectSettling && _vpnExitLastReconnectAt is { } last && (end is null || last + MinVpnExitReconnectSpacing > end))
            end = last + MinVpnExitReconnectSpacing;
        return end;
    }

    // Whether this outage has been confirmed closed for less than (trigger - 1) sync intervals.
    private bool PortClosedFloorPending(SyncConfig config) =>
        _portClosedConfirmedAt is { } since
        && _uptime.Elapsed - since < TimeSpan.FromSeconds((config.PortClosedRecoveryTriggerChecks - 1) * config.UpdateIntervalSeconds);

    // Opt-in: when the port has been confirmed closed for the configured number of checks,
    // dispatches the provider's recovery action once. Independent of the failed-sync recovery
    // trigger - the two share the action, not the gate. One-shot arming: after firing, recovery
    // stays disarmed until a verification reports the port open again (see HandlePortOpenResult),
    // so a persistently false "closed" can never cause a recovery loop.
    private async Task MaybeTriggerPortClosedRecoveryAsync(IManagedClient manager, SyncConfig config, CancellationToken cancellationToken)
    {
        if (!config.PortClosedRecoveryEnabled)
        {
            _confirmedClosedCount = 0;
            return;
        }
        // A closed port the exit-address mismatch explains gets its own remedy, a reconnect, rather than
        // the count-and-restart below: its cause is known, so there is nothing to confirm.
        if (_vpnExitMismatch)
        {
            await ReconnectForExitAddressAsync(config, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!_portClosedRecoveryArmed || _confirmedClosedCount < config.PortClosedRecoveryTriggerChecks) return;
        if (PortClosedRecoveryHeldBack(config))
        {
            _portClosedHoldEndsAt = PortClosedHoldEnd(config);
            return;
        }

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

    // Reconnects the VPN so the server may hand out a different exit address, possibly the one the port is
    // forwarded on - the only remedy for a mismatch, since nothing on this machine chooses the exit
    // address. Up to MaxVpnExitReconnects times per mismatch; after that the user is asked once to
    // change server, and the hold stays, so a server that rarely lands on the forward address cannot
    // loop. Uses the provider's normal recovery action but not the one-shot port-closed arm, which
    // belongs to closed ports with no known cause. Runs only while port-closed recovery is enabled
    // (checked by the caller): that setting is the user's consent to the VPN being restarted.
    private async Task ReconnectForExitAddressAsync(SyncConfig config, CancellationToken cancellationToken)
    {
        IVpnManager vpnManager = config.VpnManager!; // non-null: a mismatch needs a forward address, which needs a manager
        // Behind another NAT a reconnect cannot help - for an adapter no provider owns, the recovery action
        // cycles the machine's own network adapter - so none is made; the mismatch Warn already gave the cause.
        if (VpnExitMismatchBehindAnotherNat) return;
        // Ahead of the give-up as well as the next reconnect: the session the last reconnect produced must
        // have settled before it is judged either way. Any recent recovery counts, not only an exit
        // reconnect: the failed-cycle or port-closed trigger restarts the service just the same.
        if (ExitEpisodeSettling)
        {
            LogManager.Instance.LogDebug("PortSyncService.ReconnectForExitAddressAsync: a recovery was dispatched under " +
                $"{MinVpnExitReconnectSpacing.TotalMinutes:0} minutes ago - waiting for the connection to settle");
            return;
        }
        if (_vpnExitReconnects >= MaxVpnExitReconnects)
        {
            if (_vpnExitGiveUpNotified) return;
            _vpnExitGiveUpNotified = true;
            _vpnExitGiveUpForward = _vpnForwardAddress;
            // "attempts": a reconnect is counted when it is dispatched, and one the helper could not carry
            // out has already logged its own Warn, so the line must stay true either way.
            string giveUp = $"The exit address still does not match the forwarded one after {MaxVpnExitReconnects} reconnect attempts - " +
                            VpnExitAddress.DescribeAdvice(_vpnProtonOverOpenVpn, gaveUp: true);
            LogManager.Instance.LogMessage(giveUp, LogLevel.Warn);
            RaisePortVerificationFailed(giveUp + ".");
            return;
        }

        string? target = vpnManager.GetRecoveryTarget();
        if (target is null)
        {
            // Once per state: verification runs every cycle while reconnects remain, and this is a setting
            // the user has to fix, so repeating it would only keep raising the badge.
            LogManager.Instance.LogStateChange(VpnExitNoTargetStateKey,
                $"No recovery target found for '{vpnManager.ProviderName}' - cannot reconnect for a matching exit address", LogLevel.Warn);
            return;
        }
        LogManager.Instance.ClearLogState(VpnExitNoTargetStateKey);

        _vpnExitReconnects++;
        _vpnExitLastReconnectAt = _uptime.Elapsed;
        string action = vpnManager.GetRecoveryAction();
        await DispatchRecoveryAsync(action, target, vpnManager.ProviderName, cancellationToken,
            triggerLogMessage: $"Reconnecting '{vpnManager.ProviderName}' for an exit address that matches the forwarded one " +
                               $"(attempt {_vpnExitReconnects} of {MaxVpnExitReconnects}) - a new connection may get a different exit address").ConfigureAwait(false);
        // Again once the restart has returned: the helper can take most of two minutes over a slow one, and
        // the settle window must cover the provider's own reconnects after it, not the restart itself.
        _vpnExitLastReconnectAt = _uptime.Elapsed;
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
    //
    // Returns whether the token can be trusted to read an address under - healthy, or repaired just
    // now. False means the client is still bound by a token that names no live adapter, and the caller
    // skips the address check rather than spending a request on a token already known not to resolve:
    // qBittorrent answers networkInterfaceAddressList?iface=<stale token> with HTTP 200 and an empty
    // array rather than an error (measured). DiagnosticsService.AddInterfaceResultAsync returns after a
    // stale token for the same reason.
    //
    // This gate is no longer the only thing standing between that empty answer and a phantom
    // address-loss report, and must not be treated as such. It never covered the case that actually
    // produces one on an ordinary disconnect: an adapter that is merely *absent* is not stale, so
    // CheckInterfaceBindingAsync returns "leave the binding alone" and this returns true, with the
    // token no more resolvable than a stale one. GetInterfaceAddressStateAsync now maps every empty
    // answer to null, which is what makes the address check's "null means unknown, not healthy" guard
    // fire for both. Keep that mapping if this gate is ever relaxed - without it, a false Warn or Info
    // arms the port-closed rebind on nothing and poisons _lastKnownInterfaceAddresses, so the next
    // healthy cycle reports a second phantom change back.
    private async Task<bool> CheckAndRepairInterfaceBindingAsync(QBittorrentClient client, string? interfaceName, SyncConfig config, CancellationToken cancellationToken)
    {
        var (stale, expectedToken) = await client.CheckInterfaceBindingAsync(interfaceName, cancellationToken).ConfigureAwait(false);

        if (!stale || expectedToken is null || interfaceName is null)
        {
            // Healthy, or nothing to say. Re-arm so a later drift gets a fresh repair attempt, and
            // clear this condition's own balloon and log latches so a future stale binding reports again.
            _interfaceBindingRepairAttempted = false;
            _lastBindingWarningMessage = null;
            LogManager.Instance.ClearLogState(BindingStaleStateKey);
            return true;
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
            return false;
        }

        // One attempt per stale streak: if the write lands but the binding is still wrong next cycle,
        // something else is overwriting it and repeating the write every cycle would be its own loop.
        if (_interfaceBindingRepairAttempted)
        {
            LogManager.Instance.LogDebug(
                $"PortSyncService.CheckAndRepairInterfaceBindingAsync: {client.ClientName} binding still stale after a repair - not retrying until it is healthy");
            return false;
        }

        _interfaceBindingRepairAttempted = true;
        LogManager.Instance.LogMessage($"{warning}. Re-applying it", LogLevel.Warn);

        // The result is the return value, not just a log branch: a successful repair updates the
        // client's stored token (QBittorrentClient.RepairInterfaceBindingAsync), so the address check
        // that follows reads the right adapter. A failed one leaves the stale token in place.
        if (await client.RepairInterfaceBindingAsync(interfaceName, expectedToken, cancellationToken).ConfigureAwait(false))
        {
            LogManager.Instance.LogMessage($"Re-applied the {client.ClientName} network interface binding to '{interfaceName}'", LogLevel.Info);
            return true;
        }

        LogManager.Instance.LogMessage($"Could not re-apply the {client.ClientName} network interface binding - re-select it in {client.ClientName}", LogLevel.Error);
        return false;
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
        var (live, pinned) = await client.GetInterfaceAddressStateAsync(cancellationToken).ConfigureAwait(false);

        // Unknown rather than healthy: an older qBittorrent, an unreachable API, or no bound interface.
        // Leaving the remembered addresses alone means the next readable cycle compares against the last
        // real observation rather than against a gap, so a change spanning the gap is still reported.
        if (live is null) return;

        // One normalised key, used by both the comparison below and the store at the end, so the two
        // cannot disagree about what a missing interface name is. They did: the store collapsed null
        // to empty while the comparison used the raw value, so a null name could never match itself
        // and the change test was dead for any cycle that read a token but no name. Unreachable today
        // (qBittorrent writes the two preferences together), which is exactly why an asymmetry here
        // could sit unnoticed rather than announce itself.
        string interfaceKey = interfaceName ?? string.Empty;

        if (!QBittorrentClient.IsWildcardBindAddress(pinned) && !live.Contains(pinned, StringComparer.OrdinalIgnoreCase))
        {
            // Provably broken rather than suspected: the client is bound to an address the adapter does
            // not have, so it cannot be listening. Repaired on detection like the stale token beside it,
            // not deferred to the port-closed escalation, which is for the case that is only a suspicion.
            await RepairPinnedAddressAsync(client, interfaceName, pinned, live, config, cancellationToken).ConfigureAwait(false);
        }
        else if (QBittorrentClient.IsWildcardBindAddress(pinned) &&
                 _lastKnownInterfaceAddresses is { } baseline &&
                 string.Equals(baseline.Interface, interfaceKey, StringComparison.Ordinal) &&
                 !baseline.Addresses.SequenceEqual(live, StringComparer.OrdinalIgnoreCase))
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
            // Info, not Warn: on its own this is a normal VPN reconnect, not a fault. The connection
            // status is read here rather than every cycle - this branch is a transition, so the extra
            // call is rare - and it answers whether the existing restart-on-disconnect path could ever
            // see this state. The line states what happens next, because something does: leaving it at
            // "if connections stop, the listener is on the old address" would ask the user to draw a
            // conclusion the app now acts on by itself.
            // Try* rather than GetConnectionStatusAsync: that one logs a transport failure at Error for
            // the restart-on-disconnect check, which would raise a red tray badge here, inside a branch
            // this very comment calls a normal reconnect. The status only decorates the line below, and
            // the "no status" fallback already treats an unreadable one as expected.
            string? connectionStatus = await client.TryGetConnectionStatusAsync(cancellationToken).ConfigureAwait(false);
            // This line used to promise the rebind outright, and there are TWO independent ways that
            // promise is false - both were live, and the second was missed when the first was fixed.
            //
            // 1. The arm set just above is spent by a port write later in THIS SAME cycle (see
            //    UpdatePortAndNotifyAsync, which clears it because the write rebuilds the listen sockets
            //    anyway), and an address change accompanied by a new forwarded port is the common
            //    reconnect. Observed live on 2026-09-02: this line, a port write, then a VPN restart
            //    three cycles later with no rebind and no explanation.
            // 2. TryRebindClientAddressAsync refuses outright when fixInterfaceBinding is off, but
            //    nothing on this path consults that setting - CheckAndRepairInterfaceBindingAsync
            //    returns true on a healthy token before it ever reaches its own toggle check, so this
            //    branch runs and logged the promise to users who had switched the repair off entirely.
            //    For them it was not "unless the port changes", it was never.
            //
            // 3. The rebind is reachable only through the port-closed path, so the port check and
            //    port-closed recovery have to be on as well - see SyncConfig.RebindReachable. Naming only
            //    the binding setting here was wrong whenever it was one of the other two that was off.
            //
            // Hence a conditional clause rather than more hedging, and an off-branch that states the
            // requirement rather than guessing which of the three is missing.
            string rebindClause = RebindClause(client.ClientName, config);
            LogManager.Instance.LogMessage(
                $"The address on '{interfaceName}' changed from {QBittorrentClient.FormatAddressList(baseline.Addresses)} to {QBittorrentClient.FormatAddressList(live)} " +
                $"while {client.ClientName} is bound to all addresses on it ({client.ClientName} reports " +
                $"'{connectionStatus ?? "no status"}'). {rebindClause}",
                LogLevel.Info);
        }
        else
        {
            LogManager.Instance.ClearLogState(BindingAddressStateKey);
            _interfaceAddressRepairAttempted = false;   // healthy: re-arm for a later drift
        }

        // Keyed to the interface it was read from, so the next cycle only compares like with like.
        // Same normalised key the comparison above used - see interfaceKey.
        _lastKnownInterfaceAddresses = (interfaceKey, live);
    }

    // The promise in the address-change line. Three cases, because the rebind runs only from the
    // port-closed path, and only while that trigger is armed: after port-closed recovery has fired for
    // this outage it stays disarmed until a check reports the port open, so promising a rebind then
    // would be false. Split out of CheckInterfaceAddressAsync to keep it under the complexity gate.
    private string RebindClause(string clientName, SyncConfig config)
    {
        if (!config.RebindReachable)
            return $"{clientName} will not be rebound if that port stops answering - that needs the " +
                   "VPN connected, and the port check, port-closed recovery and \"Fix the network " +
                   "interface binding when it goes stale\" all switched on";
        if (!_portClosedRecoveryArmed)
            return $"{clientName} will not be rebound while recovery for this outage has already run - " +
                   "that is possible again once a check reports the port open";
        return $"Unless the forwarded port also changes first, {clientName} will be rebound " +
               "if that port stops answering, before recovery runs";
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
            $"has (it now has {QBittorrentClient.FormatAddressList(live)}), so it cannot accept incoming connections";

        string? replacement = QBittorrentClient.SelectBindAddress(live);
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

    // The bind-address predicates and the address-list wording live on QBittorrentClient, beside the
    // GetInterfaceAddressStateAsync that produces the values they interpret, because DiagnosticsService
    // has to reach the same verdict about the same field. See the remarks there.

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
    // The command string is passed through directly without sanitisation - intentional, because
    // running whatever the user chose is the entire feature.
    //
    // It is no longer only a hand-configured value, and the note here used to say so: it claimed "no
    // external or untrusted input reaches this path", which the settings backup/restore feature made
    // false. A restore writes this key from a JSON file the user may not have authored, so a restored
    // backup can install a command that runs on the next port change. Corrected rather than deleted,
    // because that is exactly the kind of claim a later reader would take at face value.
    //
    // Carrying the command in a backup is a deliberate decision, not an oversight: dropping it would
    // silently discard a setting the user configured, which is the failure the rest of the transfer
    // works to avoid. The exposure is therefore accepted - restoring someone else's settings file
    // runs the command it contains.
    private static void RunPostUpdateCommand(string cmd)
    {
        LogManager.Instance.LogDebug($"PortSyncService.RunPostUpdateCommand: {cmd}");
        try
        {
            string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            Process.Start(ProcessHelpers.CreateHiddenStartInfo(cmdExe, $"/C \"{cmd}\""))?.Dispose(); // NOSONAR S4721 - executing the command the user chose is the intended behaviour; see the note above on where the value can originate
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
            // The give-up is said here, on the first check after the last restart that still finds the
            // client disconnected - not right after that restart, which may well have worked. Logged once:
            // the counter steps past the cap, so a persistent cause does not repeat it every cycle. The
            // hint names the most common unfixable-by-restart cause: a binding the client keeps in its own
            // configuration, which a restart re-reads unchanged.
            if (_consecutiveDisconnectRestarts == MaxDisconnectRestarts)
            {
                _consecutiveDisconnectRestarts++;
                LogManager.Instance.LogMessage(
                    $"Restarting has not cleared {manager.ClientName}'s disconnected status after {MaxDisconnectRestarts} attempts - " +
                    $"no further restarts until it reconnects. If it is bound to a VPN adapter, re-select the network interface in {manager.ClientName}'s settings",
                    LogLevel.Warn);
                return;
            }
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
        if (_conflictClientName != manager.ClientName)
        {
            _conflictClientName = manager.ClientName;
            _clientSettingsConflictActive = false;
            _cyclesSinceConflictCheck = ConflictCheckEveryNCycles; // check the new client this cycle
        }

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

    // Port detection failed despite the VPN being connected. Logs at Warn (the other two
    // failure paths use Info because they correspond to expected disconnection states).
    private Task HandlePortDetectionFailureAsync(IVpnManager vpnManager, AppConfig cfg, CancellationToken cancellationToken) =>
        _recoveryGate.RegisterFailureAndTryRecoveryAsync(
            $"Port detection failed on '{vpnManager.ProviderName}'", LogLevel.Warn,
            vpnManager.GetRecoveryAction(), vpnManager.GetRecoveryTarget(), vpnManager.ProviderName,
            RecoverySettingsOf(cfg), cancellationToken);

    // The part of the cycle's configuration the failed-cycle recovery gate reads.
    private static RecoveryGate.RecoverySettings RecoverySettingsOf(AppConfig cfg) =>
        new(cfg.VpnAutoRecoveryEnabled, cfg.VpnAutoRecoveryTriggerCycles, cfg.UpdateInterval);

    // Dispatches a recovery action to the helper service. Shared by the failed-cycle trigger
    // (RecoveryGate.TriggerRecoveryIfDueAsync), the port-closed trigger (MaybeTriggerPortClosedRecoveryAsync),
    // the exit-address reconnects (ReconnectForExitAddressAsync), and the on-demand recovery test
    // (TestRecoveryAsync, manualTest = true). A manual test is
    // not counted in the session statistic, whose label reads "Auto-recoveries (session)" precisely
    // so that exclusion is correct by construction - a test the user ran by hand is not the app
    // recovering itself, and counting it would inflate a health figure. It is still recorded in the
    // history and arms the "after recovery" annotation like a real one.
    internal static async Task DispatchRecoveryAsync(string action, string recoveryTarget, string displayName, CancellationToken cancellationToken, bool manualTest = false, string? triggerLogMessage = null)
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
            Interlocked.Increment(ref _recoveryGeneration);
            Volatile.Write(ref _lastRecoveryDispatchTick, Environment.TickCount64);
            await AutoRecoveryManager.TriggerRestartAsync(recoveryTarget, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastRecoveryDispatchTick, Environment.TickCount64);
        }
        else if (action == HelperProtocol.ActionCycleAdapter)
        {
            PortHistoryManager.Append(PortHistoryKind.Recovery, null, $"{trigger} triggered for '{displayName}' (adapter cycle)");
            if (!manualTest) SessionStats.RecordRecovery();
            Interlocked.Increment(ref _recoveryGeneration);
            Volatile.Write(ref _lastRecoveryDispatchTick, Environment.TickCount64);
            await AutoRecoveryManager.TriggerCycleAdapterAsync(recoveryTarget, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastRecoveryDispatchTick, Environment.TickCount64);
        }
        else
        {
            LogManager.Instance.LogMessage($"Unknown recovery action '{action}' for '{displayName}' - skipping", LogLevel.Warn);
        }
    }

    // Which client has which of the three exclusive settings is now decided once, by whether its
    // ClientRegistry row declares the key - a client without one reads false in ReadConfig, so there
    // is nothing to gate here any more. The reasoning behind each row's declaration lives with the
    // row: RestartOnDisconnect is qBittorrent-only because it is the only client where restarting is
    // both possible and the right response to a dropped connection, and WarnOnInterfaceMismatch needs
    // a named adapter, which only qBittorrent and Nicotine+ report. That last point is about the name
    // check alone - see IManagedClient.SupportsInterfaceMismatchWarning for what each client actually
    // binds, which is not the same question and does not have the same answer.
    private static (bool ForceStart, bool Restart, bool RestartOnDisconnect, bool WarnOnInterfaceMismatch) GetClientBehaviorConfig(AppConfig cfg) =>
        (cfg.Client.ForceStart, cfg.Client.Restart, cfg.Client.RestartOnDisconnect, cfg.Client.WarnOnInterfaceMismatch);

    private static int GetDefaultPort(AppConfig cfg) => cfg.Client.DefaultPort;

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

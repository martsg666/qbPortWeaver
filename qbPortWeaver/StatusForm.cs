namespace qbPortWeaver;

/// <summary>
/// Read-only panel showing the live state of the port-sync chain (VPN, forwarded port, client,
/// listening port, reachability, last sync) sourced from the JSON status file the sync loop writes
/// each cycle. Opened from the tray menu and by double-clicking the tray icon; refreshed live by
/// MainForm on each completed cycle.
/// </summary>
public partial class StatusForm : Form
{
    /// <summary>Raised when the user clicks Sync Now. MainForm handles it by triggering an immediate
    /// sync cycle; the resulting cycle completion repaints this panel via <see cref="RefreshStatus"/>.</summary>
    public event EventHandler? SyncRequested;

    /// <summary>Raised when the user clicks Pause/Resume. MainForm handles it by toggling the sync
    /// pause (the same toggle as the tray menu item) and refreshing the panel.</summary>
    public event EventHandler? PauseResumeRequested;

    /// <summary>Raised when the user clicks Test Port. MainForm handles it by running an on-demand
    /// reachability check and reporting the outcome via <see cref="SetReachableChecking"/> /
    /// <see cref="SetReachableResult"/>.</summary>
    public event EventHandler? TestPortRequested;

    /// <summary>Raised when the user clicks Run Diagnostics. MainForm handles it by running the
    /// read-only health check and showing the results dialog, toggling the button via
    /// <see cref="SetDiagnosticsRunning"/>.</summary>
    public event EventHandler? DiagnosticsRequested;

    // Whether the history list currently shows real entries (false = empty-state row).
    // Gates the Clear History context item so it cannot "clear" an already-empty history.
    private bool _historyHasEntries;

    // Last non-null snapshot read from the status file. Retained so the statistics figures that
    // need live snapshot data (current port) and the next-sync estimate keep their last good value
    // when a refresh momentarily cannot read the file.
    private StatusSnapshot? _lastSnapshot;

    // Ticks once per second while the panel is open to advance the time-derived values (the Next
    // sync countdown, the Last sync "ago" suffix, the Monitoring since elapsed, and the Reachable
    // age) between sync cycles, without re-reading the status file. Full refreshes still happen on
    // each cycle via MainForm.
    private System.Windows.Forms.Timer? _clockTimer;

    // Remembered reachability so the panel can show "Open (4m ago)" and carry it across the cycles
    // where verification is throttled (the snapshot's portVerified is null on those cycles).
    // _reachableCheckedAt is the source event's time (the verifying cycle's timestamp, or now for a
    // manual Test Port); a later result is only adopted when its time is at least as recent, so a
    // stale snapshot never overrides a fresh manual result. _reachableUndetermined records that the
    // last thing learned was an inconclusive manual test, distinct from never having checked.
    private bool? _lastReachable;
    private DateTimeOffset? _reachableCheckedAt;
    private bool _reachableUndetermined;

    /// <summary>Set by MainForm before each refresh so the Next sync estimate can show "Paused"
    /// instead of a countdown that will never fire while sync is paused.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SyncPaused { get; set; }

    public StatusForm()
    {
        InitializeComponent();
        Text = $"{AppIdentity.AppName} | Status";
        AttachStatsContextMenu();
    }

    // The Statistics group's Clear menu also has to be attached to each child label:
    // ContextMenuStrip does not propagate from a container to its children, and the group's
    // face is almost entirely labels. Kept out of the designer file so a designer round-trip
    // cannot strip it.
    private void AttachStatsContextMenu()
    {
        foreach (Control child in grpStats.Controls)
            child.ContextMenuStrip = ctxStats;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshStatus();
        _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _clockTimer?.Stop();
        _clockTimer?.Dispose();
        _clockTimer = null;
        base.OnFormClosed(e);
    }

    // Recomputes only the values that drift with the wall clock - the Next sync countdown, the Last
    // sync "ago" suffix, the Monitoring since elapsed duration, and the Reachable age. The remaining
    // values change only when a cycle completes, so they are left untouched here. Monitoring since
    // needs no snapshot, so it advances even before the first cycle has produced one.
    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        PopulateMonitoringSince();
        if (_lastSnapshot is null) return;
        PopulateLastSync(_lastSnapshot);
        PopulateNextSync(_lastSnapshot);
        // Auto-recovery is otherwise cycle-driven, but it carries the recovery-hold countdown, which
        // drifts with the wall clock like the values above. Without this the countdown would sit frozen
        // for a whole cycle - visibly wrong once it is counting in seconds rather than minutes.
        PopulateAutoRecovery(_lastSnapshot, IsPortSyncDisabled(_lastSnapshot));
        // Advance the Reachable age, but never while a manual Test Port is in flight (button
        // disabled) - SetReachableResult owns the label until the result arrives.
        if (btnTestPort.Enabled)
            PopulateReachable(_lastSnapshot);
    }

    /// <summary>Re-reads the status file and repaints the panel. Called on load, on Refresh, and by
    /// MainForm after each sync cycle. A failed/missing read keeps the current display unchanged.</summary>
    public void RefreshStatus()
    {
        if (IsDisposed) return;
        // Synchronous read on the UI thread is intentional: the status file is a sub-1KB local
        // JSON the app just wrote, read at most once per sync cycle - offloading it would add
        // background/marshal complexity for no measurable gain.
        UpdatePauseButton();
        var snapshot = StatusManager.TryRead();
        if (snapshot is not null) _lastSnapshot = snapshot;
        PopulateHistoryAndStatistics();
        if (snapshot is null)
        {
            // No cycle has run yet (or the file was momentarily unreadable). Only show the
            // placeholder when nothing has been populated; otherwise keep the last good values.
            if (lblLastSyncValue.Text == "-")
                SetNeutral(lblLastSyncValue, "Waiting for first sync cycle…");
            return;
        }
        Populate(snapshot);
    }

    // Reads the persisted history once and feeds both consumers: the history list and the
    // history-derived statistics values.
    private void PopulateHistoryAndStatistics()
    {
        var entries = PortHistoryManager.Read();
        PopulateHistory(entries);
        PopulateStatistics(entries);
    }

    // Repaints the port history list, newest first. Rebuilt in full on each refresh - the history
    // is capped at 50 entries, so this is a trivial repaint once per sync cycle, and full rebuild
    // keeps it correct across trims and file resets.
    private void PopulateHistory(IReadOnlyList<PortHistoryEntry> entries)
    {
        _historyHasEntries = entries.Count > 0;
        lvHistory.BeginUpdate();
        lvHistory.Items.Clear();
        if (entries.Count == 0)
        {
            lvHistory.Items.Add(new ListViewItem(["-", "-", "No port changes recorded yet"]) { ForeColor = NeutralColor });
        }
        else
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                PortHistoryEntry entry = entries[i];
                lvHistory.Items.Add(new ListViewItem(
                    [
                        entry.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                        entry.Port?.ToString() ?? "-",
                        entry.Event,
                    ])
                {
                    // Normal port changes read in the default text color; confirmed-closed and
                    // recovery events carry the warning accent, mirroring the panel's other values.
                    ForeColor = entry.Kind == PortHistoryKind.PortChanged ? SystemColors.WindowText : WarnColor,
                });
            }
        }
        lvHistory.EndUpdate();
        // Size the Event column to its longest entry so long events (recovery entries, cause
        // annotations) can be read by scrolling horizontally, but never narrower than the
        // designer width so short content still fills the list without a stray blank area.
        colHistoryEvent.AutoResize(ColumnHeaderAutoResizeStyle.ColumnContent);
        if (colHistoryEvent.Width < EventColumnMinWidth)
            colHistoryEvent.Width = EventColumnMinWidth;
    }

    // Matches the Event column's designer width, which fills the list (with Time + Port) exactly.
    private const int EventColumnMinWidth = 350;

    // Fills the Statistics group: the currently managed port (from the live snapshot), today's
    // change count (from the persisted port history), and the in-memory session counters. Refreshed
    // on the same tick as the rest of the panel. Scope is spelled out per figure - "today" for the
    // calendar-day change count, "(session)" for the process-lifetime counters - so the two clocks
    // do not read alike.
    private void PopulateStatistics(IReadOnlyList<PortHistoryEntry> entries)
    {
        int changesToday = 0;
        DateTime today = DateTime.Now.Date;
        foreach (PortHistoryEntry entry in entries)
        {
            if (entry.Kind != PortHistoryKind.PortChanged) continue;
            if (entry.Timestamp.LocalDateTime.Date == today)
                changesToday++;
        }

        // The client's confirmed listening port. Shown only when the client actually reports one -
        // "-" otherwise (client down / not yet read), rather than substituting the VPN-forwarded
        // port the app intends to apply, which the client may not be listening on.
        if (_lastSnapshot?.ClientPort is int port)
            SetDefault(lblCurrentPortValue, port.ToString());
        else
            SetNeutral(lblCurrentPortValue, "-");

        SetDefault(lblChangesTodayValue, changesToday.ToString());

        // OK count is read before the total: the sync loop increments the total first, so this
        // order guarantees the derived failure count is never negative even when a cycle completes
        // between the two reads. The clamp additionally covers the Clear Statistics reset race,
        // where a sync straddling the non-atomic Reset can briefly leave the stored OK above the
        // stored total. Failures (not OKs) are surfaced - that is the number worth acting on.
        int syncsOk = SessionStats.SyncOkCount;
        int syncs = SessionStats.SyncCount;
        if (syncsOk > syncs) syncsOk = syncs;
        if (syncs == 0)
            SetNeutral(lblSyncsValue, "-");
        else
        {
            int failed = syncs - syncsOk;
            SetDefault(lblSyncsValue, failed == 0 ? $"{syncs} (all OK)" : $"{syncs} ({failed} failed)");
        }

        SetDefault(lblRecoveriesValue, SessionStats.RecoveryCount.ToString());

        PopulateMonitoringSince();
    }

    // The session start time plus its live elapsed duration. Split out so the clock tick can
    // advance the elapsed suffix once a second, matching the Last sync and Next sync values -
    // without re-running the counter reads or the history file scan in PopulateStatistics.
    private void PopulateMonitoringSince()
    {
        DateTimeOffset started = SessionStats.StartedAt;
        string startedText = started.LocalDateTime.Date == DateTime.Now.Date
            ? started.LocalDateTime.ToString("HH:mm")
            : started.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        SetDefault(lblMonitoringSinceValue, $"{startedText} ({FormatDuration(DateTimeOffset.Now - started)})");
    }

    // The single compact duration formatter for every relative-time value on the panel: "3d 4h",
    // "8h 28m", "12m", "45s". Shared by Monitoring since, the Next sync countdown, and the ">1 minute"
    // part of the "ago" suffix so their granularity (including seconds under a minute) reads alike.
    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }

    private void Populate(StatusSnapshot s)
    {
        bool disabled = IsPortSyncDisabled(s);

        PopulateVpnProvider(s, disabled);
        PopulateVpnStatus(s, disabled);
        PopulateAutoRecovery(s, disabled);
        PopulateForwardedPort(s);
        PopulateClient(s);
        PopulateListeningPort(s);
        // Skip the reachable repaint while a manual Test Port is in flight (button disabled),
        // so a sync cycle completing mid-test does not overwrite the "Checking…" label with a
        // stale snapshot value. SetReachableResult writes the real result when the test finishes.
        if (btnTestPort.Enabled)
            PopulateReachable(s);
        PopulateLastSync(s);
        PopulateNextSync(s);
        UpdateDiagnosticsHint(s, disabled);
    }

    // Surfaces the "run diagnostics" cue only when the last cycle shows a problem worth investigating:
    // an error result, the client's port out of sync, the port confirmed closed, or the client not
    // running. Benign states (sync disabled, or a VPN that is simply off) do not trigger it, so the
    // cue does not nag during normal idle periods.
    private void UpdateDiagnosticsHint(StatusSnapshot s, bool disabled)
    {
        bool outOfSync = s.ClientPort is int cp && s.VpnPort is int vp && cp != vp;
        bool closed = s.PortVerified == false;
        bool clientDown = !string.IsNullOrEmpty(s.Client) && !s.ClientRunning;
        bool error = string.Equals(s.Status, SyncStatusValues.Error, StringComparison.OrdinalIgnoreCase);
        bool problem = !disabled && (error || outOfSync || closed || clientDown);

        lblDiagnosticsHint.Visible = problem;
        if (problem)
            lblDiagnosticsHint.ForeColor = error ? ErrorColor : WarnColor;
    }

    private void PopulateVpnProvider(StatusSnapshot s, bool disabled)
    {
        if (disabled)
            SetNeutral(lblVpnProviderValue, "Disabled");
        else
            SetDefault(lblVpnProviderValue, s.VpnProvider!);
    }

    // Shared by the full repaint and the one-second tick so both agree on what "disabled" means.
    private static bool IsPortSyncDisabled(StatusSnapshot s) =>
        string.IsNullOrEmpty(s.VpnProvider) ||
        string.Equals(s.VpnProvider, RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);

    private void PopulateVpnStatus(StatusSnapshot s, bool disabled)
    {
        if (disabled)
            SetNeutral(lblVpnStatusValue, "Port sync disabled");
        else if (s.VpnConnected)
            SetColor(lblVpnStatusValue, "Connected", OkColor);
        else
            SetColor(lblVpnStatusValue, "Not connected", WarnColor);
    }

    // Recovery state lives on its own row rather than riding the VPN status line. The hold was
    // originally appended to "Not connected", which hid it in the case it most needed to explain:
    // VpnConnected is set before the port fetch, so an upstream outage with the tunnel still up reads
    // as a green "Connected" and suppressed the hold entirely. A labelled row is also reachable during
    // the ordinary failure - a streak building toward the threshold - which the VPN line cannot show.
    private void PopulateAutoRecovery(StatusSnapshot s, bool disabled)
    {
        (string text, bool warn) = DescribeAutoRecovery(s, disabled);
        if (warn) SetColor(lblAutoRecoveryValue, text, WarnColor);
        else SetNeutral(lblAutoRecoveryValue, text);

        // The label ellipsises rather than clipping (see the designer), so the tooltip carries the
        // untruncated text. The part that gets cut is the countdown at the end of the longest value,
        // which is the half a user is reading the row for.
        toolTip.SetToolTip(lblAutoRecoveryValue, text);
    }

    // One phrase for "nothing is holding recovery back any more", reached from three places: either
    // hold expiring, or the streak passing the threshold. A shared constant because those three sites
    // previously spelled it two different ways ("Attempt due" and "Recovery due"), which is the drift
    // three separate literals invite. Phrased as an action rather than a state ("due" read as overdue,
    // as though a restart had been missed) and it names the trigger explicitly, because the previous
    // wording left a reader unsure whether anything was actually going to happen. "Failed" is
    // load-bearing: a cycle that succeeds ends the streak, and nothing restarts.
    private const string RecoveryNextCycleText = "Will trigger on the next failed cycle";
    // No countdown, unlike the two holds: this state ends on a successful port read rather than at a
    // deadline. Says what stopped it and what starts it again, so the row is not simply a dead end.
    private const string RecoverySuspendedText = "Suspended - restarts did not restore the port, resumes when one is found";

    // What the Auto-recovery row says, and whether it warrants the warning accent. Split from the
    // display above so every outcome routes through one place that also sets the tooltip - with the
    // branches setting the label directly, a new state could easily be added without one.
    private static (string Text, bool Warn) DescribeAutoRecovery(StatusSnapshot s, bool disabled)
    {
        // A threshold of 0 is not a configurable value (ReadConfig clamps it to at least 1), so it can
        // only mean no cycle has published one yet - a status file written by a version before these
        // keys existed, or a cycle that failed before reading config. Reporting "Disabled" on that
        // would be a confident claim about a setting that was never read, and "Disabled" is exactly
        // what a user opens this row to rule out.
        if (s.RecoveryTriggerCycles == 0) return ("-", false);

        // "Disabled" has to account for both triggers. Auto-recovery is two independent triggers with
        // two independent settings - failed cycles and a confirmed-closed port - and either one can
        // restart the VPN with the other switched off. Testing only RecoveryEnabled here told a user
        // who had turned that one off that recovery was disabled, while the port-closed trigger was
        // still live and able to restart their VPN service.
        if (disabled || (!s.RecoveryEnabled && !s.PortClosedRecoveryEnabled)) return ("Disabled", false);

        // The failed-cycle trigger's own states, reported only while that trigger is on - with it off
        // its counters keep whatever value they last held, and none of them can lead to a recovery.
        //
        // Failed-cycle states take precedence, and the reason is a judgement call rather than an
        // impossibility - do not reorder these on the assumption that the two cannot coexist. They
        // can: PortSyncService resets _confirmedClosedCount only when the port verifies open, when
        // the trigger fires, or when it is switched off, never when a cycle fails. So a closed-check
        // count survives into a failure streak and sits hidden behind it. That is the right outcome,
        // because a port cannot be verified at all while the VPN is down: the streak is the live
        // signal and the closed count is stale history.
        if (s.RecoveryEnabled && DescribeFailedCycleRecovery(s) is string failedCycle) return (failedCycle, true);

        // Falls through to the port-closed trigger, which is why the block above returns only for its
        // active states: with no failure streak running, the port-closed trigger may still have
        // something to say, and before this it could never say it.
        if (DescribePortClosedRecovery(s) is string portClosed) return (portClosed, true);

        return ("Idle", false);
    }

    // What the failed-cycle trigger is doing, or null when it is idle. Split out so the row can fall
    // through to the port-closed trigger, and so neither description drives this method's complexity.
    private static string? DescribeFailedCycleRecovery(StatusSnapshot s)
    {
        // Checked ahead of both holds because it outranks them: they defer the next recovery, this one
        // says no further recovery will be attempted at all until a port reads successfully. Reporting a
        // countdown above it would promise a retry that is not coming.
        if (s.RecoverySuspended) return RecoverySuspendedText;

        // Both holds are reported, each naming its own cause. The offline one is checked first only
        // because it is the more serious: nothing can be recovered until the connection returns,
        // whereas the sustained floor clears on its own within a couple of cycles.
        if (DescribeRecoveryHold(s) is string hold) return hold;

        if (DescribeSustainedHold(s) is string sustained) return sustained;

        // Past the threshold with neither hold in force, recovery runs on the next failed cycle.
        // Reported as due rather than as a count: "6 of 3 failed cycles" reads as a counter still
        // climbing toward a target it passed three cycles ago, which looks like a defect even when
        // nothing is wrong. The threshold itself needs no zero check - the guard at the top of
        // DescribeAutoRecovery has already returned for the only case that can produce one.
        if (s.RecoveryFailedCycles >= s.RecoveryTriggerCycles) return RecoveryNextCycleText;

        if (s.RecoveryFailedCycles > 0)
        {
            return $"{s.RecoveryFailedCycles} of {s.RecoveryTriggerCycles} failed " +
                   $"{TextFormat.PluralizeNoun(s.RecoveryTriggerCycles, "cycle")}";
        }

        return null;
    }

    // What the port-closed trigger is doing, or null when it is off or idle. Counted in confirmed
    // closed checks rather than cycles, matching how the log reports the same progress.
    private static string? DescribePortClosedRecovery(StatusSnapshot s)
    {
        // The threshold is clamped to at least 1 when read, so 0 means no cycle has published one -
        // the same version-guard case the top of DescribeAutoRecovery handles for the other trigger.
        if (!s.PortClosedRecoveryEnabled || s.PortClosedRecoveryTriggerChecks == 0) return null;

        // The one-shot trigger has fired and cannot fire again until a verification reports the port
        // open. Worth its own state precisely because nothing else on the panel shows it: the port
        // stays closed, the Reachable row keeps saying so, and without this the row would read "Idle"
        // while the trigger for that exact condition is spent.
        // "the next scheduled check" rather than "the port to verify open": only the sync cycle's own
        // verification re-arms the trigger. The panel's Test Port button runs a throwaway read-only
        // check that touches no sync state, so wording this as something the user can go and do left
        // them clicking Test Port, being told the port is open, and seeing this line unchanged.
        if (!s.PortClosedRecoveryArmed) return "Triggered - waiting for the next scheduled check";

        if (s.PortClosedRecoveryChecks > 0)
        {
            return $"{s.PortClosedRecoveryChecks} of {s.PortClosedRecoveryTriggerChecks} closed " +
                   $"{TextFormat.PluralizeNoun(s.PortClosedRecoveryTriggerChecks, "check")}";
        }

        return null;
    }

    // "Holding - no internet connection, retry in ~15m" while the offline rate limiter is
    // waiting, or null when it is not - the same "nothing to report" signal every other describer
    // here uses, which is what tells the caller to fall through to the other hold and then the
    // failure streak.
    private static string? DescribeRecoveryHold(StatusSnapshot s)
    {
        if (s.RecoveryHoldUntil is not DateTimeOffset until) return null;
        return DescribeCountdown(until) is string when
            ? $"Holding - no internet connection, retry in {when}"
            : RecoveryNextCycleText;
    }

    // "Holding - failures too recent, retry in ~48s" while the sustained-failure
    // floor is waiting, or null when it is not. Its own line rather than a shared "holding" message,
    // because the two holds mean different things to a user: this one clears by itself in a cycle or
    // two, while the connectivity hold lasts as long as the outage does.
    private static string? DescribeSustainedHold(StatusSnapshot s)
    {
        if (s.RecoverySustainedUntil is not DateTimeOffset until) return null;
        return DescribeCountdown(until) is string when
            ? $"Holding - failures too recent, retry in {when}"
            : RecoveryNextCycleText;
    }

    // Time left until an absolute deadline, or null once it has passed. Counted down from the instant
    // the cycle wrote, so it stays correct however long that cycle took; the panel's one-second repaint
    // keeps it moving. Formatted through FormatDuration and prefixed "~" exactly like the Next sync
    // countdown, so the two read alike - a second formatter here rounded 90s up to "~2 min", which both
    // overstated the wait and disagreed with every other duration on the panel.
    private static string? DescribeCountdown(DateTimeOffset until)
    {
        TimeSpan remaining = until - DateTimeOffset.Now;
        return remaining > TimeSpan.Zero ? $"~{FormatDuration(remaining)}" : null;
    }

    private void PopulateForwardedPort(StatusSnapshot s)
    {
        if (s.VpnPort is int vpnPort)
            SetDefault(lblForwardedPortValue, vpnPort.ToString());
        else
            SetNeutral(lblForwardedPortValue, "-");
    }

    private void PopulateClient(StatusSnapshot s)
    {
        if (string.IsNullOrEmpty(s.Client))
            SetNeutral(lblClientValue, "-");
        else if (s.ClientRunning)
            SetDefault(lblClientValue, s.Client);
        else
            SetColor(lblClientValue, $"{s.Client} (not running)", ErrorColor);
    }

    // In-sync state is only judged when a forwarded port is known to compare against.
    private void PopulateListeningPort(StatusSnapshot s)
    {
        if (s.ClientPort is not int clientPort)
            SetNeutral(lblListeningPortValue, "-");
        else if (s.VpnPort is int target)
            SetColor(lblListeningPortValue,
                clientPort == target ? $"{clientPort} (in sync)" : $"{clientPort} (out of sync)",
                clientPort == target ? OkColor : WarnColor);
        else
            SetDefault(lblListeningPortValue, clientPort.ToString());
    }

    private void PopulateReachable(StatusSnapshot s)
    {
        // Adopt this cycle's verification result only when it is a definite value from a source at
        // least as recent as the one already shown, so a throttled cycle (portVerified null) keeps
        // the last known result and a stale snapshot cannot override a fresh manual test.
        if (s.PortVerified is bool verified)
        {
            DateTimeOffset checkedAt = s.Timestamp ?? DateTimeOffset.Now;
            if (_reachableCheckedAt is null || checkedAt >= _reachableCheckedAt)
            {
                _lastReachable = verified;
                _reachableCheckedAt = checkedAt;
                _reachableUndetermined = false;
            }
        }
        RenderReachable();
    }

    // Paints the Reachable label from the remembered result plus a live "ago" age. Shared by the
    // per-cycle refresh, the once-a-second tick, and the manual Test Port result so all three
    // render identically.
    private void RenderReachable()
    {
        if (_lastReachable is bool reachable)
        {
            string age = FormatReachableAge(_reachableCheckedAt);
            if (reachable)
                SetColor(lblReachableValue, $"Open{age}", OkColor);
            else
                SetColor(lblReachableValue, $"Closed{age}", WarnColor);
        }
        else if (_reachableUndetermined)
            SetNeutral(lblReachableValue, "Could not determine");
        else
            SetNeutral(lblReachableValue, "Not checked");
    }

    // " (now)" / " (4m ago)" freshness suffix for the Reachable label - same wording as Last sync
    // (see FormatAgo); empty when the check time is unknown.
    private static string FormatReachableAge(DateTimeOffset? checkedAt)
    {
        if (checkedAt is not DateTimeOffset at) return string.Empty;
        return $" ({FormatAgo(DateTimeOffset.Now - at)})";
    }

    private void PopulateLastSync(StatusSnapshot s)
    {
        if (s.Timestamp is not DateTimeOffset ts)
        {
            SetNeutral(lblLastSyncValue, "-");
            return;
        }

        // Same shape as a log timestamp, so the format comes from the one constant that defines it -
        // but deliberately NOT LoggingConstants.DateCulture: this is a displayed local time, which
        // should follow the user's locale. Only the log file itself is culture-pinned, because it is
        // a machine-readable contract two processes write to.
        string time = ts.LocalDateTime.ToString(LoggingConstants.DateFormat);
        // Capitalize the displayed result so it matches the panel's other values (Connected, Open,
        // etc.). The raw lowercase status value stays in the JSON file - that is the contract
        // external scripts read; only the panel display is title-cased.
        (string result, Color color) = s.Status switch
        {
            SyncStatusValues.Success => ("Success", OkColor),
            SyncStatusValues.Skipped => ("Skipped", NeutralColor),
            SyncStatusValues.Error => ("Error", ErrorColor),
            _ => (string.IsNullOrEmpty(s.Status) ? "Unknown" : s.Status, ErrorColor)
        };
        // Relative suffix so "how recent was this?" reads at a glance without mental arithmetic.
        SetColor(lblLastSyncValue, $"{time} - {result} ({FormatAgo(DateTimeOffset.Now - ts)})", color);
    }

    // Relative age of a past event: "now" under a minute, otherwise "12m ago" / "2h 5m ago". Shared
    // by the Last sync and Reachable lines so their freshness suffix reads identically. Sub-minute
    // reads "now" (not "45s ago") - for a freshness cue the exact seconds do not matter.
    private static string FormatAgo(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1)) return "now";
        return $"{FormatDuration(elapsed)} ago";
    }

    // When the next scheduled cycle is due, counted down to the instant the cycle published in
    // nextSyncAt. Still approximate, hence the "~": a manual sync, a network-change re-check or an
    // error backoff can move it after the status file was written.
    //
    // Deriving it from the last sync time plus the configured interval is the fallback, used only for
    // a status file written before nextSyncAt existed - not the primary path. That derivation reads
    // the duration against the wrong origin: timestamp is stamped when the cycle starts while the wait
    // begins when it ends, so the countdown runs out early by the cycle's length, up to two minutes
    // after one carrying an auto-recovery round trip. See the nextSyncAt notes in docs/SYNC-CYCLE.md.
    //
    // Four outcomes: the countdown, "Paused" while sync is paused (a countdown would imply a cycle
    // that will not fire), "Startup grace period" during the grace window (which re-checks on a short
    // poll, so a full-interval countdown would mislead), and "-" before the first cycle.
    private void PopulateNextSync(StatusSnapshot s)
    {
        if (SyncPaused)
        {
            SetNeutral(lblNextSyncValue, "Paused");
            return;
        }
        if (s.WaitingForVpn)
        {
            // Startup grace window: the cycle re-checks on a short poll, so a full-interval countdown
            // would mislead. Report the state instead, matching the "startup grace period" wording used
            // in the status message and the log.
            SetNeutral(lblNextSyncValue, "Startup grace period");
            return;
        }
        // Prefer the instant the cycle published. Deriving it from Timestamp is the fallback for a
        // status file written before nextSyncAt existed, and is deliberately not the primary path:
        // Timestamp is stamped at the cycle's start while the wait begins at its end, so the derived
        // value runs out early by however long the cycle took - up to two minutes when the cycle
        // included an auto-recovery round trip.
        DateTimeOffset dueAt;
        if (s.NextSyncAt is DateTimeOffset published)
        {
            dueAt = published;
        }
        else if (s.Timestamp is DateTimeOffset ts)
        {
            int interval = RegistrySettingsManager.GetInt(
                RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
            if (interval < AppConstants.MinUpdateIntervalSeconds)
                interval = AppConstants.DefaultUpdateIntervalSeconds;
            dueAt = ts.AddSeconds(interval);
        }
        else
        {
            SetNeutral(lblNextSyncValue, "-");
            return;
        }

        TimeSpan remaining = dueAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
            SetDefault(lblNextSyncValue, "Due now");
        else
            SetDefault(lblNextSyncValue, $"~{FormatDuration(remaining)}");
    }

    // Accent colors follow AboutForm: brighter variants in dark mode, deeper ones in light mode.
    private static Color OkColor => ThemeColors.StatusOk;
    private static Color WarnColor => ThemeColors.StatusWarning;
    private static Color ErrorColor => ThemeColors.StatusError;
    private static Color NeutralColor => SystemColors.GrayText;

    private static void SetColor(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }

    private static void SetDefault(Label label, string text) => SetColor(label, text, SystemColors.ControlText);
    private static void SetNeutral(Label label, string text) => SetColor(label, text, NeutralColor);

    private void ctxHistory_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ctxClearHistory.Enabled = _historyHasEntries;

    // Confirmed like Clear Logs: the history file is deleted outright and cannot be recovered.
    // ctxHistory_Opening already disables the item on an empty list, so this only ever prompts
    // when there is something to lose.
    private void ctxClearHistory_Click(object? sender, EventArgs e)
    {
        var confirm = ThemedMessageBox.Show(
            "The recorded port history will be deleted. This cannot be undone.\n\nContinue?",
            AppIdentity.AppName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        PortHistoryManager.Clear();
        PopulateHistoryAndStatistics();
    }

    private void ctxStats_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ctxClearStats.Enabled = SessionStats.SyncCount > 0 || SessionStats.RecoveryCount > 0;

    // Confirmed like Clear History. The counters are in memory rather than on disk, but that is not
    // the line the convention draws - it asks whether the user can get the data back, and they
    // cannot: the window runs from app start or the last clear, which on a tray app left running is
    // easily weeks of counting. MediaManagerForm's Clear Cache stays unconfirmed because a cache
    // genuinely does come back ("run Scan Now to re-index").
    // ctxStats_Opening already disables the item while every counter is zero, so this only ever
    // prompts when there is something to lose.
    private void ctxClearStats_Click(object? sender, EventArgs e)
    {
        var confirm = ThemedMessageBox.Show(
            $"The statistics counted over the past {FormatDuration(DateTimeOffset.Now - SessionStats.StartedAt)} " +
            "will be reset. This cannot be undone.\n\nContinue?",
            AppIdentity.AppName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        SessionStats.Reset();
        PopulateHistoryAndStatistics();
    }

    private void btnSyncNow_Click(object? sender, EventArgs e) => SyncRequested?.Invoke(this, EventArgs.Empty);

    private void btnPauseResume_Click(object? sender, EventArgs e) => PauseResumeRequested?.Invoke(this, EventArgs.Empty);

    private void btnTestPort_Click(object? sender, EventArgs e) => TestPortRequested?.Invoke(this, EventArgs.Empty);

    // The button label is the action it performs: "Resume" while paused, "Pause" while running.
    // MainForm keeps SyncPaused current before each refresh, so this reflects the live state.
    private void UpdatePauseButton() => btnPauseResume.Text = SyncPaused ? "Resume" : "Pause";

    private void btnRunDiagnostics_Click(object? sender, EventArgs e) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Toggles the Run Diagnostics button between its idle and in-progress states so it cannot
    /// be re-triggered while a run is in flight.</summary>
    public void SetDiagnosticsRunning(bool running)
    {
        if (IsDisposed) return;
        btnRunDiagnostics.Enabled = !running;
        btnRunDiagnostics.Text = running ? "Running…" : "Run Diagnostics";
    }

    /// <summary>Shows the in-progress state while an on-demand reachability test runs and disables the
    /// Test Port button so it cannot be re-triggered until the result arrives.</summary>
    public void SetReachableChecking()
    {
        if (IsDisposed) return;
        btnTestPort.Enabled = false;
        SetNeutral(lblReachableValue, "Checking…");
    }

    /// <summary>Displays the result of an on-demand reachability test (open / closed / could not
    /// determine) and re-enables the Test Port button. Mirrors the coloring of
    /// <see cref="PopulateReachable"/>; an undetermined result reads "Could not determine" rather than
    /// the loop's "Not checked", since the user explicitly asked for a check.</summary>
    public void SetReachableResult(bool? open)
    {
        if (IsDisposed) return;
        // Stamp the check time to now so this manual result is the most recent (a later snapshot
        // from an older cycle will not override it) and its "ago" age ticks from here.
        _reachableCheckedAt = DateTimeOffset.Now;
        if (open is bool v)
        {
            _lastReachable = v;
            _reachableUndetermined = false;
        }
        else
        {
            _lastReachable = null;
            _reachableUndetermined = true;
        }
        RenderReachable();
        btnTestPort.Enabled = true;
    }

    private void btnClose_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static
}

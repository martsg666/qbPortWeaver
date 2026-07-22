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
    // "checked ago" age) between sync cycles, without re-reading the status file. Full refreshes
    // still happen on each cycle via MainForm.
    private System.Windows.Forms.Timer? _clockTimer;

    // Remembered reachability so the panel can show "Open (checked 4m ago)" and carry it across the
    // cycles where verification is throttled (the snapshot's portVerified is null on those cycles).
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

    // Recomputes only the values that drift with the wall clock - the Next sync countdown, the
    // Last sync "ago" suffix, and the Monitoring since elapsed duration. The rest of the panel
    // changes only when a cycle completes, so it is left untouched here (and the reachable label
    // must not be repainted mid-test, which this deliberately avoids). Monitoring since needs no
    // snapshot, so it advances even before the first cycle has produced one.
    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        PopulateMonitoringSince();
        if (_lastSnapshot is null) return;
        PopulateLastSync(_lastSnapshot);
        PopulateNextSync(_lastSnapshot);
        // Advance the "checked ago" age, but never while a manual Test Port is in flight (button
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
    private const int EventColumnMinWidth = 256;

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
        SetDefault(lblMonitoringSinceValue, $"{startedText} ({FormatElapsed(DateTimeOffset.Now - started)})");
    }

    // Compact elapsed-time display for the statistics values: "3d 4h", "8h 28m", "12m", "<1m".
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalDays >= 1) return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h";
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes}m";
        return "<1m";
    }

    private void Populate(StatusSnapshot s)
    {
        bool disabled = string.IsNullOrEmpty(s.VpnProvider) ||
                        string.Equals(s.VpnProvider, RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);

        PopulateVpnProvider(s, disabled);
        PopulateVpnStatus(s, disabled);
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

    private void PopulateVpnStatus(StatusSnapshot s, bool disabled)
    {
        if (disabled)
            SetNeutral(lblVpnStatusValue, "Port sync disabled");
        else if (s.VpnConnected)
            SetColor(lblVpnStatusValue, "Connected", OkColor);
        else
            SetColor(lblVpnStatusValue, "Not connected", WarnColor);
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

    // Paints the Reachable label from the remembered result plus a live "checked ago" age. Shared by
    // the per-cycle refresh, the once-a-second tick, and the manual Test Port result so all three
    // render identically.
    private void RenderReachable()
    {
        if (_lastReachable is bool reachable)
        {
            string age = FormatCheckedAgo(_reachableCheckedAt);
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

    // " (checked just now)" / " (checked 4m ago)" suffix for the Reachable label; empty when the
    // check time is unknown.
    private static string FormatCheckedAgo(DateTimeOffset? checkedAt)
    {
        if (checkedAt is not DateTimeOffset at) return string.Empty;
        string elapsed = FormatElapsed(DateTimeOffset.Now - at);
        return elapsed == "<1m" ? " (checked just now)" : $" (checked {elapsed} ago)";
    }

    private void PopulateLastSync(StatusSnapshot s)
    {
        if (s.Timestamp is not DateTimeOffset ts)
        {
            SetNeutral(lblLastSyncValue, "-");
            return;
        }

        string time = ts.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
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
        string elapsed = FormatElapsed(DateTimeOffset.Now - ts);
        string ago = elapsed == "<1m" ? "just now" : $"{elapsed} ago";
        SetColor(lblLastSyncValue, $"{time}  -  {result} ({ago})", color);
    }

    // Best-effort estimate of when the next scheduled cycle runs: the last sync time plus the
    // configured interval. Approximate by nature (a manual sync, a network-change re-sync, or an
    // error backoff can move it), hence the "~". Shows "Paused" while sync is paused - the countdown
    // would otherwise imply a cycle that will not fire - and "-" before the first cycle.
    private void PopulateNextSync(StatusSnapshot s)
    {
        if (SyncPaused)
        {
            SetNeutral(lblNextSyncValue, "Paused");
            return;
        }
        if (s.Timestamp is not DateTimeOffset ts)
        {
            SetNeutral(lblNextSyncValue, "-");
            return;
        }

        int interval = RegistrySettingsManager.GetInt(
            RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
        if (interval < AppConstants.MinUpdateIntervalSeconds)
            interval = AppConstants.DefaultUpdateIntervalSeconds;

        TimeSpan remaining = ts.AddSeconds(interval) - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
            SetDefault(lblNextSyncValue, "due now");
        else
            SetDefault(lblNextSyncValue, $"~{FormatCountdown(remaining)}");
    }

    // Compact countdown for the next-sync estimate: "2h 5m", "3m", "45s".
    private static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}m";
        return $"{(int)remaining.TotalSeconds}s";
    }

    // Accent colors follow AboutForm: brighter variants in dark mode, deeper ones in light mode.
    private static Color OkColor => AppConstants.StatusOk;
    private static Color WarnColor => AppConstants.StatusWarning;
    private static Color ErrorColor => AppConstants.StatusError;
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
        var confirm = MessageBox.Show(
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

    // No confirmation, unlike Clear History: these are in-memory session counters that reset on
    // every app restart anyway - nothing irreversible is lost (per the confirmation convention).
    private void ctxClearStats_Click(object? sender, EventArgs e)
    {
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
        // from an older cycle will not override it) and its "checked ago" age ticks from here.
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

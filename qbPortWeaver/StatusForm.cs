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
    }

    /// <summary>Re-reads the status file and repaints the panel. Called on load, on Refresh, and by
    /// MainForm after each sync cycle. A failed/missing read keeps the current display unchanged.</summary>
    public void RefreshStatus()
    {
        if (IsDisposed) return;
        // Synchronous read on the UI thread is intentional: the status file is a sub-1KB local
        // JSON the app just wrote, read at most once per sync cycle - offloading it would add
        // background/marshal complexity for no measurable gain.
        var snapshot = StatusManager.TryRead();
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

    // Fills the Statistics group: two figures derived from the persisted port history (current
    // port held, changes today) plus the in-memory session counters. Refreshed on the same tick
    // as the rest of the panel, so "held" durations advance once per sync cycle.
    private void PopulateStatistics(IReadOnlyList<PortHistoryEntry> entries)
    {
        PortHistoryEntry? lastChange = null;
        int changesToday = 0;
        DateTime today = DateTime.Now.Date;
        foreach (PortHistoryEntry entry in entries)
        {
            if (entry.Kind != PortHistoryKind.PortChanged) continue;
            lastChange = entry; // entries are oldest first, so the last hit is the newest
            if (entry.Timestamp.LocalDateTime.Date == today)
                changesToday++;
        }

        if (lastChange is null)
            SetNeutral(lblPortHeldValue, "-");
        else
            SetDefault(lblPortHeldValue, FormatElapsed(DateTimeOffset.Now - lastChange.Timestamp));

        SetDefault(lblChangesTodayValue, changesToday.ToString());

        // OK count is read before the total: the sync loop increments the total first, so this
        // order guarantees the displayed OK count never exceeds the displayed total even when a
        // cycle completes between the two reads. The clamp additionally covers the Clear
        // Statistics reset race, where a sync straddling the non-atomic Reset can briefly leave
        // the stored OK above the stored total.
        int syncsOk = SessionStats.SyncOkCount;
        int syncs = SessionStats.SyncCount;
        if (syncsOk > syncs) syncsOk = syncs;
        if (syncs == 0)
            SetNeutral(lblSyncsValue, "-");
        else
            SetDefault(lblSyncsValue, $"{syncs} ({syncsOk} OK)");

        SetDefault(lblRecoveriesValue, SessionStats.RecoveryCount.ToString());

        DateTimeOffset started = SessionStats.StartedAt;
        string startedText = started.LocalDateTime.Date == today
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
        switch (s.PortVerified)
        {
            case true: SetColor(lblReachableValue, "Open", OkColor); break;
            case false: SetColor(lblReachableValue, "Closed", WarnColor); break;
            default: SetNeutral(lblReachableValue, "Not checked"); break;
        }
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
        SetColor(lblLastSyncValue, $"{time}  -  {result}", color);
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

    private void btnTestPort_Click(object? sender, EventArgs e) => TestPortRequested?.Invoke(this, EventArgs.Empty);

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
        switch (open)
        {
            case true: SetColor(lblReachableValue, "Open", OkColor); break;
            case false: SetColor(lblReachableValue, "Closed", WarnColor); break;
            default: SetNeutral(lblReachableValue, "Could not determine"); break;
        }
        btnTestPort.Enabled = true;
    }

    private void btnClose_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static
}

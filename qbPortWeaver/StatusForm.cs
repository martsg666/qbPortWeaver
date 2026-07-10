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

    public StatusForm()
    {
        InitializeComponent();
        Text = $"{AppIdentity.AppName} | Status";
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
        PopulateHistory();
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

    // Repaints the port history list from the persisted history file, newest first. Rebuilt in
    // full on each refresh - the history is capped at 50 entries, so this is a trivial repaint
    // once per sync cycle, and full rebuild keeps it correct across trims and file resets.
    private void PopulateHistory()
    {
        var entries = PortHistoryManager.Read();
        lvHistory.BeginUpdate();
        lvHistory.Items.Clear();
        if (entries.Count == 0)
        {
            lvHistory.Items.Add(new ListViewItem(["-", "", "No port changes recorded yet"]) { ForeColor = NeutralColor });
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

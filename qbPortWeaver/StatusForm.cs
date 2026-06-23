namespace qbPortWeaver;

/// <summary>
/// Read-only panel showing the live state of the port-sync chain (VPN, forwarded port, client,
/// listening port, reachability, last sync) sourced from the JSON status file the sync loop writes
/// each cycle. Opened from the tray menu and by double-clicking the tray icon; refreshed live by
/// MainForm on each completed cycle.
/// </summary>
public partial class StatusForm : Form
{
    private bool _isDarkMode;

    /// <summary>Raised when the user clicks Sync Now. MainForm handles it by triggering an immediate
    /// sync cycle; the resulting cycle completion repaints this panel via <see cref="RefreshStatus"/>.</summary>
    public event EventHandler? SyncRequested;

    public StatusForm()
    {
        InitializeComponent();
        Text = $"{AppIdentity.AppName} | Status";
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _isDarkMode = AppConstants.IsDarkModeEnabled();
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

    private void Populate(StatusSnapshot s)
    {
        bool disabled = string.IsNullOrEmpty(s.VpnProvider) ||
                        string.Equals(s.VpnProvider, RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);

        PopulateVpnProvider(s, disabled);
        PopulateVpnStatus(s, disabled);
        PopulateForwardedPort(s);
        PopulateClient(s);
        PopulateListeningPort(s);
        PopulateReachable(s);
        PopulateLastSync(s);
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
        string result = string.IsNullOrEmpty(s.Status) ? "unknown" : s.Status;
        Color color = s.Status switch
        {
            SyncStatusValues.Success => OkColor,
            SyncStatusValues.Skipped => NeutralColor,
            _ => ErrorColor
        };
        SetColor(lblLastSyncValue, $"{time}  -  {result}", color);
    }

    // Accent colors follow AboutForm: brighter variants in dark mode, deeper ones in light mode.
    private Color OkColor => _isDarkMode ? AppConstants.StatusOk : AppConstants.StatusOkLight;
    private Color WarnColor => _isDarkMode ? AppConstants.StatusWarning : AppConstants.StatusWarningLight;
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

    private void btnClose_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static
}

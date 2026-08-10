namespace qbPortWeaver;

/// <summary>Settings dialog for configuring VPN provider, client connection, sync interval, and extra options.</summary>
public partial class SettingsForm : Form
{
    // Set to true when the user clicks Save; MainForm reads this after the dialog closes to decide whether to trigger an immediate sync.
    internal bool SettingsSaved { get; private set; }

    private const string DiscoveringAdaptersPlaceholder = "Discovering adapters\u2026";
    private const string NoAdaptersFoundPlaceholder = "No NAT-PMP adapters found";
    private const string DefaultPortTooltip = "Port to apply when the VPN is disconnected (0 = do nothing when disconnected)";

    // Cancels in-flight async work (NAT-PMP adapter discovery, client connection tests) when the
    // form closes so probes do not run to completion in the background after the dialog is dismissed.
    private readonly CancellationTokenSource _formCloseCts = new();

    // The per-client controls that the client selection switches between. Looking them up by name
    // keeps show/hide, enable/disable, detection fill-in, and URL validation from each carrying
    // their own chain of client comparisons - which is how those four drifted apart previously.
    private sealed record ClientControls(GroupBox Group, TextBox Url, TextBox ProcessName, TextBox ExePath);

    private readonly Dictionary<string, ClientControls> _clientControls;

    public SettingsForm()
    {
        InitializeComponent();
        Text = $"{AppIdentity.AppName} | Settings";

        _clientControls = new Dictionary<string, ClientControls>(StringComparer.OrdinalIgnoreCase)
        {
            [RegistrySettingsManager.ClientNameQBittorrent] =
                new(grpQBittorrent, txtQBittorrentURL, txtQBittorrentProcessName, txtQBittorrentExePath),
            [RegistrySettingsManager.ClientNameTransmission] =
                new(grpTransmission, txtTransmissionURL, txtTransmissionProcessName, txtTransmissionExePath),
            [RegistrySettingsManager.ClientNameDeluge] =
                new(grpDeluge, txtDelugeURL, txtDelugeProcessName, txtDelugeExePath),
            [RegistrySettingsManager.ClientNameNicotine] =
                new(grpNicotine, txtNicotineURL, txtNicotineProcessName, txtNicotineExePath)
        };
    }

    // The controls for the selected client. Falls back to the registry's own default client so an
    // unrecognised stored value behaves the same here as it does in ClientRegistry.Resolve.
    private (string Name, ClientControls Controls) SelectedClient
    {
        get
        {
            string? selected = cboClient.SelectedItem?.ToString();
            if (selected is not null && _clientControls.TryGetValue(selected, out var match))
                return (selected, match);

            string fallback = ClientRegistry.All[0].Name;
            return (fallback, _clientControls[fallback]);
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // The auto-recovery rows read as inline phrases ("Trigger after [N] <unit>"), so the spinner
        // sits flush after its indented "Trigger after" label rather than on the form's field column.
        // PreferredSize.Width is the measured text width (correct even before this tab has laid out),
        // so both identical rows resolve to the same X and stay aligned with each other.
        int inlineGap = LogicalToDeviceUnits(6); // scale the 6px gap with DPI so it stays proportional at 125%+
        nudPortClosedChecks.Left     = lblPortClosedChecks.Left + lblPortClosedChecks.PreferredSize.Width + inlineGap;
        lblPortClosedChecksUnit.Left = nudPortClosedChecks.Left + nudPortClosedChecks.Width + inlineGap;
        nudRecoveryCycles.Left       = lblRecoveryCycles.Left + lblRecoveryCycles.PreferredSize.Width + inlineGap;
        lblRecoveryCyclesUnit.Left   = nudRecoveryCycles.Left + nudRecoveryCycles.Width + inlineGap;

        // The "Trigger after" labels are AutoSize (~15px tall) but share a row with the 23px-tall
        // spinner and unit label. Pinned at the same top they float above the spinner's centre, so
        // centre them vertically on the spinner using the measured heights.
        lblPortClosedChecks.Top = nudPortClosedChecks.Top + (nudPortClosedChecks.Height - lblPortClosedChecks.Height) / 2;
        lblRecoveryCycles.Top   = nudRecoveryCycles.Top + (nudRecoveryCycles.Height - lblRecoveryCycles.Height) / 2;
        SetupTooltips();
        LoadSettings();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _formCloseCts.Cancel();
        _formCloseCts.Dispose();
        base.OnFormClosed(e);
    }

    // Wire up tooltips for each setting control
    private void SetupTooltips()
    {
        toolTip.SetToolTip(cboVpnProvider, "VPN provider used for port detection (Disabled, ProtonVPN, PIA, or NAT-PMP)");
        toolTip.SetToolTip(cboNatPmpAdapter, "Network adapter to use for NAT-PMP port mapping (only applies when NAT-PMP is selected)");
        toolTip.SetToolTip(btnRefreshAdapters, "Refresh the adapter list");
        toolTip.SetToolTip(nudUpdateInterval, "How often to run the sync cycle, in seconds - controls both port sync and Media Manager frequency");
        toolTip.SetToolTip(cboClient, "Client to control (qBittorrent, Transmission, Deluge, or Nicotine+)");
        toolTip.SetToolTip(btnDetectClient, "Detect a running or installed client and fill in its selection and process details");
        toolTip.SetToolTip(btnTestRecovery, "Run the recovery action now to verify it works - restarts the VPN service (or cycles the adapter), so the VPN connection drops briefly");
        toolTip.SetToolTip(txtQBittorrentURL, "URL for the qBittorrent Web UI (e.g. http://127.0.0.1:8080). The Web UI must be enabled in qBittorrent under Tools > Options > Web UI.");
        toolTip.SetToolTip(txtQBittorrentUserName, "Username for the qBittorrent Web UI");
        toolTip.SetToolTip(txtQBittorrentPassword, "Password for the qBittorrent Web UI");
        toolTip.SetToolTip(txtQBittorrentExePath, "Path to the qBittorrent executable, used to start or restart the application");
        toolTip.SetToolTip(btnBrowseQBittorrentExePath, "Browse for the qBittorrent executable");
        toolTip.SetToolTip(btnTestQBittorrent, "Test the connection to qBittorrent using the URL and credentials above");
        toolTip.SetToolTip(txtQBittorrentProcessName, "Process name used to detect if qBittorrent is running (usually qbittorrent)");
        toolTip.SetToolTip(chkRestartQBittorrent, "Restart qBittorrent after updating the port - recommended for the change to take effect immediately");
        toolTip.SetToolTip(chkForceStartQBittorrent, "Automatically launch qBittorrent if it is not already running");
        toolTip.SetToolTip(nudQBittorrentDefaultPort, DefaultPortTooltip);
        toolTip.SetToolTip(chkWarnOnInterfaceMismatch, "Show a warning when qBittorrent's network interface does not match the configured VPN provider");
        toolTip.SetToolTip(chkRestartOnDisconnect, "Automatically restart qBittorrent when its connection status becomes disconnected");
        toolTip.SetToolTip(chkFixInterfaceBinding,
            "qBittorrent stores its network interface as an internal identifier that stops resolving when a VPN " +
            "recreates its adapter. It then listens on nothing while still showing the right adapter name, and a " +
            "restart cannot fix it. Re-applies the binding automatically when that happens.");
        toolTip.SetToolTip(txtTransmissionURL, "URL for the Transmission RPC endpoint (e.g. http://127.0.0.1:9091). Remote access must be enabled in Transmission Preferences > Remote (not required when running as a service).");
        toolTip.SetToolTip(txtTransmissionUserName, "Username for the Transmission RPC (leave empty if authentication is disabled)");
        toolTip.SetToolTip(txtTransmissionPassword, "Password for the Transmission RPC (leave empty if authentication is disabled)");
        toolTip.SetToolTip(txtTransmissionExePath, "Path to the Transmission executable, used to start or restart the application when running as a user-space process");
        toolTip.SetToolTip(btnBrowseTransmissionExePath, "Browse for the Transmission executable");
        toolTip.SetToolTip(btnTestTransmission, "Test the connection to Transmission using the URL and credentials above");
        toolTip.SetToolTip(txtTransmissionProcessName, "Process name used to detect if Transmission is running as a user-space process (e.g. transmission-qt)");
        toolTip.SetToolTip(chkRestartTransmission, "Restart Transmission after updating the port - recommended for the change to take effect immediately");
        toolTip.SetToolTip(chkForceStartTransmission, "Automatically launch Transmission if it is not already running");
        toolTip.SetToolTip(nudTransmissionDefaultPort, DefaultPortTooltip);
        toolTip.SetToolTip(txtDelugeURL, "URL for the Deluge Web UI (e.g. http://127.0.0.1:8112). The Web UI plugin must be enabled in Deluge's Plugin Manager.");
        toolTip.SetToolTip(txtDelugePassword, "Password for the Deluge Web UI");
        toolTip.SetToolTip(txtDelugeExePath, "Path to the Deluge executable, used to start or restart the application");
        toolTip.SetToolTip(btnBrowseDelugeExePath, "Browse for the Deluge executable");
        toolTip.SetToolTip(btnTestDeluge, "Test the connection to Deluge using the URL and password above");
        toolTip.SetToolTip(txtDelugeProcessName, "Process name used to detect if Deluge is running (usually deluge)");
        toolTip.SetToolTip(chkRestartDeluge, "Restart Deluge after updating the port - recommended for the change to take effect immediately");
        toolTip.SetToolTip(chkForceStartDeluge, "Automatically launch Deluge if it is not already running");
        toolTip.SetToolTip(nudDelugeDefaultPort, DefaultPortTooltip);
        toolTip.SetToolTip(txtNicotineURL, "Address of the qbPortWeaver bridge plugin running inside Nicotine+ (e.g. http://127.0.0.1:38472). Found automatically - use ⟳ to fill it in.");
        toolTip.SetToolTip(txtNicotineToken, "Access token issued by the bridge plugin. Found automatically - use ⟳ to fill it in.");
        toolTip.SetToolTip(btnDetectNicotinePlugin, "Read the address and token from the bridge plugin's connection file");
        toolTip.SetToolTip(txtNicotineExePath, "Path to the Nicotine+ executable, used to start it and to find a portable installation's data folder");
        toolTip.SetToolTip(btnBrowseNicotineExePath, "Browse for the Nicotine+ executable");
        toolTip.SetToolTip(btnTestNicotine, "Test the connection to the bridge plugin using the address and token above");
        toolTip.SetToolTip(txtNicotineProcessName, "Process name used to detect if Nicotine+ is running (usually Nicotine+)");
        toolTip.SetToolTip(chkForceStartNicotine, "Automatically launch Nicotine+ if it is not already running");
        toolTip.SetToolTip(chkNicotineWarnOnInterfaceMismatch, "Show a warning when Nicotine+'s network interface does not match the configured VPN provider");
        toolTip.SetToolTip(nudNicotineDefaultPort, DefaultPortTooltip);
        toolTip.SetToolTip(btnInstallNicotinePlugin, "Install the qbPortWeaver bridge plugin into Nicotine+. Nicotine+ has no remote control of its own, so the plugin is what makes port sync possible.");
        toolTip.SetToolTip(txtPostUpdateCmd, "Shell command to run after a successful port update (leave empty to disable)");
        toolTip.SetToolTip(chkDebugMode, "Write verbose debug entries to the log file");
        toolTip.SetToolTip(cboColorTheme, "Application color theme (System, Dark, or Light) - a restart prompt will appear if changed");
        toolTip.SetToolTip(chkResyncOnNetworkChange, "When a network or VPN connection change is detected, run a sync right away instead of waiting for the next interval - so the client follows a VPN reconnect within seconds. Pausing still suppresses the cycle.");
        toolTip.SetToolTip(chkWaitForVpnOnStartup, "For the first minute and a half after the app starts, wait quietly while the VPN is still connecting instead of reporting it as disconnected or applying the default port. The port syncs as soon as the VPN comes up.");
        toolTip.SetToolTip(chkVerifyPort, "After each sync, check that the port is reachable from the Internet. Transmission and Deluge use their built-in online port checkers; qBittorrent infers it from incoming peer activity (an idle client may report closed). Runs after a port change and periodically.");
        toolTip.SetToolTip(chkAutoRecovery, "Triggers auto-recovery (VPN service restart, or adapter cycle for NAT-PMP gateways) after the configured number of consecutive cycles where the VPN is disconnected or assigns no forwarded port. Client-side problems do not count - auto-recovery cannot fix those.");
        toolTip.SetToolTip(nudRecoveryCycles, "Number of consecutive cycles without an assigned port or VPN connection before auto-recovery is triggered");
        toolTip.SetToolTip(chkPortClosedRecovery, "Triggers auto-recovery (same action as the no-port trigger) when port verification has confirmed the assigned port closed for the configured number of checks. Fires at most once, then re-arms only after the port tests open again. Caution with qBittorrent: an idle client (no active transfers) can report closed indefinitely.");
        toolTip.SetToolTip(nudPortClosedChecks, "Number of confirmed closed checks before auto-recovery is triggered");
        toolTip.SetToolTip(chkNotifyOnPortUpdate, "Show a tray notification when the port is successfully updated");
        toolTip.SetToolTip(chkShowUpdateForm, "When checked, opens the update form at startup if a newer version is found. When unchecked, only a tray notification is shown (12-hour periodic check runs either way).");
    }

    private void LoadSettings()
    {
        // NAT-PMP placeholder must be in place before cboVpnProvider is set so that
        // cboVpnProvider_SelectedIndexChanged sees discoveryPending = true and disables
        // all adapter controls correctly while discovery is in flight.
        cboNatPmpAdapter.Items.Clear();
        cboNatPmpAdapter.Items.Add(DiscoveringAdaptersPlaceholder);
        cboNatPmpAdapter.SelectedIndex = 0;

        // General
        cboVpnProvider.Items.Clear();
        cboVpnProvider.Items.AddRange(
        [
            RegistrySettingsManager.VpnProviderDisabled,
            RegistrySettingsManager.VpnProviderProtonVpn,
            RegistrySettingsManager.VpnProviderPia,
            RegistrySettingsManager.VpnProviderNatPmp
        ]);
        cboVpnProvider.SelectedItem = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider);
        if (cboVpnProvider.SelectedIndex < 0)
            cboVpnProvider.SelectedIndex = 0;

        cboClient.Items.Clear();
        cboClient.Items.AddRange(ClientRegistry.All.Select(c => (object)c.Name).ToArray());
        cboClient.SelectedItem = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyClient);
        if (cboClient.SelectedIndex < 0) cboClient.SelectedIndex = 0;

        // NAT-PMP adapter discovery is async to avoid blocking the UI.
        // Launched after VPN provider is set so the completion callback reads the correct state.
        string savedAdapter = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);
        _ = DiscoverNatPmpAdaptersAsync(savedAdapter); // fire-and-forget; exceptions are handled inside DiscoverNatPmpAdaptersAsync

        nudUpdateInterval.Value = Math.Clamp(
            RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds),
            (int)nudUpdateInterval.Minimum, (int)nudUpdateInterval.Maximum);

        chkAutoRecovery.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryEnabled);
        nudRecoveryCycles.Value = Math.Clamp(
            RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryTriggerCycles),
            (int)nudRecoveryCycles.Minimum, (int)nudRecoveryCycles.Maximum);
        chkPortClosedRecovery.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryEnabled);
        nudPortClosedChecks.Value = Math.Clamp(
            RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryTriggerChecks),
            (int)nudPortClosedChecks.Minimum, (int)nudPortClosedChecks.Maximum);
        UpdateAutoRecoverySubControls();
        chkNotifyOnPortUpdate.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNotifyOnPortUpdate);
        chkShowUpdateForm.Checked     = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyShowUpdateFormOnStartup);
        chkResyncOnNetworkChange.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyResyncOnNetworkChange);
        chkWaitForVpnOnStartup.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyWaitForVpnOnStartup);
        chkVerifyPort.Checked         = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVerifyPortAfterSync);

        // qBittorrent
        txtQBittorrentURL.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl);
        txtQBittorrentUserName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName);
        txtQBittorrentPassword.Text = RegistrySettingsManager.GetQBittorrentPassword();
        txtQBittorrentExePath.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath);
        txtQBittorrentProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName);

        chkRestartQBittorrent.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent);
        chkForceStartQBittorrent.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent);
        chkWarnOnInterfaceMismatch.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch);
        chkRestartOnDisconnect.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect);
        chkFixInterfaceBinding.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyFixInterfaceBinding);

        nudQBittorrentDefaultPort.Value = Math.Clamp(
            RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort),
            (int)nudQBittorrentDefaultPort.Minimum, (int)nudQBittorrentDefaultPort.Maximum);

        // Transmission
        txtTransmissionURL.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUrl);
        txtTransmissionUserName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUserName);
        txtTransmissionPassword.Text = RegistrySettingsManager.GetTransmissionPassword();
        txtTransmissionExePath.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionExePath);
        txtTransmissionProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionProcessName);
        chkRestartTransmission.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyRestartTransmission);
        chkForceStartTransmission.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyForceStartTransmission);
        nudTransmissionDefaultPort.Value = Math.Clamp(
            RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyDefaultPort),
            (int)nudTransmissionDefaultPort.Minimum, (int)nudTransmissionDefaultPort.Maximum);

        // Deluge
        txtDelugeURL.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeUrl);
        txtDelugePassword.Text = RegistrySettingsManager.GetDelugePassword();
        txtDelugeExePath.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeExePath);
        txtDelugeProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeProcessName);
        chkRestartDeluge.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyRestartDeluge);
        chkForceStartDeluge.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyForceStartDeluge);
        nudDelugeDefaultPort.Value = Math.Clamp(
            RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDefaultPort),
            (int)nudDelugeDefaultPort.Minimum, (int)nudDelugeDefaultPort.Maximum);

        // Nicotine+
        txtNicotineURL.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineUrl);
        txtNicotineToken.Text = RegistrySettingsManager.GetNicotineToken();
        txtNicotineExePath.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineExePath);
        txtNicotineProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineProcessName);
        chkForceStartNicotine.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyForceStartNicotine);
        chkNicotineWarnOnInterfaceMismatch.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineWarnOnInterfaceMismatch);
        nudNicotineDefaultPort.Value = Math.Clamp(
            RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyDefaultPort),
            (int)nudNicotineDefaultPort.Minimum, (int)nudNicotineDefaultPort.Maximum);

        RefreshNicotinePluginStatus();

        UpdateClientGroupVisibility();

        // Extra
        cboColorTheme.Items.Clear();
        cboColorTheme.Items.AddRange(
        [
            RegistrySettingsManager.ColorThemeSystem,
            RegistrySettingsManager.ColorThemeDark,
            RegistrySettingsManager.ColorThemeLight
        ]);
        cboColorTheme.SelectedItem = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyColorTheme);
        if (cboColorTheme.SelectedIndex < 0) cboColorTheme.SelectedIndex = 0;
        txtPostUpdateCmd.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd);
        chkDebugMode.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode);
    }

    private void SaveSettings()
    {
        // General
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider, cboVpnProvider.SelectedItem?.ToString() ?? RegistrySettingsManager.VpnProviderDisabled);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyClient, cboClient.SelectedItem?.ToString() ?? RegistrySettingsManager.ClientNameQBittorrent);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds, ((int)nudUpdateInterval.Value).ToString());
        // If discovery is still pending (combo disabled), preserve the existing value to avoid
        // saving the "Discovering adapters…" placeholder text as the adapter name
        string adapterName = cboNatPmpAdapter.Enabled
            ? cboNatPmpAdapter.SelectedItem?.ToString() ?? string.Empty
            : RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName, adapterName);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryEnabled, chkAutoRecovery.Checked);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnAutoRecoveryTriggerCycles, ((int)nudRecoveryCycles.Value).ToString());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryEnabled, chkPortClosedRecovery.Checked);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyPortClosedRecoveryTriggerChecks, ((int)nudPortClosedChecks.Value).ToString());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNotifyOnPortUpdate, chkNotifyOnPortUpdate.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyShowUpdateFormOnStartup, chkShowUpdateForm.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyResyncOnNetworkChange, chkResyncOnNetworkChange.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyWaitForVpnOnStartup, chkWaitForVpnOnStartup.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVerifyPortAfterSync, chkVerifyPort.Checked);

        // qBittorrent
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl, txtQBittorrentURL.Text.Trim());
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName, txtQBittorrentUserName.Text.Trim());
        RegistrySettingsManager.SetQBittorrentPassword(txtQBittorrentPassword.Text);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath, txtQBittorrentExePath.Text.Trim());
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName, txtQBittorrentProcessName.Text.Trim());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent, chkRestartQBittorrent.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent, chkForceStartQBittorrent.Checked);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort, ((int)nudQBittorrentDefaultPort.Value).ToString());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch, chkWarnOnInterfaceMismatch.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect, chkRestartOnDisconnect.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyFixInterfaceBinding, chkFixInterfaceBinding.Checked);

        // Transmission
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUrl, txtTransmissionURL.Text.Trim());
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUserName, txtTransmissionUserName.Text.Trim());
        RegistrySettingsManager.SetTransmissionPassword(txtTransmissionPassword.Text);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionExePath, txtTransmissionExePath.Text.Trim());
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionProcessName, txtTransmissionProcessName.Text.Trim());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyRestartTransmission, chkRestartTransmission.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyForceStartTransmission, chkForceStartTransmission.Checked);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyDefaultPort, ((int)nudTransmissionDefaultPort.Value).ToString());

        // Deluge
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeUrl, txtDelugeURL.Text.Trim());
        RegistrySettingsManager.SetDelugePassword(txtDelugePassword.Text);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeExePath, txtDelugeExePath.Text.Trim());
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeProcessName, txtDelugeProcessName.Text.Trim());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyRestartDeluge, chkRestartDeluge.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyForceStartDeluge, chkForceStartDeluge.Checked);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDefaultPort, ((int)nudDelugeDefaultPort.Value).ToString());

        // Nicotine+ (the token is stored untrimmed, like the other clients' secrets)
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineUrl, txtNicotineURL.Text.Trim());
        RegistrySettingsManager.SetNicotineToken(txtNicotineToken.Text);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineExePath, txtNicotineExePath.Text.Trim());
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineProcessName, txtNicotineProcessName.Text.Trim());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyForceStartNicotine, chkForceStartNicotine.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineWarnOnInterfaceMismatch, chkNicotineWarnOnInterfaceMismatch.Checked);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyDefaultPort, ((int)nudNicotineDefaultPort.Value).ToString());

        // Extra
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyColorTheme, cboColorTheme.SelectedItem?.ToString() ?? RegistrySettingsManager.ColorThemeSystem);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd, txtPostUpdateCmd.Text.Trim());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode, chkDebugMode.Checked);
    }

    private void btnOK_Click(object? sender, EventArgs e)
    {
        if (cboVpnProvider.SelectedItem?.ToString() == RegistrySettingsManager.VpnProviderNatPmp &&
            cboNatPmpAdapter.Enabled &&
            cboNatPmpAdapter.SelectedItem?.ToString() == NoAdaptersFoundPlaceholder)
        {
            ThemedMessageBox.Show(
                "No NAT-PMP capable adapters were found.\n\nEnsure the adapter is up and its gateway is responding to NAT-PMP, then click \u21bb to retry.",
                AppIdentity.AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var (clientName, controls) = SelectedClient;
        string urlText = controls.Url.Text.Trim();
        if (!string.IsNullOrEmpty(urlText) &&
            (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            ThemedMessageBox.Show(
                $"The {clientName} URL is not valid. Enter a URL starting with http:// or https://",
                AppIdentity.AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string previousColorTheme = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyColorTheme);
        string selectedColorTheme = cboColorTheme.SelectedItem?.ToString() ?? RegistrySettingsManager.ColorThemeSystem;
        SaveSettings();
        LogManager.Instance.LogMessage("Settings saved", LogLevel.Info);
        SettingsSaved = true;

        // Color theme takes effect at startup via Application.SetColorMode - restart if it changed
        if (selectedColorTheme != previousColorTheme)
        {
            var result = ThemedMessageBox.Show(
                "The color theme change takes effect after restarting.\n\nRestart now?",
                AppIdentity.AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
                Application.Restart();
        }

        Close();
    }

    private void btnCancel_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static

    private void cboClient_SelectedIndexChanged(object? sender, EventArgs e) =>
        UpdateClientGroupVisibility();

    // Detects a running or installed supported client and pre-fills the selection plus that client's
    // process-name / executable fields, so the user does not have to know the exact values. Selection
    // and field values are applied but not saved - the user reviews them, Tests, then Saves.
    private void btnDetectClient_Click(object? sender, EventArgs e)
    {
        var detected = ClientDetector.DetectAll();
        if (detected.Count == 0)
        {
            ThemedMessageBox.Show(
                "No supported client was found running or installed in its default location.\n\nSelect your client manually and enter its connection details.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // A running client is the strongest signal, so prefer running ones; only fall back to the
        // installed-only matches when nothing is running. We auto-apply when that leaves a single
        // candidate and prompt when it is still ambiguous (several running, or several installed).
        var running = detected.Where(d => d.Kind == ClientDetector.DetectionKind.Running).ToList();
        var candidates = running.Count > 0 ? running : detected;

        ClientDetector.DetectedClient chosen;
        bool autoSelected = candidates.Count == 1;
        if (autoSelected)
        {
            chosen = candidates[0];
        }
        else
        {
            using var chooser = new ClientChooserForm(candidates, cboClient.SelectedItem?.ToString());
            if (chooser.ShowDialog(this) != DialogResult.OK) return;
            var picked = chooser.SelectedClient;
            if (picked is null) return;
            chosen = picked;
        }

        cboClient.SelectedItem = chosen.ClientName; // triggers UpdateClientGroupVisibility
        ApplyDetectedClientDetails(chosen);

        // The chooser already showed the user what they picked, so only confirm on an auto-selection.
        if (autoSelected)
        {
            string how = chosen.Kind == ClientDetector.DetectionKind.Running ? "running now" : "installed";
            ThemedMessageBox.Show(
                $"Detected {chosen.ClientName} ({how}).\n\nThe client selection and its process details have been filled in. Review the connection settings, then use Test before saving.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // Fills the matched client's process-name field (always) and executable field (only when a default
    // install was found, so a user's custom path is never blanked) on its Client-tab group.
    private void ApplyDetectedClientDetails(ClientDetector.DetectedClient d)
    {
        if (!_clientControls.TryGetValue(d.ClientName, out var controls)) return;

        controls.ProcessName.Text = d.ProcessName;
        if (d.ExePath is not null) controls.ExePath.Text = d.ExePath;

        // The Nicotine+ status line depends on the executable path, which may have just changed.
        if (d.ClientName == RegistrySettingsManager.ClientNameNicotine) RefreshNicotinePluginStatus();
    }

    private void UpdateClientGroupVisibility()
    {
        var (selectedName, selectedControls) = SelectedClient;

        foreach (var controls in _clientControls.Values)
            controls.Group.Visible = ReferenceEquals(controls, selectedControls);

        // Reflect the chosen client in the Client tab header.
        tabClient.Text = cboClient.SelectedItem?.ToString() ?? selectedName;
    }

    private void cboVpnProvider_SelectedIndexChanged(object? sender, EventArgs e)
    {
        bool isDisabled = cboVpnProvider.SelectedItem?.ToString() == RegistrySettingsManager.VpnProviderDisabled;
        SetPortSyncControlsEnabled(!isDisabled);

        // Only enable the adapter combo and refresh button if NAT-PMP is selected AND discovery has finished
        // (discovery replaces the placeholder and re-enables them via DiscoverNatPmpAdaptersAsync)
        bool isNatPmp = cboVpnProvider.SelectedItem?.ToString() == RegistrySettingsManager.VpnProviderNatPmp;
        bool discoveryPending = cboNatPmpAdapter.Items.Count == 1 &&
                                cboNatPmpAdapter.Items[0]?.ToString() == DiscoveringAdaptersPlaceholder;
        SetAdapterControlsEnabled(!isDisabled && isNatPmp && !discoveryPending);
    }

    private void btnRefreshAdapters_Click(object? sender, EventArgs e)
    {
        // Preserve current selection if it is a valid adapter name (not a placeholder)
        string current = cboNatPmpAdapter.Enabled &&
                         cboNatPmpAdapter.SelectedItem?.ToString() != NoAdaptersFoundPlaceholder
            ? cboNatPmpAdapter.SelectedItem?.ToString() ?? string.Empty
            : RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);

        cboNatPmpAdapter.Items.Clear();
        cboNatPmpAdapter.Items.Add(DiscoveringAdaptersPlaceholder);
        cboNatPmpAdapter.SelectedIndex = 0;
        SetAdapterControlsEnabled(false);
        _ = DiscoverNatPmpAdaptersAsync(current); // fire-and-forget; exceptions are handled inside DiscoverNatPmpAdaptersAsync
    }

    private void btnBrowseQBittorrentExePath_Click(object? sender, EventArgs e) => BrowseForExe("qBittorrent", txtQBittorrentExePath);
    private void btnBrowseDelugeExePath_Click(object? sender, EventArgs e) => BrowseForExe("Deluge", txtDelugeExePath);
    private void btnBrowseTransmissionExePath_Click(object? sender, EventArgs e) => BrowseForExe("Transmission", txtTransmissionExePath);

    private void btnBrowseNicotineExePath_Click(object? sender, EventArgs e)
    {
        BrowseForExe("Nicotine+", txtNicotineExePath);
        // A portable installation's data folder is derived from the executable path, so the
        // plugin's whereabouts may have just changed.
        RefreshNicotinePluginStatus();
    }

    // Shared OpenFileDialog driver for the client executable browse buttons.
    // Seeds InitialDirectory from the current path if the file exists so the user lands in the
    // right folder without having to navigate from scratch.
    private static void BrowseForExe(string clientName, TextBox target)
    {
        using var dlg = new OpenFileDialog
        {
            Title = $"Select {clientName} Executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
        };

        if (!string.IsNullOrWhiteSpace(target.Text) && File.Exists(target.Text))
            dlg.InitialDirectory = Path.GetDirectoryName(target.Text) ?? string.Empty;

        if (dlg.ShowDialog() == DialogResult.OK)
            target.Text = dlg.FileName;
    }

    // Runs the configured recovery action on demand so the recovery chain (helper service,
    // service discovery, VPN client relaunch) can be verified before a real failure needs it.
    // Uses the in-form provider selection like the client Test buttons. Confirmed first because
    // the action is disruptive (drops the VPN connection briefly) - Question icon, not Warning,
    // per the confirmation convention: nothing is irreversibly lost.
    private async void btnTestRecovery_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        var confirm = ThemedMessageBox.Show(
            "This will run the recovery action now: the VPN service is restarted (or the adapter cycled) and the VPN connection drops briefly.\n\nContinue?",
            AppIdentity.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        string provider = cboVpnProvider.SelectedItem?.ToString() ?? RegistrySettingsManager.VpnProviderDisabled;
        // While discovery is pending the combo is disabled and holds placeholder text, not an
        // adapter name (same guard as SaveSettings); an empty adapter takes the clean no-manager path.
        string adapter = cboNatPmpAdapter.Enabled
            ? cboNatPmpAdapter.SelectedItem?.ToString() ?? string.Empty
            : string.Empty;

        btnTestRecovery.Enabled = false;
        UseWaitCursor = true;
        try
        {
            // Linked to the form-close token like the connection tests. No extra timeout: the
            // helper pipe wait already has its own (the service stop/start takes a while by design).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_formCloseCts.Token);
            bool dispatched = await PortSyncService.TestRecoveryAsync(provider, adapter, cts.Token);
            if (IsDisposed) return;
            if (dispatched)
                ThemedMessageBox.Show(
                    "Recovery action completed. See the log for the detailed outcome.",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                ThemedMessageBox.Show(
                    "The recovery test could not run.\n\nCheck the VPN provider selection and see the log for details.",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            // Form closed while the test was in flight - nothing to report.
        }
        // The caller is an async void event handler, so an escape here would take the app down.
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Recovery test failed: {ex.Message}", LogLevel.Warn);
            if (!IsDisposed)
                ThemedMessageBox.Show(
                    $"The recovery test could not run.\n\n{ex.Message}",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed)
            {
                btnTestRecovery.Enabled = true;
                UseWaitCursor = false;
            }
        }
    }

    private async void btnTestQBittorrent_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        await RunConnectionTestAsync(
            () => new QBittorrentClient(
                txtQBittorrentURL.Text.Trim(), txtQBittorrentUserName.Text.Trim(), txtQBittorrentPassword.Text,
                txtQBittorrentProcessName.Text.Trim(), txtQBittorrentExePath.Text.Trim()),
            btnTestQBittorrent, txtQBittorrentURL.Text.Trim(), "qBittorrent");
    }

    private async void btnTestTransmission_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        await RunConnectionTestAsync(
            () => new TransmissionClient(
                txtTransmissionURL.Text.Trim(), txtTransmissionUserName.Text.Trim(), txtTransmissionPassword.Text,
                txtTransmissionProcessName.Text.Trim(), txtTransmissionExePath.Text.Trim()),
            btnTestTransmission, txtTransmissionURL.Text.Trim(), "Transmission");
    }

    private async void btnTestDeluge_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        await RunConnectionTestAsync(
            () => new DelugeClient(
                txtDelugeURL.Text.Trim(), txtDelugePassword.Text,
                txtDelugeProcessName.Text.Trim(), txtDelugeExePath.Text.Trim()),
            btnTestDeluge, txtDelugeURL.Text.Trim(), "Deluge");
    }

    private async void btnTestNicotine_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        await RunConnectionTestAsync(
            () => new NicotineClient(
                txtNicotineURL.Text.Trim(), txtNicotineToken.Text,
                txtNicotineProcessName.Text.Trim(), txtNicotineExePath.Text.Trim()),
            btnTestNicotine, txtNicotineURL.Text.Trim(), "Nicotine+");
    }

    // Reads the address and token the bridge plugin published, so the user never has to copy them.
    private void btnDetectNicotinePlugin_Click(object? sender, EventArgs e)
    {
        var handshake = NicotinePluginDiscovery.TryRead(txtNicotineExePath.Text.Trim());
        if (handshake is null)
        {
            var status = NicotinePluginInstaller.GetStatus(txtNicotineExePath.Text.Trim());
            ThemedMessageBox.Show(
                "No connection details were found.\r\n\r\n" + DescribeNextStep(status) +
                "\r\n\r\nIf Nicotine+ runs with a custom data folder, run /qbpw-connection-file inside " +
                "Nicotine+ and enter the address and token here by hand.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshNicotinePluginStatus();
            return;
        }

        txtNicotineURL.Text = handshake.Url;
        txtNicotineToken.Text = handshake.Token;
        RefreshNicotinePluginStatus();

        ThemedMessageBox.Show(
            $"Found the bridge plugin on {handshake.Url}.\r\n\r\nThe address and token have been filled in. " +
            "Use Test to confirm, then save.",
            AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Installs the bridge plugin and, when Nicotine+ is closed, offers to enable it too. Nicotine+
    // has no remote control of its own, so without the plugin there is nothing to sync against.
    private void btnInstallNicotinePlugin_Click(object? sender, EventArgs e)
    {
        string exePath = txtNicotineExePath.Text.Trim();

        var install = NicotinePluginInstaller.InstallFiles(exePath);
        if (!install.Success)
        {
            ThemedMessageBox.Show(install.Message, AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshNicotinePluginStatus();
            return;
        }

        bool running = IsNicotineRunning();
        if (running)
        {
            // Editing Nicotine+'s config now would be pointless: it rewrites the whole file from
            // memory when it exits, discarding anything changed underneath it.
            ThemedMessageBox.Show(
                $"Plugin installed to:\r\n{install.Message}\r\n\r\n" +
                "Nicotine+ is running, so enable it there: open Preferences → Plugins, tick " +
                "\"qbPortWeaver Bridge\", and apply. No restart is needed.\r\n\r\n" +
                "Then click ⟳ here to pick up the connection details.\r\n\r\n" +
                "Alternatively, close Nicotine+ and click Install plugin again to have it enabled automatically.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshNicotinePluginStatus();
            return;
        }

        var choice = ThemedMessageBox.Show(
            $"Plugin installed to:\r\n{install.Message}\r\n\r\n" +
            "Nicotine+ is closed, so it can be enabled for you now. Enable it?\r\n\r\n" +
            "Your current Nicotine+ configuration will be backed up first, and only the plugin list is changed.",
            AppIdentity.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (choice != DialogResult.Yes)
        {
            ThemedMessageBox.Show(
                "Plugin installed but not enabled.\r\n\r\nEnable \"qbPortWeaver Bridge\" in Nicotine+ under " +
                "Preferences → Plugins, then click ⟳ here.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshNicotinePluginStatus();
            return;
        }

        var enable = NicotinePluginInstaller.TryEnable(exePath, IsNicotineRunning);
        RefreshNicotinePluginStatus();

        if (!enable.Success)
        {
            ThemedMessageBox.Show(enable.Message, AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ThemedMessageBox.Show(
            "The plugin is installed and enabled.\r\n\r\nStart Nicotine+, then click ⟳ here to read its " +
            "connection details.",
            AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Uses the in-form process name rather than the saved one, so a user who has just corrected it
    // gets the right answer without saving first. Matches ManagedClientBase.IsRunning.
    private bool IsNicotineRunning()
    {
        string processName = txtNicotineProcessName.Text.Trim();
        if (processName.Length == 0) processName = "Nicotine+";

        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // Describes the plugin's state on the Nicotine+ group, from files alone - no network calls, so
    // this is safe to call while loading the form and after every action that could change it.
    private void RefreshNicotinePluginStatus()
    {
        var status = NicotinePluginInstaller.GetStatus(txtNicotineExePath.Text.Trim());
        lblNicotinePluginStatus.Text = status.Summary;

        btnInstallNicotinePlugin.Text = status.State switch
        {
            NicotinePluginState.NotInstalled => "Install plugin…",
            NicotinePluginState.Outdated => "Update plugin…",
            NicotinePluginState.NotEnabled => "Enable plugin…",
            _ => "Reinstall plugin…"
        };
    }

    private static string DescribeNextStep(NicotinePluginStatus status) => status.State switch
    {
        NicotinePluginState.DataFolderMissing =>
            "Nicotine+'s data folder was not found. Start Nicotine+ once, or set the Executable path above for a portable installation.",
        NicotinePluginState.NotInstalled =>
            "The bridge plugin is not installed. Click \"Install plugin\" first.",
        NicotinePluginState.Outdated =>
            "An older version of the bridge plugin is installed. Click \"Update plugin\".",
        NicotinePluginState.NotEnabled =>
            "The plugin is installed but not enabled. Enable \"qbPortWeaver Bridge\" in Nicotine+ under Preferences → Plugins.",
        _ => "The plugin is enabled but has not published its details yet. Start Nicotine+ and try again."
    };

    // Shared driver for the per-client "Test" buttons. Validates the URL first, then builds the
    // client from the in-form values (not the saved registry values) via the supplied factory so the
    // client is created only when the URL is valid. Probes it with GetPreferencesAsync - a full
    // auth + API round-trip that already logs detailed, client-specific failure reasons to the log
    // viewer. A non-null listening port is the success signal. The client is disposed via 'using'
    // and the button re-enabled in finally, even on cancel or form disposal.
    private async Task RunConnectionTestAsync(Func<IManagedClient> clientFactory, Button button, string url, string clientName) // NOSONAR S2325 - accesses instance state (UseWaitCursor, IsDisposed) for post-await UI safety
    {
        if (string.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ThemedMessageBox.Show(
                $"Enter a valid {clientName} URL starting with http:// or https:// before testing.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Constructed inside the try so nothing can throw past the catch to the async void caller,
        // or past the finally that restores the button and cursor. Disposed there instead of by a
        // `using`, which the try-scoping displaces.
        IManagedClient? client = null;
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            client = clientFactory();
            // Linked to the form-close token (like every other in-flight form operation) so closing
            // the dialog cancels the probe instead of letting it run out its timeout in the background.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_formCloseCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.ClientTestTimeoutSeconds));
            var (listenPort, _) = await client.GetPreferencesAsync(cts.Token);
            if (IsDisposed) return;
            if (listenPort is not null)
                ThemedMessageBox.Show(
                    $"Connected to {clientName} successfully.\n\nCurrent listening port: {listenPort}",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                ThemedMessageBox.Show(
                    $"Could not connect to {clientName}.\n\nCheck the URL and credentials, then see the log for details.",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                ThemedMessageBox.Show(
                    $"The {clientName} connection test timed out after {AppConstants.ClientTestTimeoutSeconds} seconds.\n\nCheck the URL and that the client is running.",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // The caller is an async void event handler, so an escape here would take the app down.
        // A failed connection test is never worth that.
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"{clientName} connection test failed: {ex.Message}", LogLevel.Warn);
            if (!IsDisposed)
                ThemedMessageBox.Show(
                    $"The {clientName} connection test could not run.\n\n{ex.Message}",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            client?.Dispose();
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                button.Enabled = true;
            }
        }
    }

    private void chkAutoRecovery_CheckedChanged(object? sender, EventArgs e) =>
        UpdateAutoRecoverySubControls();

    private void chkVerifyPort_CheckedChanged(object? sender, EventArgs e) =>
        UpdateAutoRecoverySubControls();

    private void chkPortClosedRecovery_CheckedChanged(object? sender, EventArgs e) =>
        UpdateAutoRecoverySubControls();

    private void UpdateAutoRecoverySubControls()
    {
        bool vpnActive = cboVpnProvider.SelectedItem?.ToString() != RegistrySettingsManager.VpnProviderDisabled;

        // Failed-sync recovery: independent trigger, gated only by its own checkbox.
        bool enabled = vpnActive && chkAutoRecovery.Checked;
        lblRecoveryCycles.Enabled = enabled;
        nudRecoveryCycles.Enabled = enabled;
        lblRecoveryCyclesUnit.Enabled = enabled;

        // Port-closed recovery: consumes verification results, so it depends only on
        // "Verify port after sync" - not on the failed-sync recovery trigger.
        bool closedRecoveryAvailable = vpnActive && chkVerifyPort.Checked;
        chkPortClosedRecovery.Enabled = closedRecoveryAvailable;
        bool closedChecksEnabled = closedRecoveryAvailable && chkPortClosedRecovery.Checked;
        lblPortClosedChecks.Enabled = closedChecksEnabled;
        nudPortClosedChecks.Enabled = closedChecksEnabled;
        lblPortClosedChecksUnit.Enabled = closedChecksEnabled;
    }

    // Enables or disables all port-sync-related controls (everything except VPN provider, update interval, and debug mode)
    private void SetPortSyncControlsEnabled(bool enabled)
    {
        // General section - client and auto-recovery controls (NAT-PMP adapter row handled by SetAdapterControlsEnabled)
        lblClient.Enabled = enabled;
        cboClient.Enabled = enabled;
        btnDetectClient.Enabled = enabled;
        chkVerifyPort.Enabled = enabled;
        chkAutoRecovery.Enabled = enabled;
        btnTestRecovery.Enabled = enabled;
        UpdateAutoRecoverySubControls();

        // Every client's section, whichever is currently shown
        foreach (var controls in _clientControls.Values) controls.Group.Enabled = enabled;

        // Extra section - post-update command (color mode and debug mode stay enabled)
        lblPostUpdateCmd.Enabled = enabled;
        txtPostUpdateCmd.Enabled = enabled;
    }

    private void SetAdapterControlsEnabled(bool enabled)
    {
        lblNatPmpAdapter.Enabled = enabled;
        cboNatPmpAdapter.Enabled = enabled;
        btnRefreshAdapters.Enabled = enabled;
    }

    private async Task DiscoverNatPmpAdaptersAsync(string savedAdapter)
    {
        try
        {
            // No ConfigureAwait(false) - continuation must run on the UI thread to update controls.
            var adapters = await NatPmpManager.DiscoverAdaptersAsync(cancellationToken: _formCloseCts.Token);

            // Guard against the form being closed while adapter discovery was in flight.
            // IsDisposed check + ObjectDisposedException catch covers the TOCTOU window between
            // the check and the first control write.
            if (IsDisposed) return;
            try
            {
                cboNatPmpAdapter.Items.Clear();
                if (adapters.Count == 0)
                {
                    cboNatPmpAdapter.Items.Add(NoAdaptersFoundPlaceholder);
                    cboNatPmpAdapter.SelectedIndex = 0;
                }
                else
                {
                    foreach (var adapter in adapters)
                        cboNatPmpAdapter.Items.Add(adapter.ProviderName);
                    cboNatPmpAdapter.SelectedItem = savedAdapter;
                    if (cboNatPmpAdapter.SelectedIndex < 0)
                        cboNatPmpAdapter.SelectedIndex = 0;
                }
                bool isNatPmp = cboVpnProvider.SelectedItem?.ToString() == RegistrySettingsManager.VpnProviderNatPmp;
                SetAdapterControlsEnabled(isNatPmp);
            }
            catch (ObjectDisposedException)
            {
                LogManager.Instance.LogDebug("SettingsForm.DiscoverNatPmpAdaptersAsync: Form disposed during adapter update");
            }
        }
        catch (OperationCanceledException)
        {
            // Form is closing - discovery was cancelled via _formCloseCts; nothing to update.
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            try
            {
                LogManager.Instance.LogDebug($"SettingsForm.DiscoverNatPmpAdaptersAsync: {ex.Message}");
                cboNatPmpAdapter.Items.Clear();
                cboNatPmpAdapter.Items.Add(NoAdaptersFoundPlaceholder);
                cboNatPmpAdapter.SelectedIndex = 0;
                bool isNatPmp = cboVpnProvider.SelectedItem?.ToString() == RegistrySettingsManager.VpnProviderNatPmp;
                SetAdapterControlsEnabled(isNatPmp);
            }
            catch (ObjectDisposedException)
            {
                LogManager.Instance.LogDebug("SettingsForm.DiscoverNatPmpAdaptersAsync: Form disposed during error recovery");
            }
        }
    }
}

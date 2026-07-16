namespace qbPortWeaver;

/// <summary>Settings dialog for configuring VPN provider, BitTorrent client connection, sync interval, and extra options.</summary>
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

    public SettingsForm()
    {
        InitializeComponent();
        Text = $"{AppIdentity.AppName} | Settings";
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
        toolTip.SetToolTip(cboBitTorrentClient, "BitTorrent client to control (qBittorrent, Transmission, or Deluge)");
        toolTip.SetToolTip(btnDetectClient, "Detect a running or installed client and fill in its selection and process details");
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
        toolTip.SetToolTip(txtPostUpdateCmd, "Shell command to run after a successful port update (leave empty to disable)");
        toolTip.SetToolTip(chkDebugMode, "Write verbose debug entries to the log file");
        toolTip.SetToolTip(cboColorTheme, "Application color theme (System, Dark, or Light) - a restart prompt will appear if changed");
        toolTip.SetToolTip(chkResyncOnNetworkChange, "When a network or VPN connection change is detected, run a sync right away instead of waiting for the next interval - so the client follows a VPN reconnect within seconds. Pausing still suppresses the cycle.");
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

        cboBitTorrentClient.Items.Clear();
        cboBitTorrentClient.Items.AddRange(ClientRegistry.All.Select(c => (object)c.Name).ToArray());
        cboBitTorrentClient.SelectedItem = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyBitTorrentClient);
        if (cboBitTorrentClient.SelectedIndex < 0) cboBitTorrentClient.SelectedIndex = 0;

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
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyBitTorrentClient, cboBitTorrentClient.SelectedItem?.ToString() ?? RegistrySettingsManager.BitTorrentClientQBittorrent);
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
            MessageBox.Show(
                "No NAT-PMP capable adapters were found.\n\nEnsure the adapter is up and its gateway is responding to NAT-PMP, then click \u21bb to retry.",
                AppIdentity.AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        bool isTransmission = cboBitTorrentClient.SelectedItem?.ToString() == RegistrySettingsManager.BitTorrentClientTransmission;
        bool isDeluge = cboBitTorrentClient.SelectedItem?.ToString() == RegistrySettingsManager.BitTorrentClientDeluge;
        string urlText, clientName;
        if (isTransmission) { urlText = txtTransmissionURL.Text.Trim(); clientName = "Transmission"; }
        else if (isDeluge) { urlText = txtDelugeURL.Text.Trim(); clientName = "Deluge"; }
        else { urlText = txtQBittorrentURL.Text.Trim(); clientName = "qBittorrent"; }
        if (!string.IsNullOrEmpty(urlText) &&
            (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            MessageBox.Show(
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
            var result = MessageBox.Show(
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

    private void cboBitTorrentClient_SelectedIndexChanged(object? sender, EventArgs e) =>
        UpdateClientGroupVisibility();

    // Detects a running or installed supported client and pre-fills the selection plus that client's
    // process-name / executable fields, so the user does not have to know the exact values. Selection
    // and field values are applied but not saved - the user reviews them, Tests, then Saves.
    private void btnDetectClient_Click(object? sender, EventArgs e)
    {
        var detected = ClientDetector.DetectAll();
        if (detected.Count == 0)
        {
            MessageBox.Show(
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
            using var chooser = new ClientChooserForm(candidates, cboBitTorrentClient.SelectedItem?.ToString());
            if (chooser.ShowDialog(this) != DialogResult.OK) return;
            var picked = chooser.SelectedClient;
            if (picked is null) return;
            chosen = picked;
        }

        cboBitTorrentClient.SelectedItem = chosen.ClientName; // triggers UpdateClientGroupVisibility
        ApplyDetectedClientDetails(chosen);

        // The chooser already showed the user what they picked, so only confirm on an auto-selection.
        if (autoSelected)
        {
            string how = chosen.Kind == ClientDetector.DetectionKind.Running ? "running now" : "installed";
            MessageBox.Show(
                $"Detected {chosen.ClientName} ({how}).\n\nThe client selection and its process details have been filled in. Review the connection settings, then use Test before saving.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // Fills the matched client's process-name field (always) and executable field (only when a default
    // install was found, so a user's custom path is never blanked) on its Client-tab group.
    private void ApplyDetectedClientDetails(ClientDetector.DetectedClient d)
    {
        if (d.ClientName == RegistrySettingsManager.BitTorrentClientTransmission)
        {
            txtTransmissionProcessName.Text = d.ProcessName;
            if (d.ExePath is not null) txtTransmissionExePath.Text = d.ExePath;
        }
        else if (d.ClientName == RegistrySettingsManager.BitTorrentClientDeluge)
        {
            txtDelugeProcessName.Text = d.ProcessName;
            if (d.ExePath is not null) txtDelugeExePath.Text = d.ExePath;
        }
        else
        {
            txtQBittorrentProcessName.Text = d.ProcessName;
            if (d.ExePath is not null) txtQBittorrentExePath.Text = d.ExePath;
        }
    }

    private void UpdateClientGroupVisibility()
    {
        string? selectedClient = cboBitTorrentClient.SelectedItem?.ToString();
        bool isTransmission = selectedClient == RegistrySettingsManager.BitTorrentClientTransmission;
        bool isDeluge = selectedClient == RegistrySettingsManager.BitTorrentClientDeluge;
        grpQBittorrent.Visible = !isTransmission && !isDeluge;
        grpDeluge.Visible = isDeluge;
        grpTransmission.Visible = isTransmission;
        // Reflect the chosen client in the Client tab header (qBittorrent / Transmission / Deluge).
        tabClient.Text = selectedClient ?? "Client";
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

    // Shared OpenFileDialog driver for the three BitTorrent-client executable browse buttons.
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

    // Shared driver for the three per-client "Test" buttons. Validates the URL first, then builds the
    // client from the in-form values (not the saved registry values) via the supplied factory so the
    // client is created only when the URL is valid. Probes it with GetPreferencesAsync - a full
    // auth + API round-trip that already logs detailed, client-specific failure reasons to the log
    // viewer. A non-null listening port is the success signal. The client is disposed via 'using'
    // and the button re-enabled in finally, even on cancel or form disposal.
    private async Task RunConnectionTestAsync(Func<IBitTorrentClient> clientFactory, Button button, string url, string clientName) // NOSONAR S2325 - accesses instance state (UseWaitCursor, IsDisposed) for post-await UI safety
    {
        if (string.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(
                $"Enter a valid {clientName} URL starting with http:// or https:// before testing.",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var client = clientFactory();
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            // Linked to the form-close token (like every other in-flight form operation) so closing
            // the dialog cancels the probe instead of letting it run out its timeout in the background.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_formCloseCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.ClientTestTimeoutSeconds));
            var (listenPort, _) = await client.GetPreferencesAsync(cts.Token);
            if (IsDisposed) return;
            if (listenPort is not null)
                MessageBox.Show(
                    $"Connected to {clientName} successfully.\n\nCurrent listening port: {listenPort}",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(
                    $"Could not connect to {clientName}.\n\nCheck the URL and credentials, then see the log for details.",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                MessageBox.Show(
                    $"The {clientName} connection test timed out after {AppConstants.ClientTestTimeoutSeconds} seconds.\n\nCheck the URL and that the client is running.",
                    AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
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
        lblBitTorrentClient.Enabled = enabled;
        cboBitTorrentClient.Enabled = enabled;
        btnDetectClient.Enabled = enabled;
        chkVerifyPort.Enabled = enabled;
        chkAutoRecovery.Enabled = enabled;
        UpdateAutoRecoverySubControls();

        // qBittorrent / Deluge / Transmission section
        grpQBittorrent.Enabled = enabled;
        grpDeluge.Enabled = enabled;
        grpTransmission.Enabled = enabled;

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

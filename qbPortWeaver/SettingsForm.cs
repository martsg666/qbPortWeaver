namespace qbPortWeaver
{
    /// <summary>Settings dialog for configuring VPN provider, BitTorrent client connection, sync interval, and extra options.</summary>
    public partial class SettingsForm : Form
    {
        internal bool SettingsSaved { get; private set; }

        private const string DiscoveringAdaptersPlaceholder = "Discovering adapters\u2026";
        private const string NoAdaptersFoundPlaceholder     = "No NAT-PMP adapters found";
        private const string DefaultPortTooltip             = "Port to apply when the VPN is disconnected (0 = do nothing when disconnected)";

        public SettingsForm()
        {
            InitializeComponent();
            Text = $"{AppConstants.AppName} | Settings";
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SetupTooltips();
            LoadSettings();
        }

        // Wire up tooltips for each setting control
        private void SetupTooltips()
        {
            toolTip.SetToolTip(cboVpnProvider,              "VPN provider used for port detection (Disabled, ProtonVPN, PIA, or NAT-PMP)");
            toolTip.SetToolTip(cboNatPmpAdapter,             "Network adapter to use for NAT-PMP port mapping (only applies when NAT-PMP is selected)");
            toolTip.SetToolTip(btnRefreshAdapters,           "Refresh the adapter list");
            toolTip.SetToolTip(nudUpdateInterval,            "How often to run the sync cycle, in seconds - controls both port sync and Media Manager frequency");
            toolTip.SetToolTip(cboBitTorrentClient,          "BitTorrent client to control (qBittorrent, Transmission, or Deluge)");
            toolTip.SetToolTip(txtQBittorrentURL,            "URL for the qBittorrent Web UI (e.g. http://127.0.0.1:8080). The Web UI must be enabled in qBittorrent under Tools > Options > Web UI.");
            toolTip.SetToolTip(txtQBittorrentUserName,       "Username for the qBittorrent Web UI");
            toolTip.SetToolTip(txtQBittorrentPassword,       "Password for the qBittorrent Web UI");
            toolTip.SetToolTip(txtQBittorrentExePath,        "Path to the qBittorrent executable, used to start or restart the application");
            toolTip.SetToolTip(btnBrowseExePath,             "Browse for the qBittorrent executable");
            toolTip.SetToolTip(txtQBittorrentProcessName,    "Process name used to detect if qBittorrent is running (usually qbittorrent)");
            toolTip.SetToolTip(chkRestartQBittorrent,        "Restart qBittorrent after updating the port - recommended for the change to take effect immediately");
            toolTip.SetToolTip(chkForceStartQBittorrent,     "Automatically launch qBittorrent if it is not already running");
            toolTip.SetToolTip(nudDefaultPort,               DefaultPortTooltip);
            toolTip.SetToolTip(lblDefaultPort,               DefaultPortTooltip);
            toolTip.SetToolTip(chkWarnOnInterfaceMismatch,   "Show a warning when qBittorrent's network interface does not match the configured VPN provider");
            toolTip.SetToolTip(chkRestartOnDisconnect,       "Automatically restart qBittorrent when its connection status becomes disconnected");
            toolTip.SetToolTip(txtTransmissionURL,           "URL for the Transmission RPC endpoint (e.g. http://127.0.0.1:9091). Remote access must be enabled in Transmission Preferences > Remote (not required when running as a service).");
            toolTip.SetToolTip(txtTransmissionUserName,      "Username for the Transmission RPC (leave empty if authentication is disabled)");
            toolTip.SetToolTip(txtTransmissionPassword,      "Password for the Transmission RPC (leave empty if authentication is disabled)");
            toolTip.SetToolTip(txtTransmissionExePath,       "Path to the Transmission executable, used to start or restart the application when running as a user-space process");
            toolTip.SetToolTip(btnBrowseTransmissionExePath, "Browse for the Transmission executable");
            toolTip.SetToolTip(txtTransmissionProcessName,   "Process name used to detect if Transmission is running as a user-space process (e.g. transmission-qt)");
            toolTip.SetToolTip(chkRestartTransmission,       "Restart Transmission after updating the port - recommended for the change to take effect immediately");
            toolTip.SetToolTip(chkForceStartTransmission,    "Automatically launch Transmission if it is not already running");
            toolTip.SetToolTip(nudTransmissionDefaultPort,   DefaultPortTooltip);
            toolTip.SetToolTip(lblTransmissionDefaultPort,   DefaultPortTooltip);
            toolTip.SetToolTip(txtDelugeURL,                 "URL for the Deluge Web UI (e.g. http://127.0.0.1:8112). The Web UI plugin must be enabled in Deluge's Plugin Manager.");
            toolTip.SetToolTip(txtDelugePassword,            "Password for the Deluge Web UI");
            toolTip.SetToolTip(txtDelugeExePath,             "Path to the Deluge executable, used to start or restart the application");
            toolTip.SetToolTip(btnBrowseDelugeExePath,       "Browse for the Deluge executable");
            toolTip.SetToolTip(txtDelugeProcessName,         "Process name used to detect if Deluge is running (usually deluge)");
            toolTip.SetToolTip(chkRestartDeluge,             "Restart Deluge after updating the port - recommended for the change to take effect immediately");
            toolTip.SetToolTip(chkForceStartDeluge,          "Automatically launch Deluge if it is not already running");
            toolTip.SetToolTip(nudDelugeDefaultPort,         DefaultPortTooltip);
            toolTip.SetToolTip(lblDelugeDefaultPort,         DefaultPortTooltip);
            toolTip.SetToolTip(txtPostUpdateCmd,             "Shell command to run after a successful port update (leave empty to disable)");
            toolTip.SetToolTip(chkDebugMode,                 "Write verbose debug entries to the log file");
            toolTip.SetToolTip(cboColorTheme,                "Application color theme (System, Dark, or Light) - a restart prompt will appear if changed");
            toolTip.SetToolTip(chkAutoRecovery,              "Automatically recover after the configured number of consecutive failed sync cycles (VPN disconnected or port detection failure)");
            toolTip.SetToolTip(nudRecoveryCycles,            "Number of consecutive failed cycles before recovery is triggered");
            toolTip.SetToolTip(chkNotifyOnPortUpdate,        "Show a tray notification when the port is successfully updated");
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
            cboBitTorrentClient.Items.AddRange(
            [
                RegistrySettingsManager.BitTorrentClientQBittorrent,
                RegistrySettingsManager.BitTorrentClientTransmission,
                RegistrySettingsManager.BitTorrentClientDeluge
            ]);
            cboBitTorrentClient.SelectedItem = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyBitTorrentClient);
            if (cboBitTorrentClient.SelectedIndex < 0) cboBitTorrentClient.SelectedIndex = 0;

            // NAT-PMP adapter discovery is async to avoid blocking the UI.
            // Launched after VPN provider is set so the completion callback reads the correct state.
            string savedAdapter = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);
            _ = DiscoverNatPmpAdaptersAsync(savedAdapter); // fire-and-forget; exceptions are handled inside DiscoverNatPmpAdaptersAsync

            nudUpdateInterval.Value = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds),
                (int)nudUpdateInterval.Minimum, (int)nudUpdateInterval.Maximum);

            chkAutoRecovery.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyAutoRecoveryEnabled);
            nudRecoveryCycles.Value = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyAutoRecoveryTriggerCycles),
                (int)nudRecoveryCycles.Minimum, (int)nudRecoveryCycles.Maximum);
            UpdateAutoRecoverySubControls();
            chkNotifyOnPortUpdate.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNotifyOnPortUpdate);

            // qBittorrent
            txtQBittorrentURL.Text         = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl);
            txtQBittorrentUserName.Text    = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName);
            txtQBittorrentPassword.Text    = RegistrySettingsManager.GetQBittorrentPassword();
            txtQBittorrentExePath.Text     = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath);
            txtQBittorrentProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName);

            chkRestartQBittorrent.Checked      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent);
            chkForceStartQBittorrent.Checked   = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent);
            chkWarnOnInterfaceMismatch.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch);
            chkRestartOnDisconnect.Checked     = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect);

            nudDefaultPort.Value = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort),
                (int)nudDefaultPort.Minimum, (int)nudDefaultPort.Maximum);

            // Transmission
            txtTransmissionURL.Text         = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUrl);
            txtTransmissionUserName.Text    = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUserName);
            txtTransmissionPassword.Text    = RegistrySettingsManager.GetTransmissionPassword();
            txtTransmissionExePath.Text     = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionExePath);
            txtTransmissionProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionProcessName);
            chkRestartTransmission.Checked    = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyRestartTransmission);
            chkForceStartTransmission.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyForceStartTransmission);
            nudTransmissionDefaultPort.Value  = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyDefaultPort),
                (int)nudTransmissionDefaultPort.Minimum, (int)nudTransmissionDefaultPort.Maximum);

            // Deluge
            txtDelugeURL.Text         = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeUrl);
            txtDelugePassword.Text    = RegistrySettingsManager.GetDelugePassword();
            txtDelugeExePath.Text     = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeExePath);
            txtDelugeProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeProcessName);
            chkRestartDeluge.Checked    = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyRestartDeluge);
            chkForceStartDeluge.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyForceStartDeluge);
            nudDelugeDefaultPort.Value  = Math.Clamp(
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
            chkDebugMode.Checked  = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode);
        }

        private void SaveSettings()
        {
            // General
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider,          cboVpnProvider.SelectedItem?.ToString() ?? RegistrySettingsManager.VpnProviderDisabled);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyBitTorrentClient,      cboBitTorrentClient.SelectedItem?.ToString() ?? RegistrySettingsManager.BitTorrentClientQBittorrent);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds, ((int)nudUpdateInterval.Value).ToString());
            // If discovery is still pending (combo disabled), preserve the existing value to avoid
            // saving the "Discovering adapters…" placeholder text as the adapter name
            string adapterName = cboNatPmpAdapter.Enabled
                ? cboNatPmpAdapter.SelectedItem?.ToString() ?? ""
                : RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName,           adapterName);
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyAutoRecoveryEnabled,       chkAutoRecovery.Checked);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyAutoRecoveryTriggerCycles, ((int)nudRecoveryCycles.Value).ToString());
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNotifyOnPortUpdate,         chkNotifyOnPortUpdate.Checked);

            // qBittorrent
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl,          txtQBittorrentURL.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName,     txtQBittorrentUserName.Text.Trim());
            RegistrySettingsManager.SetQBittorrentPassword(txtQBittorrentPassword.Text);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath,      txtQBittorrentExePath.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName,  txtQBittorrentProcessName.Text.Trim());
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent,      chkRestartQBittorrent.Checked);
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent,   chkForceStartQBittorrent.Checked);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort,             ((int)nudDefaultPort.Value).ToString());
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch, chkWarnOnInterfaceMismatch.Checked);
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect,     chkRestartOnDisconnect.Checked);

            // Transmission
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUrl,         txtTransmissionURL.Text.Trim());
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUserName,    txtTransmissionUserName.Text.Trim());
            RegistrySettingsManager.SetTransmissionPassword(txtTransmissionPassword.Text);
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionExePath,     txtTransmissionExePath.Text.Trim());
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionProcessName, txtTransmissionProcessName.Text.Trim());
            RegistrySettingsManager.SetBool         (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyRestartTransmission,     chkRestartTransmission.Checked);
            RegistrySettingsManager.SetBool         (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyForceStartTransmission,  chkForceStartTransmission.Checked);
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyDefaultPort,             ((int)nudTransmissionDefaultPort.Value).ToString());

            // Deluge
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeUrl,         txtDelugeURL.Text.Trim());
            RegistrySettingsManager.SetDelugePassword(txtDelugePassword.Text);
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeExePath,     txtDelugeExePath.Text.Trim());
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeProcessName, txtDelugeProcessName.Text.Trim());
            RegistrySettingsManager.SetBool         (RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyRestartDeluge,     chkRestartDeluge.Checked);
            RegistrySettingsManager.SetBool         (RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyForceStartDeluge,  chkForceStartDeluge.Checked);
            RegistrySettingsManager.SetValue        (RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDefaultPort,       ((int)nudDelugeDefaultPort.Value).ToString());

            // Extra
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyColorTheme,     cboColorTheme.SelectedItem?.ToString() ?? RegistrySettingsManager.ColorThemeSystem);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd, txtPostUpdateCmd.Text.Trim());
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode,     chkDebugMode.Checked);
        }

        private void btnOK_Click(object? sender, EventArgs e)
        {
            if (cboVpnProvider.SelectedItem?.ToString() == RegistrySettingsManager.VpnProviderNatPmp &&
                cboNatPmpAdapter.Enabled &&
                cboNatPmpAdapter.SelectedItem?.ToString() == NoAdaptersFoundPlaceholder)
            {
                MessageBox.Show(
                    "No NAT-PMP capable adapters were found.\n\nEnsure the adapter is up and its gateway is responding to NAT-PMP, then click \u21bb to retry.",
                    AppConstants.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            bool isTransmission = cboBitTorrentClient.SelectedItem?.ToString() == RegistrySettingsManager.BitTorrentClientTransmission;
            bool isDeluge       = cboBitTorrentClient.SelectedItem?.ToString() == RegistrySettingsManager.BitTorrentClientDeluge;
            string urlText, clientName;
            if (isTransmission) { urlText = txtTransmissionURL.Text.Trim(); clientName = "Transmission"; }
            else if (isDeluge)  { urlText = txtDelugeURL.Text.Trim();       clientName = "Deluge"; }
            else                { urlText = txtQBittorrentURL.Text.Trim();  clientName = "qBittorrent"; }
            if (!string.IsNullOrEmpty(urlText) &&
                (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                MessageBox.Show(
                    $"The {clientName} URL is not valid. Enter a URL starting with http:// or https://",
                    AppConstants.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            string previousColorTheme  = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyColorTheme);
            string selectedColorTheme  = cboColorTheme.SelectedItem?.ToString() ?? RegistrySettingsManager.ColorThemeSystem;
            SaveSettings();
            SettingsSaved = true;

            // Color theme takes effect at startup via Application.SetColorMode - restart if it changed
            if (selectedColorTheme != previousColorTheme)
            {
                var result = MessageBox.Show(
                    "The color theme change takes effect after restarting.\n\nRestart now?",
                    AppConstants.AppName,
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

        private void UpdateClientGroupVisibility()
        {
            bool isTransmission     = cboBitTorrentClient.SelectedItem?.ToString() == RegistrySettingsManager.BitTorrentClientTransmission;
            bool isDeluge           = cboBitTorrentClient.SelectedItem?.ToString() == RegistrySettingsManager.BitTorrentClientDeluge;
            grpQBittorrent.Visible  = !isTransmission && !isDeluge;
            grpDeluge.Visible       = isDeluge;
            grpTransmission.Visible = isTransmission;
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
                ? cboNatPmpAdapter.SelectedItem?.ToString() ?? ""
                : RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);

            cboNatPmpAdapter.Items.Clear();
            cboNatPmpAdapter.Items.Add(DiscoveringAdaptersPlaceholder);
            cboNatPmpAdapter.SelectedIndex = 0;
            SetAdapterControlsEnabled(false);
            _ = DiscoverNatPmpAdaptersAsync(current); // fire-and-forget; exceptions are handled inside DiscoverNatPmpAdaptersAsync
        }

        private void btnBrowseExePath_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Select qBittorrent Executable",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
            };

            if (!string.IsNullOrWhiteSpace(txtQBittorrentExePath.Text) &&
                File.Exists(txtQBittorrentExePath.Text))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(txtQBittorrentExePath.Text) ?? string.Empty;
            }

            if (dlg.ShowDialog() == DialogResult.OK)
                txtQBittorrentExePath.Text = dlg.FileName;
        }

        private void btnBrowseDelugeExePath_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Select Deluge Executable",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
            };

            if (!string.IsNullOrWhiteSpace(txtDelugeExePath.Text) &&
                File.Exists(txtDelugeExePath.Text))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(txtDelugeExePath.Text) ?? string.Empty;
            }

            if (dlg.ShowDialog() == DialogResult.OK)
                txtDelugeExePath.Text = dlg.FileName;
        }

        private void btnBrowseTransmissionExePath_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Select Transmission Executable",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
            };

            if (!string.IsNullOrWhiteSpace(txtTransmissionExePath.Text) &&
                File.Exists(txtTransmissionExePath.Text))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(txtTransmissionExePath.Text) ?? string.Empty;
            }

            if (dlg.ShowDialog() == DialogResult.OK)
                txtTransmissionExePath.Text = dlg.FileName;
        }

        private void chkAutoRecovery_CheckedChanged(object? sender, EventArgs e) =>
            UpdateAutoRecoverySubControls();

        private void UpdateAutoRecoverySubControls()
        {
            bool vpnActive = cboVpnProvider.SelectedItem?.ToString() != RegistrySettingsManager.VpnProviderDisabled;
            bool enabled   = vpnActive && chkAutoRecovery.Checked;
            lblRecoveryCycles.Enabled     = enabled;
            nudRecoveryCycles.Enabled     = enabled;
            lblRecoveryCyclesUnit.Enabled = enabled;
        }

        // Enables or disables all port-sync-related controls (everything except VPN provider, update interval, and debug mode)
        private void SetPortSyncControlsEnabled(bool enabled)
        {
            // General section - client and auto-recovery (NAT-PMP adapter row handled by SetAdapterControlsEnabled)
            lblBitTorrentClient.Enabled   = enabled;
            cboBitTorrentClient.Enabled   = enabled;
            chkAutoRecovery.Enabled       = enabled;
            UpdateAutoRecoverySubControls();

            // qBittorrent / Deluge / Transmission section
            grpQBittorrent.Enabled  = enabled;
            grpDeluge.Enabled       = enabled;
            grpTransmission.Enabled = enabled;

            // Extra section - post-update command (color mode and debug mode stay enabled)
            lblPostUpdateCmd.Enabled = enabled;
            txtPostUpdateCmd.Enabled = enabled;
        }

        private void SetAdapterControlsEnabled(bool enabled)
        {
            lblNatPmpAdapter.Enabled   = enabled;
            cboNatPmpAdapter.Enabled   = enabled;
            btnRefreshAdapters.Enabled = enabled;
        }

        private async Task DiscoverNatPmpAdaptersAsync(string savedAdapter)
        {
            try
            {
                // No ConfigureAwait(false) - continuation must run on the UI thread to update controls.
                var adapters = await NatPmpManager.DiscoverAdaptersAsync();

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
}

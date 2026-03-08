namespace qbPortWeaver
{
    public partial class SettingsForm : Form
    {
        private const string DiscoveringAdaptersPlaceholder = "Discovering adapters\u2026";
        private const string NoAdaptersFoundPlaceholder     = "No NAT-PMP adapters found";

        public SettingsForm()
        {
            InitializeComponent();
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
            toolTip.SetToolTip(cboVpnProvider,              "VPN provider used for port detection (ProtonVPN, PIA, or NAT-PMP)");
            toolTip.SetToolTip(cboNatPmpAdapter,             "Network adapter to use for NAT-PMP port mapping (only applies when NAT-PMP is selected)");
            toolTip.SetToolTip(btnRefreshAdapters,           "Refresh the adapter list");
            toolTip.SetToolTip(nudUpdateInterval,            "How often to check and sync the port, in seconds");
            toolTip.SetToolTip(txtQBittorrentURL,            "URL for the qBittorrent Web UI (e.g. http://127.0.0.1:8080)");
            toolTip.SetToolTip(txtQBittorrentUserName,       "Username for the qBittorrent Web UI");
            toolTip.SetToolTip(txtQBittorrentPassword,       "Password for the qBittorrent Web UI");
            toolTip.SetToolTip(txtQBittorrentExePath,        "Path to qbittorrent.exe, used to start or restart the application");
            toolTip.SetToolTip(btnBrowseExePath,             "Browse for the qBittorrent executable");
            toolTip.SetToolTip(txtQBittorrentProcessName,    "Process name used to detect if qBittorrent is running (usually qbittorrent)");
            toolTip.SetToolTip(chkRestartQBittorrent,        "Restart qBittorrent after updating the port — required for the change to take effect");
            toolTip.SetToolTip(chkForceStartQBittorrent,     "Automatically launch qBittorrent if it is not already running");
            toolTip.SetToolTip(nudDefaultPort,               "Port to apply when the VPN is disconnected (0 = do nothing when disconnected)");
            toolTip.SetToolTip(lblDefaultPort,               "Port to apply when the VPN is disconnected (0 = do nothing when disconnected)");
            toolTip.SetToolTip(chkWarnOnInterfaceMismatch,   "Show a warning when qBittorrent's network interface does not match the configured VPN provider");
            toolTip.SetToolTip(chkRestartOnDisconnect,       "Automatically restart qBittorrent if the connection goes offline or disconnects");
            toolTip.SetToolTip(txtPostUpdateCmd,             "Shell command to run after a successful port update (leave empty to disable)");
            toolTip.SetToolTip(chkDebugMode,                 "Write verbose debug entries to the log file");
        }

        private void LoadSettings()
        {
            // General
            cboVpnProvider.Items.Clear();
            cboVpnProvider.Items.AddRange(new object[]
            {
                RegistrySettingsManager.VpnProviderProtonVpn,
                RegistrySettingsManager.VpnProviderPia,
                RegistrySettingsManager.VpnProviderNatPmp
            });
            cboVpnProvider.SelectedItem = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider);
            if (cboVpnProvider.SelectedIndex < 0)
                cboVpnProvider.SelectedIndex = 0;

            // NAT-PMP adapter — discovered asynchronously to avoid blocking the UI
            cboNatPmpAdapter.Items.Clear();
            cboNatPmpAdapter.Items.Add(DiscoveringAdaptersPlaceholder);
            cboNatPmpAdapter.SelectedIndex = 0;
            cboNatPmpAdapter.Enabled = false;
            string savedAdapter = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);
            _ = PopulateNatPmpAdaptersAsync(savedAdapter);

            nudUpdateInterval.Value = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds),
                (int)nudUpdateInterval.Minimum, (int)nudUpdateInterval.Maximum);

            // qBittorrent
            txtQBittorrentURL.Text         = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl);
            txtQBittorrentUserName.Text    = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName);
            txtQBittorrentPassword.Text    = RegistrySettingsManager.GetPassword();
            txtQBittorrentExePath.Text     = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath);
            txtQBittorrentProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName);

            chkRestartQBittorrent.Checked      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent);
            chkForceStartQBittorrent.Checked   = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent);
            chkWarnOnInterfaceMismatch.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch);
            chkRestartOnDisconnect.Checked     = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect);

            nudDefaultPort.Value = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort),
                (int)nudDefaultPort.Minimum, (int)nudDefaultPort.Maximum);

            // Extra
            txtPostUpdateCmd.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyPostUpdateCmd);
            chkDebugMode.Checked  = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode);
        }

        private void SaveSettings()
        {
            // General
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider,          cboVpnProvider.SelectedItem?.ToString() ?? RegistrySettingsManager.VpnProviderProtonVpn);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds, ((int)nudUpdateInterval.Value).ToString());
            // If discovery is still pending (combo disabled), preserve the existing value to avoid
            // saving the "Discovering adapters…" placeholder text as the adapter name
            string adapterName = cboNatPmpAdapter.Enabled
                ? cboNatPmpAdapter.SelectedItem?.ToString() ?? ""
                : RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyNatPmpAdapterName, adapterName);

            // qBittorrent
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl,          txtQBittorrentURL.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName,     txtQBittorrentUserName.Text.Trim());
            RegistrySettingsManager.SetPassword(txtQBittorrentPassword.Text);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath,      txtQBittorrentExePath.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName,  txtQBittorrentProcessName.Text.Trim());
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent,      chkRestartQBittorrent.Checked);
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent,   chkForceStartQBittorrent.Checked);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort,             ((int)nudDefaultPort.Value).ToString());
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch, chkWarnOnInterfaceMismatch.Checked);
            RegistrySettingsManager.SetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect,     chkRestartOnDisconnect.Checked);

            // Extra
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
            SaveSettings();
            DialogResult = DialogResult.OK;
        }

        private void cboVpnProvider_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // Only enable the adapter combo and refresh button if NAT-PMP is selected AND discovery has finished
            // (discovery replaces the placeholder and re-enables them via PopulateNatPmpAdaptersAsync)
            bool isNatPmp = cboVpnProvider.SelectedItem?.ToString() == RegistrySettingsManager.VpnProviderNatPmp;
            bool discoveryPending = cboNatPmpAdapter.Items.Count == 1 &&
                                    cboNatPmpAdapter.Items[0]?.ToString() == DiscoveringAdaptersPlaceholder;
            SetAdapterControlsEnabled(isNatPmp && !discoveryPending);
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
            cboNatPmpAdapter.Enabled   = false;
            btnRefreshAdapters.Enabled = false;
            _ = PopulateNatPmpAdaptersAsync(current);
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
                dlg.InitialDirectory = Path.GetDirectoryName(txtQBittorrentExePath.Text)!;
            }

            if (dlg.ShowDialog() == DialogResult.OK)
                txtQBittorrentExePath.Text = dlg.FileName;
        }

        private void SetAdapterControlsEnabled(bool enabled)
        {
            cboNatPmpAdapter.Enabled   = enabled;
            btnRefreshAdapters.Enabled = enabled;
        }

        private async Task PopulateNatPmpAdaptersAsync(string savedAdapter)
        {
            try
            {
                // No ConfigureAwait(false) — continuation must run on the UI thread to update controls.
                var adapters = await NatPmpManager.DiscoverAdapters();

                // Guard against the form being closed while adapter discovery was in flight
                if (IsDisposed) return;

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
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"SettingsForm.PopulateNatPmpAdaptersAsync: {ex.Message}");
            }
        }
    }
}

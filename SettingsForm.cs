namespace qbPortWeaver
{
    public partial class SettingsForm : Form
    {
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
            cboVpnProvider.SelectedItem = RegistrySettingsManager.GetValue("general", "vpnProvider");
            if (cboVpnProvider.SelectedIndex < 0)
                cboVpnProvider.SelectedIndex = 0;

            // NAT-PMP adapter
            cboNatPmpAdapter.Items.Clear();
            foreach (var adapter in NatPmpManager.DiscoverAdapters())
                cboNatPmpAdapter.Items.Add(adapter.ProviderName);
            string savedAdapter = RegistrySettingsManager.GetValue("general", "natPmpAdapterName");
            if (!string.IsNullOrWhiteSpace(savedAdapter))
                cboNatPmpAdapter.SelectedItem = savedAdapter;

            if (int.TryParse(RegistrySettingsManager.GetValue("general", "updateIntervalSeconds"), out int interval))
                nudUpdateInterval.Value = Math.Clamp(interval, (int)nudUpdateInterval.Minimum, (int)nudUpdateInterval.Maximum);
            else
                nudUpdateInterval.Value = AppConstants.DEFAULT_UPDATE_INTERVAL_SECONDS;

            // qBittorrent
            txtQBittorrentURL.Text         = RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentURL");
            txtQBittorrentUserName.Text    = RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentUserName");
            txtQBittorrentPassword.Text    = RegistrySettingsManager.GetPassword();
            txtQBittorrentExePath.Text     = RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentExePath");
            txtQBittorrentProcessName.Text = RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentProcessName");

            chkRestartQBittorrent.Checked      = RegistrySettingsManager.GetValue("qBittorrent", "restartqBittorrent").Equals("True", StringComparison.OrdinalIgnoreCase);
            chkForceStartQBittorrent.Checked   = RegistrySettingsManager.GetValue("qBittorrent", "forceStartqBittorrent").Equals("True", StringComparison.OrdinalIgnoreCase);
            chkWarnOnInterfaceMismatch.Checked = RegistrySettingsManager.GetValue("qBittorrent", "warnOnInterfaceMismatch").Equals("True", StringComparison.OrdinalIgnoreCase);
            chkRestartOnDisconnect.Checked     = RegistrySettingsManager.GetValue("qBittorrent", "restartOnDisconnect").Equals("True", StringComparison.OrdinalIgnoreCase);

            if (int.TryParse(RegistrySettingsManager.GetValue("qBittorrent", "defaultPort"), out int defaultPort))
                nudDefaultPort.Value = Math.Clamp(defaultPort, (int)nudDefaultPort.Minimum, (int)nudDefaultPort.Maximum);
            else
                nudDefaultPort.Value = nudDefaultPort.Minimum;

            // Extra
            txtPostUpdateCmd.Text = RegistrySettingsManager.GetValue("extra", "postUpdateCmd");
            chkDebugMode.Checked  = RegistrySettingsManager.GetValue("extra", "debugMode").Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        private void btnOK_Click(object? sender, EventArgs e)
        {
            SaveSettings();
            DialogResult = DialogResult.OK;
        }

        private void SaveSettings()
        {
            // General
            RegistrySettingsManager.SetValue("general", "vpnProvider",           cboVpnProvider.SelectedItem?.ToString() ?? "ProtonVPN");
            RegistrySettingsManager.SetValue("general", "updateIntervalSeconds",  ((int)nudUpdateInterval.Value).ToString());
            RegistrySettingsManager.SetValue("general", "natPmpAdapterName",      cboNatPmpAdapter.SelectedItem?.ToString() ?? "");

            // qBittorrent
            RegistrySettingsManager.SetValue("qBittorrent", "qBittorrentURL",          txtQBittorrentURL.Text.Trim());
            RegistrySettingsManager.SetValue("qBittorrent", "qBittorrentUserName",     txtQBittorrentUserName.Text.Trim());
            RegistrySettingsManager.SetPassword(txtQBittorrentPassword.Text);
            RegistrySettingsManager.SetValue("qBittorrent", "qBittorrentExePath",      txtQBittorrentExePath.Text.Trim());
            RegistrySettingsManager.SetValue("qBittorrent", "qBittorrentProcessName",  txtQBittorrentProcessName.Text.Trim());
            RegistrySettingsManager.SetValue("qBittorrent", "restartqBittorrent",      chkRestartQBittorrent.Checked      ? "True" : "False");
            RegistrySettingsManager.SetValue("qBittorrent", "forceStartqBittorrent",   chkForceStartQBittorrent.Checked   ? "True" : "False");
            RegistrySettingsManager.SetValue("qBittorrent", "defaultPort",             ((int)nudDefaultPort.Value).ToString());
            RegistrySettingsManager.SetValue("qBittorrent", "warnOnInterfaceMismatch", chkWarnOnInterfaceMismatch.Checked ? "True" : "False");
            RegistrySettingsManager.SetValue("qBittorrent", "restartOnDisconnect",     chkRestartOnDisconnect.Checked     ? "True" : "False");

            // Extra
            RegistrySettingsManager.SetValue("extra", "postUpdateCmd", txtPostUpdateCmd.Text.Trim());
            RegistrySettingsManager.SetValue("extra", "debugMode",     chkDebugMode.Checked ? "True" : "False");
        }

        private void cboVpnProvider_SelectedIndexChanged(object? sender, EventArgs e)
        {
            cboNatPmpAdapter.Enabled = cboVpnProvider.SelectedItem?.ToString() == "NAT-PMP";
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
    }
}

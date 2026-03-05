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
            cboVpnProvider.SelectedItem = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, "vpnProvider");
            if (cboVpnProvider.SelectedIndex < 0)
                cboVpnProvider.SelectedIndex = 0;

            // NAT-PMP adapter — discovered on a background thread to avoid blocking the UI
            cboNatPmpAdapter.Items.Clear();
            cboNatPmpAdapter.Items.Add("Discovering adapters…");
            cboNatPmpAdapter.SelectedIndex = 0;
            cboNatPmpAdapter.Enabled = false;
            string savedAdapter = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, "natPmpAdapterName");
            _ = Task.Run(() => NatPmpManager.DiscoverAdapters())
                    .ContinueWith(t =>
                    {
                        cboNatPmpAdapter.Items.Clear();
                        if (t.Result.Count == 0)
                        {
                            cboNatPmpAdapter.Items.Add("No NAT-PMP adapters found");
                            cboNatPmpAdapter.SelectedIndex = 0;
                        }
                        else
                        {
                            foreach (var adapter in t.Result)
                                cboNatPmpAdapter.Items.Add(adapter.ProviderName);
                            cboNatPmpAdapter.SelectedItem = savedAdapter;
                            if (cboNatPmpAdapter.SelectedIndex < 0)
                                cboNatPmpAdapter.SelectedIndex = 0;
                        }
                        cboNatPmpAdapter.Enabled = cboVpnProvider.SelectedItem?.ToString() == "NAT-PMP";
                    }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());

            nudUpdateInterval.Value = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, "updateIntervalSeconds"),
                (int)nudUpdateInterval.Minimum, (int)nudUpdateInterval.Maximum);

            // qBittorrent
            txtQBittorrentURL.Text         = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentURL");
            txtQBittorrentUserName.Text    = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentUserName");
            txtQBittorrentPassword.Text    = RegistrySettingsManager.GetPassword();
            txtQBittorrentExePath.Text     = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentExePath");
            txtQBittorrentProcessName.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentProcessName");

            chkRestartQBittorrent.Checked      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, "restartqBittorrent");
            chkForceStartQBittorrent.Checked   = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, "forceStartqBittorrent");
            chkWarnOnInterfaceMismatch.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, "warnOnInterfaceMismatch");
            chkRestartOnDisconnect.Checked     = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, "restartOnDisconnect");

            nudDefaultPort.Value = Math.Clamp(
                RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionQBittorrent, "defaultPort"),
                (int)nudDefaultPort.Minimum, (int)nudDefaultPort.Maximum);

            // Extra
            txtPostUpdateCmd.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, "postUpdateCmd");
            chkDebugMode.Checked  = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionExtra, "debugMode");
        }

        private void btnOK_Click(object? sender, EventArgs e)
        {
            SaveSettings();
            DialogResult = DialogResult.OK;
        }

        private void SaveSettings()
        {
            // General
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, "vpnProvider",           cboVpnProvider.SelectedItem?.ToString() ?? "ProtonVPN");
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, "updateIntervalSeconds",  ((int)nudUpdateInterval.Value).ToString());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionGeneral, "natPmpAdapterName",      cboNatPmpAdapter.SelectedItem?.ToString() ?? "");

            // qBittorrent
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentURL",          txtQBittorrentURL.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentUserName",     txtQBittorrentUserName.Text.Trim());
            RegistrySettingsManager.SetPassword(txtQBittorrentPassword.Text);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentExePath",      txtQBittorrentExePath.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "qBittorrentProcessName",  txtQBittorrentProcessName.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "restartqBittorrent",      chkRestartQBittorrent.Checked      ? "True" : "False");
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "forceStartqBittorrent",   chkForceStartQBittorrent.Checked   ? "True" : "False");
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "defaultPort",             ((int)nudDefaultPort.Value).ToString());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "warnOnInterfaceMismatch", chkWarnOnInterfaceMismatch.Checked ? "True" : "False");
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionQBittorrent, "restartOnDisconnect",     chkRestartOnDisconnect.Checked     ? "True" : "False");

            // Extra
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionExtra, "postUpdateCmd", txtPostUpdateCmd.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionExtra, "debugMode",     chkDebugMode.Checked ? "True" : "False");
        }

        private void cboVpnProvider_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // Only enable the adapter combo if NAT-PMP is selected AND discovery has finished
            // (discovery replaces the placeholder and re-enables it via ContinueWith)
            bool isNatPmp = cboVpnProvider.SelectedItem?.ToString() == "NAT-PMP";
            bool discoveryPending = cboNatPmpAdapter.Items.Count == 1 &&
                                    cboNatPmpAdapter.Items[0]?.ToString() == "Discovering adapters…";
            cboNatPmpAdapter.Enabled = isNatPmp && !discoveryPending;
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

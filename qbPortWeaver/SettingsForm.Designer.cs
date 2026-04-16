namespace qbPortWeaver
{
    partial class SettingsForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            grpGeneral = new GroupBox();
            lblVpnProvider = new Label();
            cboVpnProvider = new ComboBox();
            lblNatPmpAdapter = new Label();
            cboNatPmpAdapter = new ComboBox();
            btnRefreshAdapters = new Button();
            lblUpdateInterval = new Label();
            nudUpdateInterval = new NumericUpDown();
            lblSeconds = new Label();
            chkAutoRecovery = new CheckBox();
            lblRecoveryCycles = new Label();
            nudRecoveryCycles = new NumericUpDown();
            lblRecoveryCyclesUnit = new Label();
            grpQBittorrent = new GroupBox();
            lblQBittorrentURL = new Label();
            txtQBittorrentURL = new TextBox();
            lblQBittorrentUserName = new Label();
            txtQBittorrentUserName = new TextBox();
            lblQBittorrentPassword = new Label();
            txtQBittorrentPassword = new TextBox();
            lblQBittorrentExePath = new Label();
            txtQBittorrentExePath = new TextBox();
            btnBrowseExePath = new Button();
            lblQBittorrentProcessName = new Label();
            txtQBittorrentProcessName = new TextBox();
            chkRestartQBittorrent = new CheckBox();
            chkForceStartQBittorrent = new CheckBox();
            lblDefaultPort = new Label();
            nudDefaultPort = new NumericUpDown();
            chkWarnOnInterfaceMismatch = new CheckBox();
            chkRestartOnDisconnect = new CheckBox();
            grpExtra = new GroupBox();
            lblPostUpdateCmd = new Label();
            txtPostUpdateCmd = new TextBox();
            chkDebugMode = new CheckBox();
            lblColorTheme = new Label();
            cboColorTheme = new ComboBox();
            btnOK = new Button();
            btnCancel = new Button();
            toolTip = new ToolTip(components);
            grpGeneral.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)nudUpdateInterval).BeginInit();
            ((System.ComponentModel.ISupportInitialize)nudRecoveryCycles).BeginInit();
            grpQBittorrent.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)nudDefaultPort).BeginInit();
            grpExtra.SuspendLayout();
            SuspendLayout();
            // 
            // grpGeneral
            // 
            grpGeneral.Controls.Add(lblVpnProvider);
            grpGeneral.Controls.Add(cboVpnProvider);
            grpGeneral.Controls.Add(lblNatPmpAdapter);
            grpGeneral.Controls.Add(cboNatPmpAdapter);
            grpGeneral.Controls.Add(btnRefreshAdapters);
            grpGeneral.Controls.Add(lblUpdateInterval);
            grpGeneral.Controls.Add(nudUpdateInterval);
            grpGeneral.Controls.Add(lblSeconds);
            grpGeneral.Controls.Add(chkAutoRecovery);
            grpGeneral.Controls.Add(lblRecoveryCycles);
            grpGeneral.Controls.Add(nudRecoveryCycles);
            grpGeneral.Controls.Add(lblRecoveryCyclesUnit);
            grpGeneral.Location = new Point(8, 8);
            grpGeneral.Name = "grpGeneral";
            grpGeneral.Size = new Size(484, 167);
            grpGeneral.TabIndex = 0;
            grpGeneral.TabStop = false;
            grpGeneral.Text = "General";
            // 
            // lblVpnProvider
            // 
            lblVpnProvider.Location = new Point(12, 24);
            lblVpnProvider.Name = "lblVpnProvider";
            lblVpnProvider.Size = new Size(130, 23);
            lblVpnProvider.TabIndex = 0;
            lblVpnProvider.Text = "VPN provider:";
            lblVpnProvider.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // cboVpnProvider
            // 
            cboVpnProvider.DropDownStyle = ComboBoxStyle.DropDownList;
            cboVpnProvider.Location = new Point(148, 24);
            cboVpnProvider.Name = "cboVpnProvider";
            cboVpnProvider.Size = new Size(200, 23);
            cboVpnProvider.TabIndex = 1;
            cboVpnProvider.SelectedIndexChanged += cboVpnProvider_SelectedIndexChanged;
            // 
            // lblNatPmpAdapter
            // 
            lblNatPmpAdapter.Location = new Point(12, 82);
            lblNatPmpAdapter.Name = "lblNatPmpAdapter";
            lblNatPmpAdapter.Size = new Size(130, 23);
            lblNatPmpAdapter.TabIndex = 5;
            lblNatPmpAdapter.Text = "NAT-PMP adapter:";
            lblNatPmpAdapter.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // cboNatPmpAdapter
            // 
            cboNatPmpAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            cboNatPmpAdapter.Location = new Point(148, 82);
            cboNatPmpAdapter.Name = "cboNatPmpAdapter";
            cboNatPmpAdapter.Size = new Size(290, 23);
            cboNatPmpAdapter.TabIndex = 6;
            // 
            // btnRefreshAdapters
            // 
            btnRefreshAdapters.Enabled = false;
            btnRefreshAdapters.Location = new Point(442, 82);
            btnRefreshAdapters.Name = "btnRefreshAdapters";
            btnRefreshAdapters.Size = new Size(26, 23);
            btnRefreshAdapters.TabIndex = 7;
            btnRefreshAdapters.Text = "↻";
            btnRefreshAdapters.Click += btnRefreshAdapters_Click;
            // 
            // lblUpdateInterval
            // 
            lblUpdateInterval.Location = new Point(12, 53);
            lblUpdateInterval.Name = "lblUpdateInterval";
            lblUpdateInterval.Size = new Size(130, 23);
            lblUpdateInterval.TabIndex = 2;
            lblUpdateInterval.Text = "Update interval:";
            lblUpdateInterval.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // nudUpdateInterval
            // 
            nudUpdateInterval.Location = new Point(148, 53);
            nudUpdateInterval.Maximum = new decimal(new int[] { 86400, 0, 0, 0 });
            nudUpdateInterval.Minimum = new decimal(new int[] { 10, 0, 0, 0 });
            nudUpdateInterval.Name = "nudUpdateInterval";
            nudUpdateInterval.Size = new Size(80, 23);
            nudUpdateInterval.TabIndex = 3;
            nudUpdateInterval.Value = new decimal(new int[] { 180, 0, 0, 0 });
            // 
            // lblSeconds
            // 
            lblSeconds.Location = new Point(234, 53);
            lblSeconds.Name = "lblSeconds";
            lblSeconds.Size = new Size(55, 23);
            lblSeconds.TabIndex = 4;
            lblSeconds.Text = "seconds";
            lblSeconds.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // chkAutoRecovery
            // 
            chkAutoRecovery.AutoSize = true;
            chkAutoRecovery.Location = new Point(12, 114);
            chkAutoRecovery.Name = "chkAutoRecovery";
            chkAutoRecovery.Size = new Size(164, 19);
            chkAutoRecovery.TabIndex = 8;
            chkAutoRecovery.Text = "Enable auto-recovery";
            chkAutoRecovery.CheckedChanged += chkAutoRecovery_CheckedChanged;
            // 
            // lblRecoveryCycles
            // 
            lblRecoveryCycles.Location = new Point(28, 136);
            lblRecoveryCycles.Name = "lblRecoveryCycles";
            lblRecoveryCycles.Size = new Size(180, 23);
            lblRecoveryCycles.TabIndex = 9;
            lblRecoveryCycles.Text = "Trigger recovery after";
            lblRecoveryCycles.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // nudRecoveryCycles
            // 
            nudRecoveryCycles.Location = new Point(212, 136);
            nudRecoveryCycles.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            nudRecoveryCycles.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            nudRecoveryCycles.Name = "nudRecoveryCycles";
            nudRecoveryCycles.Size = new Size(50, 23);
            nudRecoveryCycles.TabIndex = 10;
            nudRecoveryCycles.Value = new decimal(new int[] { 3, 0, 0, 0 });
            // 
            // lblRecoveryCyclesUnit
            // 
            lblRecoveryCyclesUnit.Location = new Point(266, 136);
            lblRecoveryCyclesUnit.Name = "lblRecoveryCyclesUnit";
            lblRecoveryCyclesUnit.Size = new Size(202, 23);
            lblRecoveryCyclesUnit.TabIndex = 11;
            lblRecoveryCyclesUnit.Text = "consecutive failed cycles";
            lblRecoveryCyclesUnit.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // grpQBittorrent
            // 
            grpQBittorrent.Controls.Add(lblQBittorrentURL);
            grpQBittorrent.Controls.Add(txtQBittorrentURL);
            grpQBittorrent.Controls.Add(lblQBittorrentUserName);
            grpQBittorrent.Controls.Add(txtQBittorrentUserName);
            grpQBittorrent.Controls.Add(lblQBittorrentPassword);
            grpQBittorrent.Controls.Add(txtQBittorrentPassword);
            grpQBittorrent.Controls.Add(lblQBittorrentExePath);
            grpQBittorrent.Controls.Add(txtQBittorrentExePath);
            grpQBittorrent.Controls.Add(btnBrowseExePath);
            grpQBittorrent.Controls.Add(lblQBittorrentProcessName);
            grpQBittorrent.Controls.Add(txtQBittorrentProcessName);
            grpQBittorrent.Controls.Add(chkRestartQBittorrent);
            grpQBittorrent.Controls.Add(chkForceStartQBittorrent);
            grpQBittorrent.Controls.Add(lblDefaultPort);
            grpQBittorrent.Controls.Add(nudDefaultPort);
            grpQBittorrent.Controls.Add(chkWarnOnInterfaceMismatch);
            grpQBittorrent.Controls.Add(chkRestartOnDisconnect);
            grpQBittorrent.Location = new Point(8, 183);
            grpQBittorrent.Name = "grpQBittorrent";
            grpQBittorrent.Size = new Size(484, 312);
            grpQBittorrent.TabIndex = 1;
            grpQBittorrent.TabStop = false;
            grpQBittorrent.Text = "qBittorrent";
            // 
            // lblQBittorrentURL
            // 
            lblQBittorrentURL.Location = new Point(12, 24);
            lblQBittorrentURL.Name = "lblQBittorrentURL";
            lblQBittorrentURL.Size = new Size(130, 23);
            lblQBittorrentURL.TabIndex = 0;
            lblQBittorrentURL.Text = "URL:";
            lblQBittorrentURL.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // txtQBittorrentURL
            // 
            txtQBittorrentURL.Location = new Point(148, 24);
            txtQBittorrentURL.Name = "txtQBittorrentURL";
            txtQBittorrentURL.Size = new Size(320, 23);
            txtQBittorrentURL.TabIndex = 1;
            // 
            // lblQBittorrentUserName
            // 
            lblQBittorrentUserName.Location = new Point(12, 53);
            lblQBittorrentUserName.Name = "lblQBittorrentUserName";
            lblQBittorrentUserName.Size = new Size(130, 23);
            lblQBittorrentUserName.TabIndex = 2;
            lblQBittorrentUserName.Text = "Username:";
            lblQBittorrentUserName.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // txtQBittorrentUserName
            // 
            txtQBittorrentUserName.Location = new Point(148, 53);
            txtQBittorrentUserName.Name = "txtQBittorrentUserName";
            txtQBittorrentUserName.Size = new Size(320, 23);
            txtQBittorrentUserName.TabIndex = 3;
            // 
            // lblQBittorrentPassword
            // 
            lblQBittorrentPassword.Location = new Point(12, 82);
            lblQBittorrentPassword.Name = "lblQBittorrentPassword";
            lblQBittorrentPassword.Size = new Size(130, 23);
            lblQBittorrentPassword.TabIndex = 4;
            lblQBittorrentPassword.Text = "Password:";
            lblQBittorrentPassword.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // txtQBittorrentPassword
            // 
            txtQBittorrentPassword.Location = new Point(148, 82);
            txtQBittorrentPassword.Name = "txtQBittorrentPassword";
            txtQBittorrentPassword.PasswordChar = '*';
            txtQBittorrentPassword.Size = new Size(320, 23);
            txtQBittorrentPassword.TabIndex = 5;
            // 
            // lblQBittorrentExePath
            // 
            lblQBittorrentExePath.Location = new Point(12, 111);
            lblQBittorrentExePath.Name = "lblQBittorrentExePath";
            lblQBittorrentExePath.Size = new Size(130, 23);
            lblQBittorrentExePath.TabIndex = 6;
            lblQBittorrentExePath.Text = "Executable:";
            lblQBittorrentExePath.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // txtQBittorrentExePath
            // 
            txtQBittorrentExePath.Location = new Point(148, 111);
            txtQBittorrentExePath.Name = "txtQBittorrentExePath";
            txtQBittorrentExePath.Size = new Size(276, 23);
            txtQBittorrentExePath.TabIndex = 7;
            // 
            // btnBrowseExePath
            // 
            btnBrowseExePath.Location = new Point(428, 111);
            btnBrowseExePath.Name = "btnBrowseExePath";
            btnBrowseExePath.Size = new Size(40, 23);
            btnBrowseExePath.TabIndex = 8;
            btnBrowseExePath.Text = "...";
            btnBrowseExePath.Click += btnBrowseExePath_Click;
            // 
            // lblQBittorrentProcessName
            // 
            lblQBittorrentProcessName.Location = new Point(12, 140);
            lblQBittorrentProcessName.Name = "lblQBittorrentProcessName";
            lblQBittorrentProcessName.Size = new Size(130, 23);
            lblQBittorrentProcessName.TabIndex = 9;
            lblQBittorrentProcessName.Text = "Process name:";
            lblQBittorrentProcessName.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // txtQBittorrentProcessName
            // 
            txtQBittorrentProcessName.Location = new Point(148, 140);
            txtQBittorrentProcessName.Name = "txtQBittorrentProcessName";
            txtQBittorrentProcessName.Size = new Size(320, 23);
            txtQBittorrentProcessName.TabIndex = 10;
            // 
            // chkRestartQBittorrent
            // 
            chkRestartQBittorrent.AutoSize = true;
            chkRestartQBittorrent.Location = new Point(12, 178);
            chkRestartQBittorrent.Name = "chkRestartQBittorrent";
            chkRestartQBittorrent.Size = new Size(314, 19);
            chkRestartQBittorrent.TabIndex = 11;
            chkRestartQBittorrent.Text = "Restart qBittorrent after a port change (recommended)";
            // 
            // chkForceStartQBittorrent
            // 
            chkForceStartQBittorrent.AutoSize = true;
            chkForceStartQBittorrent.Location = new Point(12, 203);
            chkForceStartQBittorrent.Name = "chkForceStartQBittorrent";
            chkForceStartQBittorrent.Size = new Size(217, 19);
            chkForceStartQBittorrent.TabIndex = 12;
            chkForceStartQBittorrent.Text = "Force start qBittorrent if not running";
            // 
            // lblDefaultPort
            // 
            lblDefaultPort.Location = new Point(12, 228);
            lblDefaultPort.Name = "lblDefaultPort";
            lblDefaultPort.Size = new Size(130, 23);
            lblDefaultPort.TabIndex = 13;
            lblDefaultPort.Text = "Default port:";
            lblDefaultPort.TextAlign = ContentAlignment.MiddleLeft;
            //
            // nudDefaultPort
            //
            nudDefaultPort.Location = new Point(148, 228);
            nudDefaultPort.Maximum = new decimal(new int[] { 65535, 0, 0, 0 });
            nudDefaultPort.Name = "nudDefaultPort";
            nudDefaultPort.Size = new Size(80, 23);
            nudDefaultPort.TabIndex = 14;
            // 
            // chkWarnOnInterfaceMismatch
            // 
            chkWarnOnInterfaceMismatch.AutoSize = true;
            chkWarnOnInterfaceMismatch.Location = new Point(12, 257);
            chkWarnOnInterfaceMismatch.Name = "chkWarnOnInterfaceMismatch";
            chkWarnOnInterfaceMismatch.Size = new Size(306, 19);
            chkWarnOnInterfaceMismatch.TabIndex = 15;
            chkWarnOnInterfaceMismatch.Text = "Warn when network interface doesn't match the VPN";
            // 
            // chkRestartOnDisconnect
            // 
            chkRestartOnDisconnect.AutoSize = true;
            chkRestartOnDisconnect.Location = new Point(12, 282);
            chkRestartOnDisconnect.Name = "chkRestartOnDisconnect";
            chkRestartOnDisconnect.Size = new Size(295, 19);
            chkRestartOnDisconnect.TabIndex = 16;
            chkRestartOnDisconnect.Text = "Restart qBittorrent if connection status disconnects";
            // 
            // grpExtra
            // 
            grpExtra.Controls.Add(lblColorTheme);
            grpExtra.Controls.Add(cboColorTheme);
            grpExtra.Controls.Add(lblPostUpdateCmd);
            grpExtra.Controls.Add(txtPostUpdateCmd);
            grpExtra.Controls.Add(chkDebugMode);
            grpExtra.Location = new Point(8, 503);
            grpExtra.Name = "grpExtra";
            grpExtra.Size = new Size(484, 126);
            grpExtra.TabIndex = 2;
            grpExtra.TabStop = false;
            grpExtra.Text = "Extra";
            //
            // lblColorTheme
            //
            lblColorTheme.Location = new Point(12, 24);
            lblColorTheme.Name = "lblColorTheme";
            lblColorTheme.Size = new Size(130, 23);
            lblColorTheme.TabIndex = 0;
            lblColorTheme.Text = "Color theme:";
            lblColorTheme.TextAlign = ContentAlignment.MiddleLeft;
            //
            // cboColorTheme
            //
            cboColorTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            cboColorTheme.Location = new Point(148, 24);
            cboColorTheme.Name = "cboColorTheme";
            cboColorTheme.Size = new Size(150, 23);
            cboColorTheme.TabIndex = 1;
            //
            // lblPostUpdateCmd
            //
            lblPostUpdateCmd.Location = new Point(12, 53);
            lblPostUpdateCmd.Name = "lblPostUpdateCmd";
            lblPostUpdateCmd.Size = new Size(130, 23);
            lblPostUpdateCmd.TabIndex = 2;
            lblPostUpdateCmd.Text = "Post-update:";
            lblPostUpdateCmd.TextAlign = ContentAlignment.MiddleLeft;
            //
            // txtPostUpdateCmd
            //
            txtPostUpdateCmd.Location = new Point(148, 53);
            txtPostUpdateCmd.Name = "txtPostUpdateCmd";
            txtPostUpdateCmd.Size = new Size(320, 23);
            txtPostUpdateCmd.TabIndex = 3;
            //
            // chkDebugMode
            //
            chkDebugMode.AutoSize = true;
            chkDebugMode.Location = new Point(12, 82);
            chkDebugMode.Name = "chkDebugMode";
            chkDebugMode.Size = new Size(142, 19);
            chkDebugMode.TabIndex = 4;
            chkDebugMode.Text = "Enable debug logging";
            //
            // btnOK
            //
            btnOK.Location = new Point(320, 641);
            btnOK.Name = "btnOK";
            btnOK.Size = new Size(82, 28);
            btnOK.TabIndex = 3;
            btnOK.Text = "OK";
            btnOK.Click += btnOK_Click;
            //
            // btnCancel
            //
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location = new Point(410, 641);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(82, 28);
            btnCancel.TabIndex = 4;
            btnCancel.Text = "Cancel";
            btnCancel.Click += btnCancel_Click;
            //
            // SettingsForm
            //
            AcceptButton = btnOK;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            CancelButton = btnCancel;
            ClientSize = new Size(500, 680);
            Controls.Add(grpGeneral);
            Controls.Add(grpQBittorrent);
            Controls.Add(grpExtra);
            Controls.Add(btnOK);
            Controls.Add(btnCancel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "SettingsForm";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "qbPortWeaver | Settings";
            grpGeneral.ResumeLayout(false);
            grpGeneral.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)nudUpdateInterval).EndInit();
            ((System.ComponentModel.ISupportInitialize)nudRecoveryCycles).EndInit();
            grpQBittorrent.ResumeLayout(false);
            grpQBittorrent.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)nudDefaultPort).EndInit();
            grpExtra.ResumeLayout(false);
            grpExtra.PerformLayout();
            ResumeLayout(false);
        }

        private GroupBox    grpGeneral;
        private Label       lblVpnProvider;
        private ComboBox    cboVpnProvider;
        private Label       lblNatPmpAdapter;
        private ComboBox    cboNatPmpAdapter;
        private Button      btnRefreshAdapters;
        private Label       lblUpdateInterval;
        private NumericUpDown nudUpdateInterval;
        private Label       lblSeconds;

        private GroupBox    grpQBittorrent;
        private Label       lblQBittorrentURL;
        private TextBox     txtQBittorrentURL;
        private Label       lblQBittorrentUserName;
        private TextBox     txtQBittorrentUserName;
        private Label       lblQBittorrentPassword;
        private TextBox     txtQBittorrentPassword;
        private Label       lblQBittorrentExePath;
        private TextBox     txtQBittorrentExePath;
        private Button      btnBrowseExePath;
        private Label       lblQBittorrentProcessName;
        private TextBox     txtQBittorrentProcessName;
        private CheckBox    chkRestartQBittorrent;
        private CheckBox    chkForceStartQBittorrent;
        private Label       lblDefaultPort;
        private NumericUpDown nudDefaultPort;
        private CheckBox    chkWarnOnInterfaceMismatch;
        private CheckBox    chkRestartOnDisconnect;

        private GroupBox    grpExtra;
        private Label       lblPostUpdateCmd;
        private TextBox     txtPostUpdateCmd;
        private CheckBox    chkDebugMode;
        private Label       lblColorTheme;
        private ComboBox    cboColorTheme;

        private CheckBox      chkAutoRecovery;
        private Label         lblRecoveryCycles;
        private NumericUpDown nudRecoveryCycles;
        private Label         lblRecoveryCyclesUnit;

        private Button      btnOK;
        private Button      btnCancel;
        private ToolTip     toolTip;
    }
}

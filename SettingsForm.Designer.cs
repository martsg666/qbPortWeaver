namespace qbPortWeaver
{
    partial class SettingsForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
                components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            grpGeneral               = new GroupBox();
            lblVpnProvider           = new Label();
            cboVpnProvider           = new ComboBox();
            lblNatPmpAdapter         = new Label();
            cboNatPmpAdapter         = new ComboBox();
            lblUpdateInterval        = new Label();
            nudUpdateInterval        = new NumericUpDown();
            lblSeconds               = new Label();

            grpQBittorrent           = new GroupBox();
            lblQBittorrentURL        = new Label();
            txtQBittorrentURL        = new TextBox();
            lblQBittorrentUserName   = new Label();
            txtQBittorrentUserName   = new TextBox();
            lblQBittorrentPassword   = new Label();
            txtQBittorrentPassword   = new TextBox();
            lblQBittorrentExePath    = new Label();
            txtQBittorrentExePath    = new TextBox();
            btnBrowseExePath         = new Button();
            lblQBittorrentProcessName = new Label();
            txtQBittorrentProcessName = new TextBox();
            chkRestartQBittorrent    = new CheckBox();
            chkForceStartQBittorrent = new CheckBox();
            lblDefaultPort           = new Label();
            nudDefaultPort           = new NumericUpDown();
            chkWarnOnInterfaceMismatch = new CheckBox();
            chkRestartOnDisconnect     = new CheckBox();

            grpExtra                 = new GroupBox();
            lblPostUpdateCmd         = new Label();
            txtPostUpdateCmd         = new TextBox();
            chkDebugMode             = new CheckBox();

            btnOK                    = new Button();
            btnCancel                = new Button();

            toolTip                 = new ToolTip(components);

            grpGeneral.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)nudUpdateInterval).BeginInit();
            grpQBittorrent.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)nudDefaultPort).BeginInit();
            grpExtra.SuspendLayout();
            SuspendLayout();

            // ── grpGeneral ────────────────────────────────────────────────
            grpGeneral.Controls.Add(lblVpnProvider);
            grpGeneral.Controls.Add(cboVpnProvider);
            grpGeneral.Controls.Add(lblNatPmpAdapter);
            grpGeneral.Controls.Add(cboNatPmpAdapter);
            grpGeneral.Controls.Add(lblUpdateInterval);
            grpGeneral.Controls.Add(nudUpdateInterval);
            grpGeneral.Controls.Add(lblSeconds);
            grpGeneral.Location = new Point(8, 8);
            grpGeneral.Size     = new Size(480, 113);
            grpGeneral.TabStop  = false;
            grpGeneral.Text     = "General";

            lblVpnProvider.Location  = new Point(12, 27);
            lblVpnProvider.Size      = new Size(130, 23);
            lblVpnProvider.Text      = "VPN Provider:";
            lblVpnProvider.TextAlign = ContentAlignment.MiddleLeft;

            cboVpnProvider.DropDownStyle = ComboBoxStyle.DropDownList;
            cboVpnProvider.Items.AddRange(new object[] { "ProtonVPN", "PIA", "NAT-PMP" });
            cboVpnProvider.Location  = new Point(148, 24);
            cboVpnProvider.Size      = new Size(200, 23);
            cboVpnProvider.TabIndex  = 0;
            cboVpnProvider.SelectedIndexChanged += cboVpnProvider_SelectedIndexChanged;

            lblNatPmpAdapter.Location  = new Point(12, 85);
            lblNatPmpAdapter.Size      = new Size(130, 23);
            lblNatPmpAdapter.Text      = "NAT-PMP Adapter:";
            lblNatPmpAdapter.TextAlign = ContentAlignment.MiddleLeft;

            cboNatPmpAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            cboNatPmpAdapter.Location  = new Point(148, 82);
            cboNatPmpAdapter.Size      = new Size(320, 23);
            cboNatPmpAdapter.TabIndex  = 17;

            lblUpdateInterval.Location  = new Point(12, 56);
            lblUpdateInterval.Size      = new Size(130, 23);
            lblUpdateInterval.Text      = "Update interval:";
            lblUpdateInterval.TextAlign = ContentAlignment.MiddleLeft;

            nudUpdateInterval.Location = new Point(148, 53);
            nudUpdateInterval.Minimum  = 10;
            nudUpdateInterval.Maximum  = 86400;
            nudUpdateInterval.Value    = 180;
            nudUpdateInterval.Size     = new Size(80, 23);
            nudUpdateInterval.TabIndex = 1;

            lblSeconds.Location  = new Point(234, 56);
            lblSeconds.Size      = new Size(55, 23);
            lblSeconds.Text      = "seconds";
            lblSeconds.TextAlign = ContentAlignment.MiddleLeft;

            // ── grpQBittorrent ────────────────────────────────────────────
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
            grpQBittorrent.Location = new Point(8, 133);
            grpQBittorrent.Size     = new Size(480, 320);
            grpQBittorrent.TabStop  = false;
            grpQBittorrent.Text     = "qBittorrent";

            // Row 0 — URL
            lblQBittorrentURL.Location  = new Point(12, 27);
            lblQBittorrentURL.Size      = new Size(130, 23);
            lblQBittorrentURL.Text      = "URL:";
            lblQBittorrentURL.TextAlign = ContentAlignment.MiddleLeft;

            txtQBittorrentURL.Location = new Point(148, 24);
            txtQBittorrentURL.Size     = new Size(320, 23);
            txtQBittorrentURL.TabIndex = 2;

            // Row 1 — Username
            lblQBittorrentUserName.Location  = new Point(12, 56);
            lblQBittorrentUserName.Size      = new Size(130, 23);
            lblQBittorrentUserName.Text      = "Username:";
            lblQBittorrentUserName.TextAlign = ContentAlignment.MiddleLeft;

            txtQBittorrentUserName.Location = new Point(148, 53);
            txtQBittorrentUserName.Size     = new Size(320, 23);
            txtQBittorrentUserName.TabIndex = 3;

            // Row 2 — Password
            lblQBittorrentPassword.Location  = new Point(12, 85);
            lblQBittorrentPassword.Size      = new Size(130, 23);
            lblQBittorrentPassword.Text      = "Password:";
            lblQBittorrentPassword.TextAlign = ContentAlignment.MiddleLeft;

            txtQBittorrentPassword.Location     = new Point(148, 82);
            txtQBittorrentPassword.PasswordChar = '*';
            txtQBittorrentPassword.Size         = new Size(320, 23);
            txtQBittorrentPassword.TabIndex     = 4;

            // Row 3 — Exe path
            lblQBittorrentExePath.Location  = new Point(12, 114);
            lblQBittorrentExePath.Size      = new Size(130, 23);
            lblQBittorrentExePath.Text      = "Executable:";
            lblQBittorrentExePath.TextAlign = ContentAlignment.MiddleLeft;

            txtQBittorrentExePath.Location = new Point(148, 111);
            txtQBittorrentExePath.Size     = new Size(276, 23);
            txtQBittorrentExePath.TabIndex = 5;

            btnBrowseExePath.Location  = new Point(428, 111);
            btnBrowseExePath.Size      = new Size(40, 23);
            btnBrowseExePath.Text      = "...";
            btnBrowseExePath.TabIndex  = 6;
            btnBrowseExePath.Click    += btnBrowseExePath_Click;

            // Row 4 — Process name
            lblQBittorrentProcessName.Location  = new Point(12, 143);
            lblQBittorrentProcessName.Size      = new Size(130, 23);
            lblQBittorrentProcessName.Text      = "Process name:";
            lblQBittorrentProcessName.TextAlign = ContentAlignment.MiddleLeft;

            txtQBittorrentProcessName.Location = new Point(148, 140);
            txtQBittorrentProcessName.Size     = new Size(320, 23);
            txtQBittorrentProcessName.TabIndex = 7;

            // Row 5 — Restart checkbox
            chkRestartQBittorrent.AutoSize = true;
            chkRestartQBittorrent.Location = new Point(12, 169);
            chkRestartQBittorrent.Text     = "Restart qBittorrent after a port change (recommended)";
            chkRestartQBittorrent.TabIndex = 8;

            // Row 6 — Force start checkbox
            chkForceStartQBittorrent.AutoSize = true;
            chkForceStartQBittorrent.Location = new Point(12, 195);
            chkForceStartQBittorrent.Text     = "Force start qBittorrent if not running";
            chkForceStartQBittorrent.TabIndex = 9;

            // Row 7 — Default port
            lblDefaultPort.Location  = new Point(12, 227);
            lblDefaultPort.Size      = new Size(210, 23);
            lblDefaultPort.Text      = "Default port (0 = disabled):";
            lblDefaultPort.TextAlign = ContentAlignment.MiddleLeft;

            nudDefaultPort.Location = new Point(226, 224);
            nudDefaultPort.Minimum  = 0;
            nudDefaultPort.Maximum  = 65535;
            nudDefaultPort.Value    = 0;
            nudDefaultPort.Size     = new Size(80, 23);
            nudDefaultPort.TabIndex = 10;

            // Row 8 — Interface mismatch warning
            chkWarnOnInterfaceMismatch.AutoSize = true;
            chkWarnOnInterfaceMismatch.Location = new Point(12, 256);
            chkWarnOnInterfaceMismatch.Text     = "Warn when network interface doesn't match the VPN";
            chkWarnOnInterfaceMismatch.TabIndex = 11;

            // Row 9 — Restart on disconnect
            chkRestartOnDisconnect.AutoSize = true;
            chkRestartOnDisconnect.Location = new Point(12, 285);
            chkRestartOnDisconnect.Text     = "Restart qBittorrent if connection status disconnects";
            chkRestartOnDisconnect.TabIndex = 12;

            // ── grpExtra ──────────────────────────────────────────────────
            grpExtra.Controls.Add(lblPostUpdateCmd);
            grpExtra.Controls.Add(txtPostUpdateCmd);
            grpExtra.Controls.Add(chkDebugMode);
            grpExtra.Location = new Point(8, 465);
            grpExtra.Size     = new Size(480, 84);
            grpExtra.TabStop  = false;
            grpExtra.Text     = "Extra";

            // Row 0 — Post-update command
            lblPostUpdateCmd.Location  = new Point(12, 27);
            lblPostUpdateCmd.Size      = new Size(130, 23);
            lblPostUpdateCmd.Text      = "Post-update command:";
            lblPostUpdateCmd.TextAlign = ContentAlignment.MiddleLeft;

            txtPostUpdateCmd.Location = new Point(148, 24);
            txtPostUpdateCmd.Size     = new Size(320, 23);
            txtPostUpdateCmd.TabIndex = 13;

            // Row 1 — Debug mode
            chkDebugMode.AutoSize = true;
            chkDebugMode.Location = new Point(12, 53);
            chkDebugMode.Text     = "Enable debug logging";
            chkDebugMode.TabIndex = 14;

            // ── Buttons ───────────────────────────────────────────────────
            btnOK.Location     = new Point(308, 561);
            btnOK.Size         = new Size(82, 28);
            btnOK.Text         = "OK";
            btnOK.TabIndex     = 15;
            btnOK.Click       += btnOK_Click;

            btnCancel.Location     = new Point(400, 561);
            btnCancel.Size         = new Size(82, 28);
            btnCancel.Text         = "Cancel";
            btnCancel.TabIndex     = 16;
            btnCancel.DialogResult = DialogResult.Cancel;

            // ── SettingsForm ───────────────────────────────────────────────
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode       = AutoScaleMode.Font;
            AcceptButton        = btnOK;
            CancelButton        = btnCancel;
            ClientSize          = new Size(498, 601);
            Controls.Add(grpGeneral);
            Controls.Add(grpQBittorrent);
            Controls.Add(grpExtra);
            Controls.Add(btnOK);
            Controls.Add(btnCancel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowIcon        = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Text            = $"{AppConstants.AppName} | Settings";

            grpGeneral.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)nudUpdateInterval).EndInit();
            grpQBittorrent.ResumeLayout(false);
            grpQBittorrent.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)nudDefaultPort).EndInit();
            grpExtra.ResumeLayout(false);
            grpExtra.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private GroupBox    grpGeneral;
        private Label       lblVpnProvider;
        private ComboBox    cboVpnProvider;
        private Label       lblNatPmpAdapter;
        private ComboBox    cboNatPmpAdapter;
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

        private Button      btnOK;
        private Button      btnCancel;
        private ToolTip     toolTip;
    }
}

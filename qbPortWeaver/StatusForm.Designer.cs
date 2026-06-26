namespace qbPortWeaver;

partial class StatusForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lblHeader.Font?.Dispose();
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        lblHeader = new Label();
        grpStatus = new GroupBox();
        lblVpnProviderLabel = new Label();
        lblVpnProviderValue = new Label();
        lblVpnStatusLabel = new Label();
        lblVpnStatusValue = new Label();
        lblForwardedPortLabel = new Label();
        lblForwardedPortValue = new Label();
        lblClientLabel = new Label();
        lblClientValue = new Label();
        lblListeningPortLabel = new Label();
        lblListeningPortValue = new Label();
        lblReachableLabel = new Label();
        lblReachableValue = new Label();
        lblLastSyncLabel = new Label();
        lblLastSyncValue = new Label();
        btnSyncNow = new Button();
        btnClose = new Button();
        grpStatus.SuspendLayout();
        SuspendLayout();
        // ── Header ────────────────────────────────────────────────────
        lblHeader.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        lblHeader.Location = new Point(8, 10);
        lblHeader.Name = "lblHeader";
        lblHeader.Size = new Size(364, 26);
        lblHeader.TabIndex = 0;
        lblHeader.Text = "Connection status";
        // ── grpStatus ─────────────────────────────────────────────────
        grpStatus.Controls.Add(lblVpnProviderLabel);
        grpStatus.Controls.Add(lblVpnProviderValue);
        grpStatus.Controls.Add(lblVpnStatusLabel);
        grpStatus.Controls.Add(lblVpnStatusValue);
        grpStatus.Controls.Add(lblForwardedPortLabel);
        grpStatus.Controls.Add(lblForwardedPortValue);
        grpStatus.Controls.Add(lblClientLabel);
        grpStatus.Controls.Add(lblClientValue);
        grpStatus.Controls.Add(lblListeningPortLabel);
        grpStatus.Controls.Add(lblListeningPortValue);
        grpStatus.Controls.Add(lblReachableLabel);
        grpStatus.Controls.Add(lblReachableValue);
        grpStatus.Controls.Add(lblLastSyncLabel);
        grpStatus.Controls.Add(lblLastSyncValue);
        grpStatus.Location = new Point(8, 44);
        grpStatus.Name = "grpStatus";
        grpStatus.Size = new Size(364, 236);
        grpStatus.TabIndex = 1;
        grpStatus.TabStop = false;
        grpStatus.Text = "Sync chain";
        lblVpnProviderLabel.Location = new Point(12, 24);
        lblVpnProviderLabel.Name = "lblVpnProviderLabel";
        lblVpnProviderLabel.Size = new Size(130, 23);
        lblVpnProviderLabel.TabIndex = 0;
        lblVpnProviderLabel.Text = "VPN provider:";
        lblVpnProviderLabel.TextAlign = ContentAlignment.MiddleLeft;
        lblVpnProviderValue.Location = new Point(148, 24);
        lblVpnProviderValue.Name = "lblVpnProviderValue";
        lblVpnProviderValue.Size = new Size(200, 23);
        lblVpnProviderValue.TabIndex = 1;
        lblVpnProviderValue.Text = "-";
        lblVpnProviderValue.TextAlign = ContentAlignment.MiddleLeft;
        lblVpnStatusLabel.Location = new Point(12, 53);
        lblVpnStatusLabel.Name = "lblVpnStatusLabel";
        lblVpnStatusLabel.Size = new Size(130, 23);
        lblVpnStatusLabel.TabIndex = 2;
        lblVpnStatusLabel.Text = "VPN status:";
        lblVpnStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        lblVpnStatusValue.Location = new Point(148, 53);
        lblVpnStatusValue.Name = "lblVpnStatusValue";
        lblVpnStatusValue.Size = new Size(200, 23);
        lblVpnStatusValue.TabIndex = 3;
        lblVpnStatusValue.Text = "-";
        lblVpnStatusValue.TextAlign = ContentAlignment.MiddleLeft;
        lblForwardedPortLabel.Location = new Point(12, 82);
        lblForwardedPortLabel.Name = "lblForwardedPortLabel";
        lblForwardedPortLabel.Size = new Size(130, 23);
        lblForwardedPortLabel.TabIndex = 4;
        lblForwardedPortLabel.Text = "Forwarded port:";
        lblForwardedPortLabel.TextAlign = ContentAlignment.MiddleLeft;
        lblForwardedPortValue.Location = new Point(148, 82);
        lblForwardedPortValue.Name = "lblForwardedPortValue";
        lblForwardedPortValue.Size = new Size(200, 23);
        lblForwardedPortValue.TabIndex = 5;
        lblForwardedPortValue.Text = "-";
        lblForwardedPortValue.TextAlign = ContentAlignment.MiddleLeft;
        lblClientLabel.Location = new Point(12, 111);
        lblClientLabel.Name = "lblClientLabel";
        lblClientLabel.Size = new Size(130, 23);
        lblClientLabel.TabIndex = 6;
        lblClientLabel.Text = "Client:";
        lblClientLabel.TextAlign = ContentAlignment.MiddleLeft;
        lblClientValue.Location = new Point(148, 111);
        lblClientValue.Name = "lblClientValue";
        lblClientValue.Size = new Size(200, 23);
        lblClientValue.TabIndex = 7;
        lblClientValue.Text = "-";
        lblClientValue.TextAlign = ContentAlignment.MiddleLeft;
        lblListeningPortLabel.Location = new Point(12, 140);
        lblListeningPortLabel.Name = "lblListeningPortLabel";
        lblListeningPortLabel.Size = new Size(130, 23);
        lblListeningPortLabel.TabIndex = 8;
        lblListeningPortLabel.Text = "Listening port:";
        lblListeningPortLabel.TextAlign = ContentAlignment.MiddleLeft;
        lblListeningPortValue.Location = new Point(148, 140);
        lblListeningPortValue.Name = "lblListeningPortValue";
        lblListeningPortValue.Size = new Size(200, 23);
        lblListeningPortValue.TabIndex = 9;
        lblListeningPortValue.Text = "-";
        lblListeningPortValue.TextAlign = ContentAlignment.MiddleLeft;
        lblReachableLabel.Location = new Point(12, 169);
        lblReachableLabel.Name = "lblReachableLabel";
        lblReachableLabel.Size = new Size(130, 23);
        lblReachableLabel.TabIndex = 10;
        lblReachableLabel.Text = "Reachable:";
        lblReachableLabel.TextAlign = ContentAlignment.MiddleLeft;
        lblReachableValue.Location = new Point(148, 169);
        lblReachableValue.Name = "lblReachableValue";
        lblReachableValue.Size = new Size(200, 23);
        lblReachableValue.TabIndex = 11;
        lblReachableValue.Text = "-";
        lblReachableValue.TextAlign = ContentAlignment.MiddleLeft;
        lblLastSyncLabel.Location = new Point(12, 198);
        lblLastSyncLabel.Name = "lblLastSyncLabel";
        lblLastSyncLabel.Size = new Size(130, 23);
        lblLastSyncLabel.TabIndex = 12;
        lblLastSyncLabel.Text = "Last sync:";
        lblLastSyncLabel.TextAlign = ContentAlignment.MiddleLeft;
        lblLastSyncValue.Location = new Point(148, 198);
        lblLastSyncValue.Name = "lblLastSyncValue";
        lblLastSyncValue.Size = new Size(200, 23);
        lblLastSyncValue.TabIndex = 13;
        lblLastSyncValue.Text = "-";
        lblLastSyncValue.TextAlign = ContentAlignment.MiddleLeft;
        // ── Buttons ───────────────────────────────────────────────────
        btnSyncNow.Location = new Point(8, 290);
        btnSyncNow.Name = "btnSyncNow";
        btnSyncNow.Size = new Size(96, 28);
        btnSyncNow.TabIndex = 2;
        btnSyncNow.Text = "Sync Now";
        btnSyncNow.Click += btnSyncNow_Click;
        btnClose.DialogResult = DialogResult.Cancel;
        btnClose.Location = new Point(290, 290);
        btnClose.Name = "btnClose";
        btnClose.Size = new Size(82, 28);
        btnClose.TabIndex = 3;
        btnClose.Text = "Close";
        btnClose.Click += btnClose_Click;
        // ── StatusForm ────────────────────────────────────────────────
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = btnClose;
        ClientSize = new Size(380, 326);
        Controls.Add(lblHeader);
        Controls.Add(grpStatus);
        Controls.Add(btnSyncNow);
        Controls.Add(btnClose);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "StatusForm";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "qbPortWeaver | Status"; // overridden in constructor
        grpStatus.ResumeLayout(false);
        ResumeLayout(false);
    }

    private Label    lblHeader;
    private GroupBox grpStatus;
    private Label    lblVpnProviderLabel;
    private Label    lblVpnProviderValue;
    private Label    lblVpnStatusLabel;
    private Label    lblVpnStatusValue;
    private Label    lblForwardedPortLabel;
    private Label    lblForwardedPortValue;
    private Label    lblClientLabel;
    private Label    lblClientValue;
    private Label    lblListeningPortLabel;
    private Label    lblListeningPortValue;
    private Label    lblReachableLabel;
    private Label    lblReachableValue;
    private Label    lblLastSyncLabel;
    private Label    lblLastSyncValue;
    private Button   btnSyncNow;
    private Button   btnClose;
}

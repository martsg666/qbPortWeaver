namespace qbPortWeaver;

partial class StatusForm
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
        grpHistory = new GroupBox();
        lvHistory = new ListView();
        colHistoryTime = new ColumnHeader();
        colHistoryPort = new ColumnHeader();
        colHistoryEvent = new ColumnHeader();
        ctxHistory = new ContextMenuStrip();
        ctxClearHistory = new ToolStripMenuItem();
        components = new System.ComponentModel.Container();
        components.Add(ctxHistory);
        btnSyncNow = new Button();
        btnTestPort = new Button();
        btnRunDiagnostics = new Button();
        btnClose = new Button();
        lblDiagnosticsHint = new Label();
        grpStatus.SuspendLayout();
        grpHistory.SuspendLayout();
        SuspendLayout();
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
        grpStatus.Location = new Point(8, 12);
        grpStatus.Name = "grpStatus";
        grpStatus.Size = new Size(454, 236);
        grpStatus.TabIndex = 0;
        grpStatus.TabStop = false;
        grpStatus.Text = "Connection Status";
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
        // ── grpHistory (recent port changes and recovery events; populated in PopulateHistory) ──
        grpHistory.Controls.Add(lvHistory);
        grpHistory.Location = new Point(8, 254);
        grpHistory.Name = "grpHistory";
        grpHistory.Size = new Size(454, 156);
        grpHistory.TabIndex = 6;
        grpHistory.TabStop = false;
        grpHistory.Text = "Recent Port Changes";
        colHistoryTime.Text = "Time";
        colHistoryTime.Width = 118;
        colHistoryPort.Text = "Port";
        colHistoryPort.Width = 52;
        colHistoryEvent.Text = "Event";
        colHistoryEvent.Width = 256; // fills the list width (with Time + Port) so no stray blank column area shows
        // Window (input surface), not the Control chrome: this is an embedded data panel inside a
        // label-heavy dialog, so it reads as a distinct data box (like MediaManagerForm.dgvResults).
        // Contrast with LogViewerForm.lvLog, which uses Control because it fills the whole window.
        lvHistory.BackColor = SystemColors.Window;
        lvHistory.Columns.AddRange(new[] { colHistoryTime, colHistoryPort, colHistoryEvent });
        lvHistory.FullRowSelect = true;
        lvHistory.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        lvHistory.Location = new Point(12, 22);
        lvHistory.MultiSelect = false;
        lvHistory.Name = "lvHistory";
        lvHistory.ShowGroups = false;
        lvHistory.Size = new Size(430, 122);
        lvHistory.TabIndex = 0;
        lvHistory.UseCompatibleStateImageBehavior = false;
        lvHistory.View = View.Details;
        // Discrete clear option - right-click the history list (mirrors the log viewer's context menu)
        ctxClearHistory.Text = "Clear History";
        ctxClearHistory.Click += ctxClearHistory_Click;
        ctxHistory.Items.Add(ctxClearHistory);
        ctxHistory.Opening += ctxHistory_Opening;
        lvHistory.ContextMenuStrip = ctxHistory;
        // ── Diagnostics hint (shown only when a cycle looks wrong; color set in StatusForm) ──
        lblDiagnosticsHint.Location = new Point(8, 414);
        lblDiagnosticsHint.Name = "lblDiagnosticsHint";
        lblDiagnosticsHint.Size = new Size(454, 20);
        lblDiagnosticsHint.TabIndex = 1;
        lblDiagnosticsHint.Text = "Something looks off. Click Run Diagnostics for details.";
        lblDiagnosticsHint.TextAlign = ContentAlignment.MiddleLeft;
        lblDiagnosticsHint.Visible = false;
        // ── Buttons ───────────────────────────────────────────────────
        btnSyncNow.Location = new Point(8, 440);
        btnSyncNow.Name = "btnSyncNow";
        btnSyncNow.Size = new Size(96, 28);
        btnSyncNow.TabIndex = 2;
        btnSyncNow.Text = "Sync Now";
        btnSyncNow.Click += btnSyncNow_Click;
        btnTestPort.Location = new Point(112, 440);
        btnTestPort.Name = "btnTestPort";
        btnTestPort.Size = new Size(96, 28);
        btnTestPort.TabIndex = 3;
        btnTestPort.Text = "Test Port";
        btnTestPort.Click += btnTestPort_Click;
        btnRunDiagnostics.Location = new Point(216, 440);
        btnRunDiagnostics.Name = "btnRunDiagnostics";
        btnRunDiagnostics.Size = new Size(118, 28);
        btnRunDiagnostics.TabIndex = 4;
        btnRunDiagnostics.Text = "Run Diagnostics";
        btnRunDiagnostics.Click += btnRunDiagnostics_Click;
        btnClose.DialogResult = DialogResult.Cancel;
        btnClose.Location = new Point(380, 440);
        btnClose.Name = "btnClose";
        btnClose.Size = new Size(82, 28);
        btnClose.TabIndex = 5;
        btnClose.Text = "Close";
        btnClose.Click += btnClose_Click;
        // ── StatusForm ────────────────────────────────────────────────
        AcceptButton = btnClose;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = btnClose;
        ClientSize = new Size(470, 476);
        Controls.Add(grpStatus);
        Controls.Add(grpHistory);
        Controls.Add(lblDiagnosticsHint);
        Controls.Add(btnSyncNow);
        Controls.Add(btnTestPort);
        Controls.Add(btnRunDiagnostics);
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
        grpHistory.ResumeLayout(false);
        ResumeLayout(false);
    }

    private GroupBox grpStatus;
    private GroupBox grpHistory;
    private ListView lvHistory;
    private ColumnHeader colHistoryTime;
    private ColumnHeader colHistoryPort;
    private ColumnHeader colHistoryEvent;
    private ContextMenuStrip ctxHistory;
    private ToolStripMenuItem ctxClearHistory;
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
    private Button   btnTestPort;
    private Button   btnRunDiagnostics;
    private Button   btnClose;
    private Label    lblDiagnosticsHint;
}

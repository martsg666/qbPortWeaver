namespace qbPortWeaver;

partial class UpdateAvailableForm
{
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lblTitle.Font?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        lblTitle        = new Label();
        grpInfo         = new GroupBox();
        lblMessage      = new Label();
        prgDownload     = new ProgressBar();
        lblStatus       = new Label();
        lnkReleaseNotes = new LinkLabel();
        btnUpdate       = new Button();
        btnLater        = new Button();

        grpInfo.SuspendLayout();
        SuspendLayout();

        // ── Header ────────────────────────────────────────────────────
        lblTitle.Font     = new Font("Segoe UI", 13F, FontStyle.Bold);
        lblTitle.Location = new Point(8, 8);
        lblTitle.Name     = "lblTitle";
        lblTitle.Size     = new Size(384, 26);
        lblTitle.TabIndex = 0;
        lblTitle.Text     = "Update Available";

        // ── grpInfo ───────────────────────────────────────────────────
        grpInfo.Controls.Add(lblMessage);
        grpInfo.Location = new Point(8, 42);
        grpInfo.Name     = "grpInfo";
        grpInfo.Size     = new Size(384, 106);
        grpInfo.TabIndex = 1;
        grpInfo.TabStop  = false;
        grpInfo.Text     = "New Version";

        lblMessage.AutoSize    = false;
        lblMessage.Location    = new Point(12, 20);
        lblMessage.Name        = "lblMessage";
        lblMessage.Size        = new Size(360, 78);
        lblMessage.TabIndex    = 0;
        lblMessage.Text        = "";
        lblMessage.UseMnemonic = false; // render a literal '&' (e.g. "Download & Install") instead of treating it as a mnemonic

        // ── Download progress (shown only during an in-app download) ──
        prgDownload.Location = new Point(8, 156);
        prgDownload.Name     = "prgDownload";
        prgDownload.Size     = new Size(384, 18);
        prgDownload.TabIndex = 2;
        prgDownload.Visible  = false;

        lblStatus.AutoSize    = false;
        lblStatus.Location    = new Point(8, 178);
        lblStatus.Name        = "lblStatus";
        lblStatus.Size        = new Size(384, 20);
        lblStatus.TabIndex    = 3;
        lblStatus.Text        = "";
        lblStatus.TextAlign   = ContentAlignment.MiddleLeft;
        lblStatus.Visible     = false;

        // ── Release notes link + buttons ──────────────────────────────
        lnkReleaseNotes.AutoSize     = true;
        lnkReleaseNotes.Location     = new Point(8, 212);
        lnkReleaseNotes.Name         = "lnkReleaseNotes";
        lnkReleaseNotes.TabIndex     = 4;
        lnkReleaseNotes.Text         = "View release notes";
        lnkReleaseNotes.LinkClicked += lnkReleaseNotes_LinkClicked;

        btnUpdate.Location = new Point(172, 206);
        btnUpdate.Name     = "btnUpdate";
        btnUpdate.Size     = new Size(130, 28);
        btnUpdate.TabIndex = 5;
        btnUpdate.Text     = "Download && Install";
        btnUpdate.Click   += btnUpdate_Click;

        btnLater.Location = new Point(310, 206);
        btnLater.Name     = "btnLater";
        btnLater.Size     = new Size(82, 28);
        btnLater.TabIndex = 6;
        btnLater.Text     = "Later";
        btnLater.Click   += btnLater_Click;

        // ── UpdateAvailableForm ───────────────────────────────────────
        AcceptButton        = btnUpdate;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;
        CancelButton        = btnLater;
        ClientSize          = new Size(400, 242);
        Controls.Add(lblTitle);
        Controls.Add(grpInfo);
        Controls.Add(prgDownload);
        Controls.Add(lblStatus);
        Controls.Add(lnkReleaseNotes);
        Controls.Add(btnUpdate);
        Controls.Add(btnLater);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Name            = "UpdateAvailableForm";
        Icon            = Properties.Resources.qbPortWeaver;
        ShowIcon        = true;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.CenterScreen;
        Text            = "qbPortWeaver | Update Available"; // overridden in constructor

        grpInfo.ResumeLayout(false);
        ResumeLayout(false);
    }

    private Label       lblTitle;
    private GroupBox    grpInfo;
    private Label       lblMessage;
    private ProgressBar prgDownload;
    private Label       lblStatus;
    private LinkLabel   lnkReleaseNotes;
    private Button      btnUpdate;
    private Button      btnLater;
}

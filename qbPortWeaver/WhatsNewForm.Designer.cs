namespace qbPortWeaver;

partial class WhatsNewForm
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
        grpCommunity    = new GroupBox();
        lnkCommunity  = new LinkLabel();
        grpFeatures     = new GroupBox();
        rtbFeatures     = new RichTextBox();
        btnClose        = new Button();

        grpCommunity.SuspendLayout();
        grpFeatures.SuspendLayout();
        SuspendLayout();

        // ── Header ────────────────────────────────────────────────────
        lblTitle.Font     = new Font("Segoe UI", 13F, FontStyle.Bold);
        lblTitle.Location = new Point(8, 14);
        lblTitle.Name     = "lblTitle";
        lblTitle.Size     = new Size(384, 26);
        lblTitle.TabIndex = 0;
        lblTitle.Text     = "What's New";

        // ── grpCommunity ──────────────────────────────────────────────
        grpCommunity.Controls.Add(lnkCommunity);
        grpCommunity.Location = new Point(8, 48);
        grpCommunity.Name     = "grpCommunity";
        grpCommunity.Size     = new Size(384, 90);
        grpCommunity.TabIndex = 1;
        grpCommunity.TabStop  = false;
        grpCommunity.Text     = "Community";

        lnkCommunity.AutoSize     = false;
        lnkCommunity.Location     = new Point(12, 18);
        lnkCommunity.Name         = "lnkCommunity";
        lnkCommunity.Size         = new Size(360, 56);
        lnkCommunity.TabIndex     = 0;
        lnkCommunity.Text         = "";
        lnkCommunity.LinkClicked += lnkCommunity_LinkClicked;

        // ── grpFeatures ───────────────────────────────────────────────
        grpFeatures.Controls.Add(rtbFeatures);
        grpFeatures.Location = new Point(8, 146);
        grpFeatures.Name     = "grpFeatures";
        grpFeatures.Size     = new Size(384, 310);
        grpFeatures.TabIndex = 2;
        grpFeatures.TabStop  = false;
        grpFeatures.Text     = "New in this release";

        rtbFeatures.BackColor   = SystemColors.Control;
        rtbFeatures.BorderStyle = BorderStyle.None;
        rtbFeatures.Location    = new Point(12, 20);
        rtbFeatures.Name        = "rtbFeatures";
        rtbFeatures.ReadOnly    = true;
        rtbFeatures.ScrollBars  = RichTextBoxScrollBars.Vertical;
        rtbFeatures.Size        = new Size(360, 280);
        rtbFeatures.TabIndex    = 0;
        rtbFeatures.TabStop     = false;
        rtbFeatures.Text        = "";

        // ── Buttons ───────────────────────────────────────────────────
        btnClose.DialogResult = DialogResult.Cancel;
        btnClose.Location     = new Point(310, 468);
        btnClose.Name         = "btnClose";
        btnClose.Size         = new Size(82, 28);
        btnClose.TabIndex     = 3;
        btnClose.Text         = "Close";
        btnClose.Click       += btnClose_Click;

        // ── WhatsNewForm ──────────────────────────────────────────────
        AcceptButton        = btnClose;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;
        CancelButton        = btnClose;
        ClientSize          = new Size(400, 508);
        Controls.Add(lblTitle);
        Controls.Add(grpCommunity);
        Controls.Add(grpFeatures);
        Controls.Add(btnClose);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Name            = "WhatsNewForm";
        ShowIcon        = false;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.CenterScreen;
        Text            = "What's New";

        grpCommunity.ResumeLayout(false);
        grpFeatures.ResumeLayout(false);
        ResumeLayout(false);
    }

    private Label     lblTitle;
    private GroupBox  grpCommunity;
    private LinkLabel lnkCommunity;
    private GroupBox  grpFeatures;
    private RichTextBox rtbFeatures;
    private Button    btnClose;
}

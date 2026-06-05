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
        lblTitle   = new Label();
        grpInfo    = new GroupBox();
        lblMessage = new Label();
        btnUpdate  = new Button();
        btnLater   = new Button();

        grpInfo.SuspendLayout();
        SuspendLayout();

        // ── Header ────────────────────────────────────────────────────
        lblTitle.Font     = new Font("Segoe UI", 13F, FontStyle.Bold);
        lblTitle.Location = new Point(8, 14);
        lblTitle.Name     = "lblTitle";
        lblTitle.Size     = new Size(384, 26);
        lblTitle.TabIndex = 0;
        lblTitle.Text     = "Update Available";

        // ── grpInfo ───────────────────────────────────────────────────
        grpInfo.Controls.Add(lblMessage);
        grpInfo.Location = new Point(8, 48);
        grpInfo.Name     = "grpInfo";
        grpInfo.Size     = new Size(384, 92);
        grpInfo.TabIndex = 1;
        grpInfo.TabStop  = false;
        grpInfo.Text     = "New Version";

        lblMessage.AutoSize = false;
        lblMessage.Location = new Point(12, 20);
        lblMessage.Name     = "lblMessage";
        lblMessage.Size     = new Size(360, 58);
        lblMessage.TabIndex = 0;
        lblMessage.Text     = "";

        // ── Buttons ───────────────────────────────────────────────────
        btnUpdate.Location = new Point(220, 152);
        btnUpdate.Name     = "btnUpdate";
        btnUpdate.Size     = new Size(82, 28);
        btnUpdate.TabIndex = 2;
        btnUpdate.Text     = "Update";
        btnUpdate.Click   += btnUpdate_Click;

        btnLater.Location = new Point(310, 152);
        btnLater.Name     = "btnLater";
        btnLater.Size     = new Size(82, 28);
        btnLater.TabIndex = 3;
        btnLater.Text     = "Later";
        btnLater.Click   += btnLater_Click;

        // ── UpdateAvailableForm ───────────────────────────────────────
        AcceptButton        = btnUpdate;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;
        CancelButton        = btnLater;
        ClientSize          = new Size(400, 192);
        Controls.Add(lblTitle);
        Controls.Add(grpInfo);
        Controls.Add(btnUpdate);
        Controls.Add(btnLater);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Name            = "UpdateAvailableForm";
        ShowIcon        = false;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.CenterScreen;
        Text            = "Update Available";

        grpInfo.ResumeLayout(false);
        ResumeLayout(false);
    }

    private Label    lblTitle;
    private GroupBox grpInfo;
    private Label    lblMessage;
    private Button   btnUpdate;
    private Button   btnLater;
}

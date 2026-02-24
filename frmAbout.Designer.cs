namespace qbPortWeaver
{
    partial class frmAbout
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                picIcon.Image?.Dispose();
                if (components != null) components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            picIcon                = new PictureBox();
            lblAppName             = new Label();
            lblAppVersion          = new Label();
            grpVersion             = new GroupBox();
            lblCurrentVersionLabel = new Label();
            lblCurrentVersionValue = new Label();
            lblLatestVersionLabel  = new Label();
            lblLatestVersionValue  = new Label();
            lblStatusLabel         = new Label();
            lblStatusValue         = new Label();
            grpLinks               = new GroupBox();
            lblAuthorLabel         = new Label();
            lnkAuthor              = new LinkLabel();
            lblGitHubLabel         = new Label();
            lnkGitHub              = new LinkLabel();
            btnCheckForUpdates     = new Button();
            btnClose               = new Button();

            ((System.ComponentModel.ISupportInitialize)picIcon).BeginInit();
            grpVersion.SuspendLayout();
            grpLinks.SuspendLayout();
            SuspendLayout();

            // ── picIcon ──────────────────────────────────────────────────
            picIcon.Image    = Properties.Resources.qbPortWeaver.ToBitmap();
            picIcon.Location = new Point(12, 12);
            picIcon.Size     = new Size(48, 48);
            picIcon.SizeMode = PictureBoxSizeMode.Zoom;
            picIcon.TabStop  = false;

            // ── lblAppName ────────────────────────────────────────────────
            lblAppName.Font     = new Font("Segoe UI", 13F, FontStyle.Bold, GraphicsUnit.Point);
            lblAppName.Location = new Point(70, 14);
            lblAppName.Size     = new Size(296, 26);
            lblAppName.Text     = AppConstants.APP_NAME;

            // ── lblAppVersion ─────────────────────────────────────────────
            lblAppVersion.ForeColor = SystemColors.GrayText;
            lblAppVersion.Location  = new Point(70, 44);
            lblAppVersion.Size      = new Size(296, 20);
            lblAppVersion.Text      = $"Version {AppConstants.APP_VERSION}";

            // ── grpVersion ────────────────────────────────────────────────
            grpVersion.Controls.Add(lblCurrentVersionLabel);
            grpVersion.Controls.Add(lblCurrentVersionValue);
            grpVersion.Controls.Add(lblLatestVersionLabel);
            grpVersion.Controls.Add(lblLatestVersionValue);
            grpVersion.Controls.Add(lblStatusLabel);
            grpVersion.Controls.Add(lblStatusValue);
            grpVersion.Location = new Point(8, 74);
            grpVersion.Size     = new Size(364, 107);
            grpVersion.TabStop  = false;
            grpVersion.Text     = "Version";

            // Row 0 — Current version
            lblCurrentVersionLabel.Location  = new Point(12, 24);
            lblCurrentVersionLabel.Size      = new Size(130, 23);
            lblCurrentVersionLabel.Text      = "Current version:";
            lblCurrentVersionLabel.TextAlign = ContentAlignment.MiddleLeft;

            lblCurrentVersionValue.Location  = new Point(148, 24);
            lblCurrentVersionValue.Size      = new Size(200, 23);
            lblCurrentVersionValue.Text      = AppConstants.APP_VERSION;
            lblCurrentVersionValue.TextAlign = ContentAlignment.MiddleLeft;

            // Row 1 — Latest version
            lblLatestVersionLabel.Location  = new Point(12, 51);
            lblLatestVersionLabel.Size      = new Size(130, 23);
            lblLatestVersionLabel.Text      = "Latest version:";
            lblLatestVersionLabel.TextAlign = ContentAlignment.MiddleLeft;

            lblLatestVersionValue.ForeColor = SystemColors.GrayText;
            lblLatestVersionValue.Location  = new Point(148, 51);
            lblLatestVersionValue.Size      = new Size(200, 23);
            lblLatestVersionValue.Text      = "Checking\u2026";
            lblLatestVersionValue.TextAlign = ContentAlignment.MiddleLeft;

            // Row 2 — Status
            lblStatusLabel.Location  = new Point(12, 78);
            lblStatusLabel.Size      = new Size(130, 23);
            lblStatusLabel.Text      = "Status:";
            lblStatusLabel.TextAlign = ContentAlignment.MiddleLeft;

            lblStatusValue.Location  = new Point(148, 78);
            lblStatusValue.Size      = new Size(200, 23);
            lblStatusValue.TextAlign = ContentAlignment.MiddleLeft;

            // ── grpLinks ──────────────────────────────────────────────────
            grpLinks.Controls.Add(lblAuthorLabel);
            grpLinks.Controls.Add(lnkAuthor);
            grpLinks.Controls.Add(lblGitHubLabel);
            grpLinks.Controls.Add(lnkGitHub);
            grpLinks.Location = new Point(8, 193);
            grpLinks.Size     = new Size(364, 96);
            grpLinks.TabStop  = false;
            grpLinks.Text     = "Links";

            // Row 0 — Contributors (multi-line LinkLabel to handle any number of names)
            lblAuthorLabel.Location  = new Point(12, 24);
            lblAuthorLabel.Size      = new Size(130, 23);
            lblAuthorLabel.Text      = "Contributors:";
            lblAuthorLabel.TextAlign = ContentAlignment.TopLeft;

            lnkAuthor.Location  = new Point(148, 22);
            lnkAuthor.Size      = new Size(200, 44);
            lnkAuthor.TabIndex  = 0;
            lnkAuthor.Text      = "Loading\u2026";
            lnkAuthor.TextAlign = ContentAlignment.TopLeft;
            lnkAuthor.LinkArea  = new LinkArea(0, 0); // no active link until contributors are loaded
            lnkAuthor.LinkClicked += lnkAuthor_LinkClicked;

            // Row 1 — GitHub
            lblGitHubLabel.Location  = new Point(12, 70);
            lblGitHubLabel.Size      = new Size(130, 23);
            lblGitHubLabel.Text      = "GitHub:";
            lblGitHubLabel.TextAlign = ContentAlignment.MiddleLeft;

            lnkGitHub.Location  = new Point(148, 70);
            lnkGitHub.Size      = new Size(200, 23);
            lnkGitHub.TabIndex  = 1;
            lnkGitHub.Text      = $"{AppConstants.GITHUB_REPO_OWNER}/{AppConstants.APP_NAME}";
            lnkGitHub.TextAlign = ContentAlignment.MiddleLeft;
            lnkGitHub.LinkClicked += lnkGitHub_LinkClicked;

            // ── Buttons ───────────────────────────────────────────────────
            btnCheckForUpdates.Location = new Point(8, 301);
            btnCheckForUpdates.Size     = new Size(145, 28);
            btnCheckForUpdates.TabIndex = 2;
            btnCheckForUpdates.Text     = "Check for Updates";
            btnCheckForUpdates.Click   += btnCheckForUpdates_Click;

            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Location     = new Point(286, 301);
            btnClose.Size         = new Size(82, 28);
            btnClose.TabIndex     = 3;
            btnClose.Text         = "Close";

            // ── frmAbout ──────────────────────────────────────────────────
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode       = AutoScaleMode.Font;
            CancelButton        = btnClose;
            ClientSize          = new Size(380, 337);
            Controls.Add(picIcon);
            Controls.Add(lblAppName);
            Controls.Add(lblAppVersion);
            Controls.Add(grpVersion);
            Controls.Add(grpLinks);
            Controls.Add(btnCheckForUpdates);
            Controls.Add(btnClose);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowIcon        = false;
            StartPosition   = FormStartPosition.CenterParent;
            Text            = $"{AppConstants.APP_NAME} | About";

            ((System.ComponentModel.ISupportInitialize)picIcon).EndInit();
            grpVersion.ResumeLayout(false);
            grpLinks.ResumeLayout(false);
            ResumeLayout(false);
        }

        private PictureBox  picIcon;
        private Label       lblAppName;
        private Label       lblAppVersion;
        private GroupBox    grpVersion;
        private Label       lblCurrentVersionLabel;
        private Label       lblCurrentVersionValue;
        private Label       lblLatestVersionLabel;
        private Label       lblLatestVersionValue;
        private Label       lblStatusLabel;
        private Label       lblStatusValue;
        private GroupBox    grpLinks;
        private Label       lblAuthorLabel;
        private LinkLabel   lnkAuthor;
        private Label       lblGitHubLabel;
        private LinkLabel   lnkGitHub;
        private Button      btnCheckForUpdates;
        private Button      btnClose;
    }
}

namespace qbPortWeaver
{
    partial class AboutForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                picIcon.Image?.Dispose();
                lblAppName.Font?.Dispose();
                if (components != null) components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AboutForm));
            picIcon = new PictureBox();
            lblAppName = new Label();
            lblAppVersion = new Label();
            grpVersion = new GroupBox();
            lblCurrentVersionLabel = new Label();
            lblCurrentVersionValue = new Label();
            lblLatestVersionLabel = new Label();
            lblLatestVersionValue = new Label();
            lblStatusLabel = new Label();
            lblStatusValue = new Label();
            grpLinks = new GroupBox();
            lblAuthorLabel = new Label();
            lnkAuthor = new LinkLabel();
            lblGitHubLabel = new Label();
            lnkGitHub = new LinkLabel();
            btnCheckForUpdates = new Button();
            btnClose = new Button();
            ((System.ComponentModel.ISupportInitialize)picIcon).BeginInit();
            grpVersion.SuspendLayout();
            grpLinks.SuspendLayout();
            SuspendLayout();
            // ── Header ────────────────────────────────────────────────────
            picIcon.Image = (Image)resources.GetObject("picIcon.Image");
            picIcon.Location = new Point(12, 12);
            picIcon.Name = "picIcon";
            picIcon.Size = new Size(48, 48);
            picIcon.SizeMode = PictureBoxSizeMode.Zoom;
            picIcon.TabIndex = 0;
            picIcon.TabStop = false;
            lblAppName.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            lblAppName.Location = new Point(70, 14);
            lblAppName.Name = "lblAppName";
            lblAppName.Size = new Size(296, 26);
            lblAppName.TabIndex = 1;
            lblAppName.Text = "qbPortWeaver";
            lblAppVersion.ForeColor = SystemColors.GrayText;
            lblAppVersion.Location = new Point(70, 44);
            lblAppVersion.Name = "lblAppVersion";
            lblAppVersion.Size = new Size(296, 20);
            lblAppVersion.TabIndex = 2;
            // ── grpVersion ────────────────────────────────────────────────
            grpVersion.Controls.Add(lblCurrentVersionLabel);
            grpVersion.Controls.Add(lblCurrentVersionValue);
            grpVersion.Controls.Add(lblLatestVersionLabel);
            grpVersion.Controls.Add(lblLatestVersionValue);
            grpVersion.Controls.Add(lblStatusLabel);
            grpVersion.Controls.Add(lblStatusValue);
            grpVersion.Location = new Point(8, 74);
            grpVersion.Name = "grpVersion";
            grpVersion.Size = new Size(364, 107);
            grpVersion.TabIndex = 3;
            grpVersion.TabStop = false;
            grpVersion.Text = "Version";
            lblCurrentVersionLabel.Location = new Point(12, 24);
            lblCurrentVersionLabel.Name = "lblCurrentVersionLabel";
            lblCurrentVersionLabel.Size = new Size(130, 23);
            lblCurrentVersionLabel.TabIndex = 0;
            lblCurrentVersionLabel.Text = "Current version:";
            lblCurrentVersionLabel.TextAlign = ContentAlignment.MiddleLeft;
            lblCurrentVersionValue.Location = new Point(148, 24);
            lblCurrentVersionValue.Name = "lblCurrentVersionValue";
            lblCurrentVersionValue.Size = new Size(200, 23);
            lblCurrentVersionValue.TabIndex = 1;
            lblCurrentVersionValue.Text = "";
            lblCurrentVersionValue.TextAlign = ContentAlignment.MiddleLeft;
            lblLatestVersionLabel.Location = new Point(12, 51);
            lblLatestVersionLabel.Name = "lblLatestVersionLabel";
            lblLatestVersionLabel.Size = new Size(130, 23);
            lblLatestVersionLabel.TabIndex = 2;
            lblLatestVersionLabel.Text = "Latest version:";
            lblLatestVersionLabel.TextAlign = ContentAlignment.MiddleLeft;
            lblLatestVersionValue.ForeColor = SystemColors.GrayText;
            lblLatestVersionValue.Location = new Point(148, 51);
            lblLatestVersionValue.Name = "lblLatestVersionValue";
            lblLatestVersionValue.Size = new Size(200, 23);
            lblLatestVersionValue.TabIndex = 3;
            lblLatestVersionValue.Text = "Checking…";
            lblLatestVersionValue.TextAlign = ContentAlignment.MiddleLeft;
            lblStatusLabel.Location = new Point(12, 78);
            lblStatusLabel.Name = "lblStatusLabel";
            lblStatusLabel.Size = new Size(130, 23);
            lblStatusLabel.TabIndex = 4;
            lblStatusLabel.Text = "Status:";
            lblStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            lblStatusValue.Location = new Point(148, 78);
            lblStatusValue.Name = "lblStatusValue";
            lblStatusValue.Size = new Size(200, 23);
            lblStatusValue.TabIndex = 5;
            lblStatusValue.TextAlign = ContentAlignment.MiddleLeft;
            // ── grpLinks ──────────────────────────────────────────────────
            grpLinks.Controls.Add(lblAuthorLabel);
            grpLinks.Controls.Add(lnkAuthor);
            grpLinks.Controls.Add(lblGitHubLabel);
            grpLinks.Controls.Add(lnkGitHub);
            grpLinks.Location = new Point(8, 193);
            grpLinks.Name = "grpLinks";
            grpLinks.Size = new Size(364, 96);
            grpLinks.TabIndex = 4;
            grpLinks.TabStop = false;
            grpLinks.Text = "Links";
            lblAuthorLabel.Location = new Point(12, 24);
            lblAuthorLabel.Name = "lblAuthorLabel";
            lblAuthorLabel.Size = new Size(130, 23);
            lblAuthorLabel.TabIndex = 0;
            lblAuthorLabel.Text = "Contributors:";
            lnkAuthor.LinkArea = new LinkArea(0, 0);
            lnkAuthor.Location = new Point(148, 22);
            lnkAuthor.Name = "lnkAuthor";
            lnkAuthor.Size = new Size(200, 44);
            lnkAuthor.TabIndex = 0;
            lnkAuthor.Text = "Loading…";
            lnkAuthor.LinkClicked += lnkAuthor_LinkClicked;
            lblGitHubLabel.Location = new Point(12, 70);
            lblGitHubLabel.Name = "lblGitHubLabel";
            lblGitHubLabel.Size = new Size(130, 23);
            lblGitHubLabel.TabIndex = 1;
            lblGitHubLabel.Text = "GitHub:";
            lblGitHubLabel.TextAlign = ContentAlignment.MiddleLeft;
            lnkGitHub.Location = new Point(148, 70);
            lnkGitHub.Name = "lnkGitHub";
            lnkGitHub.Size = new Size(200, 23);
            lnkGitHub.TabIndex = 1;
            lnkGitHub.TextAlign = ContentAlignment.MiddleLeft;
            lnkGitHub.LinkClicked += lnkGitHub_LinkClicked;
            // ── Buttons ───────────────────────────────────────────────────
            btnCheckForUpdates.Location = new Point(8, 301);
            btnCheckForUpdates.Name = "btnCheckForUpdates";
            btnCheckForUpdates.Size = new Size(145, 28);
            btnCheckForUpdates.TabIndex = 2;
            btnCheckForUpdates.Text = "Check for Updates";
            btnCheckForUpdates.Click += btnCheckForUpdates_Click;
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Location = new Point(286, 301);
            btnClose.Name = "btnClose";
            btnClose.Size = new Size(82, 28);
            btnClose.TabIndex = 3;
            btnClose.Text = "Close";
            // ── AboutForm ─────────────────────────────────────────────────
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            CancelButton = btnClose;
            ClientSize = new Size(380, 337);
            Controls.Add(picIcon);
            Controls.Add(lblAppName);
            Controls.Add(lblAppVersion);
            Controls.Add(grpVersion);
            Controls.Add(grpLinks);
            Controls.Add(btnCheckForUpdates);
            Controls.Add(btnClose);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "AboutForm";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "About";
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

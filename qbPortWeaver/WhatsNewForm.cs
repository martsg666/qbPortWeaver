namespace qbPortWeaver
{
    /// <summary>Displays a summary of what changed in the current version. Shown automatically on first run after an upgrade.</summary>
    public partial class WhatsNewForm : Form
    {
        // Update these constants each release. They live here (not in Designer.cs) so the designer
        // cannot overwrite them, and content changes never touch layout code.
        private const string CommunityText =
            "The GitHub repository was recently set to private for a period, and all stars were " +
            "lost. If you find qbPortWeaver useful, please star it on GitHub.";

        private const string ReleaseFeaturesText =
            "Transmission and Deluge support\n" +
            "qbPortWeaver can now manage the listening port for Transmission and Deluge in addition to qBittorrent. " +
            "Configure your client under Settings - each client has its own URL, credentials, and restart options.\n\n" +
            "Transmission runs in service mode (Windows service) or process mode (Qt desktop client) and is detected automatically.\n\n" +
            "Deluge connects via its Web UI JSON-RPC API. A configurable flush wait ensures the port change is written to disk before restart.\n\n" +
            "Media Manager - TMDB detail panel\n" +
            "Selecting a result row in Media Manager now shows a detail panel at the bottom of the window with the matched title, " +
            "TMDB ID, confidence indicator, and a poster thumbnail.\n\n" +
            "Note: if you have used Media Manager before, click Clear Cache once to populate poster thumbnails for previously cached titles.";

        public WhatsNewForm()
        {
            InitializeComponent();
            lblTitle.Text      = $"What's New in {AppConstants.AppVersion}";
            lnkCommunity.Text     = CommunityText;
            rtbFeatures.Font      = Font;
            rtbFeatures.ForeColor = ForeColor;
            rtbFeatures.Text      = ReleaseFeaturesText;
            Text               = $"{AppConstants.AppName} | What's New";

            // Set the link region to cover only "star it on GitHub" within the full sentence.
            // Debug.Assert catches a mismatch between linkText and CommunityText at development
            // time; without it, IndexOf returning -1 would silently make the entire label a link.
            const string linkText = "star it on GitHub";
            int linkStart = CommunityText.IndexOf(linkText, StringComparison.Ordinal);
            System.Diagnostics.Debug.Assert(linkStart >= 0, $"WhatsNewForm: link text '{linkText}' not found in CommunityText");
            if (linkStart >= 0)
                lnkCommunity.LinkArea = new(linkStart, linkText.Length);

            if (AppConstants.IsDarkModeEnabled())
            {
                lnkCommunity.LinkColor = Color.CornflowerBlue;
                rtbFeatures.BackColor  = Color.FromArgb(30, 30, 30);
                rtbFeatures.ForeColor  = Color.Gainsboro;
            }
        }

        private void btnClose_Click(object? sender, EventArgs e) => Close();

        private void lnkCommunity_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e) =>
            AppConstants.OpenUrl(AppConstants.GitHubRepoUrl);
    }
}

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
            "VPN Auto-Recovery\r\n" +
            "Automatically recovers when port sync fails for a configurable number " +
            "of consecutive cycles. Restarts the VPN Windows service and client " +
            "(ProtonVPN/PIA), or cycles the network adapter (NAT-PMP). Configure " +
            "the trigger count in Settings > General.\r\n\r\n" +
            "Media Manager\r\n" +
            "Automatically imports and organizes media files into library folders " +
            "on each sync cycle, using TMDB metadata for naming (movies and TV shows).\r\n\r\n" +
            "Log Viewer\r\n" +
            "Color-coded live log with level filters, color theme support, and " +
            "in-text search with match count and navigation.";

        public WhatsNewForm()
        {
            InitializeComponent();
            lblTitle.Text      = $"What's New in {AppConstants.AppVersion}";
            lnkCommunity.Text  = CommunityText;
            lblFeatures.Text   = ReleaseFeaturesText;
            Text               = $"{AppConstants.AppName} | What's New";

            // Set the link region to cover only "star it on GitHub" within the full sentence.
            // Debug.Assert catches a mismatch between linkText and CommunityText at development
            // time; without it, IndexOf returning -1 would silently make the entire label a link.
            const string linkText = "star it on GitHub";
            int linkStart = lnkCommunity.Text.IndexOf(linkText, StringComparison.Ordinal);
            System.Diagnostics.Debug.Assert(linkStart >= 0, $"WhatsNewForm: link text '{linkText}' not found in CommunityText");
            if (linkStart >= 0)
                lnkCommunity.LinkArea = new LinkArea(linkStart, linkText.Length);

            if (AppConstants.IsDarkModeEnabled())
                lnkCommunity.LinkColor = Color.CornflowerBlue;
        }

        private void btnClose_Click(object? sender, EventArgs e) => Close();

        private void lnkCommunity_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e) =>
            AppConstants.OpenUrl(AppConstants.GitHubRepoUrl);
    }
}

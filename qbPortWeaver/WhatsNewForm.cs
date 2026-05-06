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
            "New in 2.5.3\n\n" +
            "Log alert notifications\n" +
            "When a warning or error is written to the log, a tray balloon tip appears once to get your attention. " +
            "Clicking the balloon opens the log viewer at the first warning or error. " +
            "The Show Logs menu item shows a running count (e.g. Show Logs (2 warnings, 1 error)), and " +
            "hovering over the tray icon shows the same count in plain text (e.g. 2 Warnings, 1 Error). " +
            "Alerts clear automatically when you open the log viewer or clear the logs.\n\n" +
            "Previously released\n\n" +
            "New in 2.5.2\n\n" +
            "qBittorrent 5.2.0 compatibility\n" +
            "Authentication now works correctly with qBittorrent 5.2.0, which changed its Web API login response format. " +
            "No configuration changes are required.\n\n" +
            "Port update notification\n" +
            "A tray balloon tip now appears whenever the client's listening port is successfully updated to a new value. " +
            "Enabled by default - toggle it under Settings > General.\n\n" +
            "Media Manager - improved startup reliability\n" +
            "If library folders are temporarily unreachable at startup (for example, a NAS that powers on after the PC), " +
            "the library index is now retried each cycle until the folders become accessible, " +
            "preventing stale results from being carried over.\n\n" +
            "BitTorrent client support\n" +
            "Automatic port sync for qBittorrent (Web API), Transmission (RPC), and Deluge (Web JSON-RPC). " +
            "Transmission auto-detects service mode vs Qt desktop client.\n\n" +
            "VPN provider support\n" +
            "ProtonVPN (log file or NAT-PMP), Private Internet Access (via piactl), and any NAT-PMP capable VPN gateway or router.\n\n" +
            "Auto-Recovery\n" +
            "After a configurable number of failed sync cycles, qbPortWeaver can automatically restart the VPN service and client process " +
            "(ProtonVPN and PIA) or cycle the network adapter (NAT-PMP gateway). " +
            "Privileged operations are handled by a lightweight helper service - no UAC prompt required.\n\n" +
            "Tray status indicator\n" +
            "The tray icon shows a colored dot after each cycle: green (ports aligned), orange (VPN not connected), red (error), or no dot (port sync disabled). " +
            "Hover to see the current port and status.\n\n" +
            "Media Manager\n" +
            "Imports movie and TV episode files into Plex-compatible library folders on each sync cycle using TMDB for title matching. " +
            "Supports hardlink, copy, and move. Preview imports before applying with Scan Now, or apply manually with Import Now.\n\n" +
            "Log Viewer\n" +
            "Color-coded log viewer with real-time follow, level filters, subsystem filter, search with match highlighting, and prev/next issue navigation buttons to step between warnings and errors.";

        private bool _isDarkMode;

        public WhatsNewForm()
        {
            InitializeComponent();
            lblTitle.Text         = $"What's New in {AppConstants.AppVersion}";
            lnkCommunity.Text     = CommunityText;
            rtbFeatures.Text      = ReleaseFeaturesText;
            Text                  = $"{AppConstants.AppName} | What's New";

            // Set the link region to cover only "star it on GitHub" within the full sentence.
            // Debug.Assert catches a mismatch between linkText and CommunityText at development
            // time; without it, IndexOf returning -1 would silently make the entire label a link.
            const string linkText = "star it on GitHub";
            int linkStart = CommunityText.IndexOf(linkText, StringComparison.Ordinal);
            System.Diagnostics.Debug.Assert(linkStart >= 0, $"WhatsNewForm: link text '{linkText}' not found in CommunityText");
            if (linkStart >= 0) // NOSONAR S2589 - defensive runtime fallback; Debug.Assert only fires in dev builds
                lnkCommunity.LinkArea = new(linkStart, linkText.Length);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _isDarkMode           = AppConstants.IsDarkModeEnabled();
            rtbFeatures.Font      = Font;
            rtbFeatures.ForeColor = ForeColor;
            if (_isDarkMode)
            {
                lnkCommunity.LinkColor = AppConstants.DarkModeLinkColor;
                rtbFeatures.ForeColor  = AppConstants.DarkModeText;
            }
        }

        private void btnClose_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static

        private static void lnkCommunity_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e) =>
            AppConstants.OpenUrl(AppConstants.GitHubRepoUrl);
    }
}

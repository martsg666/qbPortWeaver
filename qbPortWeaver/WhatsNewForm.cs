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
            "Clicking the balloon opens the log viewer at the most recent warning or error. " +
            "The Show Logs menu item shows a running count (e.g. Show Logs (2 warnings, 1 error)), and " +
            "hovering over the tray icon shows the same count in plain text (e.g. 2 Warnings, 1 Error). " +
            "Alerts clear automatically when you open the log viewer or clear the logs.\n\n" +
            "Log viewer - issue navigation\n" +
            "Two new buttons in the log viewer let you step directly between warnings and errors " +
            "without affecting your current search. Use Prev Issue / Next Issue to jump through " +
            "problems one at a time.\n\n" +
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
            "New in 2.5.0\n\n" +
            "Transmission and Deluge support\n" +
            "Automatic port sync for Transmission (RPC) and Deluge (Web JSON-RPC). " +
            "Transmission auto-detects service mode vs Qt desktop client. Configure each client via Settings.\n\n" +
            "Media Manager - TMDB detail panel\n" +
            "Selecting a result row now shows the matched title, poster thumbnail, vote count, and overview.\n\n" +
            "New in 2.4.1\n\n" +
            "VPN Auto-Recovery\n" +
            "After a configurable number of failed sync cycles, automatically restarts the VPN service and client " +
            "(ProtonVPN or PIA) or cycles the network adapter (NAT-PMP). Configure the failure threshold in Settings.\n\n" +
            "Media Manager\n" +
            "Automatically organizes media files into library folders on each sync cycle using TMDB for naming. " +
            "Supports movies and TV shows, with hardlink, copy, or move.\n\n" +
            "Log Viewer\n" +
            "Color-coded live log with level and subsystem filters, search with match highlighting, and theme support.\n\n" +
            "New in 2.4.0\n\n" +
            "NAT-PMP support\n" +
            "Port sync with any NAT-PMP capable VPN gateway or router - no ProtonVPN or PIA account required.\n\n" +
            "New in 2.3.0\n\n" +
            "Restart on disconnect\n" +
            "Optionally restarts qBittorrent when its connection status changes to disconnected. Requires the Executable and Process name to be configured in Settings.\n\n" +
            "New in 2.0.0\n\n" +
            "Tray status indicator\n" +
            "The tray icon shows a colored dot after each sync cycle: green (ports aligned), orange (VPN not connected), red (error), or no dot (port sync disabled). Hover to see the current port and status.\n\n" +
            "Settings dialog\n" +
            "All configuration options moved into a dedicated Settings form (tray menu -> Settings), replacing the previous Notepad shortcut. Settings are stored in the Windows Registry with passwords encrypted via Windows DPAPI.\n\n" +
            "VPN interface mismatch warning\n" +
            "Shows a tray warning if qBittorrent's network interface does not match the configured VPN, or if bound to all interfaces (potential traffic leak).\n\n" +
            "New in 1.7.0\n\n" +
            "Last-run status file\n" +
            "Writes a JSON status file after each sync cycle, exposing VPN port, client port, timestamps, and status for external scripts or monitoring.\n\n" +
            "Clear Logs\n" +
            "New tray menu option to delete all log files and start fresh.\n\n" +
            "New in 1.6.0\n\n" +
            "Private Internet Access (PIA) support\n" +
            "Added PIA as a supported VPN provider alongside ProtonVPN, via the piactl CLI.\n\n" +
            "Default port fallback\n" +
            "Optionally sets the client's listening port to a configured default when the VPN is not connected.\n\n" +
            "New in 1.5.0\n\n" +
            "Automatic update checker\n" +
            "Checks GitHub for new releases on startup and every 12 hours, and offers to open the download page.\n\n" +
            "New in 1.4.0\n\n" +
            "Force start\n" +
            "Optionally launches the BitTorrent client automatically if it is not running during a sync cycle.\n\n" +
            "New in 1.3.0\n\n" +
            "Post-update command\n" +
            "Optionally runs a custom script or command after a successful port update.\n\n" +
            "v1.0.0 - Initial release\n" +
            "Automatic ProtonVPN port sync for qBittorrent.";

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

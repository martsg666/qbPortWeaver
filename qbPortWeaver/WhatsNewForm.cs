namespace qbPortWeaver;

/// <summary>Displays a summary of what changed in the current version. Shown automatically on first run after an upgrade.</summary>
public partial class WhatsNewForm : Form
{
    // Update these constants each release. They live here (not in Designer.cs) so the designer
    // cannot overwrite them, and content changes never touch layout code.
    private const string CommunityText =
        "If you find qbPortWeaver useful, please star it on GitHub.";

    private const string ReleaseFeaturesText =
        "New in 2.5.8\n\n" +
        "Test your port on demand\n" +
        "The Status panel now has a Test Port button that checks whether your listening port is reachable " +
        "from the Internet right then, without waiting for the next scheduled check. The result - open, " +
        "closed, or could not determine - appears next to Reachable.\n\n" +
        "Detect your client\n" +
        "The Settings window can now find your client for you. Click Detect on the General tab and " +
        "qbPortWeaver selects whichever supported client (qBittorrent, Transmission, or Deluge) is running " +
        "or installed and fills in its process and executable details. If more than one is found, it asks " +
        "you to choose. Review the connection settings, use Test, then Save.\n\n" +
        "Support for Proton's new protocols\n" +
        "Proton VPN 5.x.y adds in-house protocols (Proton WireGuard and Proton Stealth) that " +
        "name their tunnel adapter differently than the earlier protocols. qbPortWeaver now detects it " +
        "automatically, in both log-file and NAT-PMP modes. After switching to one of these protocols, " +
        "reselect the active adapter wherever you have pinned it - the NAT-PMP Adapter setting and your " +
        "client's network interface binding.\n\n" +
        "Previously released\n\n" +
        "New in 2.5.7\n\n" +
        "Status panel\n" +
        "A new Status window shows the live state of your port sync at a glance: your VPN provider and " +
        "whether it is connected, the forwarded port, your client and its listening port (with an in-sync " +
        "indicator), whether the port is reachable, and the time and result of the last sync. Colors flag " +
        "anything out of sync, closed, or in error, and a Sync Now button runs an immediate cycle. Open it " +
        "from the tray menu (Show Status), or by double-clicking the tray icon. Double-clicking now opens " +
        "the Status panel instead of the log viewer; the log viewer is still available from Show Logs.\n\n" +
        "Sync on network change\n" +
        "qbPortWeaver can now run a sync the moment a network or VPN connection change is detected, " +
        "so your client follows a VPN reconnect within seconds instead of waiting for the next interval. " +
        "Rapid changes are grouped into a single sync, and pausing still suppresses it. " +
        "Enabled by default - turn it off under Settings > General.\n\n" +
        "Smarter auto-recovery\n" +
        "Auto-recovery now waits until sync failures have persisted, not just counted up, before " +
        "restarting your VPN. A brief network blip - like a router reboot that triggers several quick " +
        "re-syncs - no longer force-restarts the VPN service when it would have reconnected on its own.\n\n" +
        "New in 2.5.6\n\n" +
        "Port verification\n" +
        "After each sync, qbPortWeaver can now check that your port is actually reachable from the Internet, " +
        "not just configured. If the port stays closed, a warning appears in the log and as a tray notification. " +
        "Optionally, auto-recovery can run automatically after a configurable number of closed checks " +
        "(a VPN service restart, or adapter cycle for NAT-PMP gateways) - see Settings > General. " +
        "Verification is enabled by default: Transmission and Deluge use their " +
        "built-in online port checkers, while qBittorrent infers reachability from incoming connections " +
        "(an idle client may report closed).\n\n" +
        "Pause and resume syncing\n" +
        "A new Pause Syncing item in the tray menu temporarily stops sync cycles, including media imports, " +
        "without changing any settings. While paused, the tray icon shows a gray dot and Sync Port Now still runs a single cycle on demand. " +
        "Syncing always resumes when the application restarts.\n\n" +
        "Tabbed Settings\n" +
        "The Settings window is now organised into General, Client and Extra tabs, " +
        "so it fits comfortably on smaller screens.\n\n" +
        "Accurate update check results\n" +
        "If the update check cannot reach GitHub (for example when you are offline), it now tells you " +
        "it could not check instead of reporting that you are up to date.\n\n" +
        "Better guidance when a client does not start\n" +
        "If the client launches but runs under a different process name than the one configured, " +
        "the log now points you directly at the Process name field in Settings instead of showing " +
        "a generic start failure.\n\n" +
        "Consistent Transmission credential errors\n" +
        "A wrong Transmission username or password is now reported with the same clear message " +
        "on every operation.\n\n" +
        "More precise log filtering\n" +
        "Filtering the log viewer by subsystem now matches only the subsystem column, so unrelated " +
        "lines that merely mention a subsystem name in their text no longer appear.\n\n" +
        "New in 2.5.5\n\n" +
        "Test connection from Settings\n" +
        "Each client section in Settings now has a Test button next to the URL. " +
        "Click it to check the connection to qBittorrent, Transmission, or Deluge using the values you have entered, " +
        "without saving first. A message confirms success and shows the current listening port, " +
        "or points you to the log if it does not connect.\n\n" +
        "Check for updates on demand\n" +
        "A new Check for Updates item in the tray menu lets you check for a newer version right away " +
        "instead of waiting for the periodic check. You always get a result, even when you are already up to date.\n\n" +
        "Layered TV libraries\n" +
        "The Media Manager now recognises TV episodes organised in season subfolders with numbered " +
        "filenames (Show Name/Season N/01-Title.mp4) in addition to the existing SxxExx naming pattern. " +
        "The show name comes from the parent folder, the season from the season subfolder, and the episode " +
        "number from the filename prefix. English, French, Spanish and Italian season indicators are recognised.\n\n" +
        "More resilient media import\n" +
        "When a source or library folder is on a network share that is temporarily offline (for example a NAS " +
        "that is rebooting), the Media Manager now detects it quickly and skips that import cycle instead of " +
        "stalling, then resumes automatically once the share is reachable again.\n\n" +
        "Stable log viewer memory\n" +
        "Keeping the log viewer open during a long debugging session used to cause memory usage to grow over time. " +
        "The viewer now keeps a steady footprint regardless of how long it stays open.\n\n" +
        "New in 2.5.4\n\n" +
        "Less intrusive update notifications\n" +
        "The 12-hour background update check no longer interrupts you with a form. " +
        "When a new version is found, an Update available item appears at the top of the tray menu " +
        "and a one-shot tray notification is shown. Click the menu item to open the update form. " +
        "A new setting under Settings > General lets you turn off the update form at startup; " +
        "the tray indicators still appear when an update is available.\n\n" +
        "Lower log viewer memory usage\n" +
        "Opening the log viewer used to leave a noticeable memory footprint after closing it. " +
        "The viewer now releases its content promptly on close so the application returns to its baseline memory usage.\n\n" +
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
        "Log viewer - previous log files\n" +
        "A new dropdown in the log viewer lets you browse rotated backup files without leaving the viewer. " +
        "Select Current for the live log or Backup 1-4 for older rotated files.\n\n" +
        "More log history\n" +
        "The number of retained log files has been increased from 3 to 5, and the per-file size limit raised from 5 MB to 20 MB (100 MB total). " +
        "This keeps significantly more history available in the log viewer, especially useful when reviewing warnings and errors over longer periods.\n\n" +
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
        // Catches a forgotten release-notes update at development time: the title below is
        // derived from the assembly version, but ReleaseFeaturesText is a hardcoded constant,
        // so nothing else forces the two to agree. Debug builds only - no release impact.
        System.Diagnostics.Debug.Assert(
            ReleaseFeaturesText.Contains($"New in {AppConstants.AppVersion}", StringComparison.Ordinal),
            $"WhatsNewForm: ReleaseFeaturesText has no 'New in {AppConstants.AppVersion}' section - update it for this release");
        lblTitle.Text = $"What's New in {AppConstants.AppVersion}";
        lnkCommunity.Text = CommunityText;
        // rtbFeatures content is rendered with a visual hierarchy in OnLoad (RenderFeatures),
        // once the theme-aware font and colour are known.
        Text = $"{AppIdentity.AppName} | What's New";

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
        _isDarkMode = AppConstants.IsDarkModeEnabled();
        rtbFeatures.Font = Font;
        rtbFeatures.ForeColor = ForeColor;
        if (_isDarkMode)
        {
            lnkCommunity.LinkColor = AppConstants.DarkModeLinkColor;
            rtbFeatures.ForeColor = AppConstants.DarkModeText;
        }
        RenderFeatures();
    }

    // Renders ReleaseFeaturesText into the RichTextBox with a visual hierarchy instead of flat text:
    // version dividers ("New in X.Y.Z", "Previously released") in bold and slightly larger, each
    // feature's title line in bold, and body paragraphs in the normal font. ReleaseFeaturesText stays
    // the single editable content source - this method only applies presentation, so the content is
    // reproduced verbatim (blocks split on the blank-line separator, then re-joined with one).
    private void RenderFeatures()
    {
        Color textColor = rtbFeatures.ForeColor;
        Font baseFont = rtbFeatures.Font;
        using var versionFont = new Font(baseFont.FontFamily, baseFont.Size + 1.5f, FontStyle.Bold);
        using var titleFont = new Font(baseFont, FontStyle.Bold);

        rtbFeatures.Clear();
        bool first = true;
        foreach (var block in ReleaseFeaturesText.Split("\n\n", StringSplitOptions.None))
        {
            if (block.Length == 0) continue;
            if (!first) rtbFeatures.AppendText("\n\n");
            first = false;

            int firstBreak = block.IndexOf('\n');
            string headerLine = firstBreak < 0 ? block : block[..firstBreak];
            string body = firstBreak < 0 ? string.Empty : block[(firstBreak + 1)..];

            rtbFeatures.SelectionColor = textColor;
            rtbFeatures.SelectionFont = IsVersionDivider(headerLine) ? versionFont : titleFont;
            rtbFeatures.AppendText(headerLine);

            if (body.Length > 0)
            {
                rtbFeatures.SelectionFont = baseFont;
                rtbFeatures.AppendText("\n" + body);
            }
        }

        // Reset to the top so the dialog opens showing the newest release, not scrolled to the end.
        rtbFeatures.Select(0, 0);
        rtbFeatures.ScrollToCaret();
    }

    // True for the section dividers that should stand out the most: the per-version headers
    // ("New in X.Y.Z") and the "Previously released" break. Everything else (feature titles)
    // gets the lighter bold style.
    private static bool IsVersionDivider(string headerLine) =>
        headerLine.StartsWith("New in ", StringComparison.Ordinal) ||
        headerLine.StartsWith("v1.0.0", StringComparison.Ordinal) || // the lone initial-release header uses the vX.Y.Z form
        headerLine == "Previously released";

    private void btnClose_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static

    private static void lnkCommunity_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e) =>
        AppConstants.OpenUrl(AppConstants.GitHubRepoUrl);
}

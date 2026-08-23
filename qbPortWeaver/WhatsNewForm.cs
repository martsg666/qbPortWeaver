namespace qbPortWeaver;

/// <summary>Displays a summary of what changed in the current version. Shown automatically on first run after an upgrade.</summary>
public partial class WhatsNewForm : Form
{
    // Update these constants each release. They live here (not in Designer.cs) so the designer
    // cannot overwrite them, and content changes never touch layout code.
    private const string CommunityText =
        "If you find qbPortWeaver useful, please star it on GitHub.";

    private const string ReleaseFeaturesText =
        "New in 2.6.6\n\n" +
        "See what auto-recovery is doing\n" +
        "The Status window has a new Auto-recovery line. It counts the failed cycles building up " +
        "toward a recovery attempt, and when something is holding that attempt back it says what " +
        "and for how long - whether it is waiting for the failures to persist, so a brief blip does " +
        "not force a restart, or waiting because your connection itself is down. A pause now reads " +
        "as deliberate rather than the app having given up.\n\n" +
        "Narrow the log to a time range\n" +
        "The Log Viewer has a new time filter beside the subsystem and file pickers: show everything, " +
        "or just the last 15 minutes, hour or 24 hours, or a start and end time you pick " +
        "yourself. Useful when you know roughly " +
        "when something went wrong and do not want to scroll through everything around it.\n\n" +
        "An icon on every window\n" +
        "Settings, About, Status and the other dialogs now carry the qbPortWeaver icon, so they are " +
        "easy to pick out when you switch windows with Alt+Tab. Until now they appeared there with " +
        "no icon at all.\n\n" +
        "Stops repeating the same warning\n" +
        "A problem that stays until you fix it - a setting left blank, a library folder that is " +
        "offline - used to be logged again on every sync cycle, pushing the tray warning count up " +
        "and burying everything else. It is now reported once when it appears, again only if it " +
        "changes, and once more if it comes back after clearing.\n\n" +
        "Previously released\n\n" +
        "New in 2.6.5\n\n" +
        "Spots client settings that undo your forwarded port\n" +
        "Some options in your client work against the port you are forwarding: a randomised listening " +
        "port, or the client doing its own router forwarding. qbPortWeaver already turns these off " +
        "whenever it sets the port, and now tells you if one has been switched back on, naming it as " +
        "your client labels it. Diagnostics reports it too. This matters most for Transmission and " +
        "Nicotine+, where such a setting shows no symptom at all until the client is next restarted - " +
        "so you are told about it rather than left to find it.\n\n" +
        "Tells you when your VPN does not come back\n" +
        "When qbPortWeaver restarts your VPN service to recover the connection, it now checks that the " +
        "service really did start again, instead of assuming it worked. If it did not, you get a clear " +
        "message rather than a quiet note saying it was restarted. Worth having if you use a killswitch: " +
        "a VPN service that fails to come back can leave you with no connection at all.\n\n" +
        "Calmer behaviour when your internet goes down\n" +
        "If the connection itself drops, restarting your VPN cannot bring it back, so qbPortWeaver no " +
        "longer keeps trying at full speed. It checks whether anything is reachable, then spaces its " +
        "attempts out to 5, 10 and then 15 minutes for as long as the outage lasts, and returns to " +
        "normal on its own once the connection is back. It never stops retrying entirely, so a VPN " +
        "that is genuinely stuck still gets restarted.\n\n" +
        "Finds your VPN provider for you\n" +
        "Settings can now detect which VPN provider is installed on your machine and select it, the " +
        "same way the Client row already fills itself in. Gateways that use NAT-PMP have nothing to " +
        "detect, so pick NAT-PMP yourself if that is what you use.\n\n" +
        "New in 2.6.4\n\n" +
        "Nicotine+ support\n" +
        "qbPortWeaver can now keep Nicotine+ (Soulseek) on your VPN's forwarded port, alongside " +
        "qBittorrent, Transmission and Deluge. Nicotine+ has no remote control of its own, so " +
        "Settings has an Install Plugin button that sets up a small bridge plugin for you - and " +
        "enables it too, if Nicotine+ is closed at the time. The port then changes while " +
        "Nicotine+ keeps running: no restart, no interrupted transfers.\n\n" +
        "Auto-Recovery settings on their own tab\n" +
        "The port-verification and auto-recovery options have moved to a dedicated Auto-Recovery tab " +
        "in Settings, so the General tab is less crowded and the recovery options are easier to find.\n\n" +
        "qBittorrent keeps listening after a VPN reconnect\n" +
        "qBittorrent remembers its network interface as an internal identifier, and when a VPN " +
        "recreates its adapter that identifier stops working while the adapter name still looks " +
        "right. qBittorrent then listens on nothing, and restarting it does not help. qbPortWeaver " +
        "now spots this and puts it right for you, restoring the adapter you selected. Diagnostics " +
        "reports it too, instead of passing every check while nothing is listening. You can turn the " +
        "fix off in Settings if you would rather just be warned about it.\n\n" +
        "Fewer pointless restarts\n" +
        "When restarting a disconnected client does not help, qbPortWeaver now stops after three " +
        "tries and explains why, rather than restarting it every cycle and interrupting your " +
        "transfers each time. It resumes on its own once the client reconnects.\n\n" +
        "Steadier port forwarding\n" +
        "Port mappings on NAT-PMP gateways now cover both TCP and UDP, so gateways that treat the two " +
        "separately forward everything your client needs. A port your VPN reports that cannot be used " +
        "is now ignored instead of being passed to your client, which previously could leave it on a " +
        "random port while everything still looked healthy. Auto-recovery also always restores your " +
        "network adapter now, even if it is interrupted part-way through cycling it.\n\n" +
        "Clearing statistics asks first\n" +
        "Clear Statistics on the Status window now confirms before it resets, the way Clear History " +
        "and Clear Logs already did. The counters run from when the app started, so on a machine " +
        "left running they can cover weeks, and there is no way to get them back.\n\n" +
        "Tidier settings storage\n" +
        "Every client's settings are now stored under the same names, so they are easier to compare " +
        "and any client added later stays consistent. Your existing settings move across on their " +
        "own the first time you run this version. Only worth knowing if you read qbPortWeaver's " +
        "settings from your own scripts.\n\n" +
        "New in 2.6.3\n\n" +
        "Pop-up messages match your theme\n" +
        "Confirmation and alert pop-ups now follow your chosen theme, so in dark mode they no " +
        "longer appear in bright white.\n\n" +
        "Waits for your VPN at startup\n" +
        "If the app starts before your VPN has finished connecting, it now waits quietly for a " +
        "short time instead of reporting the VPN as disconnected, and syncs your port as soon as " +
        "the connection is up. You can turn this off in Settings.\n\n" +
        "New in 2.6.2\n\n" +
        "Statistics on the Status panel\n" +
        "A new Statistics section on the Status window shows your current port, how many times " +
        "it changed today, and this session's sync and recovery counts - a quick read on whether " +
        "your setup is stable or churning. The Status window now also estimates when the next " +
        "sync is due, how long ago the last one ran, and when the port was last checked, updating " +
        "live, and adds a Pause/Resume button so you can hold and restart syncing without leaving " +
        "the window.\n\n" +
        "Test your auto-recovery\n" +
        "A new Test button next to the Auto-recovery settings runs the recovery action on " +
        "demand, so you can confirm the whole chain works - helper service, VPN restart, " +
        "client relaunch - before a real failure needs it.\n\n" +
        "Port history shows the cause\n" +
        "Port changes in the Status window's history now note what prompted them - a network " +
        "change or a recovery - so you can see at a glance why your port moved.\n\n" +
        "New in 2.6.1\n\n" +
        "Port history on the Status panel\n" +
        "The Status window now lists your recent port changes and recovery events with " +
        "timestamps, kept across restarts - see at a glance when your port last changed and why.\n\n" +
        "Built-in user guide\n" +
        "A new Help item in the tray menu opens the full user guide in its own window - browse " +
        "it by section from a contents list or search it with Ctrl+F. Setup instructions, feature " +
        "descriptions, and recommended configuration are available right in the app, no browser " +
        "or internet connection needed.\n\n" +
        "Media Manager layout\n" +
        "The Media Manager window is now organised into General and Folders tabs, so it fits " +
        "comfortably on smaller screens and higher display scaling.\n\n" +
        "Log viewer fixes\n" +
        "Filtering the log while the window is maximized no longer leaves entries misplaced or " +
        "hidden until you scroll. Switching between log files while a large one is still loading " +
        "no longer mixes or duplicates entries, and a rare crash while drag-selecting was fixed. " +
        "The log file list now refreshes each time you open it, so new backups appear without " +
        "reopening the viewer.\n\n" +
        "Confirmation before clearing\n" +
        "Clearing your logs or your port history now asks you to confirm first, so a stray click " +
        "cannot wipe them.\n\n" +
        "New in 2.6.0\n\n" +
        "One-click diagnostics\n" +
        "A new Run Diagnostics action - on the Status panel and the tray menu - checks your entire " +
        "port-sync setup in one pass and shows a pass, warning, or fail result for each step: VPN " +
        "connected and forwarding a port, client running and reachable, ports in sync, client bound to " +
        "the VPN adapter, and the port reachable from the Internet. Each result includes a short hint on " +
        "how to fix it, a Re-run button refreshes the report, and Copy Report puts it on your clipboard " +
        "for a support request. It is read-only - it never changes your port or restarts anything.\n\n" +
        "In-app update\n" +
        "When a new version is available, the update window can now download and install it for you. " +
        "Click Download & Install and qbPortWeaver fetches the installer and runs it; the update " +
        "relaunches the app when it finishes. The release notes stay one click away, and it falls back " +
        "to the download page if anything goes wrong.\n\n" +
        "Better Transmission support\n" +
        "The port reachability check - port verification, the Status panel's Test Port, and diagnostics - " +
        "now works correctly with recent Transmission versions, which had changed how their built-in port " +
        "test works.\n\n" +
        "Consistent theming across every window\n" +
        "Every window - Settings, Status, Diagnostics, the log viewer, Media Manager, and the tray menu - now " +
        "follows your color theme (System, Dark, or Light) uniformly, using the native Windows colors so text " +
        "and surfaces stay legible in both light and dark mode.\n\n" +
        "A faster log viewer\n" +
        "The log viewer now handles very large logs effortlessly: switching level and subsystem filters " +
        "applies instantly, scrolling and resizing stay smooth, and memory usage stays low even when the " +
        "viewer is left open for days. Selection now works per line - click or drag to select entries and " +
        "copy them.\n\n" +
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
        "ProtonVPN 5.1.5 adds the Proton Protocols (Proton WireGuard and Proton Stealth) that " +
        "name their tunnel adapter differently than the earlier protocols. qbPortWeaver now detects them " +
        "automatically, in both log-file and NAT-PMP modes. After switching to one of these protocols, " +
        "reselect the active adapter wherever you have pinned it - the NAT-PMP Adapter setting and your " +
        "client's network interface binding.\n\n" +
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
        "Enabled by default - turn it off under Settings → General.\n\n" +
        "Smarter auto-recovery\n" +
        "Auto-recovery now waits until sync failures have persisted, not just counted up, before " +
        "restarting your VPN. A brief network blip - like a router reboot that triggers several quick " +
        "re-syncs - no longer force-restarts the VPN service when it would have reconnected on its own.\n\n" +
        "New in 2.5.6\n\n" +
        "Port verification\n" +
        "After each sync, qbPortWeaver can now check that your port is actually reachable from the Internet, " +
        "not just configured. If the port stays closed, a warning appears in the log and as a tray notification. " +
        "Optionally, auto-recovery can run automatically after a configurable number of closed checks " +
        "(a VPN service restart, or adapter cycle for NAT-PMP gateways) - see Settings → General. " +
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
        "A new setting under Settings → General lets you turn off the update form at startup; " +
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
        "Enabled by default - toggle it under Settings → General.\n\n" +
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
        "All configuration options moved into a dedicated Settings form (tray menu → Settings), replacing the previous Notepad shortcut. Settings are stored in the Windows Registry with passwords encrypted via Windows DPAPI.\n\n" +
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
        "Force-start\n" +
        "Optionally launches qBittorrent automatically if it is not running during a sync cycle.\n\n" +
        "New in 1.3.0\n\n" +
        "Post-update command\n" +
        "Optionally runs a custom script or command after a successful port update.\n\n" +
        "v1.0.0 - Initial release\n" +
        "Automatic ProtonVPN port sync for qBittorrent.";

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
        rtbFeatures.Font = Font;
        rtbFeatures.ForeColor = SystemColors.ControlText; // match the group box (mode-aware, blends in)
        if (AppConstants.IsDarkModeEnabled())
            lnkCommunity.LinkColor = AppConstants.LinkDark;
        RenderFeatures();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Shown non-modally on first run by the tray-only app (no foreground window), so it can open
        // behind whatever launched us (e.g. the installer). If the user never sees it they never
        // dismiss it, and the "last seen version" is never recorded - so it keeps reappearing.
        AppConstants.BringFormToFront(this);
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

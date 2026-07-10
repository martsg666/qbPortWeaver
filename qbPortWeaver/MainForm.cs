using System.ComponentModel;
using System.Runtime.InteropServices;

namespace qbPortWeaver;

/// <summary>Main application form - hosts the system tray icon, context menu, and owns the background port-sync service.</summary>
public partial class MainForm : Form
{
    // Tray icon, menu and auto-start menu item
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    // Menu-item fields are declared in the order they appear in the tray menu (top to bottom).
    private ToolStripMenuItem _updateAvailableMenuItem = null!;
    private ToolStripSeparator _updateSeparator = null!;
    private ToolStripMenuItem _pauseSyncMenuItem = null!;
    private ToolStripMenuItem _showLogsMenuItem = null!;
    private ToolStripMenuItem _checkUpdatesMenuItem = null!;
    private ToolStripMenuItem _autoStartMenuItem = null!;

    private const string ShowLogsMenuText = "Show Logs";
    private const string PauseSyncingMenuText = "Pause Syncing";
    private const string ResumeSyncingMenuText = "Resume Syncing";
    private const string LogAlertBalloonMessage = "Check the log viewer for warnings or errors.";
    private const int WsExToolWindow = 0x80; // hides the form from Alt+Tab

    // Unviewed warn/error counts for log alert badge (UI thread only)
    private int _unviewedWarnCount;
    private int _unviewedErrorCount;
    private bool _logAlertBalloonShown;
    private bool _logAlertBalloonPending;

    // Status tray icons (generated at startup; disposed in MainForm.Designer.cs)
    private Icon? _iconBase;
    private Icon? _iconOk;
    private Icon? _iconWarning;
    private Icon? _iconError;
    private Icon? _iconPaused;

    // Bold font for the update-available menu item, owned by MainForm because WinForms
    // does not dispose Fonts assigned to ToolStripMenuItem. Disposed in MainForm.Designer.cs.
    private Font? _updateMenuItemFont;

    // Services (assigned in the ctor after the designer guard; null! initializer keeps the field
    // non-nullable for runtime callers while satisfying flow analysis on the design-time early-return path)
    private readonly PortSyncService _portSyncService = null!;

    // Child forms (null when closed)
    private LogViewerForm? _logViewerForm;
    private SettingsForm? _settingsForm;
    private MediaManagerForm? _mediaManagerForm;
    private AboutForm? _aboutForm;
    private HelpForm? _helpForm;
    private StatusForm? _statusForm;
    private UpdateAvailableForm? _updateAvailableForm;
    private DiagnosticsForm? _diagnosticsForm;
    // Guards against concurrent diagnostics runs from the Status button and the tray menu. UI thread only.
    private bool _diagnosticsRunning;

    // Last sync status (written from background thread, read on UI thread)
    private volatile TrayStatus? _lastSyncStatus;

    // Cancellation token for the inter-cycle delay - cancelled by manual sync requests to skip the wait.
    // Swapped atomically via Interlocked.Exchange; InterruptDelay catches ObjectDisposedException
    // for the residual window where the UI thread read the old reference before the swap completed.
    private CancellationTokenSource _delayCts = new();

    // Semaphore to prevent concurrent port sync cycles. Also serialises access to
    // PortSyncService instance state (e.g. _lastKnownNatPmpManager) - see PortSyncService.cs.
    // Does NOT cover MediaManagerService: media import runs on a separate fire-and-forget
    // task so a long library scan cannot delay the next port sync cycle.
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);

    // Running flag for the fire-and-forget media import task. 0 = idle, 1 = running.
    // Subsequent sync cycles skip the import if the previous one is still in flight rather
    // than queueing - prevents pile-up when a media import takes longer than the sync interval.
    private int _mediaImportRunning;

    // Manual sync triggered flag (thread-safe with volatile)
    private volatile bool _manualSyncTriggered;

    // Set when a network-change re-sync is triggered. Gives that cycle a single short follow-up
    // wait (like manual sync) so a cycle that ran before the VPN finished settling retries
    // promptly instead of resetting the clock to a full interval. Unlike _manualSyncTriggered it
    // does NOT override pause or log as a manual sync.
    private volatile bool _quickRecheckPending;

    // Pause flag for the sync loop (thread-safe with volatile). Deliberately in-memory only:
    // a restart always resumes syncing, so the app can never silently sit in a paused state
    // the user forgot about. While paused the whole cycle body is skipped - port sync AND the
    // media import kick-off - because the cycle is the unit the loop schedules.
    private volatile bool _syncPaused;

    // Shutdown cancellation token to signal graceful exit
    private readonly CancellationTokenSource _shutdownCts = new();

    // Guard so SetVisibleCore fires OnLoad exactly once
    private bool _formLoaded;

    // Periodic update check timer (fires every 12 hours)
    private System.Windows.Forms.Timer _updateCheckTimer = null!;

    // One-shot debounce timer for network-change triggered re-syncs. Re-armed on each
    // NetworkAddressChanged event; fires once the burst settles (see OnNetworkAddressChanged).
    private System.Threading.Timer? _resyncDebounceTimer;

    // Last version for which the user was already shown an update prompt
    private string? _lastNotifiedVersion;

    // Update detected by the latest check, surfaced via the tray menu item and tooltip
    // until the user updates (next process launch resets _pendingUpdate). Null when no
    // update is pending. The update balloon is informational only - Windows 11 routes
    // ToolTipIcon.Info balloons through Action Center and does not reliably fire
    // BalloonTipClicked, so the tray menu item is the only clickable entry point.
    private (string Version, string Url, string? MsiUrl)? _pendingUpdate;

    public MainForm()
    {
        InitializeComponent();

        // Guard against the Visual Studio designer instantiating MainForm: runtime-only side effects
        // (log file creation, registry writes, live tray icon) must not fire at design time.
        if (LicenseManager.UsageMode == LicenseUsageMode.Designtime) return;

        LogManager.Initialize(AppConstants.GetLogFilePath());

        // Ensure all registry keys exist, writing defaults for any missing ones
        RegistrySettingsManager.EnsureDefaults();

        // Refresh the Windows startup Run-key entry if the install was moved or upgraded
        // (covers Chocolatey upgrades and manual moves that would otherwise silently break autostart)
        StartupManager.RefreshStartupPathIfMoved();

        _portSyncService = new PortSyncService();
        _portSyncService.SyncCompleted += OnSyncCompleted;
        _portSyncService.InterfaceMismatchDetected += ShowWarningBalloon;
        _portSyncService.PortVerificationFailed += ShowWarningBalloon;
        _portSyncService.PortUpdated += OnPortUpdated;

        InitializeStatusIcons();
        InitializeTrayIcon();
        UpdateTrayTooltip();

        LogManager.Instance.WarnOrErrorLogged += OnWarnOrErrorLogged;
    }

    private void MainForm_Load(object? sender, EventArgs e) => InitializeAfterLoad();

    // Not async: everything here is either synchronous or explicitly fire-and-forget
    // (Task.Run/PerformUpdateCheckAsync are intentionally not awaited).
    private void InitializeAfterLoad()
    {
        try
        {
            // Log once at startup so the version is visible in the log file for diagnostics
            LogManager.Instance.LogMessage($"{AppIdentity.AppName} {AppConstants.AppVersion} starting", LogLevel.Info);

            // Perform initial log rotation check
            LogManager.Instance.CheckAndRotateLogFile();

            // Start main loop immediately so port syncing is not blocked by dialogs
            // Fire-and-forget: exceptions inside the while loop are caught per-cycle.
            // A synchronous throw before the loop body (e.g. during Task.Run startup) would be
            // silently lost - acceptable since RunMainLoopAsync has no synchronous preamble.
            _ = Task.Run(RunMainLoopAsync);

            // Wake the loop promptly on a network change (e.g. VPN reconnect) instead of waiting
            // out the full interval. The timer starts disarmed; OnNetworkAddressChanged arms it.
            _resyncDebounceTimer = new System.Threading.Timer(OnResyncDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

            // Show What's New on first run after an upgrade (non-modal - does not block port sync)
            if (RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyLastSeenVersion) != AppConstants.AppVersion)
            {
                var whatsNew = new WhatsNewForm();
                whatsNew.FormClosed += (_, _) =>
                    RegistrySettingsManager.SetAppValue(RegistrySettingsManager.KeyLastSeenVersion, AppConstants.AppVersion);
                whatsNew.Show(); // NOSONAR S6966 - non-modal is intentional; ShowAsync would block until closed
            }

            // Check for updates on GitHub (non-modal - does not block port sync).
            // The check itself always runs; the user setting only controls whether the
            // UpdateAvailableForm opens at startup. When disabled, the user still gets the
            // tray balloon + persistent menu item so the prompt is not silent.
            bool intrusiveStartupCheck = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyShowUpdateFormOnStartup);
            _ = PerformUpdateCheckAsync(intrusive: intrusiveStartupCheck);

            // Schedule periodic update checks every 12 hours
            _updateCheckTimer = new System.Windows.Forms.Timer { Interval = AppConstants.AutoUpdateCheckIntervalMs };
            _updateCheckTimer.Tick += OnUpdateCheckTimerTick;
            _updateCheckTimer.Start();
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Fatal startup error: {ex}", LogLevel.Error);
            try
            {
                InvokeOnUiThread(() =>
                {
                    MessageBox.Show($"Fatal startup error: {ex.Message}\n\nThe application will now exit.",
                        AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                });
            }
            catch (Exception)
            {
                Application.Exit();
            }
        }
    }

    // Prevents the form from ever becoming visible - this is a tray-only app with no visible window.
    // Application.Run() calls Show() internally; overriding SetVisibleCore blocks it permanently.
    // CreateHandle() ensures the window handle exists for the message pump, and OnLoad() fires
    // the Load event exactly once so InitializeAfterLoad (sync loop, timers, etc.) runs normally.
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated)
            CreateHandle();
        if (!_formLoaded)
        {
            _formLoaded = true;
            OnLoad(EventArgs.Empty);
        }
        base.SetVisibleCore(false);
    }

    // Marks the window as a tool window so it is excluded from the Alt+Tab switcher.
    // ShowInTaskbar = false hides it from the taskbar but not from Alt+Tab - WS_EX_TOOLWINDOW handles both.
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;
            return cp;
        }
    }

    // Handle form closing (user exit, Windows shutdown/restart/logoff)
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Signal the main loop to stop
        _shutdownCts.Cancel();

        // Unsubscribe before teardown so background threads cannot marshal onto a disposed form
        LogManager.Instance.WarnOrErrorLogged -= OnWarnOrErrorLogged;

        // Stop the update check timer before closing child forms to prevent it firing during teardown
        _updateCheckTimer?.Stop();
        _updateCheckTimer?.Dispose();

        // Stop reacting to network changes and dispose the debounce timer
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _resyncDebounceTimer?.Dispose();

        // Hide tray icon immediately to avoid ghost icon
        _trayIcon.Visible = false;

        // Close all child forms
        _logViewerForm?.Close();
        _settingsForm?.Close();
        _mediaManagerForm?.Close();
        _aboutForm?.Close();
        _statusForm?.Close();
        _updateAvailableForm?.Close();
        _diagnosticsForm?.Close();

        // Resources are disposed in Dispose(bool) via MainForm.Designer.cs
        base.OnFormClosing(e);
    }

    // Pre-generates the four status icon variants (colored dot in the bottom-right corner)
    private void InitializeStatusIcons()
    {
        _iconBase = Properties.Resources.qbPortWeaver;
        _iconOk = CreateStatusIcon(_iconBase, AppConstants.TrayDotOk);
        _iconWarning = CreateStatusIcon(_iconBase, AppConstants.TrayDotWarning);
        _iconError = CreateStatusIcon(_iconBase, AppConstants.TrayDotError);
        _iconPaused = CreateStatusIcon(_iconBase, AppConstants.TrayDotPaused);
    }

    // Draws a small filled circle onto a 16x16 copy of the base icon and returns it as an Icon
    private static Icon CreateStatusIcon(Icon baseIcon, Color dotColor)
    {
        using var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        using var borderBrush = new SolidBrush(AppConstants.TrayIconDotBorder);
        using var dotBrush = new SolidBrush(dotColor);

        g.Clear(Color.Transparent);
        using var icon16 = new Icon(baseIcon, 16, 16);
        g.DrawIcon(icon16, new Rectangle(0, 0, 16, 16));

        // Status dot in the bottom-right quadrant of the 16×16 icon:
        // 7×7 dark border circle, then 5×5 colored fill - visible on both light and dark taskbars
        const int DotBorderOrigin = 9;  // 16 - 7 = 9 px offset places border flush with icon edge
        const int DotBorderSize = 7;
        const int DotFillOrigin = 10; // 1 px inset from border on each side
        const int DotFillSize = 5;
        g.FillEllipse(borderBrush, DotBorderOrigin, DotBorderOrigin, DotBorderSize, DotBorderSize);
        g.FillEllipse(dotBrush, DotFillOrigin, DotFillOrigin, DotFillSize, DotFillSize);

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            // Clone creates an owned Icon that frees itself on Dispose.
            // The raw HICON from GetHicon() must be freed separately - Icon.FromHandle does not own it.
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    // Builds the context menu and creates the tray icon
    private void InitializeTrayIcon()
    {
        _trayMenu = new ContextMenuStrip();
        // Render with the system renderer so the menu follows the app color mode (Application.SetColorMode)
        // like the rest of the UI, instead of the light "professional" gradient the default renderer uses.
        _trayMenu.RenderMode = ToolStripRenderMode.System;

        // Update notification - inserted at the top so it is the first thing the user sees
        // when an update is pending. Hidden until a check reports a newer version.
        // The bold font is stored as a field so MainForm can dispose it - WinForms does not
        // dispose Fonts assigned to ToolStripMenuItem on its own.
        _updateMenuItemFont = new Font(_trayMenu.Font, FontStyle.Bold);
        _updateAvailableMenuItem = new ToolStripMenuItem("Update available")
        {
            Visible = false,
            Font = _updateMenuItemFont // emphasised so it stands out among regular menu items
        };
        _updateAvailableMenuItem.Click += updateAvailable_Click;
        _trayMenu.Items.Add(_updateAvailableMenuItem);

        // Separator paired with the update item - hidden until an update is pending so the
        // menu never opens with a leading separator. Toggled alongside _updateAvailableMenuItem.
        _updateSeparator = new ToolStripSeparator { Visible = false };
        _trayMenu.Items.Add(_updateSeparator);

        // Sync control
        _trayMenu.Items.Add("Sync Port Now", null, syncPortNow_Click);
        _pauseSyncMenuItem = new ToolStripMenuItem(PauseSyncingMenuText);
        _pauseSyncMenuItem.Click += pauseSync_Click;
        _trayMenu.Items.Add(_pauseSyncMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // Status and diagnostics
        _trayMenu.Items.Add("Show Status", null, showStatus_Click);
        _trayMenu.Items.Add("Run Diagnostics", null, runDiagnostics_Click);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // Logs
        _showLogsMenuItem = new ToolStripMenuItem(ShowLogsMenuText);
        _showLogsMenuItem.Click += showLogs_Click;
        _trayMenu.Items.Add(_showLogsMenuItem);
        _trayMenu.Items.Add("Clear Logs", null, clearLogs_Click);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // Configuration
        _trayMenu.Items.Add("Settings", null, showSettings_Click);
        _trayMenu.Items.Add("Media Manager", null, showMediaManager_Click);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // Info
        _checkUpdatesMenuItem = new ToolStripMenuItem("Check for Updates");
        _checkUpdatesMenuItem.Click += checkUpdates_Click;
        _trayMenu.Items.Add(_checkUpdatesMenuItem);
        _trayMenu.Items.Add("Help", null, showHelp_Click);
        _trayMenu.Items.Add("About", null, showAbout_Click);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // App
        _autoStartMenuItem = new ToolStripMenuItem("Start Automatically with Windows")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsStartupEnabled()
        };
        _autoStartMenuItem.Click += autoStart_Click;
        _trayMenu.Items.Add(_autoStartMenuItem);

        _trayMenu.Items.Add("Exit", null, exit_Click);

        _trayIcon = new NotifyIcon
        {
            Icon = _iconBase,
            Text = $"{AppIdentity.AppName} {AppConstants.AppVersion}",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.MouseDoubleClick += trayIcon_MouseDoubleClick;
        _trayIcon.BalloonTipClicked += trayIcon_BalloonTipClicked;
    }

    private void showLogs_Click(object? sender, EventArgs e) => ShowLogViewer();

    private void clearLogs_Click(object? sender, EventArgs e)
    {
        LogManager.Instance.ClearLogs();
        ResetLogAlerts();
        _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName, "Logs cleared", ToolTipIcon.Info);
    }

    private void showSettings_Click(object? sender, EventArgs e)
    {
        ShowOrActivate(() => _settingsForm, f => _settingsForm = f, () => new SettingsForm(), OnSettingsFormClosed);
    }

    private void OnSettingsFormClosed(SettingsForm frm)
    {
        if (frm.SettingsSaved)
        {
            LogManager.Instance.LogMessage("Settings changed, triggering sync cycle", LogLevel.Info);
            InterruptDelay();
        }
    }

    private void showMediaManager_Click(object? sender, EventArgs e)
    {
        ShowOrActivate(() => _mediaManagerForm, f => _mediaManagerForm = f, () => new MediaManagerForm());
    }

    private void showAbout_Click(object? sender, EventArgs e)
    {
        ShowOrActivate(() => _aboutForm, f => _aboutForm = f, CreateAboutForm);
    }

    private void showHelp_Click(object? sender, EventArgs e)
    {
        ShowOrActivate(() => _helpForm, f => _helpForm = f, () => new HelpForm());
    }

    // Builds the About dialog and routes its Update button into the shared in-app update dialog,
    // the same download-and-install flow as the tray "Update available" item.
    private AboutForm CreateAboutForm()
    {
        var form = new AboutForm();
        form.UpdateRequested += info => ShowUpdateAvailableForm(info.Version, info.ReleaseUrl, info.MsiUrl);
        return form;
    }

    private void showStatus_Click(object? sender, EventArgs e) => ShowStatusPanel();

    private async void runDiagnostics_Click(object? sender, EventArgs e) => await RunDiagnosticsAsync(null); // async void is correct here (WinForms event handler)

    private void ShowStatusPanel()
    {
        ShowOrActivate(() => _statusForm, f => _statusForm = f, CreateStatusForm);
    }

    // Builds the Status panel and wires its Sync Now, Test Port, and Run Diagnostics buttons to the
    // shared manual-sync trigger, an on-demand reachability check, and the read-only health check.
    private StatusForm CreateStatusForm()
    {
        var form = new StatusForm();
        form.SyncRequested += (_, _) => RequestManualSync();
        form.TestPortRequested += async (_, _) => await RunManualPortTestAsync(form); // async void event handler (WinForms)
        form.DiagnosticsRequested += async (_, _) => await RunDiagnosticsAsync(form); // async void event handler (WinForms)
        return form;
    }

    // Runs an on-demand port-reachability check from the Status panel, reporting the outcome back to
    // the panel. Builds a fresh client off the saved settings, so it is safe to run alongside a cycle.
    private async Task RunManualPortTestAsync(StatusForm form)
    {
        form.SetReachableChecking();
        LogManager.Instance.LogMessage("Manual port test requested", LogLevel.Info);
        bool? open;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.ClientTestTimeoutSeconds));
        try
        {
            open = await PortSyncService.TestActivePortAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (form.IsDisposed || _shutdownCts.IsCancellationRequested)
                return; // app shutting down - the form is closing, nothing to report
            // Timed out: report as undetermined and re-enable the button so the user can retry.
            form.SetReachableResult(null);
            LogManager.Instance.LogMessage($"Manual port test timed out after {AppConstants.ClientTestTimeoutSeconds}s", LogLevel.Warn);
            return;
        }
        if (form.IsDisposed) return;
        form.SetReachableResult(open);
        string result = open switch { true => "open", false => "closed", _ => "could not be determined" };
        LogManager.Instance.LogMessage($"Manual port test result: {result}", LogLevel.Info);
    }

    // From the Status panel button or the tray menu: runs diagnostics and opens (or refreshes) the
    // results dialog. 'form' is the Status panel when triggered there (so its button can be toggled),
    // or null from the tray. Guarded so the two entry points and the dialog's Re-run cannot run
    // concurrently.
    private async Task RunDiagnosticsAsync(StatusForm? form)
    {
        if (_diagnosticsRunning) return;
        _diagnosticsRunning = true;
        form?.SetDiagnosticsRunning(true);
        LogManager.Instance.LogMessage("Diagnostics requested", LogLevel.Info);
        try
        {
            var results = await RunDiagnosticsCoreAsync();
            if (results is not null) ShowDiagnosticsResults(results);
        }
        finally
        {
            _diagnosticsRunning = false;
            if (form is { IsDisposed: false }) form.SetDiagnosticsRunning(false);
        }
    }

    // From the results dialog's Re-run button: re-runs diagnostics and refreshes the dialog in place.
    private async Task RerunDiagnosticsAsync(DiagnosticsForm form)
    {
        if (_diagnosticsRunning) return;
        _diagnosticsRunning = true;
        form.SetRunning(true);
        LogManager.Instance.LogMessage("Diagnostics re-run requested", LogLevel.Info);
        try
        {
            var results = await RunDiagnosticsCoreAsync();
            if (results is not null && !form.IsDisposed) form.SetResults(results);
        }
        finally
        {
            _diagnosticsRunning = false;
            if (!form.IsDisposed) form.SetRunning(false);
        }
    }

    // Runs the diagnostics service with the standard shutdown-linked timeout and error handling. Builds
    // fresh managers/clients off the saved settings, so it is safe to run alongside a sync cycle.
    // Returns the results, or null when cancelled, timed out, or failed (already logged).
    private async Task<IReadOnlyList<DiagnosticResult>?> RunDiagnosticsCoreAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.DiagnosticsTimeoutSeconds));
        try
        {
            return await DiagnosticsService.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_shutdownCts.IsCancellationRequested)
                LogManager.Instance.LogMessage($"Diagnostics timed out after {AppConstants.DiagnosticsTimeoutSeconds}s", LogLevel.Warn);
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Diagnostics failed: {ex.Message}", LogLevel.Error);
            return null;
        }
    }

    // Opens the diagnostics results dialog, or refreshes the already-open one in place, so a fresh run
    // never stacks windows or shows stale results.
    private void ShowDiagnosticsResults(IReadOnlyList<DiagnosticResult> results)
    {
        if (_shutdownCts.IsCancellationRequested) return;
        if (_diagnosticsForm is { IsDisposed: false } existing)
        {
            existing.SetResults(results);
            existing.Activate();
            return;
        }
        var frm = new DiagnosticsForm(results);
        _diagnosticsForm = frm;
        frm.RefreshRequested += async (_, _) => await RerunDiagnosticsAsync(frm); // async void event handler (WinForms)
        frm.FormClosed += (_, _) => { if (ReferenceEquals(_diagnosticsForm, frm)) _diagnosticsForm = null; };
        frm.Show();
    }

    private void autoStart_Click(object? sender, EventArgs e) => StartupManager.SetStartup(_autoStartMenuItem.Checked);

    private void trayIcon_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowStatusPanel();
    }

    private void syncPortNow_Click(object? sender, EventArgs e) => RequestManualSync();

    // Triggers an immediate sync cycle by interrupting the current wait interval. Shared by the
    // "Sync Port Now" tray item and the Status panel's "Sync Now" button.
    // Works while paused too: the loop runs exactly one cycle (the manual flag overrides the
    // pause check), then returns to the paused state.
    private void RequestManualSync()
    {
        _manualSyncTriggered = true;
        LogManager.Instance.LogMessage("Manual sync requested", LogLevel.Info);
        InterruptDelay();
    }

    // Toggles the sync pause. On pause the tray feedback flips immediately (the loop itself
    // only notices at its next wakeup, but no cycle can start once the flag is set, so the
    // pause is already effective). On resume the delay is interrupted so a cycle starts now.
    private void pauseSync_Click(object? sender, EventArgs e)
    {
        _syncPaused = !_syncPaused;
        _pauseSyncMenuItem.Text = _syncPaused ? ResumeSyncingMenuText : PauseSyncingMenuText;
        if (_syncPaused)
        {
            LogManager.Instance.LogMessage("Syncing paused by user", LogLevel.Info);
            OnSyncCompleted(new TrayStatus(SyncState.Paused, null, "Port sync paused"));
        }
        else
        {
            LogManager.Instance.LogMessage("Syncing resumed by user", LogLevel.Info);
            InterruptDelay();
        }
    }

    // A network address changed (adapter up/down, VPN reconnect). Re-arm the one-shot debounce
    // timer; the re-sync fires once the burst settles. Runs on a thread-pool thread.
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        try { _resyncDebounceTimer?.Change(AppConstants.ResyncDebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException)
        {
            // Defensive: the timer was disposed during shutdown between the null check and Change.
        }
    }

    // Fires after network changes settle. Wakes the sync loop early unless shutting down, paused,
    // or the user disabled the option. Reads the setting fresh so a Settings toggle takes effect
    // immediately. Respects pause like the scheduled loop does (no cycle runs while paused).
    private void OnResyncDebounceElapsed(object? state)
    {
        if (_shutdownCts.IsCancellationRequested || _syncPaused) return;
        if (!RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyResyncOnNetworkChange))
            return;
        LogManager.Instance.LogMessage("Network change detected, triggering sync cycle", LogLevel.Info);
        // Set before interrupting so the triggered cycle observes it and schedules a short re-check.
        _quickRecheckPending = true;
        InterruptDelay();
    }

    // Interrupts the current inter-cycle delay so the next sync cycle starts immediately
    private void InterruptDelay()
    {
        try { _delayCts.Cancel(); }
        catch (ObjectDisposedException)
        {
            // Defensive: Cancel() throws if the CTS was disposed between the read of _delayCts and this call.
        }
    }

    private void exit_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static

    // Called by PortSyncService when a sync cycle completes, and directly by the pause
    // handling (pauseSync_Click and the paused loop branch) to publish the Paused status.
    private void OnSyncCompleted(TrayStatus status)
    {
        _lastSyncStatus = status;
        if (!_shutdownCts.IsCancellationRequested)
            InvokeOnUiThread(() =>
            {
                UpdateTrayIcon(status.State);
                UpdateTrayTooltip();
                // Refresh the Status panel live if it is open (the status file was written before
                // this event fired, so it reflects the cycle that just completed).
                if (_statusForm is { IsDisposed: false })
                    _statusForm.RefreshStatus();
            });
    }

    // Shared handler for PortSyncService warning events (interface mismatch, port verification
    // failure) - both fire transition-only and warrant a one-shot warning balloon.
    private void ShowWarningBalloon(string message)
    {
        if (_shutdownCts.IsCancellationRequested) return;
        InvokeOnUiThread(() => { _logAlertBalloonPending = false; _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName, message, ToolTipIcon.Warning); });
    }

    // Called by PortSyncService when the BitTorrent client's listening port is successfully updated.
    // Info balloons are not clickable on Windows 11 (routed silently through Action Center), so there
    // is no need to clear _logAlertBalloonPending here - a click on this balloon will not fire
    // BalloonTipClicked, so it cannot mistakenly trigger the log viewer.
    private void OnPortUpdated(string message) // NOSONAR S2325 - ShowBalloonTip is an instance method, handler cannot be static
    {
        if (_shutdownCts.IsCancellationRequested) return;
        InvokeOnUiThread(() => _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName, message, ToolTipIcon.Info));
    }

    // Runs the port-sync loop until shutdown is requested.
    // Exceptions are caught per-cycle so one bad cycle never kills the app.
    private async Task RunMainLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            int updateInterval = AppConstants.DefaultUpdateIntervalSeconds;
            try
            {
                // Paused: skip the whole cycle body (port sync and media import). A manual
                // "Sync Port Now" click overrides the pause for exactly one cycle; the flag is
                // consumed by the normal manual-sync handling after that cycle completes.
                if (_syncPaused && !_manualSyncTriggered)
                    PublishPausedStatus();
                else
                    updateInterval = await RunSyncCycleAsync();

                if (await ShutdownRequestedDuringDelayAsync(updateInterval))
                    return;
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Sync cycle failed, retrying in {updateInterval}s: {ex.Message}", LogLevel.Error);
                if (!await TryDelayAfterErrorAsync(updateInterval))
                    break;
            }
        }

        LogManager.Instance.LogMessage("Main loop exited gracefully", LogLevel.Info);
    }

    // Publishes the Paused tray status while cycles are being skipped. Re-published on every
    // skipped interval because a one-shot manual sync while paused leaves that cycle's result
    // on the tray, and this puts the paused status back.
    private void PublishPausedStatus()
    {
        LogManager.Instance.LogDebug("MainForm.RunMainLoopAsync: Sync paused - skipping cycle");
        OnSyncCompleted(new TrayStatus(SyncState.Paused, null, "Port sync paused"));
    }

    // Runs one full sync cycle (port sync under the semaphore, then the media import kick-off)
    // and returns the interval in seconds to wait before the next cycle.
    private async Task<int> RunSyncCycleAsync()
    {
        int updateInterval;
        await _updateSemaphore.WaitAsync(_shutdownCts.Token);
        try
        {
            LogManager.Instance.LogBlankLine();
            LogManager.Instance.LogMessage("Sync cycle started", LogLevel.Info);
            updateInterval = await _portSyncService.RunAsync(_shutdownCts.Token);
        }
        finally
        {
            _updateSemaphore.Release();
        }

        TryKickOffMediaImport();

        // After a manual sync, wait only 10 seconds before next check.
        if (_manualSyncTriggered)
        {
            _manualSyncTriggered = false;
            _quickRecheckPending = false; // manual takes precedence; do not also queue a quick re-check
            updateInterval = AppConstants.ManualSyncWaitSeconds;
            LogManager.Instance.LogMessage("Manual sync completed", LogLevel.Info);
        }
        // A network-change-triggered cycle may have run before the VPN finished settling; re-check
        // shortly instead of resetting to a full interval. One-shot, so it does not spin.
        else if (_quickRecheckPending)
        {
            _quickRecheckPending = false;
            updateInterval = AppConstants.ManualSyncWaitSeconds;
        }
        return updateInterval;
    }

    // Waits out the retry interval after a failed cycle. Returns false when the loop should
    // stop: shutdown cancellation, or an unexpected delay failure.
    private async Task<bool> TryDelayAfterErrorAsync(int updateInterval)
    {
        try
        {
            await Task.Delay(updateInterval * AppConstants.MillisecondsPerSecond, _shutdownCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception delayEx)
        {
            // Unexpected: Task.Delay should only throw OperationCanceledException via the token.
            // Anything else here indicates a runtime issue we cannot recover from in this loop.
            // Log the full exception (including type) so the failure is visible in the log file.
            LogManager.Instance.LogMessage($"Unexpected exception during retry delay: {delayEx}", LogLevel.Error);
            return false;
        }
    }

    // Kicks off the media import on a separate fire-and-forget task so a long library scan
    // does not delay the next port sync cycle. Skipped when a previous import is still in
    // flight - queueing them would let imports pile up indefinitely on slow storage.
    private void TryKickOffMediaImport()
    {
        if (Interlocked.CompareExchange(ref _mediaImportRunning, 1, 0) != 0)
        {
            LogManager.Instance.LogDebug("MainForm.TryKickOffMediaImport: Media import skipped - previous import still running");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await MediaManagerService.ImportAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                // Shutdown path: import was cancelled mid-cycle. Swallow silently - the
                // main loop's own shutdown handling logs the exit.
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Media import cycle failed: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _mediaImportRunning, 0);
            }
        });
    }

    // Waits for the next cycle interval, handling manual-update interrupts.
    // Returns true if shutdown was requested (caller should stop looping), false otherwise.
    private async Task<bool> ShutdownRequestedDuringDelayAsync(int updateInterval)
    {
        try
        {
            LogManager.Instance.LogDebug($"MainForm.ShutdownRequestedDuringDelayAsync: Waiting {updateInterval} seconds before next cycle");
            // Link both tokens: _delayCts (manual sync) and _shutdownCts (app exit)
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_delayCts.Token, _shutdownCts.Token);
            await Task.Delay(updateInterval * AppConstants.MillisecondsPerSecond, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                LogManager.Instance.LogMessage("Shutdown requested, exiting main loop", LogLevel.Info);
                return true;
            }
            // Delay interrupted (manual sync or settings change) - loop will restart immediately
            LogManager.Instance.LogMessage("Delay interrupted, starting next cycle", LogLevel.Info);
        }

        // Atomically swap in a fresh token and dispose the old one
        Interlocked.Exchange(ref _delayCts, new CancellationTokenSource()).Dispose();
        return false;
    }

    // Periodic update-check timer tick. Uses intrusive: false so a 12-hour tick surfaces an available
    // update via the tray (menu item + balloon) rather than popping the UpdateAvailableForm in the user's face.
    private async void OnUpdateCheckTimerTick(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
        => await PerformUpdateCheckAsync(intrusive: false);

    // Checks GitHub for a newer release.
    // When <paramref name="intrusive"/> is true (startup call or manual tray click), opens the
    // UpdateAvailableForm directly when a newer version is found.
    // When false (12-hour timer tick), surfaces the result through the tray menu item and tooltip
    // plus a one-shot informational balloon, so the user is not interrupted. The menu item is the
    // only clickable entry point - Windows 11 routes ToolTipIcon.Info balloons silently through
    // Action Center and does not reliably fire BalloonTipClicked. The menu item stays visible
    // until the user updates (process restart with matching version clears _pendingUpdate naturally).
    // When <paramref name="manual"/> is true (user-initiated from the tray menu), intrusive is also
    // set so a found update opens the form; the same-version dedup is bypassed so the user always
    // gets feedback, and "up to date" / failure outcomes show a balloon so the click is never silent.
    private async Task PerformUpdateCheckAsync(bool intrusive = true, bool manual = false)
    {
        try
        {
            LogManager.Instance.LogDebug("MainForm.PerformUpdateCheckAsync: Checking for application updates");
            var info = await UpdateChecker.GetLatestReleaseInfoAsync(_shutdownCts.Token);
            if (info is null)
                NotifyUpdateCheckFailed(manual);
            else if (info.IsNewer)
                PresentAvailableUpdate(info, intrusive, manual);
            else
                NotifyUpToDate(manual);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"MainForm.PerformUpdateCheckAsync: {ex.Message}");
            NotifyUpdateCheckFailed(manual);
        }
    }

    // Check failed or was cancelled by shutdown - UpdateChecker already logged the cause at Debug.
    // Kept distinct from NotifyUpToDate so a failed check is never reported as "up to date"
    // (the manual balloon would otherwise show false information).
    private void NotifyUpdateCheckFailed(bool manual)
    {
        if (manual)
            _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName,
                "Could not check for updates - see the log for details.", ToolTipIcon.Warning);
    }

    private void NotifyUpToDate(bool manual)
    {
        LogManager.Instance.LogMessage($"Application is up to date ({AppConstants.AppVersion})", LogLevel.Info);
        if (manual)
            _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName,
                $"{AppIdentity.AppName} {AppConstants.AppVersion} is up to date.", ToolTipIcon.Info);
    }

    // Surfaces a newer release: persistent tray menu item + tooltip line, then either the
    // update form (intrusive) or a one-shot informational balloon (background timer tick).
    private void PresentAvailableUpdate(LatestReleaseInfo info, bool intrusive, bool manual)
    {
        if (!manual && info.Version == _lastNotifiedVersion)
        {
            LogManager.Instance.LogDebug($"MainForm.PresentAvailableUpdate: Version {info.Version} available (already notified)");
            return;
        }

        _lastNotifiedVersion = info.Version;
        _pendingUpdate = (info.Version, info.ReleaseUrl, info.MsiUrl);
        LogManager.Instance.LogMessage($"New application version available: {info.Version}", LogLevel.Info);

        _updateAvailableMenuItem.Text = $"Update available ({info.Version})";
        _updateAvailableMenuItem.Visible = true;
        _updateSeparator.Visible = true;
        UpdateTrayTooltip();

        if (intrusive)
        {
            ShowUpdateAvailableForm(info.Version, info.ReleaseUrl, info.MsiUrl);
        }
        else
        {
            // Info balloon: not clickable on Windows 11 (routed silently through Action Center).
            // Shown purely as a visual hint that an update is available; the tray menu item is
            // the actual entry point to open the update form.
            _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName,
                $"Version {info.Version} is available. Open the tray menu to install.",
                ToolTipIcon.Info);
        }
    }

    // Manual tray-triggered update check. Disables the menu item while the request is in flight
    // so rapid clicks do not stack multiple HTTP calls; re-enabled in finally even on cancellation.
    private async void checkUpdates_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        _checkUpdatesMenuItem.Enabled = false;
        try
        {
            await PerformUpdateCheckAsync(intrusive: true, manual: true);
        }
        finally
        {
            if (!IsDisposed)
                _checkUpdatesMenuItem.Enabled = true;
        }
    }

    private void updateAvailable_Click(object? sender, EventArgs e)
    {
        if (_pendingUpdate is not { } update) return;
        ShowUpdateAvailableForm(update.Version, update.Url, update.MsiUrl);
    }

    // Opens or activates the singleton UpdateAvailableForm. Wrapping in ShowOrActivate
    // prevents repeated clicks (menu or startup intrusive path) from stacking multiple
    // windows on top of each other.
    private void ShowUpdateAvailableForm(string version, string url, string? msiUrl) =>
        ShowOrActivate(
            () => _updateAvailableForm,
            f => _updateAvailableForm = f,
            () => new UpdateAvailableForm(version, url, msiUrl));

    // Swaps the tray icon to reflect the current sync state
    private void UpdateTrayIcon(SyncState state)
    {
        _trayIcon.Icon = state switch
        {
            SyncState.Synced => _iconOk ?? _iconBase!,
            SyncState.VpnDisconnected => _iconWarning ?? _iconBase!,
            SyncState.Error => _iconError ?? _iconBase!,
            SyncState.Disabled => _iconBase!,
            SyncState.Paused => _iconPaused ?? _iconBase!,
            _ => _iconBase!
        };
    }

    // Rebuilds the tray tooltip text from the last sync status
    private void UpdateTrayTooltip()
    {
        string statusLine = _lastSyncStatus switch
        {
            { State: SyncState.Synced, Port: int p } => $"Port {p} | Synced",
            { State: SyncState.VpnDisconnected, Port: int p } => $"VPN not connected | Default port {p}",
            { State: SyncState.VpnDisconnected } => "VPN not connected",
            { State: SyncState.Disabled } => "Port sync disabled",
            { State: SyncState.Paused } => "Port sync paused",
            { State: SyncState.Error, Message: var m } => $"Error | {m}",
            _ => "Starting\u2026"
        };

        string countSuffix = string.Empty;
        if (_unviewedWarnCount > 0 || _unviewedErrorCount > 0)
        {
            string wPart = _unviewedWarnCount > 0 ? Pluralize(_unviewedWarnCount, "Warning") : "";
            string ePart = _unviewedErrorCount > 0 ? Pluralize(_unviewedErrorCount, "Error") : "";
            string sep = (wPart.Length > 0 && ePart.Length > 0) ? ", " : "";
            countSuffix = $"\n{wPart}{sep}{ePart}";
        }

        string updateSuffix = _pendingUpdate is { } pu ? $"\nUpdate available: {pu.Version}" : string.Empty;

        string header = $"{AppIdentity.AppName} {AppConstants.AppVersion}\n";

        // Prioritise the status line over the update suffix: if including the update line would
        // force the status to be truncated, drop the update line entirely. The persistent menu
        // item still carries the update info, so the tooltip can omit it under pressure.
        int budgetWithUpdate = AppConstants.MaxTooltipLength - header.Length - countSuffix.Length - updateSuffix.Length;
        string effectiveUpdateSuffix = statusLine.Length > budgetWithUpdate ? string.Empty : updateSuffix;

        int statusBudget = AppConstants.MaxTooltipLength - header.Length - countSuffix.Length - effectiveUpdateSuffix.Length;
        if (statusLine.Length > statusBudget)
            statusLine = statusLine[..Math.Max(0, statusBudget)];

        _trayIcon.Text = $"{header}{statusLine}{countSuffix}{effectiveUpdateSuffix}";
    }

    // Marshals an action to the UI thread, using Invoke if called from a background thread
    private void InvokeOnUiThread(Action action) // NOSONAR S2325 - InvokeRequired/Invoke are instance members, method cannot be static
    {
        if (InvokeRequired)
            Invoke(action);
        else
            action();
    }

    // Called from background threads via LogManager.WarnOrErrorLogged; marshals to UI thread
    private void OnWarnOrErrorLogged(LogLevel level)
    {
        if (_shutdownCts.IsCancellationRequested) return;
        InvokeOnUiThread(() =>
        {
            // Re-check inside the marshalled lambda: a disposal can happen between the
            // outer guard and Invoke completing, leaving the lambda about to touch a
            // disposed form (counter fields, menu item, NotifyIcon).
            if (IsDisposed || _shutdownCts.IsCancellationRequested) return;

            if (level == LogLevel.Warn) _unviewedWarnCount++;
            else _unviewedErrorCount++;

            UpdateShowLogsMenuItem();
            UpdateTrayTooltip();

            if (!_logAlertBalloonShown)
            {
                _logAlertBalloonShown = true;
                _logAlertBalloonPending = true;
                _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName,
                    LogAlertBalloonMessage, ToolTipIcon.Warning);
            }
        });
    }

    private void UpdateShowLogsMenuItem()
    {
        if (_unviewedWarnCount == 0 && _unviewedErrorCount == 0)
        {
            _showLogsMenuItem.Text = ShowLogsMenuText;
            return;
        }

        string warnPart = _unviewedWarnCount > 0 ? Pluralize(_unviewedWarnCount, "warning") : "";
        string errorPart = _unviewedErrorCount > 0 ? Pluralize(_unviewedErrorCount, "error") : "";
        string badge = (warnPart.Length > 0 && errorPart.Length > 0) ? $"{warnPart}, {errorPart}" : $"{warnPart}{errorPart}";
        _showLogsMenuItem.Text = $"{ShowLogsMenuText} ({badge})";
    }

    private void ResetLogAlerts()
    {
        _unviewedWarnCount = 0;
        _unviewedErrorCount = 0;
        _logAlertBalloonShown = false;
        _logAlertBalloonPending = false;
        _showLogsMenuItem.Text = ShowLogsMenuText;
        UpdateTrayTooltip();
    }

    private void ShowLogViewer()
    {
        bool navigateToLatestIssue = _unviewedWarnCount > 0 || _unviewedErrorCount > 0;
        ResetLogAlerts();
        // For an already-open viewer, only the post-activate hook is needed (the constructor
        // bool field only takes effect on initial load); this passes navigation through both paths.
        ShowOrActivate(
            () => _logViewerForm,
            f => _logViewerForm = f,
            () => new LogViewerForm(LogManager.Instance.LogFilePath, navigateToLatestIssue),
            onActivated: f => { if (navigateToLatestIssue) f.NavigateToLatestIssue(); });
    }

    private void trayIcon_BalloonTipClicked(object? sender, EventArgs e)
    {
        if (!_logAlertBalloonPending) return;
        ShowLogViewer();
    }

    // Brings an existing child form to front, or creates and shows a new one.
    // onClosed runs after the form is closed. onActivated runs after the form is shown
    // or re-activated (covers both new and existing).
    private static void ShowOrActivate<T>(Func<T?> getter, Action<T?> setter, Func<T> factory, Action<T>? onClosed = null, Action<T>? onActivated = null) where T : Form
    {
        var existing = getter();
        if (existing is { IsDisposed: false })
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.BringToFront();
            existing.Activate();
            onActivated?.Invoke(existing);
            return;
        }

        var frm = factory();
        setter(frm);
        frm.FormClosed += (_, _) =>
        {
            onClosed?.Invoke(frm);
            setter(null);
        };
        frm.Show();
        onActivated?.Invoke(frm);
    }

    private static string Pluralize(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}

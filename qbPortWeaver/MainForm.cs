using qbPortWeaver.Shared;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace qbPortWeaver;

/// <summary>Main application form - hosts the system tray icon, context menu, and owns the background port-sync service.</summary>
public partial class MainForm : Form
{
    // Tray icon, menu and auto-start menu item
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private ToolStripMenuItem _autoStartMenuItem = null!;
    private ToolStripMenuItem _showLogsMenuItem = null!;
    private ToolStripMenuItem _updateAvailableMenuItem = null!;

    private const string ShowLogsMenuText = "Show Logs";
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
    private UpdateAvailableForm? _updateAvailableForm;

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

    // Shutdown cancellation token to signal graceful exit
    private readonly CancellationTokenSource _shutdownCts = new();

    // Guard so SetVisibleCore fires OnLoad exactly once
    private bool _formLoaded;

    // Periodic update check timer (fires every 12 hours)
    private System.Windows.Forms.Timer _updateCheckTimer = null!;

    // Last version for which the user was already shown an update prompt
    private string? _lastNotifiedVersion;

    // Update detected by the latest check, surfaced via the tray menu item and tooltip
    // until the user updates (next process launch resets _pendingUpdate). Null when no
    // update is pending. The update balloon is informational only - Windows 11 routes
    // ToolTipIcon.Info balloons through Action Center and does not reliably fire
    // BalloonTipClicked, so the tray menu item is the only clickable entry point.
    private (string Version, string Url)? _pendingUpdate;

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
        _portSyncService.InterfaceMismatchDetected += OnInterfaceMismatchDetected;
        _portSyncService.PortUpdated += OnPortUpdated;

        InitializeStatusIcons();
        InitializeTrayIcon();
        UpdateTrayTooltip();

        LogManager.Instance.WarnOrErrorLogged += OnWarnOrErrorLogged;
    }

    private void MainForm_Load(object sender, EventArgs e) => _ = MainForm_LoadAsync();

    private async Task MainForm_LoadAsync()
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
    // the Load event exactly once so MainForm_LoadAsync (sync loop, timers, etc.) runs normally.
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

        // Hide tray icon immediately to avoid ghost icon
        _trayIcon.Visible = false;

        // Close all child forms
        _logViewerForm?.Close();
        _settingsForm?.Close();
        _mediaManagerForm?.Close();
        _aboutForm?.Close();
        _updateAvailableForm?.Close();

        // Resources are disposed in Dispose(bool) via MainForm.Designer.cs
        base.OnFormClosing(e);
    }

    // Pre-generates the three status icon variants (colored dot in the bottom-right corner)
    private void InitializeStatusIcons()
    {
        _iconBase = Properties.Resources.qbPortWeaver;
        _iconOk = CreateStatusIcon(_iconBase, AppConstants.StatusOk);
        _iconWarning = CreateStatusIcon(_iconBase, AppConstants.StatusWarning);
        _iconError = CreateStatusIcon(_iconBase, AppConstants.StatusError);
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

        _trayMenu.Items.Add("Sync Port Now", null, syncPortNow_Click);
        _showLogsMenuItem = new ToolStripMenuItem(ShowLogsMenuText);
        _showLogsMenuItem.Click += showLogs_Click;
        _trayMenu.Items.Add(_showLogsMenuItem);
        _trayMenu.Items.Add("Clear Logs", null, clearLogs_Click);
        _trayMenu.Items.Add("Settings", null, showSettings_Click);
        _trayMenu.Items.Add("Media Manager", null, showMediaManager_Click);
        _trayMenu.Items.Add("About", null, showAbout_Click);

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
        ShowOrActivate(() => _aboutForm, f => _aboutForm = f, () => new AboutForm());
    }

    private void autoStart_Click(object? sender, EventArgs e) => StartupManager.SetStartup(_autoStartMenuItem.Checked);

    private void trayIcon_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowLogViewer();
    }

    // Triggers an immediate sync cycle by interrupting the current wait interval
    private void syncPortNow_Click(object? sender, EventArgs e)
    {
        _manualSyncTriggered = true;
        LogManager.Instance.LogMessage("Manual sync requested", LogLevel.Info);
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

    // Called by PortSyncService when a sync cycle completes
    private void OnSyncCompleted(TrayStatus status)
    {
        _lastSyncStatus = status;
        if (!_shutdownCts.IsCancellationRequested)
            InvokeOnUiThread(() => { UpdateTrayIcon(status.State); UpdateTrayTooltip(); });
    }

    // Called by PortSyncService when qBittorrent's network interface doesn't match the configured VPN provider
    private void OnInterfaceMismatchDetected(string message)
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
                await _updateSemaphore.WaitAsync(_shutdownCts.Token);
                LogManager.Instance.LogBlankLine();
                LogManager.Instance.LogMessage("Sync cycle started", LogLevel.Info);
                try
                {
                    updateInterval = await _portSyncService.RunAsync(_shutdownCts.Token);
                }
                finally
                {
                    _updateSemaphore.Release();
                }

                TryKickOffMediaImport();

                // After a manual sync, wait only 10 seconds before next check
                if (_manualSyncTriggered)
                {
                    _manualSyncTriggered = false;
                    updateInterval = AppConstants.ManualSyncWaitSeconds;
                    LogManager.Instance.LogMessage("Manual sync completed", LogLevel.Info);
                }

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
                try { await Task.Delay(updateInterval * AppConstants.MillisecondsPerSecond, _shutdownCts.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception delayEx)
                {
                    // Unexpected: Task.Delay should only throw OperationCanceledException via the token.
                    // Anything else here indicates a runtime issue we cannot recover from in this loop.
                    // Log the full exception (including type) so the failure is visible in the log file.
                    LogManager.Instance.LogMessage($"Unexpected exception during retry delay: {delayEx}", LogLevel.Error);
                    break;
                }
            }
        }

        LogManager.Instance.LogMessage("Main loop exited gracefully", LogLevel.Info);
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

    // Event handler for the periodic update-check timer - async void is correct here (event handler).
    // Uses intrusive: false so a 12-hour tick surfaces an available update via the tray (menu item +
    // balloon) rather than popping the UpdateAvailableForm in the user's face.
    private async void OnUpdateCheckTimerTick(object? sender, EventArgs e)
        => await PerformUpdateCheckAsync(intrusive: false);

    // Checks GitHub for a newer release.
    // When <paramref name="intrusive"/> is true (startup call), opens the UpdateAvailableForm directly.
    // When false (12-hour timer tick), surfaces the result through the tray menu item and tooltip
    // plus a one-shot informational balloon, so the user is not interrupted. The menu item is the
    // only clickable entry point - Windows 11 routes ToolTipIcon.Info balloons silently through
    // Action Center and does not reliably fire BalloonTipClicked. The menu item stays visible
    // until the user updates (process restart with matching version clears _pendingUpdate naturally).
    private async Task PerformUpdateCheckAsync(bool intrusive = true)
    {
        try
        {
            LogManager.Instance.LogDebug("MainForm.PerformUpdateCheckAsync: Checking for application updates");
            var update = await UpdateChecker.GetAvailableUpdateAsync(_shutdownCts.Token);
            if (update.HasValue)
            {
                if (update.Value.Version == _lastNotifiedVersion)
                {
                    LogManager.Instance.LogDebug($"MainForm.PerformUpdateCheckAsync: Version {update.Value.Version} available (already notified)");
                    return;
                }

                _lastNotifiedVersion = update.Value.Version;
                _pendingUpdate = update.Value;
                LogManager.Instance.LogMessage($"New application version available: {update.Value.Version}", LogLevel.Info);

                _updateAvailableMenuItem.Text = $"Update available ({update.Value.Version})";
                _updateAvailableMenuItem.Visible = true;
                UpdateTrayTooltip();

                if (intrusive)
                {
                    ShowUpdateAvailableForm(update.Value.Version, update.Value.Url);
                }
                else
                {
                    // Info balloon: not clickable on Windows 11 (routed silently through Action Center).
                    // Shown purely as a visual hint that an update is available; the tray menu item is
                    // the actual entry point to open the update form.
                    _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppIdentity.AppName,
                        $"Version {update.Value.Version} is available. Open the tray menu to install.",
                        ToolTipIcon.Info);
                }
            }
            else
            {
                LogManager.Instance.LogMessage($"Application is up to date ({AppConstants.AppVersion})", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"MainForm.PerformUpdateCheckAsync: {ex.Message}");
        }
    }

    private void updateAvailable_Click(object? sender, EventArgs e)
    {
        if (_pendingUpdate is not { } update) return;
        ShowUpdateAvailableForm(update.Version, update.Url);
    }

    // Opens or activates the singleton UpdateAvailableForm. Wrapping in ShowOrActivate
    // prevents repeated clicks (menu or startup intrusive path) from stacking multiple
    // windows on top of each other.
    private void ShowUpdateAvailableForm(string version, string url) =>
        ShowOrActivate(
            () => _updateAvailableForm,
            f => _updateAvailableForm = f,
            () => new UpdateAvailableForm(version, url));

    // Swaps the tray icon to reflect the current sync state
    private void UpdateTrayIcon(SyncState state)
    {
        _trayIcon.Icon = state switch
        {
            SyncState.Synced => _iconOk ?? _iconBase!,
            SyncState.VpnDisconnected => _iconWarning ?? _iconBase!,
            SyncState.Error => _iconError ?? _iconBase!,
            SyncState.Disabled => _iconBase!,
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

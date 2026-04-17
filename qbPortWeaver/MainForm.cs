using System.ComponentModel;
using System.Runtime.InteropServices;

namespace qbPortWeaver
{
    /// <summary>Main application form - hosts the system tray icon, context menu, and owns the background port-sync service.</summary>
    public partial class MainForm : Form
    {
        // Tray icon, menu and auto-start menu item
        private NotifyIcon _trayIcon = null!;
        private ContextMenuStrip _trayMenu = null!;
        private ToolStripMenuItem _autoStartMenuItem = null!;

        // Status tray icons (generated at startup; disposed in MainForm.Designer.cs)
        private Icon? _iconBase;
        private Icon? _iconOk;
        private Icon? _iconWarning;
        private Icon? _iconError;

        // Services (assigned in the ctor after the designer guard; null! initializer keeps the field
        // non-nullable for runtime callers while satisfying flow analysis on the design-time early-return path)
        private readonly PortSyncService _portSyncService = null!;

        // Child forms (null when closed)
        private LogViewerForm? _logViewerForm;
        private SettingsForm? _settingsForm;
        private MediaManagerForm? _mediaManagerForm;
        private AboutForm? _aboutForm;

        // Last sync status (written from background thread, read on UI thread)
        private volatile TrayStatus? _lastSyncStatus;

        // Cancellation token for the inter-cycle delay - cancelled by manual sync requests to skip the wait
        private volatile CancellationTokenSource _delayCts = new();

        // Semaphore to prevent concurrent sync cycles. Also serialises access to
        // PortSyncService instance state (e.g. _lastKnownNatPmpManager) - see PortSyncService.cs.
        private readonly SemaphoreSlim _updateSemaphore = new(1, 1);

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

        public MainForm()
        {
            InitializeComponent();

            // Guard against the Visual Studio designer instantiating MainForm: runtime-only side effects
            // (log file creation, registry writes, live tray icon) must not fire at design time.
            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime) return;

            LogManager.Initialize(AppConstants.GetLogFilePath());

            // Ensure all registry keys exist, writing defaults for any missing ones
            RegistrySettingsManager.EnsureDefaults();

            _portSyncService = new PortSyncService();
            _portSyncService.SyncCompleted += OnSyncCompleted;
            _portSyncService.InterfaceMismatchDetected += OnInterfaceMismatchDetected;

            InitializeStatusIcons();
            InitializeTrayIcon();
            UpdateTrayTooltip();
        }

        private void MainForm_Load(object sender, EventArgs e) => _ = MainForm_LoadAsync();

        private async Task MainForm_LoadAsync()
        {
            try
            {
                // Log once at startup so the version is visible in the log file for diagnostics
                LogManager.Instance.LogMessage($"{AppConstants.AppName} {AppConstants.AppVersion} starting", LogLevel.Info);

                // Perform initial log rotation check
                LogManager.Instance.CheckAndRotateLogFile();

                // Start main loop immediately so port syncing is not blocked by dialogs
                _ = Task.Run(RunMainLoopAsync); // fire-and-forget; exceptions are handled inside RunMainLoopAsync

                // Show What's New on first run after an upgrade (non-modal - does not block port sync)
                if (RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyLastSeenVersion) != AppConstants.AppVersion)
                {
                    var whatsNew = new WhatsNewForm();
                    whatsNew.FormClosed += (_, _) =>
                        RegistrySettingsManager.SetAppValue(RegistrySettingsManager.KeyLastSeenVersion, AppConstants.AppVersion);
                    whatsNew.Show(); // NOSONAR S6966 - non-modal is intentional; ShowAsync would block until closed
                }

                // Check for updates on GitHub (non-modal - does not block port sync)
                _ = PerformUpdateCheckAsync();

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
                            AppConstants.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                const int WS_EX_TOOLWINDOW = 0x80;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // Handle form closing (user exit, Windows shutdown/restart/logoff)
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Signal the main loop to stop
            _shutdownCts.Cancel();

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

            // Resources are disposed in Dispose(bool) via MainForm.Designer.cs
            base.OnFormClosing(e);
        }

        // Pre-generates the three status icon variants (colored dot in the bottom-right corner)
        private void InitializeStatusIcons()
        {
            _iconBase    = Properties.Resources.qbPortWeaver;
            _iconOk      = CreateStatusIcon(_iconBase, Color.LimeGreen);
            _iconWarning = CreateStatusIcon(_iconBase, Color.Orange);
            _iconError   = CreateStatusIcon(_iconBase, Color.Red);
        }

        // Draws a small filled circle onto a 16x16 copy of the base icon and returns it as an Icon
        private static Icon CreateStatusIcon(Icon baseIcon, Color dotColor)
        {
            using var bmp         = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g           = Graphics.FromImage(bmp);
            using var borderBrush = new SolidBrush(Color.FromArgb(60, 60, 60));
            using var dotBrush    = new SolidBrush(dotColor);

            g.Clear(Color.Transparent);
            using var icon16 = new Icon(baseIcon, 16, 16);
            g.DrawIcon(icon16, new Rectangle(0, 0, 16, 16));

            // 7×7 dark border circle, then 5×5 colored fill - visible on both light and dark taskbars
            g.FillEllipse(borderBrush, 9, 9, 7, 7);
            g.FillEllipse(dotBrush,   10, 10, 5, 5);

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
            _trayMenu.Items.Add("Sync Port Now", null, syncPortNow_Click);
            _trayMenu.Items.Add("Show Logs", null, showLogs_Click);
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
                Text = $"{AppConstants.AppName} {AppConstants.AppVersion}",
                Visible = true,
                ContextMenuStrip = _trayMenu
            };
            _trayIcon.MouseDoubleClick += trayIcon_MouseDoubleClick;
        }

        private void showLogs_Click(object? sender, EventArgs e) => ShowLogViewer();

        private void clearLogs_Click(object? sender, EventArgs e)
        {
            LogManager.Instance.ClearLogs();
            _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppConstants.AppName, "Logs cleared", ToolTipIcon.Info);
        }

        private void showSettings_Click(object? sender, EventArgs e)
        {
            ShowOrActivate(() => _settingsForm, f => _settingsForm = f, () => new SettingsForm(), OnSettingsFormClosed);
        }

        private void OnSettingsFormClosed(SettingsForm frm)
        {
            if (frm.DialogResult == DialogResult.OK)
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

        private void exit_Click(object? sender, EventArgs e) => Close();

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
            InvokeOnUiThread(() => _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppConstants.AppName, message, ToolTipIcon.Warning));
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
                        await MediaManagerService.ImportAsync(_shutdownCts.Token);
                    }
                    finally
                    {
                        _updateSemaphore.Release();
                    }

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
                    catch (Exception) { break; }
                }
            }

            LogManager.Instance.LogMessage("Main loop exited gracefully", LogLevel.Info);
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

            // Reset token for next loop iteration (properly dispose old one)
            var oldToken = _delayCts;
            _delayCts = new CancellationTokenSource();
            oldToken.Dispose();
            return false;
        }

        // Event handler for the periodic update-check timer - async void is correct here (event handler)
        private async void OnUpdateCheckTimerTick(object? sender, EventArgs e)
            => await PerformUpdateCheckAsync();

        // Checks GitHub for a newer release and prompts the user to open the download page if one is found
        private async Task PerformUpdateCheckAsync()
        {
            try
            {
                LogManager.Instance.LogDebug("MainForm.PerformUpdateCheckAsync: Checking for application updates");
                var update = await UpdateChecker.GetAvailableUpdateAsync();
                if (update.HasValue)
                {
                    if (update.Value.Version == _lastNotifiedVersion)
                    {
                        LogManager.Instance.LogDebug($"MainForm.PerformUpdateCheckAsync: Version {update.Value.Version} available (already notified)");
                        return;
                    }

                    _lastNotifiedVersion = update.Value.Version;
                    LogManager.Instance.LogMessage($"New application version available: {update.Value.Version}", LogLevel.Info);
                    new UpdateAvailableForm(update.Value.Version, update.Value.Url).Show(); // NOSONAR S6966 - non-modal is intentional; ShowAsync would block until closed
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

        // Swaps the tray icon to reflect the current sync state
        private void UpdateTrayIcon(SyncState state)
        {
            _trayIcon.Icon = state switch
            {
                SyncState.Synced          => _iconOk      ?? _iconBase!,
                SyncState.VpnDisconnected => _iconWarning ?? _iconBase!,
                SyncState.Error           => _iconError   ?? _iconBase!,
                SyncState.Disabled        =>                  _iconBase!,
                _                         =>                  _iconBase!
            };
        }

        // Rebuilds the tray tooltip text from the last sync status
        private void UpdateTrayTooltip()
        {
            string statusLine = _lastSyncStatus switch
            {
                { State: SyncState.Synced, Port: int p }              => $"Port {p} | Synced",
                { State: SyncState.VpnDisconnected, Port: int p }     => $"VPN not connected | Default port {p}",
                { State: SyncState.VpnDisconnected }                  => "VPN not connected",
                { State: SyncState.Disabled }                         => "Port sync disabled",
                { State: SyncState.Error, Message: var m }            => $"Error | {m}",
                _                                                      => "Starting\u2026"
            };

            string text = $"{AppConstants.AppName} {AppConstants.AppVersion}\n{statusLine}";

            // Tooltip is limited to 63 characters
            if (text.Length > AppConstants.MaxTooltipLength)
                text = text[..AppConstants.MaxTooltipLength];

            _trayIcon.Text = text;
        }

        // Marshals an action to the UI thread, using Invoke if called from a background thread
        private void InvokeOnUiThread(Action action)
        {
            if (InvokeRequired)
                Invoke(action);
            else
                action();
        }

        private void ShowLogViewer()
            => ShowOrActivate(() => _logViewerForm, f => _logViewerForm = f, () => new LogViewerForm(LogManager.Instance.LogFilePath));

        // Brings an existing child form to front, or creates and shows a new one.
        // The optional onClosed callback runs after the form is closed.
        private static void ShowOrActivate<T>(Func<T?> getter, Action<T?> setter, Func<T> factory, Action<T>? onClosed = null) where T : Form
        {
            var existing = getter();
            if (existing is { IsDisposed: false })
            {
                if (existing.WindowState == FormWindowState.Minimized)
                    existing.WindowState = FormWindowState.Normal;
                existing.BringToFront();
                existing.Activate();
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
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyIcon(IntPtr hIcon);
    }
}

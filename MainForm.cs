using System.Diagnostics;
using System.Runtime.InteropServices;

namespace qbPortWeaver
{
    public partial class MainForm : Form
    {
        // Tray icon, menu and auto-start menu item
        private NotifyIcon _trayIcon = null!;
        private ContextMenuStrip _trayMenu = null!;
        private ToolStripMenuItem _autoStartMenuItem = null!;

        // Status tray icons (generated at startup; disposed in MainForm.Designer.cs)
        private Icon? _iconBase;
        private Icon? _iconOK;
        private Icon? _iconWarning;
        private Icon? _iconError;

        // Services
        private readonly PortSyncService _portSyncService;

        // Last sync status (written from background thread, read on UI thread)
        private volatile TrayStatus? _lastSyncStatus;

        // Cancellation token to interrupt waiting
        private CancellationTokenSource _delayCts = new CancellationTokenSource();

        // Semaphore to prevent concurrent updates
        private readonly SemaphoreSlim _updateSemaphore = new SemaphoreSlim(1, 1);

        // Manual sync triggered flag (thread-safe with volatile)
        private volatile bool _manualSyncTriggered;

        // Shutdown cancellation token to signal graceful exit
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

        // Periodic update check timer (fires every 12 hours)
        private System.Windows.Forms.Timer _updateCheckTimer = null!;

        // Last version for which the user was already shown an update prompt
        private string? _lastNotifiedVersion;

        public MainForm()
        {
            InitializeComponent();

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

        private async void MainForm_Load(object sender, EventArgs e)
        {
            // Start minimized and hide from taskbar
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;

            // Log once at startup so the version is visible in the log file for diagnostics
            LogManager.Instance.LogMessage($"{AppConstants.AppName} {AppConstants.AppVersion} starting", LogLevel.Info);

            // Perform initial log rotation check
            LogManager.Instance.CheckAndRotateLogFile();

            // Check for updates on GitHub (startup check)
            await PerformUpdateCheckAsync();

            // Schedule periodic update checks every 12 hours
            _updateCheckTimer = new System.Windows.Forms.Timer { Interval = AppConstants.AutoUpdateCheckIntervalMs };
            _updateCheckTimer.Tick += async (s, e) => await PerformUpdateCheckAsync();
            _updateCheckTimer.Start();

            // Start main loop (intentional fire-and-forget)
            _ = Task.Run(RunMainLoopAsync);
        }

        // Handle form closing (user exit, Windows shutdown/restart/logoff)
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Signal the main loop to stop
            _shutdownCts.Cancel();

            // Hide tray icon immediately to avoid ghost icon
            _trayIcon.Visible = false;

            // Resources are disposed in Dispose(bool) via MainForm.Designer.cs
            base.OnFormClosing(e);
        }

        // Triggers an immediate sync cycle by interrupting the current wait interval
        private void SynchronizePortNow_Click(object? sender, EventArgs e)
        {
            _manualSyncTriggered = true;
            LogManager.Instance.LogMessage("Manual sync requested", LogLevel.Info);

            // Interrupt the wait inside the main loop immediately
            try { _delayCts.Cancel(); }
            catch (ObjectDisposedException)
            {
                // Expected: if the delay completed naturally, the token is already disposed before Cancel() is called.
            }
        }

        private void Exit_Click(object? sender, EventArgs e)
        {
            this.Close();
        }

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

        // Pre-generates the three status icon variants (colored dot in the bottom-right corner)
        private void InitializeStatusIcons()
        {
            _iconBase    = Properties.Resources.qbPortWeaver;
            _iconOK      = CreateStatusIcon(_iconBase, Color.LimeGreen);
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

            // 7×7 dark border circle, then 5×5 colored fill — visible on both light and dark taskbars
            g.FillEllipse(borderBrush, 9, 9, 7, 7);
            g.FillEllipse(dotBrush,   10, 10, 5, 5);

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                // Clone creates an owned Icon that frees itself on Dispose.
                // The raw HICON from GetHicon() must be freed separately — Icon.FromHandle does not own it.
                return (Icon)Icon.FromHandle(hIcon).Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }

        // Builds the context menu and creates the tray icon
        private void InitializeTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Synchronize Port Now", null, SynchronizePortNow_Click);
            _trayMenu.Items.Add("Show Logs", null, (s, e) => OpenFileInNotepad(AppConstants.GetLogFilePath()));
            _trayMenu.Items.Add("Clear Logs", null, (s, e) =>
            {
                LogManager.Instance.ClearLogs();
                _trayIcon.ShowBalloonTip(AppConstants.BalloonTipDurationMs, AppConstants.AppName, "Logs cleared", ToolTipIcon.Info);
            });
            _trayMenu.Items.Add("Settings", null, (s, e) =>
            {
                using var frm = new SettingsForm();
                frm.ShowDialog(this);
            });
            _trayMenu.Items.Add("About", null, (s, e) =>
            {
                using var frm = new AboutForm();
                frm.ShowDialog(this);
            });

            _autoStartMenuItem = new ToolStripMenuItem("Start Automatically with Windows")
            {
                CheckOnClick = true,
                Checked = StartupManager.IsStartupEnabled()
            };
            _autoStartMenuItem.Click += (s, e) => StartupManager.SetStartup(_autoStartMenuItem.Checked);
            _trayMenu.Items.Add(_autoStartMenuItem);

            _trayMenu.Items.Add("Exit", null, Exit_Click);

            _trayIcon = new NotifyIcon
            {
                Icon = _iconBase,
                Text = $"{AppConstants.AppName} {AppConstants.AppVersion}",
                Visible = true,
                ContextMenuStrip = _trayMenu
            };
        }

        // Checks GitHub for a newer release and prompts the user to open the download page if one is found
        private async Task PerformUpdateCheckAsync()
        {
            try
            {
                LogManager.Instance.LogMessage("Checking for application updates", LogLevel.Info);
                var update = await UpdateChecker.GetAvailableUpdateAsync();
                if (update.HasValue)
                {
                    if (update.Value.Version == _lastNotifiedVersion)
                    {
                        LogManager.Instance.LogMessage($"New version {update.Value.Version} available (already notified)", LogLevel.Info);
                        return;
                    }

                    _lastNotifiedVersion = update.Value.Version;
                    LogManager.Instance.LogMessage($"New application version available: {update.Value.Version}", LogLevel.Info);
                    var result = MessageBox.Show(
                        $"A new version of {AppConstants.AppName} is available: {update.Value.Version}\n\nWould you like to open the download page?",
                        $"{AppConstants.AppName} - Update Available",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);

                    if (result == DialogResult.Yes)
                        AppConstants.OpenUrl(update.Value.Url);
                }
                else
                {
                    LogManager.Instance.LogMessage("Application is up to date", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"MainForm.PerformUpdateCheckAsync: {ex.Message}");
            }
        }

        // Runs the port-sync loop until shutdown is requested
        private async Task RunMainLoopAsync()
        {
            try
            {
                while (!_shutdownCts.IsCancellationRequested)
                {
                    await _updateSemaphore.WaitAsync(_shutdownCts.Token);
                    int updateInterval;
                    try
                    {
                        updateInterval = await _portSyncService.RunAsync(_shutdownCts.Token);
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
                        LogManager.Instance.LogMessage("Manual sync complete", LogLevel.Info);
                    }

                    if (await ShutdownRequestedDuringDelayAsync(updateInterval))
                        return;
                }

                LogManager.Instance.LogMessage("Main loop exited gracefully", LogLevel.Info);
            }
            catch (OperationCanceledException)
            {
                // Shutdown was requested while waiting on semaphore or during work
                LogManager.Instance.LogMessage("Main loop exited due to shutdown", LogLevel.Info);
            }
            catch (Exception ex)
            {
                HandleMainLoopException(ex);
            }
        }

        // Waits for the next cycle interval, handling manual-update interrupts.
        // Returns true if shutdown was requested (caller should stop looping), false otherwise.
        private async Task<bool> ShutdownRequestedDuringDelayAsync(int updateInterval)
        {
            try
            {
                LogManager.Instance.LogDebug($"MainForm.ShutdownRequestedDuringDelayAsync: waiting {updateInterval} seconds before next cycle");
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
                // Manual sync interrupts delay — loop will restart immediately
                LogManager.Instance.LogMessage("Delay interrupted by manual sync", LogLevel.Info);
            }

            // Reset token for next loop iteration (properly dispose old one)
            var oldToken = _delayCts;
            _delayCts = new CancellationTokenSource();
            oldToken.Dispose();
            return false;
        }

        // Handles an unexpected exception from the main loop, showing an error dialog unless shutting down
        private void HandleMainLoopException(Exception ex)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                LogManager.Instance.LogMessage($"Main loop exited during shutdown: {ex.Message}", LogLevel.Info);
                return;
            }

            LogManager.Instance.LogMessage($"Main loop crashed: {ex.Message}", LogLevel.Error);

            string message = $"Critical error in main loop: {ex.Message}\n\nThe application will now exit.";
            try
            {
                InvokeOnUiThread(() =>
                {
                    MessageBox.Show(message, AppConstants.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                });
            }
            catch (Exception)
            {
                // Form is already disposed, just exit
                Application.Exit();
            }
        }

        // Swaps the tray icon to reflect the current sync state
        private void UpdateTrayIcon(SyncState state)
        {
            _trayIcon.Icon = state switch
            {
                SyncState.OK              => _iconOK      ?? _iconBase!,
                SyncState.VpnDisconnected => _iconWarning ?? _iconBase!,
                SyncState.Error           => _iconError   ?? _iconBase!,
                _                         =>                 _iconBase!
            };
        }

        // Rebuilds the tray tooltip text from the last sync status
        private void UpdateTrayTooltip()
        {
            string statusLine = _lastSyncStatus switch
            {
                { State: SyncState.OK, Port: int p }                  => $"Port {p} | Synced",
                { State: SyncState.VpnDisconnected, Port: int p }     => $"VPN not connected | Default port {p}",
                { State: SyncState.VpnDisconnected }                  => "VPN not connected",
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
            if (this.InvokeRequired)
                this.Invoke(action);
            else
                action();
        }

        // Opens a file in Notepad; logs a warning if the file does not exist or the process fails to start
        private static void OpenFileInNotepad(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string notepadExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
                    var startInfo = new ProcessStartInfo(notepadExe, $"\"{filePath}\"")
                    {
                        UseShellExecute = true
                    };
                    Process.Start(startInfo)?.Dispose();
                }
                else
                {
                    LogManager.Instance.LogMessage($"Failed to open file in Notepad: file not found ({filePath})", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to open file in Notepad: {ex.Message}", LogLevel.Warn);
            }
        }

        // P/Invoke declarations
        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool DestroyIcon(IntPtr hIcon);
        }
    }
}

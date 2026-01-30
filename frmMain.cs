using Microsoft.Win32;
using System.Diagnostics;

namespace qbPortWeaver
{
    public partial class frmMain : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        // Constants
        const string APP_NAME = "qbPortWeaver";
        const string APP_VERSION = "1.1.0";
        const string LOG_FILE_NAME = "qbPortWeaver.log";
        const string INI_FILE_NAME = "qbPortWeaver.ini";

        // Get logfile, inifile and ProtonVPN paths
        string logFilePath = GetLogFilePath();
        string iniFilePath = GetIniFilePath();
        string protonVPNLogFilePath = GetProtonVPNLogFilePath();

        // Initialize port update count
        int portUpdateCount = 0;

        // Cancellation token to interrupt waiting
        CancellationTokenSource delayCancel = new CancellationTokenSource();

        // Manual update triggered flag 
        bool manualUpdateTriggered = false;

        // Declare LogManager instance
        private LogManager logManager;

        // Constructor
        public frmMain()
        {
            InitializeComponent();

            // Initialize LogManager
            logManager = new LogManager(logFilePath);

            InitializeTrayIcon();
            UpdateTrayTooltip();
        }

        // Main load event
        private void frmMain_Load(object sender, EventArgs e)
        {
            // Start minimized and hide from taskbar
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;

            // Start main loop
            Task.Run(async () =>
            {
                while (true)
                {
                    string updateIntervalSeconds;

                    if (manualUpdateTriggered)
                    {
                        // Skip loop-triggered update once
                        manualUpdateTriggered = false;

                        // 10 seconds wait after manual update and back to configured interval
                        updateIntervalSeconds = "10";
                    }
                    else
                    {
                        // Run port update check
                        updateIntervalSeconds = await CheckForPortUpdate();
                    }

                    // Parsing update interval, defaulting to 180 seconds if invalid
                    int updateInterval;
                    if (!int.TryParse(updateIntervalSeconds, out updateInterval))
                    {
                        // Handle invalid value, default to 180 seconds
                        updateInterval = 180;
                    }
                    
                    // Converting seconds to milliseconds for Task.Delay
                    updateInterval = updateInterval * 1000;

                    // Wait for the specified interval or until cancellation
                    try
                    {
                        logManager.LogMessage($"Waiting for {updateIntervalSeconds} seconds", "INFO");
                        await Task.Delay(updateInterval, delayCancel.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        // Normal when manual update interrupts delay
                        logManager.LogMessage("Delay interrupted by manual update, resuming in 10 seconds", "INFO");
                    }
                    // Reset token for next loop iteration
                    delayCancel.Dispose();
                    delayCancel = new CancellationTokenSource();
                }
            });
        }

        // Main port update logic, returns updateIntervalSeconds
        private async Task<string> CheckForPortUpdate()
        {
            try
            {
                // Check and delete log file if exceeds size limit
                logManager.CheckAndDeleteLogFile();

                // Starting
                logManager.LogMessage($"Starting {APP_NAME} {APP_VERSION}", "INFO");

                // Must initialize updateIntervalSeconds to avoid uninitialized variable error
                string updateIntervalSeconds = "180";

                // Loading INI file and creating default if missing
                var oIniFile = new IniFileManager(iniFilePath);
                if (!oIniFile.Load())
                {
                    logManager.LogMessage($"Failed to load INI file: {iniFilePath}", "ERROR");
                    logManager.LogMessage($"Creating default INI file: {iniFilePath}", "INFO");
                    if (!oIniFile.CreateIniFileIfMissing())
                    {
                        logManager.LogMessage($"Failed to create default INI file: {iniFilePath}", "ERROR");
                    }
                    else
                    {
                        logManager.LogMessage($"Successfully created default INI file: {iniFilePath}", "INFO");
                    }
                    return updateIntervalSeconds;
                }
                else
                {
                    logManager.LogMessage($"Successfully loaded: {iniFilePath}", "INFO");
                }

                // Reading configuration values
                updateIntervalSeconds = oIniFile.GetValue("general", "updateIntervalSeconds");
                string qBittorrentURL = oIniFile.GetValue("qBittorrent", "qBittorrentURL");
                string qBittorrentUserName = oIniFile.GetValue("qBittorrent", "qBittorrentUserName");
                string qBittorrentPassword = oIniFile.GetValue("qBittorrent", "qBittorrentPassword");
                string qBittorrentExePath = oIniFile.GetValue("qBittorrent", "qBittorrentExePath");
                string qBittorrentProcessName = oIniFile.GetValue("qBittorrent", "qBittorrentProcessName");
                string restartqBittorrent = oIniFile.GetValue("qBittorrent", "restartqBittorrent");

                // Creating ProtonVPN manager instance
                var oProtonVPN = new ProtonVPNManager(protonVPNLogFilePath);

                // Checking if ProtonVPN is connected
                if (!oProtonVPN.IsProtonVPNConnected())
                {
                    logManager.LogMessage("Completed: ProtonVPN is not connected", "ERROR");
                    return updateIntervalSeconds;
                }
                else
                {
                    logManager.LogMessage("ProtonVPN is connected", "INFO");
                }

                // Proton VPN is connected, now we can proceed to get the port
                int? ProtonVPNport = oProtonVPN.GetProtonVPNPort();
                if (!ProtonVPNport.HasValue)
                {
                    logManager.LogMessage("Completed: Could not determine ProtonVPN's port", "ERROR");
                    return updateIntervalSeconds;
                }
                else
                {
                    logManager.LogMessage($"ProtonVPN's port found in logfile: {ProtonVPNport.Value}", "INFO");
                }

                // Creating qBittorrent manager instance
                var oqBittorrent = new qBittorrentManager(qBittorrentURL, qBittorrentUserName, qBittorrentPassword, qBittorrentProcessName, qBittorrentExePath);

                // Checking if qBittorrent is running
                if (!oqBittorrent.IsRunning())
                {
                    logManager.LogMessage("Completed: qBittorrent is not running", "ERROR");
                    return updateIntervalSeconds;
                }
                else
                {
                    logManager.LogMessage("qBittorrent is running", "INFO");
                }

                // Getting current listening port
                int? qBittorrentPort = await oqBittorrent.GetListeningPortAsync();
                if (!qBittorrentPort.HasValue)
                {
                    logManager.LogMessage("Completed: Could not determine current qBittorrent's port", "ERROR");
                    return updateIntervalSeconds;
                }
                else
                {
                    logManager.LogMessage($"Current port found in qBittorrent's configuration: {qBittorrentPort.Value}", "INFO");
                }

                // Comparing ports and updating if necessary
                if (qBittorrentPort.Value != ProtonVPNport.Value)
                {
                    logManager.LogMessage($"Ports do not match, updating qBittorrent's port to: {ProtonVPNport.Value}", "INFO");
                    bool setPortResult = await oqBittorrent.SetListeningPortAsync(ProtonVPNport.Value);
                    if (!setPortResult)
                    {
                        logManager.LogMessage($"Completed: Failed to set qBittorrent's port to: {ProtonVPNport.Value}", "ERROR");
                        return updateIntervalSeconds;
                    }
                    else
                    {
                        logManager.LogMessage($"Successfully set qBittorrent's port to: {ProtonVPNport.Value}", "INFO");
                        // Incrementing port update count
                        portUpdateCount++;
                        UpdateTrayTooltip();
                    }

                    // Restarting qBittorrent, defaulting to true if invalid value
                    bool shouldRestart;
                    if (!bool.TryParse(restartqBittorrent, out shouldRestart))
                    {
                        shouldRestart = true;
                    }

                    if (shouldRestart)
                    {
                        logManager.LogMessage("Restarting qBittorent", "INFO");
                        bool restartResult = oqBittorrent.Restart();
                        if (!restartResult)
                        {
                            logManager.LogMessage("Completed: Failed to restart qBittorrent", "ERROR");
                            return updateIntervalSeconds;
                        }
                        else
                        {
                            logManager.LogMessage("Successfully restarted qBittorrent", "INFO");
                        }
                    }
                }
                else
                {
                    logManager.LogMessage("Ports match, no update needed", "INFO");
                }
                logManager.LogMessage("Completed successfully", "INFO");
                return updateIntervalSeconds;
            }
            catch (Exception ex)
            {
                logManager.LogMessage($"Completed: An unexpected error occurred: {ex.Message}", "ERROR");
                return "180";
            }
        }

        // Open received file in notepad.exe
        public static void OpenFileInNotepad(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var psi = new ProcessStartInfo("notepad.exe", $"\"{filePath}\"")
                    {
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                else
                {
                    Debug.WriteLine($"File not found: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open: {filePath} in Notepad: {ex.Message}");
            }
        }


        // Returns ProtonVPN logfile path
        public static string GetProtonVPNLogFilePath()
        {
            string fullpath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Proton",
                "Proton VPN",
                "Logs",
                "client-logs.txt"
            );
            return fullpath;
        }

        // Returns INI file path
        public static string GetIniFilePath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                APP_NAME
            );
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, INI_FILE_NAME);
        }

        // Returns logfile path
        public static string GetLogFilePath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                APP_NAME
            );
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, LOG_FILE_NAME);
        }

        private ToolStripMenuItem autoStartMenuItem;

        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();

            // Adding update now option
            trayMenu.Items.Add("Update Now", null, UpdateNow_Click);

            // Adding show logs
            trayMenu.Items.Add("Show Logs", null, (s, e) => OpenFileInNotepad(GetLogFilePath()));

            // Adding show configuration (INI file)
            trayMenu.Items.Add("Show Configuration", null, (s, e) => OpenFileInNotepad(GetIniFilePath()));

            // Add option to start automatically with Windows
            autoStartMenuItem = new ToolStripMenuItem("Start Automatically with Windows")
            {
                CheckOnClick = true,
                Checked = IsStartupEnabled()
            };
            autoStartMenuItem.Click += (s, e) =>
            {
                SetStartup(autoStartMenuItem.Checked);
            };
            trayMenu.Items.Add(autoStartMenuItem);

            // Add exit option
            trayMenu.Items.Add("Exit", null, Exit_Click);

            // Create tray icon
            trayIcon = new NotifyIcon
            {
                Icon = Properties.Resources.qbPortWeaver,
                Text = APP_NAME + " " + APP_VERSION,
                Visible = true,
                ContextMenuStrip = trayMenu
            };
        }

        public static void SetStartup(bool enable)
        {
            string appName = APP_NAME;
            string exePath = Application.ExecutablePath;

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (enable)
                    key.SetValue(appName, exePath);
                else
                    key.DeleteValue(appName, false);
            }
        }

        public static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                return key.GetValue(APP_NAME) != null;
            }
        }

        // Manual update now click event
        private async void UpdateNow_Click(object sender, EventArgs e)
        {
            // Optional: show temporary notification
            trayIcon.ShowBalloonTip(750, APP_NAME, "Updating port...", ToolTipIcon.Info);

            // Set manual update flag to true
            manualUpdateTriggered = true;

            // Log manual update request
            logManager.LogMessage("Manual update requested", "INFO");

            // Interrupt the wait inside the main loop immediately
            delayCancel.Cancel();

            // Perform port update
            string updateIntervalSeconds = await CheckForPortUpdate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            // Minimize to tray
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.ShowInTaskbar = false;
            }
        }

        private void Exit_Click(object sender, EventArgs e)
        {
            trayIcon.Visible = false; // Hide tray icon before exit
            Application.Exit();
        }

        // Update tray tooltip with current status
        private void UpdateTrayTooltip()
        {
            trayIcon.Text = $"{APP_NAME} {APP_VERSION}\nPort updates: {portUpdateCount}";
        }

    }
}

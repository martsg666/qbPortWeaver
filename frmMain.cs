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
        const string APP_VERSION = "1.0.0";
        const string LOG_FILE_NAME = "qbPortWeaver.log";
        const string INI_FILE_NAME = "qbPortWeaver.ini";

        // Constructor
        public frmMain()
        {
            InitializeComponent();
            InitializeTrayIcon();
        }

        // Main load event
        private void frmMain_Load(object sender, EventArgs e)
        {
            // Start minimized and hide from taskbar
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;

            // Get logfile, inifile and ProtonVPN paths
            string logFilePath = GetLogFilePath();
            string iniFilePath = GetIniFilePath();
            string protonVPNLogFilePath = GetProtonVPNLogFilePath();

            // Start main loop
            Task.Run(async () =>
            {
                while (true)
                {
                    // Check and delete log file if exceeds size limit
                    CheckAndDeleteLogFile(logFilePath);

                    // Starting
                    LogMessage(logFilePath, $"Starting {APP_NAME} {APP_VERSION}", "INFO");

                    // Must initialize updateIntervalSeconds to avoid uninitialized variable error
                    string updateIntervalSeconds = "180";

                    // Loading INI file and creating default if missing
                    var oIniFile = new IniFileManager(iniFilePath);
                    if (!oIniFile.Load())
                    {
                        LogMessage(logFilePath, $"Failed to load INI file: {iniFilePath}", "ERROR");
                        LogMessage(logFilePath, $"Creating default INI file: {iniFilePath}", "INFO");
                        if (!oIniFile.CreateIniFileIfMissing())
                        {
                            LogMessage(logFilePath, $"Failed to create default INI file: {iniFilePath}", "ERROR");
                        }
                        else
                        {
                            LogMessage(logFilePath, $"Successfully created default INI file: {iniFilePath}", "INFO");
                        }
                        goto EndLoop;
                    }
                    else
                    {
                        LogMessage(logFilePath, $"Successfully loaded: {iniFilePath}", "INFO");
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
                        LogMessage(logFilePath, "ProtonVPN is not connected", "ERROR");
                        goto EndLoop;
                    }
                    else
                    {
                        LogMessage(logFilePath, "ProtonVPN is connected", "INFO");
                    }

                    // Proton VPN is connected, now we can proceed to get the port
                    int? ProtonVPNport = oProtonVPN.GetProtonVPNPort();
                    if (!ProtonVPNport.HasValue)
                    {
                        LogMessage(logFilePath, "Could not determine ProtonVPN's port", "ERROR");
                        goto EndLoop;
                    }
                    else
                    {
                        LogMessage(logFilePath, $"ProtonVPN's port found in logfile: {ProtonVPNport.Value}", "INFO");
                    }

                    // Creating qBittorrent manager instance
                    var oqBittorrent = new qBittorrentManager(qBittorrentURL, qBittorrentUserName, qBittorrentPassword, qBittorrentProcessName, qBittorrentExePath);

                    // Checking if qBittorrent is running
                    if (!oqBittorrent.IsRunning())
                    {
                        LogMessage(logFilePath, "qBittorrent is not running", "ERROR");
                        goto EndLoop;
                    }
                    else
                    {
                        LogMessage(logFilePath, "qBittorrent is running", "INFO");
                    }

                    // Getting current listening port
                    int? qBittorrentPort = await oqBittorrent.GetListeningPortAsync();
                    if (!qBittorrentPort.HasValue)
                    {
                        LogMessage(logFilePath, "Could not determine current qBittorrent's port", "ERROR");
                        goto EndLoop;
                    }
                    else
                    {
                        LogMessage(logFilePath, $"Current port found in qBittorrent's configuration: {qBittorrentPort.Value}", "INFO");
                    }

                    // Comparing ports and updating if necessary
                    if (qBittorrentPort.Value != ProtonVPNport.Value)
                    {
                        LogMessage(logFilePath, $"Ports do not match, updating qBittorrent's port to: {ProtonVPNport.Value}", "INFO");
                        bool setPortResult = await oqBittorrent.SetListeningPortAsync(ProtonVPNport.Value);
                        if (!setPortResult)
                        {
                            LogMessage(logFilePath, $"Failed to set qBittorrent's port to: {ProtonVPNport.Value}", "ERROR");
                            goto EndLoop;
                        }
                        else
                        {
                            LogMessage(logFilePath, $"Successfully set qBittorrent's port to: {ProtonVPNport.Value}", "INFO");
                        }

                        // Restarting qBittorrent, defaulting to true if invalid value
                        bool shouldRestart;
                        if (!bool.TryParse(restartqBittorrent, out shouldRestart))
                        {
                            shouldRestart = true;
                        }

                        if (shouldRestart)
                        {
                            LogMessage(logFilePath, "Restarting qBittorent", "INFO");
                            bool restartResult = oqBittorrent.Restart();
                            if (!restartResult)
                            {
                                LogMessage(logFilePath, "Failed to restart qBittorrent", "ERROR");
                                goto EndLoop;
                            }
                            else
                            {
                                LogMessage(logFilePath, "Successfully restarted qBittorrent", "INFO");
                            }
                        }
                    }
                    else
                    {
                        LogMessage(logFilePath, "Ports match, no update needed", "INFO");
                    }

                EndLoop:
                    // Parsing update interval, defaulting to 180 seconds if invalid
                    int updateInterval;
                    if (!int.TryParse(updateIntervalSeconds, out updateInterval))
                    {
                        // Handle invalid value, default to 180 seconds
                        updateInterval = 180;
                    }
                    LogMessage(logFilePath, "Completed", "INFO");
                    LogMessage(logFilePath, $"Waiting for: {updateInterval} seconds", "INFO");
                    // Converting seconds to milliseconds for Task.Delay
                    updateInterval = updateInterval * 1000;
                    await Task.Delay(updateInterval);
                }
            });
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

        // Check log file size and delete if exceeds 5 MB
        public static void CheckAndDeleteLogFile(string logFilePath)
        {
            try
            {
                if (File.Exists(logFilePath))
                {
                    FileInfo fileInfo = new FileInfo(logFilePath);

                    // Taille limite en octets (5 Mo = 5 * 1024 * 1024)
                    long maxSize = 5 * 1024 * 1024;

                    if (fileInfo.Length > maxSize)
                    {
                        File.Delete(logFilePath);
                        Debug.WriteLine($"Log file deleted because it exceeded 5 MB: {logFilePath}");
                    }
                    else
                    {
                        Debug.WriteLine($"Log file size is OK: {fileInfo.Length} bytes");
                    }
                }
                else
                {
                    Debug.WriteLine($"Log file does not exist: {logFilePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking or deleting log file: {ex.Message}");
            }
        }


        // Logging message to logfile
        public static void LogMessage(string logFilePath, string message, string type)
        {
            try
            {
                type = type.PadRight(5);
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {type} | {message}{Environment.NewLine}";
                File.AppendAllText(logFilePath, logEntry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write to logfile: {ex.Message}");
            }
        }

        private ToolStripMenuItem autoStartMenuItem;

        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();

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
    }
}

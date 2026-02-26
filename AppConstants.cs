using System.Diagnostics;

namespace qbPortWeaver
{
    public static class AppConstants
    {
        // Application metadata
        public const string APP_NAME = "qbPortWeaver";
        public const string APP_VERSION = "2.2.0";

        // Timing
        public const int DEFAULT_UPDATE_INTERVAL_SECONDS = 180;
        public const int MANUAL_SYNC_WAIT_SECONDS = 10;
        public const int MILLISECONDS_PER_SECOND = 1000;
        public const int AUTO_UPDATE_CHECK_INTERVAL_MS = 12 * 60 * 60 * MILLISECONDS_PER_SECOND;

        // UI
        public const int MAX_TOOLTIP_LENGTH = 63;
        public const int BALLOON_TIP_DURATION_MS = 750;

        // GitHub — only the owner is a literal; all URLs are derived
        public const string GITHUB_REPO_OWNER = "martsg666";
        public static readonly string GITHUB_REPO_URL = $"https://github.com/{GITHUB_REPO_OWNER}/{APP_NAME}";

        private const string LOG_FILE_NAME    = "qbPortWeaver.log";
        private const string STATUS_FILE_NAME = "qbPortWeaver.status.json";

        // App data folder, created once on first access
        private static readonly string _appDataFolder = Directory.CreateDirectory(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), APP_NAME)
        ).FullName;

        public static string GetLogFilePath()      => Path.Combine(_appDataFolder, LOG_FILE_NAME);
        public static string GetStatusFilePath()   => Path.Combine(_appDataFolder, STATUS_FILE_NAME);

        public static string GetProtonVPNLogFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Proton", "Proton VPN", "Logs", "client-logs.txt"
        );

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
                    Process.Start(psi)?.Dispose();
                }
                else
                {
                    LogManager.Instance.LogMessage($"Could not open file in Notepad, file not found: {filePath}", "WARN");
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to open file in Notepad: {ex.Message}", "WARN");
            }
        }
    }
}

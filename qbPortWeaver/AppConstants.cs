using System.Diagnostics;

namespace qbPortWeaver
{
    public static class AppConstants
    {
        // Application metadata
        public const string AppName = "qbPortWeaver";
        public static readonly string AppVersion =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        // Timing
        public const int DefaultUpdateIntervalSeconds = 180;
        public const int MinUpdateIntervalSeconds     = 10;
        public const int ManualSyncWaitSeconds        = 10;
        public const int MillisecondsPerSecond        = 1000;
        public const int AutoUpdateCheckIntervalMs    = 12 * 60 * 60 * MillisecondsPerSecond;

        // UI
        public const int MaxTooltipLength  = 63;
        public const int BalloonTipDurationMs = 750;

        // HTTP — shared timeout used by all outbound HTTP clients
        public const int HttpTimeoutSeconds = 10;

        // Named pipe used to communicate with the SYSTEM helper service for session 0 actions.
        // Must match HelperPipeServer.PipeName in qbPortWeaver.HelperService.
        public const string HelperServicePipeName = "qbPortWeaverHelper";

        // GitHub — only the owner is a literal; all URLs are derived
        public const string GitHubRepoOwner = "martsg666";
        public static readonly string GitHubRepoUrl = $"https://github.com/{GitHubRepoOwner}/{AppName}";

        private const string LogFileName    = "qbPortWeaver.log";
        private const string StatusFileName = "qbPortWeaver.status.json";

        private static string? _appDataFolder;

        private static string AppDataFolder => _appDataFolder ??= Directory.CreateDirectory(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName)
        ).FullName;

        public static string GetLogFilePath()    => Path.Combine(AppDataFolder, LogFileName);
        public static string GetStatusFilePath() => Path.Combine(AppDataFolder, StatusFileName);

        public static string GetProtonVPNLogFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Proton", "Proton VPN", "Logs", "client-logs.txt"
        );

        // Opens a URL in the default browser using ShellExecute
        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"AppConstants.OpenUrl: failed to open URL: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}

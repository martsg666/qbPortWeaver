using System.Diagnostics;

namespace qbPortWeaver
{
    /// <summary>Application-wide constants, file path helpers, and shared utility methods for qbPortWeaver.</summary>
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

        // HTTP - shared timeout used by all outbound HTTP clients
        public const int HttpTimeoutSeconds = 10;

        // Named pipe used to communicate with the SYSTEM helper service for session 0 actions.
        // Must match HelperPipeServer.PipeName in qbPortWeaver.HelperService.
        public const string HelperServicePipeName = "qbPortWeaverHelper";

        // GitHub - only the owner is a literal; all URLs are derived
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

        /// <summary>
        /// Kills a process (including its entire process tree) and waits up to <paramref name="timeoutMs"/> for exit.
        /// Escalation: <c>Process.Kill</c> → wait → <c>taskkill /F /T</c> → retry <c>Process.Kill</c>
        /// (handles processes that resist .NET's TerminateProcess, e.g. qBittorrent during active I/O).
        /// Returns <see langword="true"/> if the process exited (or had already exited), <see langword="false"/> if it may still be running.
        /// </summary>
        public static bool KillProcess(Process process, int timeoutMs = 5000)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return true; // already exited
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // access denied or process protected
            }
            if (process.WaitForExit(timeoutMs)) return true;

            // Process.Kill failed to terminate in time - fall back to taskkill /F /T
            try
            {
                using var taskkill = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                    $"/F /T /PID {process.Id}")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });
                taskkill?.WaitForExit(timeoutMs);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"AppConstants.KillProcess: taskkill fallback failed: {ex.Message}");
            }
            if (process.WaitForExit(timeoutMs)) return true;

            // Last resort: retry Process.Kill after taskkill may have weakened the process tree
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            return process.WaitForExit(timeoutMs);
        }

        /// <summary>Opens a URL in the default browser using ShellExecute.</summary>
        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to open URL: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}

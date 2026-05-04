using Microsoft.Win32;
using System.Diagnostics;
using System.ServiceProcess;

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
        public const int MaxTooltipLength  = 63; // NotifyIcon.Text is capped at 63 characters by Windows
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

        /// <summary>Returns the full path to the application log file.</summary>
        public static string GetLogFilePath()    => Path.Combine(AppDataFolder, LogFileName);

        /// <summary>Returns the full path to the application status JSON file.</summary>
        public static string GetStatusFilePath() => Path.Combine(AppDataFolder, StatusFileName);

        /// <summary>Returns the full path for a named data file stored in the application data folder.</summary>
        internal static string GetDataFilePath(string fileName) => Path.Combine(AppDataFolder, fileName);

        /// <summary>Deletes a file if it exists, swallowing IO and permission errors.</summary>
        internal static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path); // no-op if the file does not exist
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogDebug($"AppConstants.TryDeleteFile: Could not delete '{path}': {ex.Message}");
            }
        }

        /// <summary>Writes content to a temp file then atomically renames it over the target.
        /// If the process is killed mid-write, only the .tmp file is lost and the original is untouched.</summary>
        internal static void WriteAtomic(string path, string content)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, content);
            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDeleteFile(temp);
                throw;
            }
        }

        /// <summary>Returns the full path to the ProtonVPN log file, resolved from the registry setting.</summary>
        public static string GetProtonVpnLogFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyProtonVpnLogFilePath));

        /// <summary>
        /// Kills a process (including its entire process tree) and waits up to <paramref name="timeoutMs"/> for exit.
        /// Escalation: <c>Process.Kill</c> → wait → <c>taskkill /F /T</c> → retry <c>Process.Kill</c>
        /// (handles processes that resist .NET's TerminateProcess, e.g. qBittorrent during active I/O).
        /// Returns <see langword="true"/> if the process exited (or had already exited), <see langword="false"/> if it may still be running.
        /// The caller is responsible for disposing <paramref name="process"/>.
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
                using var taskkill = Process.Start(CreateHiddenStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                    $"/F /T /PID {process.Id}"));
                taskkill?.WaitForExit(timeoutMs);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to run taskkill fallback for PID {process.Id}: {ex.Message}", LogLevel.Warn);
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

        /// <summary>Kills all running processes matching <paramref name="processName"/> and logs outcomes per process.</summary>
        internal static void KillProcessesByName(string processName, int killTimeoutMs, string clientName)
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!KillProcess(proc, killTimeoutMs))
                        LogManager.Instance.LogMessage($"{clientName} process (PID {proc.Id}) still running after kill attempts", LogLevel.Warn);
                }
                catch (Exception ex) { LogManager.Instance.LogDebug($"{clientName}.KillProcessesByName: Failed to kill process: {ex.Message}"); }
                finally { proc.Dispose(); }
            }
        }

        /// <summary>
        /// Searches all installed Windows services for one whose <c>ServiceName</c> or <c>DisplayName</c>
        /// contains <paramref name="searchTerm"/> and returns the <c>ServiceName</c>, or
        /// <see langword="null"/> if no match is found.
        /// </summary>
        internal static string? FindServiceName(string searchTerm)
        {
            ServiceController[]? services = null;
            try
            {
                services = ServiceController.GetServices();
                return services
                    .FirstOrDefault(s =>
                        s.ServiceName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                        s.DisplayName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    ?.ServiceName;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"AppConstants.FindServiceName: {ex.Message}");
                return null;
            }
            finally
            {
                if (services is not null)
                    foreach (var s in services) s.Dispose();
            }
        }

        /// <summary>
        /// Reads the <c>ImagePath</c> for the named Windows service from the registry and returns
        /// the directory containing the service executable, or <see langword="null"/> if the
        /// service key is absent or the path cannot be resolved.
        /// Handles quoted paths and trailing arguments: <c>"C:\path\exe.exe" -arg</c>.
        /// </summary>
        internal static string? GetServiceExeDirectory(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                if (key?.GetValue("ImagePath") is not string imagePath) return null;

                imagePath = Environment.ExpandEnvironmentVariables(imagePath.Trim());
                if (imagePath.StartsWith('"'))
                {
                    int end = imagePath.IndexOf('"', 1);
                    imagePath = end > 0 ? imagePath[1..end] : imagePath[1..];
                }
                else
                {
                    int space = imagePath.IndexOf(' ');
                    if (space > 0) imagePath = imagePath[..space];
                }

                return Path.GetDirectoryName(Path.GetFullPath(imagePath));
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"AppConstants.GetServiceExeDirectory: {serviceName} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves an executable path from the directory of a Windows service, caching the result.
        /// Returns <see langword="null"/> if the service or file is not found; the cache remains as
        /// <see cref="string.Empty"/> on any miss or transient error so the next cycle retries.
        /// Only a successful resolution is cached permanently.
        /// </summary>
        internal static string? FindExeInServiceDirectory(ref string? cache, string exeFileName, Func<string?> findServiceName, string logPrefix)
        {
            if (cache != string.Empty) return cache;
            try
            {
                string? serviceName = findServiceName();
                string? serviceDir  = serviceName is not null ? GetServiceExeDirectory(serviceName) : null;
                if (serviceDir is null)
                {
                    LogManager.Instance.LogDebug($"{logPrefix}: service executable directory not found");
                    return null;
                }

                string exePath = Path.Combine(serviceDir, exeFileName);
                if (!File.Exists(exePath))
                {
                    LogManager.Instance.LogDebug($"{logPrefix}: {exeFileName} not found at: {exePath}");
                    return null;
                }

                LogManager.Instance.LogDebug($"{logPrefix}: Found {exeFileName} at: {exePath}");
                return cache = exePath;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"{logPrefix}: {ex.Message}");
                return null; // transient error: cache left as string.Empty so next cycle retries
            }
        }

        /// <summary>Creates a ProcessStartInfo configured to run a hidden, windowless process.</summary>
        public static ProcessStartInfo CreateHiddenStartInfo(string fileName, string arguments) =>
            new(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true };

        // UI helpers

        /// <summary>
        /// Returns <see langword="true"/> if the effective color theme is dark.
        /// Checks <see cref="SystemColors.Control"/> brightness, which reflects the mode
        /// applied by <see cref="Application.SetColorMode"/> at startup
        /// (<see cref="SystemColorMode.System"/>, <see cref="SystemColorMode.Dark"/>, or <see cref="SystemColorMode.Classic"/>).
        /// </summary>
        public static bool IsDarkModeEnabled() =>
            SystemColors.Control.GetBrightness() < 0.5f;

        public static readonly Color DarkModeBackground      = Color.FromArgb(30,  30,  30);
        public static readonly Color DarkModeBorder          = Color.FromArgb(80,  80,  80);
        public static readonly Color DarkModeSecondaryText   = Color.FromArgb(160, 160, 160);
        public static readonly Color DarkModeCheckedBack     = Color.FromArgb(55,  55,  55);
        public static readonly Color DarkModeSearchHighlight = Color.FromArgb(100, 85,  0);
        public static readonly Color LightModeDimmed         = Color.FromArgb(180, 180, 180);
        public static readonly Color LightModeCheckedBack    = Color.FromArgb(225, 225, 235);
        public static readonly Color TrayIconDotBorder       = Color.FromArgb(60,  60,  60);

        // Text and link colors
        public static readonly Color DarkModeText        = Color.Gainsboro;
        public static readonly Color DarkModeLinkColor   = Color.CornflowerBlue;
        public static readonly Color DarkModeMeta        = Color.DimGray;
        public static readonly Color LightModeSearchHighlight = Color.Yellow;

        // Severity / confidence level colors (paired dark/light)
        public static readonly Color DarkModeError       = Color.OrangeRed;
        public static readonly Color LightModeError      = Color.Crimson;
        public static readonly Color DarkModeWarning     = Color.Gold;
        public static readonly Color LightModeWarning    = Color.Goldenrod;
        public static readonly Color DarkModeInfo        = Color.DodgerBlue;
        public static readonly Color LightModeInfo       = Color.SteelBlue;
        public static readonly Color LogLevelDebug       = Color.DarkOrange;     // same in both modes

        // Status indicator colors (tray icon dots and status labels)
        public static readonly Color StatusOk            = Color.LimeGreen;      // tray dot and dark mode label
        public static readonly Color StatusOkLight       = Color.Green;          // light mode label
        public static readonly Color StatusWarning       = Color.Orange;         // tray dot and dark mode label
        public static readonly Color StatusWarningLight  = Color.DarkOrange;     // light mode label
        public static readonly Color StatusError         = Color.Red;            // tray dot

        /// <summary>Opens a URL in the default browser using ShellExecute.</summary>
        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to open URL '{url}': {ex.Message}", LogLevel.Warn);
            }
        }
    }
}

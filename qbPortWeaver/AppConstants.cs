using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace qbPortWeaver;

/// <summary>Application-wide constants, file path helpers, and shared utility methods for qbPortWeaver.</summary>
public static class AppConstants
{
    // Application metadata
    public static readonly string AppVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // Timing
    public const int DefaultUpdateIntervalSeconds = 180;
    public const int MinUpdateIntervalSeconds = 10;
    public const int ManualSyncWaitSeconds = 10;
    public const int MillisecondsPerSecond = 1000;
    public const int AutoUpdateCheckIntervalMs = 12 * 60 * 60 * MillisecondsPerSecond;
    // Debounce window for network-change triggered re-syncs. NetworkAddressChanged fires in
    // bursts when an adapter comes up, so a single reconnect is coalesced into one wake.
    public const int ResyncDebounceMs = 2500;

    // UI
    public const int MaxTooltipLength = 127; // NotifyIcon.Text max in modern Windows / .NET (the historic 63-char limit was pre-Windows 2000)
    // Passed as the timeout to NotifyIcon.ShowBalloonTip. Modern Windows ignores this value -
    // the OS controls the display duration (and routes Win11 toasts through Action Center) - so
    // it is only a nominal hint; changing it does not alter how long a notification stays up.
    public const int BalloonTipDurationMs = 750;

    // HTTP - shared timeout used by all outbound HTTP clients
    public const int HttpTimeoutSeconds = 10;

    // Upper bound for a user-initiated client test (Settings connection test or Status-panel port
    // reachability check). Above HttpTimeoutSeconds to allow for the auth handshake or the external
    // port-check round trip. Shared so both test paths time out consistently.
    public const int ClientTestTimeoutSeconds = 20;

    // GitHub - only the owner is a literal; all URLs are derived
    public const string GitHubRepoOwner = "martsg666";
    public static readonly string GitHubRepoUrl = $"https://github.com/{GitHubRepoOwner}/{AppIdentity.AppName}";

    private const string StatusFileName = "qbPortWeaver.status.json";

    private static readonly Lazy<string> _appDataFolder = new(() => Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.AppName)
    ).FullName);

    private static string AppDataFolder => _appDataFolder.Value;

    /// <summary>Returns the full path to the application log file.</summary>
    public static string GetLogFilePath() => Path.Combine(AppDataFolder, AppIdentity.LogFileName);

    /// <summary>Returns the full path to the application status JSON file.</summary>
    public static string GetStatusFilePath() => Path.Combine(AppDataFolder, StatusFileName);

    /// <summary>Returns the full path for a named data file stored in the application data folder.</summary>
    internal static string GetDataFilePath(string fileName) => Path.Combine(AppDataFolder, fileName);

    /// <summary>Deletes a file if it exists, swallowing IO and permission errors. Never throws.</summary>
    internal static void DeleteFileSafely(string path)
    {
        try
        {
            File.Delete(path); // no-op if the file does not exist
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Guarded with IsInitialized because this is a generic file helper reachable from
            // early startup paths before LogManager.Initialize runs. The kill/service helpers
            // below call LogManager.Instance unguarded by design - they only run inside the
            // active sync loop, which cannot start until after initialization.
            if (LogManager.IsInitialized)
                LogManager.Instance.LogDebug($"AppConstants.DeleteFileSafely: Could not delete '{path}': {ex.Message}");
        }
    }

    /// <summary>Writes content to a temp file then atomically renames it over the target.
    /// If the process is killed mid-write, only the .tmp file is lost and the original is untouched.</summary>
    internal static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            DeleteFileSafely(temp);
            throw;
        }
    }

    /// <summary>Returns the full path to the ProtonVPN log file, resolved from the registry setting.</summary>
    public static string GetProtonVpnLogFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyProtonVpnLogFilePath));

    /// <summary>
    /// Kills a process (including its entire process tree) and waits up to <paramref name="timeoutMs"/> for exit.
    /// Escalation lives in <see cref="ProcessKillHelper.KillProcessTreeWithEscalation"/> so the helper
    /// service can use the same logic. This wrapper logs the per-stage outcome at Warn using
    /// <paramref name="contextLabel"/> as the prefix so the user sees what kind of process failed.
    /// Returns <see langword="true"/> if the process exited (or had already exited), <see langword="false"/> if it could not be killed.
    /// The caller is responsible for disposing <paramref name="process"/>.
    /// </summary>
    public static bool KillProcess(Process process, string contextLabel, int timeoutMs = 5000)
    {
        var result = ProcessKillHelper.KillProcessTreeWithEscalation(process, timeoutMs);
        if (result.TaskkillError is not null)
            LogManager.Instance.LogMessage($"Failed to run taskkill fallback for {contextLabel} (PID {process.Id}): {result.TaskkillError.Message}", LogLevel.Warn);

        switch (result.Outcome)
        {
            case ProcessKillOutcome.AccessDenied:
                LogManager.Instance.LogMessage($"{contextLabel} (PID {process.Id}) could not be killed - access denied or process protected", LogLevel.Warn);
                return false;
            case ProcessKillOutcome.StillRunning:
                LogManager.Instance.LogMessage($"{contextLabel} (PID {process.Id}) still running after kill attempts", LogLevel.Warn);
                return false;
            default:
                return true;
        }
    }

    /// <summary>Kills all running processes matching <paramref name="processName"/>; per-process outcome is logged inside <see cref="KillProcess"/>.</summary>
    internal static void KillProcessesByName(string processName, int killTimeoutMs, string clientName)
    {
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            try
            {
                KillProcess(proc, $"{clientName} process", killTimeoutMs);
            }
            catch (Exception ex) { LogManager.Instance.LogDebug($"{clientName}.KillProcessesByName: Failed to kill process: {ex.Message}"); }
            finally { proc.Dispose(); }
        }
    }

    /// <summary>
    /// Searches all installed Windows services for one whose <c>ServiceName</c> or <c>DisplayName</c>
    /// contains <paramref name="searchTerm"/> and returns the <c>ServiceName</c>, or
    /// <see langword="null"/> if no match is found. When multiple services match, the first one
    /// returned by <see cref="ServiceController.GetServices"/> is used; that order is not guaranteed
    /// to be stable across reboots, so callers should pass a precise enough search term that no more
    /// than one service can match.
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
            else if (!File.Exists(imagePath))
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
    /// Returns <see langword="null"/> if the service or file is not found; the cache is left as
    /// <see langword="null"/> or <see cref="string.Empty"/> on any miss or transient error so
    /// the next cycle retries. Only a successful resolution is cached permanently (non-empty string).
    /// Callers may initialize the cache field to either <see langword="null"/> or
    /// <see cref="string.Empty"/> - both are treated as "not yet searched".
    /// </summary>
    internal static string? FindExeInServiceDirectory(ref string? cache, string exeFileName, Func<string?> findServiceName, string logPrefix)
    {
        // cache is { Length: > 0 } means a path was previously resolved - return it.
        // null or empty means "not yet searched" - both proceed to the lookup below.
        if (cache is { Length: > 0 }) return cache;
        try
        {
            string? serviceName = findServiceName();
            string? serviceDir = serviceName is not null ? GetServiceExeDirectory(serviceName) : null;
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
            return null; // transient error: cache left at its prior state (null or empty) so next cycle retries
        }
    }

    // UI helpers

    /// <summary>
    /// Returns <see langword="true"/> if the effective color theme is dark.
    /// Checks <see cref="SystemColors.Control"/> brightness, which reflects the mode
    /// applied by <see cref="Application.SetColorMode"/> at startup
    /// (<see cref="SystemColorMode.System"/>, <see cref="SystemColorMode.Dark"/>, or <see cref="SystemColorMode.Classic"/>).
    /// </summary>
    public static bool IsDarkModeEnabled() =>
        SystemColors.Control.GetBrightness() < 0.5f;

    public static readonly Color DarkModeBackground = Color.FromArgb(30, 30, 30);
    public static readonly Color DarkModeBorder = Color.FromArgb(80, 80, 80);
    public static readonly Color DarkModeSecondaryText = Color.FromArgb(160, 160, 160);
    public static readonly Color DarkModeCheckedBack = Color.FromArgb(55, 55, 55);
    public static readonly Color DarkModeSearchHighlight = Color.FromArgb(100, 85, 0);
    public static readonly Color LightModeDimmed = Color.FromArgb(180, 180, 180);
    public static readonly Color LightModeCheckedBack = Color.FromArgb(225, 225, 235);
    public static readonly Color TrayIconDotBorder = Color.FromArgb(60, 60, 60);

    // Text and link colors
    public static readonly Color DarkModeText = Color.Gainsboro;
    public static readonly Color DarkModeLinkColor = Color.CornflowerBlue;
    public static readonly Color DarkModeMeta = Color.DimGray;
    public static readonly Color LightModeSearchHighlight = Color.Yellow;

    // Severity / confidence level colors (paired dark/light)
    public static readonly Color DarkModeError = Color.OrangeRed;
    public static readonly Color LightModeError = Color.Crimson;
    public static readonly Color DarkModeWarning = Color.Gold;
    public static readonly Color LightModeWarning = Color.Goldenrod;
    public static readonly Color DarkModeInfo = Color.DodgerBlue;
    public static readonly Color LightModeInfo = Color.SteelBlue;
    public static readonly Color LogLevelDebug = Color.DarkOrange;     // same in both modes

    // Status indicator colors (tray icon dots and status labels)
    public static readonly Color StatusOk = Color.LimeGreen;      // tray dot and dark mode label
    public static readonly Color StatusOkLight = Color.Green;          // light mode label
    public static readonly Color StatusWarning = Color.Orange;         // tray dot and dark mode label
    public static readonly Color StatusWarningLight = Color.DarkOrange;     // light mode label
    public static readonly Color StatusError = Color.Red;            // tray dot
    public static readonly Color StatusPaused = Color.Gray;          // tray dot - syncing paused by the user (intentionally idle)

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

    /// <summary>Copies text to the clipboard, swallowing the transient <see cref="ExternalException"/> thrown
    /// when another process holds the clipboard open (clipboard managers, RDP). Empty text is replaced with a
    /// single space because <see cref="Clipboard.SetText(string)"/> rejects an empty string.</summary>
    public static void TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text);
        }
        catch (ExternalException ex)
        {
            LogManager.Instance.LogDebug($"AppConstants.TrySetClipboardText: Clipboard unavailable: {ex.Message}");
        }
    }

    /// <summary>Returns clipboard text, or <see langword="null"/> when the clipboard holds no text or is transiently locked by another process.</summary>
    public static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException ex)
        {
            LogManager.Instance.LogDebug($"AppConstants.TryGetClipboardText: Clipboard unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns <see langword="true"/> if the clipboard contains text; <see langword="false"/> if empty or transiently locked by another process.</summary>
    public static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (ExternalException ex)
        {
            LogManager.Instance.LogDebug($"AppConstants.ClipboardHasText: Clipboard unavailable: {ex.Message}");
            return false;
        }
    }
}

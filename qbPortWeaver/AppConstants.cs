using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Drawing2D;
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

    // Upper bound for a full diagnostics run. It chains several probes (VPN port, client auth, port
    // reachability), each already individually bounded, so the total needs more headroom than a single test.
    public const int DiagnosticsTimeoutSeconds = 60;

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
    /// </summary>
    internal static string? GetServiceExeDirectory(string serviceName)
    {
        string? exePath = GetServiceExePath(serviceName);
        return exePath is null ? null : Path.GetDirectoryName(exePath);
    }

    /// <summary>
    /// Reads the <c>ImagePath</c> for the named Windows service from the registry and returns the
    /// full path to the service executable, or <see langword="null"/> if the service key is absent
    /// or the path cannot be resolved. Handles quoted paths and trailing arguments: <c>"C:\path\exe.exe" -arg</c>.
    /// </summary>
    internal static string? GetServiceExePath(string serviceName)
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

            return Path.GetFullPath(imagePath);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"AppConstants.GetServiceExePath: {serviceName} - {ex.Message}");
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

    // Theme accent colors. Surfaces and body text follow the OS theme natively (Application.SetColorMode
    // + SystemColors); only these semantic accents are defined here. Accents that need a different shade
    // per mode - for contrast against the mode-aware background - are paired with a Dark and a Light
    // variant; the rest look the same in both modes. Naming: <Group><Semantic>[Dark|Light].

    // Search-match highlight background (log viewer)
    public static readonly Color SearchHighlightDark  = Color.FromArgb(100, 85, 0);
    public static readonly Color SearchHighlightLight = Color.Yellow;

    // Link text (dark only; light mode uses the LinkLabel default blue)
    public static readonly Color LinkDark = Color.CornflowerBlue;

    // Log viewer per-level line colors
    public static readonly Color LogErrorDark    = Color.OrangeRed;
    public static readonly Color LogErrorLight   = Color.Crimson;
    public static readonly Color LogWarningDark  = Color.Gold;
    public static readonly Color LogWarningLight = Color.Goldenrod;
    public static readonly Color LogInfoDark     = Color.DodgerBlue;
    public static readonly Color LogInfoLight    = Color.SteelBlue;
    public static readonly Color LogDebug        = Color.DarkOrange; // same in both modes

    // Status colors: tray icon dots (mode-independent - use the vivid Dark variant) and status labels
    public static readonly Color StatusOkDark       = Color.LimeGreen;
    public static readonly Color StatusOkLight      = Color.Green;
    public static readonly Color StatusWarningDark  = Color.Orange;
    public static readonly Color StatusWarningLight = Color.DarkOrange;
    public static readonly Color StatusError        = Color.Red;  // same in both modes
    public static readonly Color StatusPaused       = Color.Gray; // same in both modes; tray dot only

    // Mode-resolved accents: each returns the Dark or Light variant for the active OS theme, so callers
    // pick the right shade without hand-writing the dark/light ternary. Accents with a single
    // mode-independent value (LogDebug, StatusError, StatusPaused) need no resolver.
    public static Color SearchHighlight => IsDarkModeEnabled() ? SearchHighlightDark : SearchHighlightLight;
    public static Color LogError        => IsDarkModeEnabled() ? LogErrorDark : LogErrorLight;
    public static Color LogWarning      => IsDarkModeEnabled() ? LogWarningDark : LogWarningLight;
    public static Color LogInfo         => IsDarkModeEnabled() ? LogInfoDark : LogInfoLight;
    public static Color StatusOk        => IsDarkModeEnabled() ? StatusOkDark : StatusOkLight;
    public static Color StatusWarning   => IsDarkModeEnabled() ? StatusWarningDark : StatusWarningLight;

    // Vivid, mode-independent colors for the tray status dots (the taskbar's brightness is independent of
    // the app theme, so a dot must look the same in both modes). Used as a complete set so a tray dot
    // never reaches for the mode-resolved StatusOk/StatusWarning label accessors above by mistake: OK and
    // Warning take the vivid Dark accents; Error and Paused pass through the single-value colors.
    public static Color TrayDotOk      => StatusOkDark;
    public static Color TrayDotWarning => StatusWarningDark;
    public static Color TrayDotError   => StatusError;
    public static Color TrayDotPaused  => StatusPaused;

    // Tray icon dot border
    public static readonly Color TrayIconDotBorder = Color.FromArgb(60, 60, 60);

    /// <summary>
    /// Owner-draws a crisp up/down chevron centered in a nav button, in the button's ForeColor.
    /// Drawn instead of a font glyph so it is always centered and its size/weight are exact.
    /// Shared by the log viewer's and help viewer's search-nav Paint handlers.
    /// </summary>
    public static void DrawNavChevron(Button btn, Graphics g, bool up)
    {
        float scale = btn.DeviceDpi / 96f;
        float halfW = 5f * scale;     // chevron half-width
        float halfH = 3.25f * scale;  // chevron half-height
        float cx = btn.ClientSize.Width / 2f;
        float cy = btn.ClientSize.Height / 2f;
        float armY  = up ? cy + halfH : cy - halfH; // the two ends
        float apexY = up ? cy - halfH : cy + halfH; // the point

        PointF[] chevron = [new(cx - halfW, armY), new(cx, apexY), new(cx + halfW, armY)];

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(btn.ForeColor, 1.8f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, chevron);
    }

    /// <summary>
    /// Lays out the right-aligned search group shared by the log viewer and help viewer toolbars:
    /// the search box, match counter, and prev/next nav buttons pinned to the toolbar's right edge,
    /// with the clear (×) button floating inside the search box. Positions are computed from the
    /// toolbar's actual client width (not designer coordinates) so the group lands correctly whatever
    /// the form padding or DPI. <paramref name="rightMargin"/> is the logical-pixel gap between the
    /// last button and the toolbar's right edge - 0 when the form's own padding already supplies the
    /// visual margin (help viewer), or the desired inset when it does not (log viewer). Vertical
    /// centering uses the search box's font-driven height, known only after layout, so call from OnLoad.
    /// </summary>
    public static void LayoutSearchToolbar(Control toolbar, TextBox search, Label matchCount, Button prev, Button next, Button clear, int rightMargin)
    {
        int gap = toolbar.LogicalToDeviceUnits(4);
        int margin = toolbar.LogicalToDeviceUnits(rightMargin);
        int top = (toolbar.Height - search.Height) / 2;

        // Right-aligned group, positioned from the toolbar's right edge inward.
        next.Left = toolbar.ClientSize.Width - margin - next.Width;
        prev.Left = next.Left - prev.Width;
        matchCount.Left = prev.Left - gap - matchCount.Width;
        search.Left = matchCount.Left - gap - search.Width;

        // Vertically center the group on the search box; the nav buttons match its height.
        search.Top = top;
        prev.Height = next.Height = search.Height;
        prev.Top = next.Top = top;
        matchCount.Top = top + (search.Height - matchCount.Height) / 2;

        // Clear button floats inside the right interior of the search box. The 4px inset shrinks it to
        // fit within the box's 2px top/bottom border; the 2px margin keeps it clear of the right edge.
        int size = search.Height - toolbar.LogicalToDeviceUnits(4);
        int clearMargin = toolbar.LogicalToDeviceUnits(2);
        clear.Size = new Size(size, size);
        clear.Location = new Point(search.Right - size - clearMargin, top + clearMargin);
        clear.BringToFront(); // must sit above the native TextBox HWND or it is hidden behind it
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
            LogManager.Instance.LogMessage($"Failed to open URL '{url}': {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Raises a form to the foreground. The tray-only app has no foreground window, so a form shown
    /// non-modally at startup (What's New, the update prompt) can open behind the window that launched
    /// us; this forces it up. The brief TopMost toggle raises the z-order past the foreground lock
    /// without leaving the window permanently always-on-top.
    /// </summary>
    public static void BringFormToFront(Form form)
    {
        form.BringToFront();
        form.TopMost = true;
        form.TopMost = false;
        form.Activate();
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

namespace qbPortWeaver;

/// <summary>The app's semantic accent colours and the dark-mode test that resolves them.
/// <para>Surfaces and body text follow the OS theme natively; only these accents are defined here.</para></summary>
public static class ThemeColors
{
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
}

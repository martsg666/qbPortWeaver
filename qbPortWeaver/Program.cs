namespace qbPortWeaver;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Route UI-thread event-handler exceptions through Application.ThreadException instead
        // of the OS crash dialog. Must be called before ApplicationConfiguration.Initialize().
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Application.SetColorMode(ReadColorTheme());
        // Route every menu (tray + all context menus) through the system renderer so they follow the
        // app color mode set above. The default professional renderer paints a light gradient that
        // ignores dark mode; setting this once here means no individual ContextMenuStrip has to
        // remember it. Must come after SetColorMode and before any menu is created.
        ToolStripManager.RenderMode = ToolStripManagerRenderMode.System;
        ApplicationConfiguration.Initialize();

        // Enforce single instance per Windows user using a named mutex.
        // Local\ prefix scopes the mutex to the current session so each Windows user can run
        // their own instance (settings, credentials and pipe token are all per-user).
        // Using initiallyOwned: false + WaitOne(0) instead of the initiallyOwned: true constructor
        // overload so that an AbandonedMutexException (thrown when a previous instance crashed
        // without releasing the mutex) can be caught and treated as "we are the new instance".
        // The OS transfers ownership to us when the mutex is abandoned, so the catch is safe.
        using var mutex = new Mutex(false, "Local\\qbPortWeaver_SingleInstance");
        bool isNewInstance;
        try { isNewInstance = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { isNewInstance = true; }

        if (!isNewInstance)
        {
            ThemedMessageBox.Show(
                $"{AppIdentity.AppName} is already running.",
                AppIdentity.AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        // Last line of defence. MainForm's own Load handler already reports failures it can see, but
        // the constructor runs before the message loop exists, so Application.ThreadException cannot
        // reach it - an escape there would otherwise be an OS crash dialog. The very first thing the
        // constructor does is resolve the log file path, which creates %LocalAppData%\qbPortWeaver;
        // if that fails (redirected profile on an unreachable share, permissions, disk full) it
        // throws before LogManager exists, so without this the app would die with no log entry and
        // no explanation. Nothing here can recover the app - it exits either way - but it exits
        // having said why.
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex) // NOSONAR S2221 - deliberate catch-all: the alternative is an unhandled crash
        {
            ReportFatalStartupError(ex);
        }
    }

    // Native MessageBox rather than ThemedMessageBox, for the same reason MainForm's fatal-startup
    // path uses one: on this path the themed dialog's own layout and theming machinery may be part
    // of what failed, and this box only precedes exit.
    private static void ReportFatalStartupError(Exception ex)
    {
        if (LogManager.IsInitialized)
            LogManager.Instance.LogMessage($"Fatal error: {ex}", LogLevel.Error);

        try
        {
            MessageBox.Show(
                $"{AppIdentity.AppName} could not start.\n\n{ex.Message}",
                AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception dialogEx) // NOSONAR S2221 - showing the dialog is best-effort; never mask the original failure
        {
            System.Diagnostics.Debug.WriteLine($"Program.ReportFatalStartupError: {dialogEx.Message}");
        }
    }

    private static void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        if (LogManager.IsInitialized)
            LogManager.Instance.LogMessage($"Unhandled UI thread exception: {e.Exception}", LogLevel.Error);
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (LogManager.IsInitialized)
            LogManager.Instance.LogMessage($"Unhandled exception (IsTerminating={e.IsTerminating}): {e.ExceptionObject}", LogLevel.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved(); // defensive: .NET 4.0 terminated on unobserved task exceptions; .NET 4.5+ and .NET Core do not, but SetObserved is kept as a safeguard
        if (LogManager.IsInitialized)
            LogManager.Instance.LogMessage($"Unobserved task exception: {e.Exception}", LogLevel.Error);
    }

    // Reads the color theme setting directly from the registry before LogManager is initialized.
    // Must not use RegistrySettingsManager to avoid a dependency on LogManager at this early stage.
    //
    // This runs BEFORE any migration. EnsureDefaults - and with it MigrateLegacyKeys - is reached in
    // MainForm's constructor, which is several statements below the SetColorMode call this feeds, so
    // a value read here is the raw pre-migration registry. Renaming a key that is read this early
    // therefore costs one launch: on the first start after upgrading, the new name does not exist
    // yet, this falls through to its default, and only the launch after that reads the migrated
    // value. That is live today for colorMode -> colorTheme (renamed at 2.6.4): a user upgrading
    // from 2.6.3 or earlier sees the System theme once instead of their Dark/Light choice. Cosmetic
    // and self-correcting, and the setting itself is never at risk - MigrateLegacyKeys carries it
    // across before the defaults are written - so it is accepted rather than worked around.
    //
    // The point to carry forward: anything read before MainForm's constructor bypasses migration.
    // A future rename of a key read here needs either a fallback to the old name in this method, or
    // the same accepted one-launch cost. The theme is currently the only such setting.
    private static SystemColorMode ReadColorTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"{RegistrySettingsManager.BaseKeyPath}\{RegistrySettingsManager.SectionExtra}");
            return (key?.GetValue(RegistrySettingsManager.KeyColorTheme) as string) switch
            {
                RegistrySettingsManager.ColorThemeDark => SystemColorMode.Dark,
                RegistrySettingsManager.ColorThemeLight => SystemColorMode.Classic,
                _ => SystemColorMode.System
            };
        }
        catch (Exception ex) // NOSONAR S2221 - registry read before LogManager is initialized; any failure must fall back to System mode
        {
            System.Diagnostics.Debug.WriteLine($"Program.ReadColorTheme: {ex.Message}");
            return SystemColorMode.System;
        }
    }
}

using qbPortWeaver.Shared;

namespace qbPortWeaver
{
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
            try   { isNewInstance = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { isNewInstance = true; }

            if (!isNewInstance)
            {
                MessageBox.Show(
                    $"{AppIdentity.AppName} is already running.",
                    AppIdentity.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            Application.Run(new MainForm());
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            if (LogManager.IsInitialized)
                LogManager.Instance.LogMessage($"Unhandled UI thread exception: {e.Exception}", LogLevel.Error);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
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
        private static SystemColorMode ReadColorTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    $@"{RegistrySettingsManager.BaseKeyPath}\{RegistrySettingsManager.SectionExtra}");
                return (key?.GetValue(RegistrySettingsManager.KeyColorTheme) as string) switch
                {
                    RegistrySettingsManager.ColorThemeDark  => SystemColorMode.Dark,
                    RegistrySettingsManager.ColorThemeLight => SystemColorMode.Classic,
                    _                                      => SystemColorMode.System
                };
            }
            catch (Exception ex) // NOSONAR S2221 - registry read before LogManager is initialized; any failure must fall back to System mode
            {
                System.Diagnostics.Debug.WriteLine($"Program.ReadColorTheme: {ex.Message}");
                return SystemColorMode.System;
            }
        }
    }
}

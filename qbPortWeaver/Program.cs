namespace qbPortWeaver
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetColorMode(ReadColorTheme());
            ApplicationConfiguration.Initialize();

            // Enforce single instance using a named mutex.
            // Using initiallyOwned: false + WaitOne(0) instead of the initiallyOwned: true constructor
            // overload so that an AbandonedMutexException (thrown when a previous instance crashed
            // without releasing the mutex) can be caught and treated as "we are the new instance".
            // The OS transfers ownership to us when the mutex is abandoned, so the catch is safe.
            using var mutex = new Mutex(false, "Global\\qbPortWeaver_SingleInstance");
            bool isNewInstance;
            try   { isNewInstance = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { isNewInstance = true; }

            if (!isNewInstance)
            {
                MessageBox.Show(
                    $"{AppConstants.AppName} is already running.",
                    AppConstants.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            Application.Run(new MainForm());
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
            catch // NOSONAR S2221 - registry read before LogManager is initialized; any failure must fall back to System mode
            {
                return SystemColorMode.System;
            }
        }
    }
}

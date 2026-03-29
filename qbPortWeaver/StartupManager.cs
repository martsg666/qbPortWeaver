using Microsoft.Win32;

namespace qbPortWeaver
{
    /// <summary>Manages the Windows startup registry entry so the application can launch at logon.</summary>
    public static class StartupManager
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Returns <see langword="true"/> if the application is registered to start with Windows.</summary>
        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey);
                return key?.GetValue(AppConstants.AppName) is not null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"StartupManager.IsStartupEnabled: {ex.Message}");
                return false;
            }
        }

        /// <summary>Adds or removes the application from the Windows startup registry key.</summary>
        public static void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key is null)
                {
                    LogManager.Instance.LogMessage("Failed to update startup setting: could not open registry Run key", LogLevel.Warn);
                    return;
                }

                if (enable)
                {
                    key.SetValue(AppConstants.AppName, Application.ExecutablePath);
                    LogManager.Instance.LogMessage("Windows startup enabled", LogLevel.Info);
                }
                else
                {
                    key.DeleteValue(AppConstants.AppName, false);
                    LogManager.Instance.LogMessage("Windows startup disabled", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to update startup setting: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}

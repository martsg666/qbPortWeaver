using Microsoft.Win32;

namespace qbPortWeaver
{
    public static class StartupManager
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey);
                return key?.GetValue(AppConstants.AppName) != null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"StartupManager.IsStartupEnabled: {ex.Message}");
                return false;
            }
        }

        public static void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key == null)
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

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

        /// <summary>
        /// If the Run-key entry exists and points at a different path than the currently running
        /// executable, rewrites it to the current path. No-op if startup is disabled (entry absent)
        /// or already current. Covers the case where the install was moved or upgraded in place
        /// (e.g. a Chocolatey upgrade that lands the binary at a new versioned path).
        /// </summary>
        public static void RefreshStartupPathIfMoved()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
                if (key?.GetValue(AppConstants.AppName) is not string storedValue)
                    return; // startup disabled - leave it alone

                string expectedValue = $"\"{Application.ExecutablePath}\"";
                if (string.Equals(storedValue, expectedValue, StringComparison.OrdinalIgnoreCase))
                    return; // already current

                key.SetValue(AppConstants.AppName, expectedValue);
                LogManager.Instance.LogMessage(
                    $"Windows startup path refreshed: '{storedValue}' -> '{expectedValue}'",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"StartupManager.RefreshStartupPathIfMoved: {ex.Message}");
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
                    // Quote the path so CreateProcess parses it as a single token regardless of embedded spaces.
                    string quotedPath = $"\"{Application.ExecutablePath}\"";
                    key.SetValue(AppConstants.AppName, quotedPath);
                    LogManager.Instance.LogMessage($"Windows startup enabled at {quotedPath}", LogLevel.Info);
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

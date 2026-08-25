using Microsoft.Win32;

namespace qbPortWeaver;

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
            return key?.GetValue(AppIdentity.AppName) is not null;
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
    /// Opens the key read-only first - many managed environments restrict write access to the
    /// Run key via group policy, and we only need write access when a rewrite is actually needed.
    /// </summary>
    public static void RefreshStartupPathIfMoved()
    {
        // Declared outside the try so the failure paths can name the path that is being left behind.
        // Failing here is not cosmetic: the Run key keeps pointing at a binary that has moved, so at
        // the next logon the app either does not start or starts an older copy - a functional failure
        // with a confusing symptom. Both failure paths therefore log at Warn, where the success does
        // at Info; logging the failure lower than the success left the case that matters invisible
        // with debug mode off. This runs once at startup, so Warn here cannot become a repeating badge.
        string? storedValue = null;
        try
        {
            using (var readKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey))
                storedValue = readKey?.GetValue(AppIdentity.AppName) as string;

            if (storedValue is null)
                return; // startup disabled - leave it alone

            string expectedValue = $"\"{Application.ExecutablePath}\"";
            if (string.Equals(storedValue, expectedValue, StringComparison.OrdinalIgnoreCase))
                return; // already current

            using var writeKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            if (writeKey is null)
            {
                LogManager.Instance.LogMessage(
                    $"Could not update the Windows startup entry - it still points at '{storedValue}'. " +
                    $"{AppIdentity.AppName} may not start at logon, or may start an older copy. " +
                    "Write access to the Run key is often restricted by group policy on managed machines.",
                    LogLevel.Warn);
                return;
            }
            writeKey.SetValue(AppIdentity.AppName, expectedValue);
            LogManager.Instance.LogMessage(
                $"Windows startup path refreshed: '{storedValue}' -> '{expectedValue}'",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            // storedValue is still null when the read itself threw, so the entry was never inspected
            // and naming a stale path would be wrong.
            string detail = storedValue is null
                ? "the current entry could not be read"
                : $"it still points at '{storedValue}'";
            LogManager.Instance.LogMessage(
                $"Could not update the Windows startup entry - {detail}: {ex.Message}",
                LogLevel.Warn);
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
                key.SetValue(AppIdentity.AppName, quotedPath);
                LogManager.Instance.LogMessage($"Windows startup enabled at {quotedPath}", LogLevel.Info);
            }
            else
            {
                key.DeleteValue(AppIdentity.AppName, false);
                LogManager.Instance.LogMessage("Windows startup disabled", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to update startup setting: {ex.Message}", LogLevel.Warn);
        }
    }
}

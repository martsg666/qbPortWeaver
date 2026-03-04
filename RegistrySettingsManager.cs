using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace qbPortWeaver
{
    public static class RegistrySettingsManager
    {
        private const string BASE_KEY_PATH = @"Software\qbPortWeaver\Settings";

        // Default values for all settings (single source of truth)
        internal static readonly Dictionary<string, Dictionary<string, string>> Defaults =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["general"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["vpnProvider"]           = "ProtonVPN",
                    ["updateIntervalSeconds"] = "180",
                    ["natPmpAdapterName"]     = ""
                },
                ["qBittorrent"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["qBittorrentURL"]          = "http://127.0.0.1:8080",
                    ["qBittorrentUserName"]     = "admin",
                    ["qBittorrentPassword"]     = "",
                    ["qBittorrentExePath"]      = @"C:\Program Files\qBittorrent\qbittorrent.exe",
                    ["qBittorrentProcessName"]  = "qbittorrent",
                    ["restartqBittorrent"]      = "True",
                    ["forceStartqBittorrent"]   = "False",
                    ["defaultPort"]             = "0",
                    ["warnOnInterfaceMismatch"] = "True",
                    ["restartOnDisconnect"]     = "False"
                },
                ["extra"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["postUpdateCmd"] = "",
                    ["debugMode"]     = "False"
                }
            };

        // Ensures all settings keys exist in the registry, writing defaults for any missing keys
        public static void EnsureDefaults()
        {
            bool anyWritten = false;
            foreach (var section in Defaults)
            {
                try
                {
                    using var regKey = Registry.CurrentUser.CreateSubKey($@"{BASE_KEY_PATH}\{section.Key}");
                    anyWritten |= WriteDefaultsForSection(regKey, section.Key, section.Value);
                }
                catch (Exception ex)
                {
                    LogManager.Instance.LogDebug($"RegistrySettingsManager.EnsureDefaults [{section.Key}]: {ex.Message}");
                }
            }

            if (anyWritten)
                LogManager.Instance.LogMessage("Registry settings initialized with defaults", "INFO");
        }

        // Reads a value from the registry; returns the hardcoded default if not found
        public static string GetValue(string section, string key)
        {
            try
            {
                using var regKey = Registry.CurrentUser.OpenSubKey($@"{BASE_KEY_PATH}\{section}");
                if (regKey?.GetValue(key) is string value)
                {
                    LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue: [{section}] {key} = {value}");
                    return value;
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue [{section}\\{key}]: {ex.Message}");
            }

            string fallback = GetDefault(section, key);
            LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue: [{section}] {key} not found, returning default: {fallback}");
            return fallback;
        }

        // Reads the qBittorrent password from the registry and decrypts it with DPAPI.
        public static string GetPassword()
        {
            try
            {
                using var regKey = Registry.CurrentUser.OpenSubKey($@"{BASE_KEY_PATH}\qBittorrent");
                if (regKey?.GetValue("qBittorrentPassword") is string storedValue && storedValue.Length > 0)
                {
                    try
                    {
                        byte[] encrypted = Convert.FromBase64String(storedValue);
                        byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                        return Encoding.UTF8.GetString(decrypted);
                    }
                    catch (Exception)
                    {
                        // Not a valid DPAPI blob — return empty rather than exposing garbled data
                        LogManager.Instance.LogDebug("RegistrySettingsManager.GetPassword: stored value is not a valid DPAPI blob");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"RegistrySettingsManager.GetPassword: {ex.Message}");
            }

            return GetDefault("qBittorrent", "qBittorrentPassword");
        }

        // Writes a single value to the registry
        public static void SetValue(string section, string key, string value)
        {
            try
            {
                using var regKey = Registry.CurrentUser.CreateSubKey($@"{BASE_KEY_PATH}\{section}");
                regKey.SetValue(key, value, RegistryValueKind.String);
                LogManager.Instance.LogDebug($"RegistrySettingsManager.SetValue: [{section}] {key} = {value}");
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"RegistrySettingsManager.SetValue [{section}\\{key}]: {ex.Message}");
            }
        }

        // Encrypts plaintext with DPAPI (CurrentUser scope) and writes it to the registry.
        public static void SetPassword(string plaintext)
        {
            try
            {
                string encoded = EncryptPassword(plaintext);
                using var regKey = Registry.CurrentUser.CreateSubKey($@"{BASE_KEY_PATH}\qBittorrent");
                regKey.SetValue("qBittorrentPassword", encoded, RegistryValueKind.String);
                LogManager.Instance.LogDebug("RegistrySettingsManager.SetPassword: password saved (encrypted)");
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"RegistrySettingsManager.SetPassword: {ex.Message}");
            }
        }

        // Writes any missing keys for one registry section; returns true if anything was written
        private static bool WriteDefaultsForSection(RegistryKey regKey, string sectionName,
            Dictionary<string, string> sectionDefaults)
        {
            bool anyWritten = false;
            foreach (var kvp in sectionDefaults)
            {
                if (regKey.GetValue(kvp.Key) != null)
                    continue;

                // The password is always stored encrypted; encrypt before the initial write.
                if (sectionName.Equals("qBittorrent", StringComparison.OrdinalIgnoreCase) &&
                    kvp.Key.Equals("qBittorrentPassword", StringComparison.OrdinalIgnoreCase))
                {
                    regKey.SetValue(kvp.Key, EncryptPassword(kvp.Value), RegistryValueKind.String);
                }
                else
                {
                    regKey.SetValue(kvp.Key, kvp.Value, RegistryValueKind.String);
                }

                anyWritten = true;
            }
            return anyWritten;
        }

        // Encrypts a plaintext password with DPAPI and returns a Base64 string.
        private static string EncryptPassword(string plaintext)
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        // Returns the hardcoded default for a setting; returns empty string if the section or key is not found
        private static string GetDefault(string section, string key)
        {
            if (Defaults.TryGetValue(section, out var sectionDefaults) &&
                sectionDefaults.TryGetValue(key, out var value))
                return value;
            return string.Empty;
        }
    }
}

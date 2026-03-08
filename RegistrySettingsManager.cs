using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace qbPortWeaver
{
    public static class RegistrySettingsManager
    {
        private const string BaseKeyPath = @"Software\qbPortWeaver\Settings";
        // Explicit string literals guarantee stable boolean registry serialization independent of framework internals.
        private const string ValueTrue   = "True";
        private const string ValueFalse  = "False";

        public const string SectionGeneral     = "general";
        public const string SectionQBittorrent = "qBittorrent";
        public const string SectionExtra       = "extra";

        public const string VpnProviderProtonVpn = "ProtonVPN";
        public const string VpnProviderPia       = "PIA";
        public const string VpnProviderNatPmp    = "NAT-PMP";

        // Registry key name strings are frozen — changing them would silently break existing installations
        // by orphaning previously saved values. Casing inconsistencies (e.g. "restartqBittorrent" vs
        // "qBittorrentURL") are historical and must be preserved for backward compatibility.

        // Registry key names — general section
        public const string KeyVpnProvider          = "vpnProvider";
        public const string KeyUpdateIntervalSeconds = "updateIntervalSeconds";
        public const string KeyNatPmpAdapterName    = "natPmpAdapterName";

        // Registry key names — qBittorrent section
        public const string KeyQBittorrentUrl          = "qBittorrentURL";
        public const string KeyQBittorrentUserName     = "qBittorrentUserName";
        public const string KeyQBittorrentPassword     = "qBittorrentPassword";
        public const string KeyQBittorrentExePath      = "qBittorrentExePath";
        public const string KeyQBittorrentProcessName  = "qBittorrentProcessName";
        public const string KeyRestartQBittorrent      = "restartqBittorrent";
        public const string KeyForceStartQBittorrent   = "forceStartqBittorrent";
        public const string KeyDefaultPort             = "defaultPort";
        public const string KeyWarnOnInterfaceMismatch = "warnOnInterfaceMismatch";
        public const string KeyRestartOnDisconnect     = "restartOnDisconnect";

        // Registry key names — extra section
        public const string KeyPostUpdateCmd = "postUpdateCmd";
        public const string KeyDebugMode     = "debugMode";

        // Default values for all settings (single source of truth)
        internal static readonly Dictionary<string, Dictionary<string, string>> Defaults =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [SectionGeneral] = new(StringComparer.OrdinalIgnoreCase)
                {
                    [KeyVpnProvider]          = VpnProviderProtonVpn,
                    [KeyUpdateIntervalSeconds] = "180",
                    [KeyNatPmpAdapterName]    = ""
                },
                [SectionQBittorrent] = new(StringComparer.OrdinalIgnoreCase)
                {
                    [KeyQBittorrentUrl]          = "http://127.0.0.1:8080",
                    [KeyQBittorrentUserName]     = "admin",
                    [KeyQBittorrentPassword]     = "",
                    [KeyQBittorrentExePath]      = @"C:\Program Files\qBittorrent\qbittorrent.exe",
                    [KeyQBittorrentProcessName]  = "qbittorrent",
                    [KeyRestartQBittorrent]      = ValueTrue,
                    [KeyForceStartQBittorrent]   = ValueFalse,
                    [KeyDefaultPort]             = "0",
                    [KeyWarnOnInterfaceMismatch] = ValueTrue,
                    [KeyRestartOnDisconnect]     = ValueFalse
                },
                [SectionExtra] = new(StringComparer.OrdinalIgnoreCase)
                {
                    [KeyPostUpdateCmd] = "",
                    [KeyDebugMode]     = ValueFalse
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
                    using var regKey = Registry.CurrentUser.CreateSubKey($@"{BaseKeyPath}\{section.Key}");
                    anyWritten |= WriteDefaultsForSection(regKey, section.Key, section.Value);
                }
                catch (Exception ex)
                {
                    LogManager.Instance.LogDebug($"RegistrySettingsManager.EnsureDefaults: [{section.Key}]: {ex.Message}");
                }
            }

            if (anyWritten)
                LogManager.Instance.LogMessage("Registry settings initialized with defaults", LogLevel.Info);
        }

        // Reads a value from the registry; returns the hardcoded default if not found
        public static string GetValue(string section, string key)
        {
            try
            {
                using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{section}");
                if (regKey?.GetValue(key) is string value)
                {
                    LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue: [{section}] {key} = {MaskSensitiveValue(key, value)}");
                    return value;
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue: [{section}] {key}: {ex.Message}");
            }

            string fallback = GetDefault(section, key);
            LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue: [{section}] {key} not found, returning default: {fallback}");
            return fallback;
        }

        // Reads a bool value from the registry; returns false if not found or not parseable
        public static bool GetBool(string section, string key) =>
            bool.TryParse(GetValue(section, key), out bool result) && result;

        // Reads an int value from the registry; returns 0 if not found or not parseable
        public static int GetInt(string section, string key) =>
            int.TryParse(GetValue(section, key), out int result) ? result : 0;

        // Reads the qBittorrent password from the registry and decrypts it with DPAPI
        public static string GetPassword()
        {
            try
            {
                using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{SectionQBittorrent}");
                if (regKey?.GetValue(KeyQBittorrentPassword) is string storedValue && storedValue.Length > 0)
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

            return GetDefault(SectionQBittorrent, KeyQBittorrentPassword);
        }

        // Writes a single value to the registry
        public static void SetValue(string section, string key, string value)
        {
            try
            {
                using var regKey = Registry.CurrentUser.CreateSubKey($@"{BaseKeyPath}\{section}");
                regKey.SetValue(key, value, RegistryValueKind.String);
                LogManager.Instance.LogDebug($"RegistrySettingsManager.SetValue: [{section}] {key} = {MaskSensitiveValue(key, value)}");
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to save setting [{section}] {key}: {ex.Message}", LogLevel.Warn);
            }
        }

        // Writes a bool value to the registry as "True" or "False"
        public static void SetBool(string section, string key, bool value) =>
            SetValue(section, key, value ? ValueTrue : ValueFalse);

        // Encrypts the password with DPAPI (CurrentUser scope) and writes it to the registry
        public static void SetPassword(string plaintext)
        {
            try
            {
                string encoded = EncryptPassword(plaintext);
                using var regKey = Registry.CurrentUser.CreateSubKey($@"{BaseKeyPath}\{SectionQBittorrent}");
                regKey.SetValue(KeyQBittorrentPassword, encoded, RegistryValueKind.String);
                LogManager.Instance.LogDebug("RegistrySettingsManager.SetPassword: password saved (encrypted)");
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to save password: {ex.Message}", LogLevel.Warn);
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
                if (sectionName.Equals(SectionQBittorrent, StringComparison.OrdinalIgnoreCase) &&
                    kvp.Key.Equals(KeyQBittorrentPassword, StringComparison.OrdinalIgnoreCase))
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

        // Encrypts a plaintext password with DPAPI and returns a Base64 string
        private static string EncryptPassword(string plaintext)
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        // Returns "***" for the password key to avoid writing plaintext credentials to the log
        private static string MaskSensitiveValue(string key, string value) =>
            key.Equals(KeyQBittorrentPassword, StringComparison.OrdinalIgnoreCase) ? "***" : value;

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

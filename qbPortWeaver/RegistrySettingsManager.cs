using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace qbPortWeaver;

/// <summary>Reads and writes application settings from the Windows registry under <c>HKCU\Software\qbPortWeaver\settings</c>.</summary>
public static class RegistrySettingsManager
{
    internal const string BaseKeyPath = AppIdentity.AppRegistryKey + @"\settings";
    // Explicit string literals guarantee stable boolean registry serialization independent of framework internals.
    private const string ValueTrue = "True";
    private const string ValueFalse = "False";

    public const string SectionGeneral = "general";
    public const string SectionQBittorrent = "qbittorrent";
    public const string SectionTransmission = "transmission";
    public const string SectionDeluge = "deluge";
    public const string SectionNicotine = "nicotine";
    public const string SectionExtra = "extra";
    public const string SectionMedia = "media";

    public const string VpnProviderDisabled = "Disabled";
    public const string VpnProviderProtonVpn = "ProtonVPN";
    public const string VpnProviderPia = "PIA";
    public const string VpnProviderNatPmp = "NAT-PMP";

    public const string ClientNameQBittorrent = "qBittorrent";
    public const string ClientNameTransmission = "Transmission";
    public const string ClientNameDeluge = "Deluge";
    public const string ClientNameNicotine = "Nicotine+";

    // Registry key name strings are frozen - changing them would silently break existing installations
    // by orphaning previously saved values. The one exception is KeyClient, renamed from the legacy
    // "bitTorrentClient" to the protocol-neutral "client"; MigrateLegacyKeys carries the old value over.

    // Registry key names - general section
    public const string KeyVpnProvider = "vpnProvider";
    public const string KeyUpdateIntervalSeconds = "updateIntervalSeconds";
    public const string KeyNatPmpAdapterName = "natPmpAdapterName";
    public const string KeyClient = "client";
    // Former name for KeyClient, migrated to the current name on startup (see MigrateLegacyKeys).
    private const string LegacyKeyClient = "bitTorrentClient";

    // Registry key names - qBittorrent section
    public const string KeyQBittorrentUrl = "qBittorrentURL";
    public const string KeyQBittorrentUserName = "qBittorrentUserName";
    public const string KeyQBittorrentPassword = "qBittorrentPassword";
    public const string KeyQBittorrentExePath = "qBittorrentExePath";
    public const string KeyQBittorrentProcessName = "qBittorrentProcessName";
    public const string KeyRestartQBittorrent = "restartqBittorrent";
    public const string KeyForceStartQBittorrent = "forceStartqBittorrent";
    public const string KeyDefaultPort = "defaultPort";
    public const string KeyWarnOnInterfaceMismatch = "warnOnInterfaceMismatch";
    public const string KeyRestartOnDisconnect = "restartOnDisconnect";

    // Registry key names - transmission section
    public const string KeyTransmissionUrl = "transmissionURL";
    public const string KeyTransmissionUserName = "transmissionUserName";
    public const string KeyTransmissionPassword = "transmissionPassword";
    public const string KeyTransmissionProcessName = "transmissionProcessName";
    public const string KeyTransmissionExePath = "transmissionExePath";
    public const string KeyRestartTransmission = "restartTransmission";
    public const string KeyForceStartTransmission = "forceStartTransmission";

    // Registry key names - deluge section
    public const string KeyDelugeUrl = "delugeURL";
    public const string KeyDelugePassword = "delugePassword";
    public const string KeyDelugeProcessName = "delugeProcessName";
    public const string KeyDelugeExePath = "delugeExePath";
    public const string KeyRestartDeluge = "restartDeluge";
    public const string KeyForceStartDeluge = "forceStartDeluge";

    // Registry key names - nicotine section. Nicotine+ is reached through the qbPortWeaver
    // bridge plugin, which authenticates with a token it issues itself - so there is no user
    // name, and the token occupies the section's single-secret slot.
    public const string KeyNicotineUrl = "nicotineURL";
    public const string KeyNicotineToken = "nicotineToken";
    public const string KeyNicotineProcessName = "nicotineProcessName";
    public const string KeyNicotineExePath = "nicotineExePath";
    public const string KeyRestartNicotine = "restartNicotine";
    public const string KeyForceStartNicotine = "forceStartNicotine";
    public const string KeyNicotineWarnOnInterfaceMismatch = "nicotineWarnOnInterfaceMismatch";

    // Registry key names - extra section
    public const string KeyPostUpdateCmd = "postUpdateCmd";
    public const string KeyDebugMode = "debugMode";
    public const string KeyColorTheme = "colorMode";

    // Color theme values
    public const string ColorThemeSystem = "System";
    public const string ColorThemeDark = "Dark";
    public const string ColorThemeLight = "Light";

    // Registry key names - media section
    public const string KeyMediaEnabled = "mediaEnabled";
    public const string KeyTmdbApiKey = "tmdbApiKey";
    public const string KeyMediaSourceFolders = "sourceFolders";
    public const string KeyMediaCreateFolders = "createFolders";
    public const string KeyMediaDeleteEmptyFolders = "deleteEmptyFolders";
    public const string KeyMediaDryRun = "dryRun";
    public const string KeyMediaMoviesLibraryPath = "moviesLibraryPath";
    public const string KeyMediaTvShowsLibraryPath = "tvShowsLibraryPath";
    public const string KeyMediaImportMode = "importMode";

    public const string ImportModeHardlink = "Hardlink";
    public const string ImportModeCopy = "Copy";
    public const string ImportModeMove = "Move";

    // Registry key names - app level (not in a section)
    public const string KeyLastSeenVersion = "lastSeenVersion";
    public const string KeyProtonVpnLogFilePath = "protonVpnLogFilePath";
    public const string KeyProtonVpnServiceSearchTerm = "protonVpnServiceSearchTerm";
    public const string KeyPiaServiceSearchTerm = "piaServiceSearchTerm";
    public const string KeyTransmissionServiceSearchTerm = "transmissionServiceSearchTerm";
    public const string KeyProtonVpnClientProcessName = "protonVpnClientProcessName";
    public const string KeyProtonVpnAdapterName = "protonVpnAdapterName";
    public const string KeyProtonVpnNativeAdapterName = "protonVpnNativeAdapterName";
    public const string KeyPiaAdapterName = "piaAdapterName";
    public const string KeyPiaClientProcessName = "piaClientProcessName";
    public const string KeyPiactlProcessName = "piactlProcessName";

    // Registry key names - general section (auto-recovery)
    // Registry string values are frozen for backward compatibility.
    public const string KeyVpnAutoRecoveryEnabled = "vpnAutoRecoveryEnabled";
    public const string KeyVpnAutoRecoveryTriggerCycles = "vpnAutoRecoveryTriggerCycles";

    // Registry key names - general section (notifications)
    public const string KeyNotifyOnPortUpdate = "notifyOnPortUpdate";

    // Registry key names - general section (port verification)
    public const string KeyVerifyPortAfterSync = "verifyPortAfterSync";
    public const string KeyPortClosedRecoveryEnabled = "portClosedRecoveryEnabled";
    public const string KeyPortClosedRecoveryTriggerChecks = "portClosedRecoveryTriggerChecks";

    // Registry key names - general section (updates)
    public const string KeyShowUpdateFormOnStartup = "showUpdateFormOnStartup";

    // Registry key names - general section (sync triggering)
    public const string KeyResyncOnNetworkChange = "resyncOnNetworkChange";
    public const string KeyWaitForVpnOnStartup = "waitForVpnOnStartup";

    // Default values for all settings (single source of truth)
    private static readonly Dictionary<string, Dictionary<string, string>> _defaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SectionGeneral] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyVpnProvider] = VpnProviderDisabled,
                [KeyUpdateIntervalSeconds] = "180",
                [KeyNatPmpAdapterName] = "",
                [KeyVpnAutoRecoveryEnabled] = ValueTrue,
                [KeyVpnAutoRecoveryTriggerCycles] = "3",
                [KeyClient] = ClientNameQBittorrent,
                [KeyNotifyOnPortUpdate] = ValueTrue,
                [KeyShowUpdateFormOnStartup] = ValueTrue,
                [KeyResyncOnNetworkChange] = ValueTrue,
                [KeyWaitForVpnOnStartup] = ValueTrue,
                [KeyVerifyPortAfterSync] = ValueTrue,
                [KeyPortClosedRecoveryEnabled] = ValueTrue,
                [KeyPortClosedRecoveryTriggerChecks] = "3"
            },
            [SectionQBittorrent] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyQBittorrentUrl] = "http://127.0.0.1:8080",
                [KeyQBittorrentUserName] = "admin",
                [KeyQBittorrentPassword] = "",
                [KeyQBittorrentExePath] = @"C:\Program Files\qBittorrent\qbittorrent.exe",
                [KeyQBittorrentProcessName] = "qbittorrent",
                [KeyRestartQBittorrent] = ValueTrue,
                [KeyForceStartQBittorrent] = ValueTrue,
                [KeyDefaultPort] = "0",
                [KeyWarnOnInterfaceMismatch] = ValueTrue,
                [KeyRestartOnDisconnect] = ValueTrue
            },
            [SectionTransmission] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyTransmissionUrl] = "http://127.0.0.1:9091",
                [KeyTransmissionUserName] = "",
                [KeyTransmissionPassword] = "",
                [KeyTransmissionProcessName] = "transmission-qt",
                [KeyTransmissionExePath] = @"C:\Program Files\Transmission\transmission-qt.exe",
                [KeyRestartTransmission] = ValueTrue,
                [KeyForceStartTransmission] = ValueTrue,
                [KeyDefaultPort] = "0"
            },
            [SectionDeluge] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyDelugeUrl] = "http://127.0.0.1:8112",
                [KeyDelugePassword] = "",
                [KeyDelugeProcessName] = "deluge",
                [KeyDelugeExePath] = @"C:\Program Files\Deluge\deluge.exe",
                [KeyRestartDeluge] = ValueTrue,
                [KeyForceStartDeluge] = ValueTrue,
                [KeyDefaultPort] = "0"
            },
            [SectionNicotine] = new(StringComparer.OrdinalIgnoreCase)
            {
                // The URL and token are normally discovered from the bridge plugin's connection
                // file; these defaults only matter when that file cannot be found (Nicotine+
                // started with a custom data folder) and the user fills them in by hand.
                [KeyNicotineUrl] = "http://127.0.0.1:38472",
                [KeyNicotineToken] = "",
                [KeyNicotineProcessName] = "Nicotine+",
                [KeyNicotineExePath] = @"C:\Program Files\Nicotine+\Nicotine+.exe",
                // The bridge applies the port to the running client, so there is nothing a
                // restart would fix - and killing Nicotine+ discards its configuration.
                [KeyRestartNicotine] = ValueFalse,
                [KeyForceStartNicotine] = ValueTrue,
                [KeyNicotineWarnOnInterfaceMismatch] = ValueTrue,
                [KeyDefaultPort] = "0"
            },
            [SectionExtra] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyPostUpdateCmd] = "",
                [KeyDebugMode] = ValueFalse,
                [KeyColorTheme] = ColorThemeSystem
            },
            [SectionMedia] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyMediaEnabled] = ValueFalse,
                [KeyTmdbApiKey] = "",
                [KeyMediaSourceFolders] = "",
                [KeyMediaCreateFolders] = ValueTrue,
                [KeyMediaDeleteEmptyFolders] = ValueFalse,
                [KeyMediaDryRun] = ValueTrue,
                [KeyMediaMoviesLibraryPath] = "",
                [KeyMediaTvShowsLibraryPath] = "",
                [KeyMediaImportMode] = ImportModeHardlink
            }
        };

    // Default values for app-level keys (single source of truth; written on first run)
    private static readonly Dictionary<string, string> _appDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [KeyProtonVpnLogFilePath] = @"Proton\Proton VPN\Logs\client-logs.txt",
            [KeyProtonVpnServiceSearchTerm] = "ProtonVPN Service",
            [KeyPiaServiceSearchTerm] = "PrivateInternetAccessService",
            [KeyTransmissionServiceSearchTerm] = "Transmission",
            [KeyProtonVpnClientProcessName] = "ProtonVPN.Client",
            [KeyProtonVpnAdapterName] = "ProtonVPN",
            [KeyProtonVpnNativeAdapterName] = "ProTUN",
            [KeyPiaAdapterName] = "PIA",
            [KeyPiaClientProcessName] = "pia-client",
            [KeyPiactlProcessName] = "piactl",
        };

    /// <summary>Reads a string value from the app-level registry key (<c>HKCU\Software\qbPortWeaver</c>), above the settings sections. Returns the registered default if the key is missing.</summary>
    public static string GetAppValue(string key)
    {
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey(AppIdentity.AppRegistryKey);
            if (regKey?.GetValue(key) is string value)
                return value;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.GetAppValue: {key} - {ex.Message}");
        }

        return _appDefaults.TryGetValue(key, out var fallback) ? fallback : string.Empty;
    }

    /// <summary>Writes a string value to the app-level registry key (<c>HKCU\Software\qbPortWeaver</c>), above the settings sections.</summary>
    public static void SetAppValue(string key, string value)
    {
        try
        {
            using var regKey = Registry.CurrentUser.CreateSubKey(AppIdentity.AppRegistryKey);
            regKey.SetValue(key, value, RegistryValueKind.String);
            LogManager.Instance.LogDebug($"RegistrySettingsManager.SetAppValue: {key} = {MaskSensitiveValue(key, value)}");
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to save app-level setting {key}: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Returns the pipe session token from <c>HKCU\Software\qbPortWeaver\pipeSessionToken</c>,
    /// generating and persisting a new one if none exists. Used by the tray app to authenticate
    /// pipe messages sent to the helper service.
    /// </summary>
    public static string GetOrCreatePipeSessionToken()
    {
        try
        {
            using var regKey = Registry.CurrentUser.CreateSubKey(AppIdentity.AppRegistryKey);
            if (regKey.GetValue(AppIdentity.PipeSessionTokenKey) is string existing && existing.Length > 0)
                return existing;
            // 32 hex chars = 128 bits of CSPRNG entropy. Used to authenticate pipe messages
            // sent to the SYSTEM helper service; HKCU's per-user ACL is the primary defense.
            var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
            regKey.SetValue(AppIdentity.PipeSessionTokenKey, token, RegistryValueKind.String);
            return token;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.GetOrCreatePipeSessionToken: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Ensures all settings keys exist in the registry, writing the registered defaults for any that are missing.</summary>
    public static void EnsureDefaults()
    {
        // Must run before defaults are written below: a renamed key's new name does not exist yet on
        // an existing install, so without the carry-over EnsureDefaults would write the default over it.
        MigrateLegacyKeys();

        bool anyWritten = false;
        foreach (var section in _defaults)
        {
            try
            {
                using var regKey = Registry.CurrentUser.CreateSubKey($@"{BaseKeyPath}\{section.Key}");
                anyWritten |= WriteDefaultsForSection(regKey, section.Value);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"RegistrySettingsManager.EnsureDefaults: [{section.Key}] - {ex.Message}");
            }
        }

        try
        {
            using var appKey = Registry.CurrentUser.CreateSubKey(AppIdentity.AppRegistryKey);
            foreach (var kvp in _appDefaults.Where(kvp => appKey.GetValue(kvp.Key) is null))
            {
                appKey.SetValue(kvp.Key, kvp.Value, RegistryValueKind.String);
                anyWritten = true;
            }
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.EnsureDefaults: [app] - {ex.Message}");
        }

        if (anyWritten)
            LogManager.Instance.LogMessage("Registry default values written for missing keys", LogLevel.Info);
    }

    /// <summary>Carries values stored under a former key name over to the current one, once, on startup.</summary>
    /// <remarks>Only the client-selection key has been renamed so far (<see cref="LegacyKeyClient"/> ->
    /// <see cref="KeyClient"/>). The old value is copied only when the new name is not already set, then
    /// the old value is removed, so a user's saved client choice survives the rename.</remarks>
    private static void MigrateLegacyKeys()
    {
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{SectionGeneral}", writable: true);
            if (regKey?.GetValue(LegacyKeyClient) is not string legacyValue) return;

            if (regKey.GetValue(KeyClient) is null)
                regKey.SetValue(KeyClient, legacyValue, RegistryValueKind.String);
            regKey.DeleteValue(LegacyKeyClient, throwOnMissingValue: false);
            LogManager.Instance.LogMessage("Migrated the client setting to its new registry key name", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.MigrateLegacyKeys: {ex.Message}");
        }
    }

    /// <summary>Reads a string value from the registry. Returns the registered default if the key is missing or unreadable.</summary>
    public static string GetValue(string section, string key)
    {
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{section}");
            if (regKey?.GetValue(key) is string value)
                return value;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue: [{section}] {key} - {ex.Message}");
        }

        string fallback = GetDefault(section, key);
        LogManager.Instance.LogDebug($"RegistrySettingsManager.GetValue: [{section}] {key} not found, returning default: {MaskSensitiveValue(key, fallback)}");
        return fallback;
    }

    /// <summary>
    /// Returns every stored key/value in a section, sorted by key, with sensitive values (passwords,
    /// API keys, tokens) masked as <c>***</c>. Intended for the diagnostics report. Returns an empty
    /// list when the section has no stored values or cannot be read.
    /// </summary>
    internal static IReadOnlyList<(string Key, string Value)> GetSectionSnapshot(string section)
    {
        var result = new List<(string Key, string Value)>();
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{section}");
            if (regKey is null) return result;
            foreach (var name in regKey.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                string value = regKey.GetValue(name)?.ToString() ?? string.Empty;
                result.Add((name, MaskSensitiveValue(name, value)));
            }
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.GetSectionSnapshot: [{section}] - {ex.Message}");
        }
        return result;
    }

    /// <summary>Reads a bool value from the registry. Returns the registered default if the key is missing or not parseable.</summary>
    public static bool GetBool(string section, string key)
    {
        if (bool.TryParse(GetValue(section, key), out bool result)) return result;
        return bool.TryParse(GetDefault(section, key), out bool fallback) && fallback;
    }

    /// <summary>Reads an int value from the registry. Returns the registered default if the key is missing or not parseable.</summary>
    public static int GetInt(string section, string key)
    {
        if (int.TryParse(GetValue(section, key), out int result)) return result;
        return int.TryParse(GetDefault(section, key), out int fallback) ? fallback : 0;
    }

    /// <summary>Reads the qBittorrent password from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetQBittorrentPassword() =>
        GetEncryptedValue(SectionQBittorrent, KeyQBittorrentPassword);

    /// <summary>Reads the Transmission password from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetTransmissionPassword() =>
        GetEncryptedValue(SectionTransmission, KeyTransmissionPassword);

    /// <summary>Reads the Deluge password from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetDelugePassword() =>
        GetEncryptedValue(SectionDeluge, KeyDelugePassword);

    /// <summary>Reads the Nicotine+ bridge plugin token from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetNicotineToken() =>
        GetEncryptedValue(SectionNicotine, KeyNicotineToken);

    /// <summary>Reads the TMDB API key from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetTmdbApiKey() =>
        GetEncryptedValue(SectionMedia, KeyTmdbApiKey);

    /// <summary>Reads a DPAPI-encrypted string value from the registry. Returns the registered default if the key is missing or empty, or an empty string if the stored value cannot be decrypted.</summary>
    public static string GetEncryptedValue(string section, string key)
    {
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{section}");
            if (regKey?.GetValue(key) is string storedValue && storedValue.Length > 0)
            {
                byte[] encrypted;
                try
                {
                    encrypted = Convert.FromBase64String(storedValue);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    // Not valid Base64 - this is plaintext from before encryption was added (backward compat).
                    // Pre-encryption installs stored values directly; treat them as the actual value.
                    LogManager.Instance.LogDebug($"RegistrySettingsManager.GetEncryptedValue: [{section}] {key} is plaintext ({ex.GetType().Name}), returning raw value");
                    return storedValue;
                }

                try
                {
                    byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decrypted);
                }
                catch (CryptographicException ex)
                {
                    // Value parsed as Base64 but DPAPI cannot decrypt it - typically a machine/profile
                    // change (registry restored on a different machine) or DPAPI master key rotation.
                    // Returning the raw Base64 would produce a confusing auth failure; surface the
                    // underlying cause at Warn so the user can re-enter the value in Settings.
                    LogManager.Instance.LogMessage(
                        $"Failed to decrypt [{section}] {key}: {ex.Message} - re-enter the value in Settings",
                        LogLevel.Warn);
                    return string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.GetEncryptedValue: [{section}] {key} - {ex.Message}");
        }

        return GetDefault(section, key);
    }

    /// <summary>Writes a string value to the registry under the given section and key.</summary>
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

    /// <summary>Writes a bool value to the registry as <c>"True"</c> or <c>"False"</c>.</summary>
    public static void SetBool(string section, string key, bool value) =>
        SetValue(section, key, value ? ValueTrue : ValueFalse);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetQBittorrentPassword(string plaintext) =>
        SetEncryptedValue(SectionQBittorrent, KeyQBittorrentPassword, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetTransmissionPassword(string plaintext) =>
        SetEncryptedValue(SectionTransmission, KeyTransmissionPassword, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetDelugePassword(string plaintext) =>
        SetEncryptedValue(SectionDeluge, KeyDelugePassword, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetNicotineToken(string plaintext) =>
        SetEncryptedValue(SectionNicotine, KeyNicotineToken, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetTmdbApiKey(string plaintext) =>
        SetEncryptedValue(SectionMedia, KeyTmdbApiKey, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry under the given section and key.</summary>
    public static void SetEncryptedValue(string section, string key, string plaintext)
    {
        try
        {
            string encoded = EncryptValue(plaintext);
            using var regKey = Registry.CurrentUser.CreateSubKey($@"{BaseKeyPath}\{section}");
            regKey.SetValue(key, encoded, RegistryValueKind.String);
            LogManager.Instance.LogDebug($"RegistrySettingsManager.SetEncryptedValue: [{section}] {key} saved (encrypted)");
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to save encrypted setting [{section}] {key}: {ex.Message}", LogLevel.Warn);
        }
    }

    // Keys that are stored DPAPI-encrypted rather than as plaintext registry strings.
    // Used by WriteDefaultsForSection to encrypt initial values. Add any future
    // sensitive setting here to ensure it is stored encrypted.
    private static readonly HashSet<string> _encryptedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        KeyQBittorrentPassword,
        KeyTransmissionPassword,
        KeyDelugePassword,
        KeyNicotineToken,
        KeyTmdbApiKey
    };

    // Keys whose values must never be written to logs in plaintext. Superset of _encryptedKeys
    // plus app-level secrets stored plaintext but protected by the HKCU ACL (e.g. the pipe
    // session token used to authenticate messages to the SYSTEM helper service).
    private static readonly HashSet<string> _logMaskedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        KeyQBittorrentPassword,
        KeyTransmissionPassword,
        KeyDelugePassword,
        KeyNicotineToken,
        KeyTmdbApiKey,
        AppIdentity.PipeSessionTokenKey
    };

    // Writes any missing keys for one registry section; returns true if anything was written
    private static bool WriteDefaultsForSection(RegistryKey regKey,
        Dictionary<string, string> sectionDefaults)
    {
        bool anyWritten = false;
        foreach (var kvp in sectionDefaults)
        {
            if (regKey.GetValue(kvp.Key) is not null)
                continue;

            // Sensitive keys are stored DPAPI-encrypted, but skip the encrypt round-trip for
            // empty defaults: GetEncryptedValue returns the registered default for missing or
            // empty values, so an empty-encrypted blob is indistinguishable from a plain empty
            // string in observable behaviour.
            string valueToWrite = _encryptedKeys.Contains(kvp.Key) && kvp.Value.Length > 0
                ? EncryptValue(kvp.Value)
                : kvp.Value;
            regKey.SetValue(kvp.Key, valueToWrite, RegistryValueKind.String);

            anyWritten = true;
        }
        return anyWritten;
    }

    // Encrypts a plaintext value with DPAPI and returns a Base64 string
    private static string EncryptValue(string plaintext)
    {
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    // Returns "***" for sensitive keys to avoid writing credentials or session tokens to the log
    private static string MaskSensitiveValue(string key, string value) =>
        _logMaskedKeys.Contains(key) ? "***" : value;

    // Returns the registered default for a setting; returns empty string if the section or key is not found
    private static string GetDefault(string section, string key)
    {
        if (_defaults.TryGetValue(section, out var sectionDefaults) &&
            sectionDefaults.TryGetValue(key, out var value))
            return value;
        return string.Empty;
    }
}

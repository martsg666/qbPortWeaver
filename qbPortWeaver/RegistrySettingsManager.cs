using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace qbPortWeaver;

/// <summary>Reads and writes application settings from the Windows registry under <c>HKCU\Software\qbPortWeaver\settings</c>.</summary>
public static class RegistrySettingsManager
{
    internal const string BaseKeyPath = AppIdentity.SettingsRegistryKey;
    // Explicit string literals guarantee stable boolean registry serialization independent of framework internals.
    private const string ValueTrue = "True";
    private const string ValueFalse = "False";

    /// <summary>Separator for settings that store a list in a single value (currently the media source folders).</summary>
    /// <remarks>The same character the helper service uses to delimit its pipe messages, and chosen for the
    /// same reason: it is in <see cref="Path.GetInvalidFileNameChars"/>, so no path can contain one and a
    /// folder name can never be mistaken for a delimiter. Replaced <c>;</c>, which is legal in a Windows
    /// path; <see cref="MigrateFolderListSeparator"/> carries existing values over.</remarks>
    public const char ListSeparator = '|';

    public const string SectionGeneral = "general";
    public const string SectionQBittorrent = "qbittorrent";
    public const string SectionTransmission = "transmission";
    public const string SectionDeluge = "deluge";
    public const string SectionNicotine = "nicotine";
    // Shared with the helper service, which reads the debug flag from this section while
    // impersonating the pipe client - see AppIdentity.DebugModeValueName.
    public const string SectionExtra = AppIdentity.ExtraSettingsSection;
    public const string SectionMedia = "media";

    public const string VpnProviderDisabled = "Disabled";
    public const string VpnProviderProtonVpn = "ProtonVPN";
    public const string VpnProviderPia = "PIA";
    public const string VpnProviderNatPmp = "NAT-PMP";

    public const string ClientNameQBittorrent = "qBittorrent";
    public const string ClientNameTransmission = "Transmission";
    public const string ClientNameDeluge = "Deluge";
    public const string ClientNameNicotine = "Nicotine+";

    // Registry key name strings are persisted, so changing one orphans previously saved values
    // unless MigrateClientKeys carries it over. Two shipped values read as untidy and are left
    // alone, listed here so a tidy-up pass recognises them as decided rather than missed:
    //   "colorMode"    - predates the "theme" wording used by KeyColorTheme and the UI.
    //   "mediaEnabled" - carries a section prefix its eight KeyMedia* siblings drop, since the
    //                    section name already supplies it.
    // The C# constant names, by contrast, are free to change: only the string values are persisted.

    // Registry key names - general section
    public const string KeyVpnProvider = "vpnProvider";
    public const string KeyUpdateIntervalSeconds = "updateIntervalSeconds";
    public const string KeyNatPmpAdapterName = "natPmpAdapterName";
    public const string KeyClient = "client";
    // Former name for KeyClient, migrated to the current name on startup (see MigrateLegacyKeys).
    private const string LegacyKeyClient = "bitTorrentClient";

    // Registry key names - client sections (qbittorrent, transmission, deluge, nicotine).
    //
    // One set of names, reused verbatim in every client section: the section already says which
    // client the value belongs to, so repeating it in the key name added nothing and was the source
    // of the old inconsistency (client-first "qBittorrentURL" beside purpose-first
    // "restartqBittorrent"). Every client section now holds identically named values, so the four
    // can be compared directly, and adding a client needs no new key names at all.
    //
    // Not every client uses every key: KeyUserName is absent where the client has no user name
    // (Deluge, Nicotine+) and KeyRestart where the client is never restarted (Nicotine+). See
    // ClientRegistry, which records for each client which of these apply.
    //
    // KeyPassword holds the client's single secret whatever its nature - a Web UI password for
    // three of them, the bridge plugin's bearer token for Nicotine+. The Nicotine+ UI still calls
    // it a token, which is what the plugin issues; only the storage name is shared.
    public const string KeyUrl = "url";
    public const string KeyUserName = "userName";
    public const string KeyPassword = "password";
    public const string KeyExePath = "exePath";
    public const string KeyProcessName = "processName";
    public const string KeyRestart = "restart";
    public const string KeyForceStart = "forceStart";
    public const string KeyDefaultPort = "defaultPort";
    // Stored only in the sections whose clients can report a bound interface (qBittorrent,
    // Nicotine+); the last two are qBittorrent-only today but need no new names if that changes.
    public const string KeyWarnOnInterfaceMismatch = "warnOnInterfaceMismatch";
    public const string KeyRestartOnDisconnect = "restartOnDisconnect";
    public const string KeyFixInterfaceBinding = "fixInterfaceBinding";

    // Registry key names - extra section
    public const string KeyPostUpdateCmd = "postUpdateCmd";
    public const string KeyDebugMode = AppIdentity.DebugModeValueName;
    public const string KeyColorTheme = "colorMode";

    // Color theme values
    public const string ColorThemeSystem = "System";
    public const string ColorThemeDark = "Dark";
    public const string ColorThemeLight = "Light";

    // Registry key names - media section
    public const string KeyMediaEnabled = "mediaEnabled";
    public const string KeyMediaTmdbApiKey = "tmdbApiKey";
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
                [KeyUrl] = "http://127.0.0.1:8080",
                [KeyUserName] = "admin",
                [KeyPassword] = "",
                [KeyExePath] = @"C:\Program Files\qBittorrent\qbittorrent.exe",
                [KeyProcessName] = "qbittorrent",
                [KeyRestart] = ValueTrue,
                [KeyForceStart] = ValueTrue,
                [KeyWarnOnInterfaceMismatch] = ValueTrue,
                [KeyRestartOnDisconnect] = ValueTrue,
                // On by default, like the app's other remediation settings. The write is the same
                // setPreferences call the port sync already makes every cycle, and it restores the
                // adapter the user picked rather than choosing one - a far smaller intervention than
                // auto-recovery, which restarts services by default. Turning it off downgrades the
                // behaviour to a warning; detection runs either way.
                [KeyFixInterfaceBinding] = ValueTrue,
                [KeyDefaultPort] = "0"
            },
            [SectionTransmission] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyUrl] = "http://127.0.0.1:9091",
                [KeyUserName] = "",
                [KeyPassword] = "",
                [KeyExePath] = @"C:\Program Files\Transmission\transmission-qt.exe",
                [KeyProcessName] = "transmission-qt",
                [KeyRestart] = ValueTrue,
                [KeyForceStart] = ValueTrue,
                [KeyDefaultPort] = "0"
            },
            [SectionDeluge] = new(StringComparer.OrdinalIgnoreCase)
            {
                [KeyUrl] = "http://127.0.0.1:8112",
                [KeyPassword] = "",
                [KeyExePath] = @"C:\Program Files\Deluge\deluge.exe",
                [KeyProcessName] = "deluge",
                [KeyRestart] = ValueTrue,
                [KeyForceStart] = ValueTrue,
                [KeyDefaultPort] = "0"
            },
            [SectionNicotine] = new(StringComparer.OrdinalIgnoreCase)
            {
                // The URL and token are normally discovered from the bridge plugin's connection
                // file; these defaults only matter when that file cannot be found (Nicotine+
                // started with a custom data folder) and the user fills them in by hand.
                [KeyUrl] = "http://127.0.0.1:38472",
                [KeyPassword] = "",
                [KeyExePath] = @"C:\Program Files\Nicotine+\Nicotine+.exe",
                [KeyProcessName] = "Nicotine+",
                [KeyForceStart] = ValueTrue,
                [KeyWarnOnInterfaceMismatch] = ValueTrue,
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
                [KeyMediaTmdbApiKey] = "",
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
        MigrateClientKeys();
        MigrateFolderListSeparator();

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
    // The client-prefixed names every client section used before the key names were unified, paired
    // with the shared name that replaced each one. Ordered per section; a section's entries are
    // independent, so a partially migrated section completes on the next run.
    private static readonly (string Section, string LegacyKey, string NewKey)[] _legacyClientKeys =
    [
        (SectionQBittorrent, "qBittorrentURL",         KeyUrl),
        (SectionQBittorrent, "qBittorrentUserName",    KeyUserName),
        (SectionQBittorrent, "qBittorrentPassword",    KeyPassword),
        (SectionQBittorrent, "qBittorrentExePath",     KeyExePath),
        (SectionQBittorrent, "qBittorrentProcessName", KeyProcessName),
        (SectionQBittorrent, "restartqBittorrent",     KeyRestart),
        (SectionQBittorrent, "forceStartqBittorrent",  KeyForceStart),

        (SectionTransmission, "transmissionURL",         KeyUrl),
        (SectionTransmission, "transmissionUserName",    KeyUserName),
        (SectionTransmission, "transmissionPassword",    KeyPassword),
        (SectionTransmission, "transmissionExePath",     KeyExePath),
        (SectionTransmission, "transmissionProcessName", KeyProcessName),
        (SectionTransmission, "restartTransmission",     KeyRestart),
        (SectionTransmission, "forceStartTransmission",  KeyForceStart),

        (SectionDeluge, "delugeURL",         KeyUrl),
        (SectionDeluge, "delugePassword",    KeyPassword),
        (SectionDeluge, "delugeExePath",     KeyExePath),
        (SectionDeluge, "delugeProcessName", KeyProcessName),
        (SectionDeluge, "restartDeluge",     KeyRestart),
        (SectionDeluge, "forceStartDeluge",  KeyForceStart),

        // Nicotine+ never shipped, but dev and beta machines carry these from 2.6.4 pre-releases.
        (SectionNicotine, "nicotineURL",         KeyUrl),
        (SectionNicotine, "nicotineToken",       KeyPassword),
        (SectionNicotine, "nicotineExePath",     KeyExePath),
        (SectionNicotine, "nicotineProcessName", KeyProcessName),
        (SectionNicotine, "forceStartNicotine",  KeyForceStart),
    ];

    // Moves each client section's values from their old client-prefixed names to the shared names.
    //
    // Values are copied as raw objects and never decrypted: three of these hold DPAPI blobs, and a
    // decrypt/re-encrypt round trip would turn any single failure into a lost password. Copy first,
    // delete only once the write is confirmed, and skip a key whose new name already holds a value,
    // so an interrupted run resumes safely and a second run is a no-op.
    //
    // Must run before EnsureDefaults, or the defaults would populate the new names first and every
    // real setting would be left behind under its old name.
    private static void MigrateClientKeys()
    {
        int moved = 0;
        foreach (var (section, legacyKey, newKey) in _legacyClientKeys)
        {
            try
            {
                using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{section}", writable: true);
                if (regKey?.GetValue(legacyKey) is not object legacyValue) continue;

                if (regKey.GetValue(newKey) is null)
                {
                    regKey.SetValue(newKey, legacyValue, regKey.GetValueKind(legacyKey));
                    // Read back before discarding the only other copy.
                    if (regKey.GetValue(newKey) is null) continue;
                    moved++;
                }
                regKey.DeleteValue(legacyKey, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                // Leave the legacy value in place: the setting falls back to its default for now,
                // and the next start retries. Losing the value would be the worse outcome.
                LogManager.Instance.LogDebug(
                    $"RegistrySettingsManager.MigrateClientKeys: [{section}] {legacyKey} - {ex.Message}");
            }
        }

        if (moved > 0)
            LogManager.Instance.LogMessage($"Migrated {moved} client settings to the shared registry key names", LogLevel.Info);
    }

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

    /// <summary>Rewrites a folder list still stored with the former <c>;</c> separator onto <see cref="ListSeparator"/>, once, on startup.</summary>
    /// <remarks>
    /// <para>Only migrates when every <c>;</c>-delimited segment is a rooted path. That is what tells a
    /// legacy multi-folder value apart from a single folder whose own name contains a <c>;</c> - the case
    /// the separator change exists to fix. A single path cannot contain a rooted-looking segment, because
    /// <c>:</c> and <c>\</c> are both invalid in a folder name, so the test cannot misfire.</para>
    /// <para>Idempotent: after the rewrite no <c>;</c> remains, and a value that fails the test is left
    /// alone and then reads correctly as one folder under the new separator.</para>
    /// </remarks>
    private static void MigrateFolderListSeparator()
    {
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey($@"{BaseKeyPath}\{SectionMedia}", writable: true);
            if (regKey?.GetValue(KeyMediaSourceFolders) is not string stored || !stored.Contains(';')) return;

            var segments = stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2 || !Array.TrueForAll(segments, Path.IsPathRooted)) return;

            regKey.SetValue(KeyMediaSourceFolders, string.Join(ListSeparator, segments), RegistryValueKind.String);
            LogManager.Instance.LogMessage($"Migrated {segments.Length} media source folders to the new list separator", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"RegistrySettingsManager.MigrateFolderListSeparator: {ex.Message}");
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
        GetEncryptedValue(SectionQBittorrent, KeyPassword);

    /// <summary>Reads the Transmission password from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetTransmissionPassword() =>
        GetEncryptedValue(SectionTransmission, KeyPassword);

    /// <summary>Reads the Deluge password from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetDelugePassword() =>
        GetEncryptedValue(SectionDeluge, KeyPassword);

    /// <summary>Reads the Nicotine+ bridge plugin token from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetNicotineToken() =>
        GetEncryptedValue(SectionNicotine, KeyPassword);

    /// <summary>Reads the TMDB API key from the registry and decrypts it with DPAPI (CurrentUser scope). Returns an empty string if missing or decryption fails.</summary>
    public static string GetTmdbApiKey() =>
        GetEncryptedValue(SectionMedia, KeyMediaTmdbApiKey);

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
        SetEncryptedValue(SectionQBittorrent, KeyPassword, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetTransmissionPassword(string plaintext) =>
        SetEncryptedValue(SectionTransmission, KeyPassword, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetDelugePassword(string plaintext) =>
        SetEncryptedValue(SectionDeluge, KeyPassword, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetNicotineToken(string plaintext) =>
        SetEncryptedValue(SectionNicotine, KeyPassword, plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> with DPAPI (CurrentUser scope) and writes the result to the registry.</summary>
    public static void SetTmdbApiKey(string plaintext) =>
        SetEncryptedValue(SectionMedia, KeyMediaTmdbApiKey, plaintext);

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
        // One entry covers all four client sections: they share the key name, and these sets are
        // matched on the key name alone, independently of which section it was read from.
        KeyPassword,
        KeyMediaTmdbApiKey
    };

    // Keys whose values must never be written to logs in plaintext. Superset of _encryptedKeys
    // plus app-level secrets stored plaintext but protected by the HKCU ACL (e.g. the pipe
    // session token used to authenticate messages to the SYSTEM helper service).
    private static readonly HashSet<string> _logMaskedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        KeyPassword,
        KeyMediaTmdbApiKey,
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

namespace qbPortWeaver;

/// <summary>
/// Single source of truth for everything per-client: the stored setting value (also the display
/// name), the registry section and key names for its connection settings, its process names and
/// default install location (used by <see cref="ClientDetector"/>), and a factory that constructs
/// the <see cref="IBitTorrentClient"/> from a <see cref="ClientConfig"/>. Defining them once here
/// keeps construction, config reading, and detection from drifting apart.
/// <para>Adding a client: add one entry here (its key names and construction factory) plus its
/// Settings UI. <see cref="PortSyncService"/> reads the active client's config, constructs it, and
/// logs it entirely from this table, so no per-client branch is needed there; detection and
/// resolution pick up the new entry automatically.</para>
/// </summary>
internal static class ClientRegistry
{
    /// <summary>
    /// The per-client facts. <paramref name="Name"/> is both the stored client setting value and the
    /// user-facing display name (the same string in this app). <paramref name="UserNameKey"/> is
    /// <see langword="null"/> for clients that do not authenticate by username (e.g. Deluge).
    /// The default-port setting shares one key (<see cref="RegistrySettingsManager.KeyDefaultPort"/>)
    /// across all clients, so it is not stored here. <paramref name="ProcessNames"/>[0] doubles as the
    /// default process-name field value; <paramref name="DefaultExeFolder"/>/<paramref name="DefaultExeFile"/>
    /// are resolved under the real Program Files folders at runtime. <paramref name="Factory"/> builds
    /// the client from the config block <see cref="PortSyncService"/> reads for the active client.
    /// </summary>
    internal sealed record ClientInfo(
        string Name,
        string Section,
        string UrlKey,
        string? UserNameKey,
        string PasswordKey,
        string ProcessNameKey,
        string ExePathKey,
        string RestartKey,
        string ForceStartKey,
        string[] ProcessNames,
        string DefaultExeFolder,
        string DefaultExeFile,
        Func<ClientConfig, IBitTorrentClient> Factory);

    // qBittorrent is listed first: it is the default when the stored value is missing or unrecognized,
    // and detection probes candidates in this order so the result is deterministic.
    private static readonly ClientInfo[] _clients =
    [
        new(Name: RegistrySettingsManager.BitTorrentClientQBittorrent, Section: RegistrySettingsManager.SectionQBittorrent,
            UrlKey: RegistrySettingsManager.KeyQBittorrentUrl, UserNameKey: RegistrySettingsManager.KeyQBittorrentUserName,
            PasswordKey: RegistrySettingsManager.KeyQBittorrentPassword, ProcessNameKey: RegistrySettingsManager.KeyQBittorrentProcessName,
            ExePathKey: RegistrySettingsManager.KeyQBittorrentExePath, RestartKey: RegistrySettingsManager.KeyRestartQBittorrent,
            ForceStartKey: RegistrySettingsManager.KeyForceStartQBittorrent,
            ProcessNames: ["qbittorrent"], DefaultExeFolder: "qBittorrent", DefaultExeFile: "qbittorrent.exe",
            Factory: c => new QBittorrentClient(c.Url, c.UserName, c.Password, c.ProcessName, c.ExePath)),

        new(Name: RegistrySettingsManager.BitTorrentClientTransmission, Section: RegistrySettingsManager.SectionTransmission,
            UrlKey: RegistrySettingsManager.KeyTransmissionUrl, UserNameKey: RegistrySettingsManager.KeyTransmissionUserName,
            PasswordKey: RegistrySettingsManager.KeyTransmissionPassword, ProcessNameKey: RegistrySettingsManager.KeyTransmissionProcessName,
            ExePathKey: RegistrySettingsManager.KeyTransmissionExePath, RestartKey: RegistrySettingsManager.KeyRestartTransmission,
            ForceStartKey: RegistrySettingsManager.KeyForceStartTransmission,
            ProcessNames: ["transmission-qt", "transmission-daemon"], DefaultExeFolder: "Transmission", DefaultExeFile: "transmission-qt.exe",
            Factory: c => new TransmissionClient(c.Url, c.UserName, c.Password, c.ProcessName, c.ExePath)),

        // Deluge has no username field (Web UI uses password only), so UserNameKey is null.
        new(Name: RegistrySettingsManager.BitTorrentClientDeluge, Section: RegistrySettingsManager.SectionDeluge,
            UrlKey: RegistrySettingsManager.KeyDelugeUrl, UserNameKey: null,
            PasswordKey: RegistrySettingsManager.KeyDelugePassword, ProcessNameKey: RegistrySettingsManager.KeyDelugeProcessName,
            ExePathKey: RegistrySettingsManager.KeyDelugeExePath, RestartKey: RegistrySettingsManager.KeyRestartDeluge,
            ForceStartKey: RegistrySettingsManager.KeyForceStartDeluge,
            ProcessNames: ["deluge", "deluged"], DefaultExeFolder: "Deluge", DefaultExeFile: "deluge.exe",
            Factory: c => new DelugeClient(c.Url, c.Password, c.ProcessName, c.ExePath)),
    ];

    /// <summary>All supported clients, in canonical order (qBittorrent, Transmission, Deluge).</summary>
    internal static IReadOnlyList<ClientInfo> All => _clients;

    /// <summary>Resolves a stored client setting value to its facts, defaulting to qBittorrent when unrecognized.</summary>
    internal static ClientInfo Resolve(string clientSetting) =>
        Array.Find(_clients, c => c.Name.Equals(clientSetting, StringComparison.OrdinalIgnoreCase)) ?? _clients[0];
}

/// <summary>
/// Per-client connection and behaviour settings for one sync cycle, built by
/// <see cref="PortSyncService"/> for the active client only and consumed by that client's
/// <see cref="ClientRegistry.ClientInfo.Factory"/>. <see cref="UserName"/> is empty for clients that
/// do not authenticate by username (e.g. Deluge).
/// </summary>
internal sealed record ClientConfig(
    string Url,
    string UserName,
    string Password,
    string ProcessName,
    string ExePath,
    bool Restart,
    bool ForceStart,
    int DefaultPort);

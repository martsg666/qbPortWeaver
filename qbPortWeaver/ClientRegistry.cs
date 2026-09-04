namespace qbPortWeaver;

/// <summary>
/// Single source of truth for everything per-client: the stored setting value (also the display
/// name), the registry section and key names for its connection settings, its process names and
/// default install location (used by <see cref="ClientDetector"/>), and a factory that constructs
/// the <see cref="IManagedClient"/> from a <see cref="ClientConfig"/>. Defining them once here
/// keeps construction, config reading, and detection from drifting apart.
/// <para>Adding a client: add one entry here (its key names and construction factory) plus its
/// Settings UI. <see cref="PortSyncService"/> reads the active client's config and constructs it
/// entirely from this table, and detection and resolution pick up the new entry automatically.</para>
/// <para><b>The three exclusive settings are covered by this table too</b>, as of 2.6.8:
/// <c>warnOnInterfaceMismatch</c> (qBittorrent and Nicotine+), <c>restartOnDisconnect</c> and
/// <c>fixInterfaceBinding</c> (qBittorrent only) are nullable keys on <see cref="ClientInfo"/>,
/// exactly like <c>UserNameKey</c> and <c>RestartKey</c>, and their values ride on
/// <see cref="ClientConfig"/> with the rest of the per-client block.</para>
/// <para>They previously lived as flat fields on <c>AppConfig</c>, resolved by comparing the active
/// section against hardcoded client identities in <b>two</b> places -
/// <c>PortSyncService.GetClientBehaviorConfig</c> and <c>PortSyncService.LogConfigDebug</c>. Because
/// the two sites were independent, they could drift: Nicotine+'s <c>warnOnInterfaceMismatch</c> was
/// honoured by the first while missing from the second until 2.6.8, leaving a support log unable to
/// say whether the one warning that client has a setting for was even enabled. Both now read the key
/// from this row, so behaviour and reporting cannot disagree. The Settings UI is still separate, as
/// it is for every other key here - see the note on the fields themselves.</para>
/// </summary>
internal static class ClientRegistry
{
    /// <summary>
    /// The per-client facts. <paramref name="Name"/> is both the stored client setting value and the
    /// user-facing display name (the same string in this app), and <paramref name="Section"/> is the
    /// registry section holding its settings.
    /// <para>The key fields carry that client's own <c>Key*</c> constants from
    /// <see cref="RegistrySettingsManager"/>. Every client section stores the same names, so these all
    /// resolve to the same strings; they exist so that code which runs against whichever client is
    /// active reaches its settings through the client it was handed, rather than naming one client's
    /// constant and relying on the strings happening to match.</para>
    /// <para><paramref name="UserNameKey"/> is <see langword="null"/> where the client does not
    /// authenticate by user name (Deluge, Nicotine+); its single secret still lives under
    /// <paramref name="PasswordKey"/> like everyone else's - a Web UI password for three of them, the
    /// bridge plugin's bearer token for Nicotine+. <paramref name="RestartKey"/> is
    /// <see langword="null"/> where the client is never restarted (Nicotine+), which is also why it
    /// has no restart checkbox in Settings: a setting that cannot change anything should not exist in
    /// the registry to be hand-edited.</para>
    /// <paramref name="ProcessNames"/>[0] doubles as the default process-name field value;
    /// <paramref name="DefaultExeFolder"/>/<paramref name="DefaultExeFile"/> are resolved under the
    /// real Program Files folders at runtime. <paramref name="Factory"/> builds the client from the
    /// config block <see cref="PortSyncService"/> reads for the active client.
    /// </summary>
    internal sealed record ClientInfo(
        string Name,
        string Section,
        string UrlKey,
        string? UserNameKey,
        string PasswordKey,
        string ExePathKey,
        string ProcessNameKey,
        string? RestartKey,
        string ForceStartKey,
        string DefaultPortKey,
        // The three exclusive settings, null for a client that does not have one. Nullable for the
        // same reason UserNameKey and RestartKey are: absence is a property of the client, and a null
        // key is what both the config read and the debug dump branch on, so neither can claim a
        // setting the client does not have. Declaring the key here is the single act that turns a
        // setting on *in the sync loop*, so behaviour and the debug dump can no longer disagree - but
        // it is not the only place: the Settings UI still declares its own checkbox per client, as it
        // does for every other key here. A key declared with no checkbox behind it reads false
        // forever, and the dump would report a setting the user has no way to enable.
        string? WarnOnInterfaceMismatchKey,
        string? RestartOnDisconnectKey,
        string? FixInterfaceBindingKey,
        string[] ProcessNames,
        string DefaultExeFolder,
        string DefaultExeFile,
        Func<ClientConfig, IManagedClient> Factory);

    // qBittorrent is listed first: it is the default when the stored value is missing or unrecognized,
    // and detection probes candidates in this order so the result is deterministic.
    private static readonly ClientInfo[] _clients =
    [
        new(Name: RegistrySettingsManager.ClientNameQBittorrent, Section: RegistrySettingsManager.SectionQBittorrent,
            UrlKey: RegistrySettingsManager.KeyQBittorrentUrl, UserNameKey: RegistrySettingsManager.KeyQBittorrentUserName,
            PasswordKey: RegistrySettingsManager.KeyQBittorrentPassword, ExePathKey: RegistrySettingsManager.KeyQBittorrentExePath,
            ProcessNameKey: RegistrySettingsManager.KeyQBittorrentProcessName, RestartKey: RegistrySettingsManager.KeyQBittorrentRestart,
            ForceStartKey: RegistrySettingsManager.KeyQBittorrentForceStart, DefaultPortKey: RegistrySettingsManager.KeyQBittorrentDefaultPort,
            WarnOnInterfaceMismatchKey: RegistrySettingsManager.KeyQBittorrentWarnOnInterfaceMismatch,
            RestartOnDisconnectKey: RegistrySettingsManager.KeyQBittorrentRestartOnDisconnect,
            FixInterfaceBindingKey: RegistrySettingsManager.KeyQBittorrentFixInterfaceBinding,
            ProcessNames: ["qbittorrent"], DefaultExeFolder: "qBittorrent", DefaultExeFile: "qbittorrent.exe",
            Factory: c => new QBittorrentClient(c.Url, c.UserName, c.Password, c.ProcessName, c.ExePath)),

        new(Name: RegistrySettingsManager.ClientNameTransmission, Section: RegistrySettingsManager.SectionTransmission,
            UrlKey: RegistrySettingsManager.KeyTransmissionUrl, UserNameKey: RegistrySettingsManager.KeyTransmissionUserName,
            PasswordKey: RegistrySettingsManager.KeyTransmissionPassword, ExePathKey: RegistrySettingsManager.KeyTransmissionExePath,
            ProcessNameKey: RegistrySettingsManager.KeyTransmissionProcessName, RestartKey: RegistrySettingsManager.KeyTransmissionRestart,
            ForceStartKey: RegistrySettingsManager.KeyTransmissionForceStart, DefaultPortKey: RegistrySettingsManager.KeyTransmissionDefaultPort,
            // All three null: Transmission reports a bind address rather than an adapter name, so there
            // is no name to compare (see IManagedClient.SupportsInterfaceMismatchWarning), and the
            // restart-on-disconnect and binding repairs are qBittorrent-only.
            WarnOnInterfaceMismatchKey: null, RestartOnDisconnectKey: null, FixInterfaceBindingKey: null,
            ProcessNames: ["transmission-qt", "transmission-daemon"], DefaultExeFolder: "Transmission", DefaultExeFile: "transmission-qt.exe",
            Factory: c => new TransmissionClient(c.Url, c.UserName, c.Password, c.ProcessName, c.ExePath)),

        // Deluge's Web UI authenticates with a password alone, so it has no user name.
        new(Name: RegistrySettingsManager.ClientNameDeluge, Section: RegistrySettingsManager.SectionDeluge,
            UrlKey: RegistrySettingsManager.KeyDelugeUrl, UserNameKey: null,
            PasswordKey: RegistrySettingsManager.KeyDelugePassword, ExePathKey: RegistrySettingsManager.KeyDelugeExePath,
            ProcessNameKey: RegistrySettingsManager.KeyDelugeProcessName, RestartKey: RegistrySettingsManager.KeyDelugeRestart,
            ForceStartKey: RegistrySettingsManager.KeyDelugeForceStart, DefaultPortKey: RegistrySettingsManager.KeyDelugeDefaultPort,
            // All three null, for the same reasons as Transmission.
            WarnOnInterfaceMismatchKey: null, RestartOnDisconnectKey: null, FixInterfaceBindingKey: null,
            ProcessNames: ["deluge", "deluged"], DefaultExeFolder: "Deluge", DefaultExeFile: "deluge.exe",
            Factory: c => new DelugeClient(c.Url, c.Password, c.ProcessName, c.ExePath)),

        // Nicotine+ is a Soulseek client rather than a BitTorrent one, driven through the
        // qbPortWeaver bridge plugin's local API. It authenticates with a token the plugin issues
        // rather than a user name and password, so the token occupies the shared password slot. The
        // '+' in the process name is literal: Process.GetProcessesByName compares exactly, so
        // "Nicotine+" matches "Nicotine+.exe" and nothing else.
        // RestartKey is null because Nicotine+ is never restarted: the bridge applies the port to
        // the running client, so a restart would fix nothing, and killing the process would discard
        // its configuration (Nicotine+ only saves on a graceful shutdown). See
        // NicotineClient.RestartAsync, which is a no-op for the same reason.
        new(Name: RegistrySettingsManager.ClientNameNicotine, Section: RegistrySettingsManager.SectionNicotine,
            UrlKey: RegistrySettingsManager.KeyNicotineUrl, UserNameKey: null,
            PasswordKey: RegistrySettingsManager.KeyNicotinePassword, ExePathKey: RegistrySettingsManager.KeyNicotineExePath,
            ProcessNameKey: RegistrySettingsManager.KeyNicotineProcessName, RestartKey: null,
            ForceStartKey: RegistrySettingsManager.KeyNicotineForceStart, DefaultPortKey: RegistrySettingsManager.KeyNicotineDefaultPort,
            // Nicotine+ reports an adapter name, so the mismatch warning applies to it as it does to
            // qBittorrent. The other two do not: it is never restarted (see RestartKey above), and the
            // stale-binding repair reads and writes qBittorrent's own configuration.
            WarnOnInterfaceMismatchKey: RegistrySettingsManager.KeyNicotineWarnOnInterfaceMismatch,
            RestartOnDisconnectKey: null, FixInterfaceBindingKey: null,
            ProcessNames: ["Nicotine+"], DefaultExeFolder: "Nicotine+", DefaultExeFile: "Nicotine+.exe",
            Factory: c => new NicotineClient(c.Url, c.Password, c.ProcessName, c.ExePath)),
    ];

    /// <summary>All supported clients, in canonical order (qBittorrent, Transmission, Deluge, Nicotine+).</summary>
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
    int DefaultPort,
    bool WarnOnInterfaceMismatch,
    bool RestartOnDisconnect,
    bool FixInterfaceBinding);

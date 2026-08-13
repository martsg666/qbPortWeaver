namespace qbPortWeaver;

/// <summary>
/// Single source of truth for everything per-client: the stored setting value (also the display
/// name), the registry section and key names for its connection settings, its process names and
/// default install location (used by <see cref="ClientDetector"/>), and a factory that constructs
/// the <see cref="IManagedClient"/> from a <see cref="ClientConfig"/>. Defining them once here
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
    /// user-facing display name (the same string in this app), and <paramref name="Section"/> is the
    /// registry section holding its settings.
    /// <para>No key names are stored here. Every client section uses the same key names (see the
    /// <c>Key*</c> constants on <see cref="RegistrySettingsManager"/>), so the section alone
    /// identifies a setting. What does vary is which of those keys a client has at all, and that is
    /// what <paramref name="HasUserName"/> and <paramref name="HasRestart"/> record:</para>
    /// <list type="bullet">
    /// <item><description><paramref name="HasUserName"/> is <see langword="false"/> where the client
    /// does not authenticate by user name (Deluge, Nicotine+). Its single secret is then stored under
    /// <see cref="RegistrySettingsManager.KeyPassword"/> like everyone else's - a Web UI password for
    /// three of them, the bridge plugin's bearer token for Nicotine+.</description></item>
    /// <item><description><paramref name="HasRestart"/> is <see langword="false"/> where the client is
    /// never restarted (Nicotine+), which is also why it has no restart checkbox in Settings: a
    /// setting that cannot change anything should not exist in the registry to be hand-edited.</description></item>
    /// </list>
    /// <paramref name="ProcessNames"/>[0] doubles as the default process-name field value;
    /// <paramref name="DefaultExeFolder"/>/<paramref name="DefaultExeFile"/> are resolved under the
    /// real Program Files folders at runtime. <paramref name="Factory"/> builds the client from the
    /// config block <see cref="PortSyncService"/> reads for the active client.
    /// </summary>
    internal sealed record ClientInfo(
        string Name,
        string Section,
        bool HasUserName,
        bool HasRestart,
        string[] ProcessNames,
        string DefaultExeFolder,
        string DefaultExeFile,
        Func<ClientConfig, IManagedClient> Factory);

    // qBittorrent is listed first: it is the default when the stored value is missing or unrecognized,
    // and detection probes candidates in this order so the result is deterministic.
    private static readonly ClientInfo[] _clients =
    [
        new(Name: RegistrySettingsManager.ClientNameQBittorrent, Section: RegistrySettingsManager.SectionQBittorrent,
            HasUserName: true, HasRestart: true,
            ProcessNames: ["qbittorrent"], DefaultExeFolder: "qBittorrent", DefaultExeFile: "qbittorrent.exe",
            Factory: c => new QBittorrentClient(c.Url, c.UserName, c.Password, c.ProcessName, c.ExePath)),

        new(Name: RegistrySettingsManager.ClientNameTransmission, Section: RegistrySettingsManager.SectionTransmission,
            HasUserName: true, HasRestart: true,
            ProcessNames: ["transmission-qt", "transmission-daemon"], DefaultExeFolder: "Transmission", DefaultExeFile: "transmission-qt.exe",
            Factory: c => new TransmissionClient(c.Url, c.UserName, c.Password, c.ProcessName, c.ExePath)),

        // Deluge's Web UI authenticates with a password alone, so it has no user name.
        new(Name: RegistrySettingsManager.ClientNameDeluge, Section: RegistrySettingsManager.SectionDeluge,
            HasUserName: false, HasRestart: true,
            ProcessNames: ["deluge", "deluged"], DefaultExeFolder: "Deluge", DefaultExeFile: "deluge.exe",
            Factory: c => new DelugeClient(c.Url, c.Password, c.ProcessName, c.ExePath)),

        // Nicotine+ is a Soulseek client rather than a BitTorrent one, driven through the
        // qbPortWeaver bridge plugin's local API. It authenticates with a token the plugin issues
        // rather than a user name and password, so the token occupies the shared password slot. The
        // '+' in the process name is literal: Process.GetProcessesByName compares exactly, so
        // "Nicotine+" matches "Nicotine+.exe" and nothing else.
        // HasRestart is false because Nicotine+ is never restarted: the bridge applies the port to
        // the running client, so a restart would fix nothing, and killing the process would discard
        // its configuration (Nicotine+ only saves on a graceful shutdown). See
        // NicotineClient.RestartAsync, which is a no-op for the same reason.
        new(Name: RegistrySettingsManager.ClientNameNicotine, Section: RegistrySettingsManager.SectionNicotine,
            HasUserName: false, HasRestart: false,
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
    int DefaultPort);

namespace qbPortWeaver;

/// <summary>
/// Single source of truth for the static facts about each supported BitTorrent client: its stored
/// setting value (also the display name), its registry section and URL key (used by resolution
/// helpers), and its process names and default install location (used by <see cref="ClientDetector"/>).
/// Defining them once here keeps the per-client switches and the detection table from drifting apart.
/// <para>Adding a client: add an entry here, then wire its construction
/// (<see cref="PortSyncService"/>.CreateBitTorrentClient), its config (Build*Config), and its Settings
/// UI - those stay per-client by nature. Detection picks up the new entry automatically.</para>
/// </summary>
internal static class ClientRegistry
{
    /// <summary>
    /// The static facts about one supported client. <paramref name="Name"/> is both the stored client
    /// setting value and the user-facing display name (the same string in this app).
    /// <paramref name="ProcessNames"/>[0] doubles as the default process-name field value;
    /// <paramref name="DefaultExeFolder"/>/<paramref name="DefaultExeFile"/> are resolved under the real
    /// Program Files folders at runtime.
    /// </summary>
    internal sealed record ClientInfo(
        string Name,
        string Section,
        string UrlKey,
        string[] ProcessNames,
        string DefaultExeFolder,
        string DefaultExeFile);

    // qBittorrent is listed first: it is the default when the stored value is missing or unrecognized,
    // and detection probes candidates in this order so the result is deterministic.
    private static readonly ClientInfo[] _clients =
    [
        new(RegistrySettingsManager.BitTorrentClientQBittorrent,  RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyQBittorrentUrl,  ["qbittorrent"],                            "qBittorrent",  "qbittorrent.exe"),
        new(RegistrySettingsManager.BitTorrentClientTransmission, RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUrl, ["transmission-qt", "transmission-daemon"], "Transmission", "transmission-qt.exe"),
        new(RegistrySettingsManager.BitTorrentClientDeluge,       RegistrySettingsManager.SectionDeluge,       RegistrySettingsManager.KeyDelugeUrl,       ["deluge", "deluged"],                      "Deluge",       "deluge.exe"),
    ];

    /// <summary>All supported clients, in canonical order (qBittorrent, Transmission, Deluge).</summary>
    internal static IReadOnlyList<ClientInfo> All => _clients;

    /// <summary>Resolves a stored client setting value to its facts, defaulting to qBittorrent when unrecognized.</summary>
    internal static ClientInfo Resolve(string clientSetting) =>
        Array.Find(_clients, c => c.Name.Equals(clientSetting, StringComparison.OrdinalIgnoreCase)) ?? _clients[0];
}

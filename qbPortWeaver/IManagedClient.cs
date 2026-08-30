namespace qbPortWeaver;

/// <summary>
/// Client-agnostic contract for reading and setting the listening port and driving process
/// lifecycle for a peer-to-peer client. Implemented by qBittorrent, Transmission, and Deluge
/// (BitTorrent) and Nicotine+ (Soulseek); the contract carries no protocol-specific assumptions.
/// </summary>
public interface IManagedClient : IDisposable
{
    /// <summary>
    /// Display name of the client.
    /// Used in log messages and status output in place of hard-coded client names.
    /// </summary>
    string ClientName { get; }

    /// <summary>
    /// Returns <see langword="true"/> if this client supports network interface mismatch warnings.
    /// qBittorrent and Nicotine+ expose a named adapter, enabling the check. Transmission and Deluge
    /// report a bind address instead, so there is no name to compare and the check is skipped for them.
    /// <para>That is a statement about the *name* check only, and not about their exposure to a moved
    /// address. Measured on a live pair: Transmission binds a wildcard socket (<c>0.0.0.0</c>, its
    /// default) and so cannot be stranded by a rotating tunnel address, while Deluge binds every local
    /// address individually even with <c>listen_interface</c> empty, which is the same shape that
    /// strands qBittorrent. Deluge is therefore exposed to what the qBittorrent address check closes,
    /// and deliberately has no equivalent: qBittorrent is by a wide margin the most used of the four,
    /// so that check earns its complexity there and not in a second copy carrying its own client quirks
    /// to verify and keep in step. The residual exposure is the same narrow window, and a port write
    /// repairs Deluge just as it repairs qBittorrent. A settled decision, not an oversight.</para>
    /// </summary>
    bool SupportsInterfaceMismatchWarning { get; }

    /// <summary>
    /// Returns <see langword="true"/> if the client process is currently running.
    /// </summary>
    bool IsRunning();

    /// <summary>
    /// Launches the client and returns <see langword="true"/> if it starts successfully.
    /// For clients running as a Windows service, starts the service.
    /// For clients running as a user-space process, launches the executable directly.
    /// </summary>
    Task<bool> ForceStartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts the client and returns <see langword="true"/> on success.
    /// For clients running as a Windows service, delegates to the helper service (Session 0).
    /// For clients running as a user-space process, behaviour is client-defined: some kill and
    /// relaunch the executable; others are no-ops if the port change is already live in-place.
    /// </summary>
    Task<bool> RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current listening port and the bound network interface's <b>display name</b> from
    /// the client's settings.
    /// <para>The name is what the interface mismatch check compares, for the clients that report one
    /// (qBittorrent, Nicotine+). It is deliberately not the whole story for qBittorrent, which stores
    /// the binding as a separate opaque identifier: the name can still read correctly while that
    /// identifier no longer resolves, so <see cref="QBittorrentClient"/> validates the identifier on
    /// its own rather than through this contract. Do not treat a plausible name here as proof that
    /// the client is actually bound to that adapter.</para>
    /// <para>Transmission and Deluge report a bind IPv4 address instead, which is read but not
    /// consumed: the VPN-assigned IP rotates on reconnection, so it is not a reliable signal -
    /// users should rely on the VPN client's killswitch instead. Both therefore return
    /// <see cref="SupportsInterfaceMismatchWarning"/> = <see langword="false"/>.</para>
    /// Returns <c>(null, null)</c> if the client is unreachable or the values cannot be read.
    /// </summary>
    Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the client's listening port. Returns <see langword="true"/> on success.
    /// </summary>
    Task<bool> SetListeningPortAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the client's current connection status string, or <see langword="null"/> if unsupported or unreachable.
    /// For qBittorrent this is one of "connected", "firewalled", or "disconnected".
    /// For clients that do not expose connection status, always returns <see langword="null"/>.
    /// </summary>
    Task<string?> GetConnectionStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests whether the listening port is reachable from outside. Returns
    /// <see langword="true"/> when open, <see langword="false"/> when closed, or
    /// <see langword="null"/> when it cannot be determined (client unreachable, no internet,
    /// or port-test service unavailable).
    /// Transmission, Deluge and Nicotine+ actively probe via their projects' online port-check services.
    /// qBittorrent infers the result from incoming peer activity, so an idle client may report
    /// closed even when the port is open - callers should confirm before alerting.
    /// </summary>
    Task<bool?> TestListeningPortAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the client's own settings that are currently working against the synchronized port -
    /// randomised ports, and the client's built-in UPnP/NAT-PMP port mapping.
    /// <para>These are written to a safe value whenever the port is set, so this exists to catch the
    /// window in between: a user can enable one in the client's UI at any time, and nothing corrects
    /// it until the VPN's port next changes, which may be days. That interval is the "every check
    /// passes and the port is still wrong" case, and no other check can see it.</para>
    /// <para>Read-only and best-effort. An empty list means the settings were read and none of them
    /// conflict; <see langword="null"/> means they could not be read at all, which is a different
    /// thing and must stay distinguishable - a caller that collapsed the two would report a clean
    /// configuration for a check that never ran. Settings a client does not expose are omitted rather
    /// than guessed at.</para>
    /// </summary>
    Task<IReadOnlyList<ClientSettingConflict>?> GetConflictingSettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One client setting found working against the synchronized port.
/// </summary>
/// <param name="SettingName">The setting as the user sees it in the client's own UI, so the report
/// names something they can actually go and find, not the underlying protocol key.</param>
/// <param name="Effect">What it does to the forwarded port, in one sentence.</param>
public sealed record ClientSettingConflict(string SettingName, string Effect);

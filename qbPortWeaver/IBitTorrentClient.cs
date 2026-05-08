namespace qbPortWeaver
{
    /// <summary>
    /// Client-agnostic contract for reading and setting the listening port and driving
    /// process lifecycle for a BitTorrent client (qBittorrent, Transmission, etc.).
    /// </summary>
    public interface IBitTorrentClient : IDisposable
    {
        /// <summary>
        /// Display name of the BitTorrent client.
        /// Used in log messages and status output in place of hard-coded client names.
        /// </summary>
        string ClientName { get; }

        /// <summary>
        /// Returns <see langword="true"/> if this client supports network interface mismatch warnings.
        /// qBittorrent exposes a named adapter via its API, enabling the check.
        /// Transmission and Deluge do not expose adapter names via their APIs, so the check is skipped.
        /// </summary>
        bool SupportsInterfaceMismatchWarning { get; }

        /// <summary>
        /// Returns <see langword="true"/> if the BitTorrent client process is currently running.
        /// </summary>
        bool IsRunning();

        /// <summary>
        /// Launches the BitTorrent client and returns <see langword="true"/> if it starts successfully.
        /// For clients running as a Windows service, starts the service.
        /// For clients running as a user-space process, launches the executable directly.
        /// </summary>
        Task<bool> ForceStartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Restarts the BitTorrent client and returns <see langword="true"/> on success.
        /// For clients running as a Windows service, delegates to the helper service (Session 0).
        /// For clients running as a user-space process, behaviour is client-defined: some kill and
        /// relaunch the executable; others are no-ops if the port change is already live in-place.
        /// </summary>
        Task<bool> RestartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the client's current listening port and bound interface identifier.
        /// The bound interface identifier is consumed only when
        /// <see cref="SupportsInterfaceMismatchWarning"/> is <see langword="true"/>:
        /// for qBittorrent it is the bound network adapter name (e.g. <c>"ProtonVPN TUN"</c>).
        /// Transmission and Deluge populate the value from their APIs (Transmission's
        /// <c>bind-address-ipv4</c>, Deluge's <c>listen_interface</c>) but those clients
        /// currently do not support interface mismatch detection - the value is returned
        /// for future use and is ignored by the interface-match logic today.
        /// Returns <c>(null, null)</c> if the client is unreachable or the values cannot be read.
        /// </summary>
        Task<(int? ListenPort, string? BoundInterfaceOrAddress)> GetPreferencesAsync(CancellationToken cancellationToken = default);

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
    }
}

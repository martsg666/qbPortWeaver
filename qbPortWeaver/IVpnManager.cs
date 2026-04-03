namespace qbPortWeaver
{
    public interface IVpnManager
    {
        /// <summary>
        /// Display name of the provider or gateway used for port detection.
        /// For ProtonVPN and PIA this is the provider name (e.g. "ProtonVPN", "PIA").
        /// For NAT-PMP this is the network adapter name of the responding gateway.
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Returns <c>true</c> if the provider or gateway is currently reachable and active.
        /// For ProtonVPN and PIA this means the VPN tunnel adapter is up.
        /// For NAT-PMP this means the configured network adapter is up (gateway responsiveness is verified at creation time).
        /// </summary>
        bool IsVpnConnected();

        /// <summary>
        /// Returns the externally-reachable forwarded port, or <c>null</c> if it cannot be determined.
        /// For ProtonVPN this is read from the client log file.
        /// For PIA this is queried via <c>piactl get portforward</c>.
        /// For NAT-PMP this is the external port assigned by the gateway via a UDP port-mapping request.
        /// </summary>
        Task<int?> GetVpnPortAsync();

        /// <summary>
        /// Returns the recovery target sent to the helper service for auto-recovery, or <c>null</c>
        /// if recovery is not supported.
        /// For ProtonVPN and PIA this is the provider token (e.g. "ProtonVPN", "PIA").
        /// For NAT-PMP this is the network adapter name (e.g. "ProtonVPN TUN").
        /// </summary>
        string? GetRecoveryTarget();
    }
}

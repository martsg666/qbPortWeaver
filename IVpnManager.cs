namespace qbPortWeaver
{
    public interface IVpnManager
    {
        /// <summary>
        /// Display name of the provider or gateway used for port detection.
        /// For ProtonVPN and PIA this is the provider name (e.g. "ProtonVPN", "PIA").
        /// For NAT-PMP this is the network adapter description of the responding gateway.
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Returns <c>true</c> if the provider or gateway is currently reachable and active.
        /// For ProtonVPN and PIA this means the VPN tunnel adapter is up.
        /// For NAT-PMP this means the configured adapter is up and its gateway is responding.
        /// </summary>
        bool IsVpnConnected();

        /// <summary>
        /// Returns the externally-reachable forwarded port, or <c>null</c> if it cannot be determined.
        /// For ProtonVPN this is read from the client log file.
        /// For PIA this is queried via <c>piactl get portforward</c>.
        /// For NAT-PMP this is the external port assigned by the gateway via a UDP port-mapping request.
        /// </summary>
        Task<int?> GetVpnPortAsync();
    }
}

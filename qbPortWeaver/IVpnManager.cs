namespace qbPortWeaver
{
    /// <summary>
    /// Provider-agnostic contract for reading the forwarded port and driving auto-recovery
    /// for a VPN tunnel (ProtonVPN, PIA) or NAT-PMP gateway.
    /// </summary>
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
        /// For NAT-PMP this is the provider token when the adapter belongs to a known provider,
        /// or the adapter name when it does not (e.g. a standalone NAT-PMP gateway).
        /// </summary>
        string? GetRecoveryTarget();

        /// <summary>
        /// Returns the auto-recovery action to request from the helper service.
        /// For ProtonVPN and PIA this is always <see cref="AutoRecoveryManager.ActionRestart"/>.
        /// For NAT-PMP this is <see cref="AutoRecoveryManager.ActionRestart"/> when the adapter belongs
        /// to a known provider, or <see cref="AutoRecoveryManager.ActionCycleAdapter"/> otherwise.
        /// </summary>
        string GetRecoveryAction();

        /// <summary>
        /// Returns <c>true</c> if <paramref name="interfaceName"/> matches this provider's adapter naming convention.
        /// For ProtonVPN this means the name contains "ProtonVPN".
        /// For PIA this means the name contains "PIA".
        /// For NAT-PMP this is a bidirectional contains check against the configured adapter name,
        /// since the name in settings and the Windows connection name may differ in length.
        /// </summary>
        bool IsAdapterMatch(string interfaceName);
    }
}

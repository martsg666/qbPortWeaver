namespace qbPortWeaver;

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
    /// Returns <see langword="true"/> if the provider or gateway is currently reachable and active.
    /// For ProtonVPN this means the tunnel adapter is up; for PIA it means piactl reports the connection
    /// state as Connected. For NAT-PMP this means the configured network adapter is up (gateway responsiveness is verified at creation time).
    /// </summary>
    bool IsVpnConnected();

    /// <summary>
    /// Returns the externally-reachable forwarded port, or <see langword="null"/> if it cannot be determined.
    /// For ProtonVPN this is read from the client log file.
    /// For PIA this is queried via <c>piactl get portforward</c>.
    /// For NAT-PMP this is the external port assigned by the gateway, requested as two RFC 6886
    /// mappings - UDP then TCP - since the protocols are independent and a gateway may grant one
    /// without the other. The returned port is the UDP grant; a TCP mapping that is refused or
    /// lands on a different port is reported but does not change the result.
    /// <para>Cancellation is best-effort and differs by provider. NAT-PMP genuinely aborts an
    /// in-flight request, while ProtonVPN and PIA wrap synchronous work in <c>Task.Run</c>, where the
    /// token can only prevent the work starting - once running it completes regardless. Both are
    /// bounded anyway (PIA by piactl's process timeout, ProtonVPN by the size of the log scan), and
    /// shutdown does not await the sync loop, so uncancelled work dies with the process rather than
    /// delaying exit. Do not rely on the token to shorten an in-flight port read.</para>
    /// </summary>
    Task<int?> GetVpnPortAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the most recent <see cref="GetVpnPortAsync"/> call established that no forwarded port will
    /// be assigned until the user changes something - port forwarding switched off in the provider's own
    /// settings, or a connected region that does not offer it. This is a durable configuration state, not
    /// a fault, and must never drive auto-recovery: restarting the VPN cannot create a forward the account
    /// or region does not offer, so retrying on a timer only tears the tunnel down repeatedly for nothing.
    /// <para>Distinct from a transient failure to read the port (still establishing, provider busy,
    /// unreadable output), which stays a failed cycle and does contribute to the recovery threshold.</para>
    /// <para>Defaults to <see langword="false"/>. Only PIA reports its port-forward state distinctly
    /// enough to separate durable from transient: ProtonVPN's log carries a port or it does not, and a
    /// NAT-PMP gateway that refuses a mapping may well grant the next one.</para>
    /// </summary>
    bool PortForwardingUnavailable => false;

    /// <summary>
    /// Returns the recovery target passed to <c>AutoRecoveryManager.TriggerRestartAsync</c> or
    /// <c>TriggerCycleAdapterAsync</c>, or <see langword="null"/> if recovery is not supported.
    /// For ProtonVPN and PIA this is the provider token (e.g. "ProtonVPN", "PIA").
    /// For NAT-PMP this is the provider token when the adapter belongs to a known provider,
    /// or the adapter name when it does not (e.g. a standalone NAT-PMP gateway).
    /// </summary>
    string? GetRecoveryTarget();

    /// <summary>
    /// Returns the auto-recovery action to request from the helper service.
    /// For ProtonVPN and PIA this is always <see cref="qbPortWeaver.Shared.HelperProtocol.ActionRestart"/>.
    /// For NAT-PMP this is <see cref="qbPortWeaver.Shared.HelperProtocol.ActionRestart"/> when the adapter belongs
    /// to a known provider, or <see cref="qbPortWeaver.Shared.HelperProtocol.ActionCycleAdapter"/> otherwise.
    /// </summary>
    string GetRecoveryAction();

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="interfaceName"/> matches this provider's adapter naming convention.
    /// Each implementation performs a bidirectional case-insensitive substring match against its configured
    /// adapter name(s), so e.g. registry "ProtonVPN" matches Windows adapter "ProtonVPN TUN" and vice versa.
    /// NAT-PMP and PIA match a single configured name; ProtonVPN matches either its legacy name
    /// ("ProtonVPN" / "ProtonVPN TUN") or its in-house tunnel name ("ProTUN").
    /// </summary>
    bool IsAdapterMatch(string interfaceName);
}

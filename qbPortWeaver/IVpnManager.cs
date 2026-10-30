namespace qbPortWeaver;

/// <summary>
/// Provider-agnostic contract for reading the forwarded port and driving auto-recovery
/// for a VPN tunnel (ProtonVPN, PIA), a NAT-PMP gateway, or a port set in Settings (Static port).
/// </summary>
public interface IVpnManager
{
    /// <summary>
    /// Display name of the provider or gateway used for port detection.
    /// For ProtonVPN and PIA this is the provider name (e.g. "ProtonVPN", "PIA").
    /// For NAT-PMP this is the network adapter name of the responding gateway; for Static port, the
    /// network adapter chosen in Settings.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns <see langword="true"/> if the provider or gateway is currently reachable and active.
    /// For ProtonVPN this means the tunnel adapter is up; for PIA it means piactl reports the connection
    /// state as Connected. For NAT-PMP this means the configured network adapter is up; the gateway answered
    /// when the manager was created, except for the instance reused when it does not answer and the stand-in
    /// used before any gateway has.
    /// For Static port it means the configured network adapter is up.
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
    /// For Static port this is the port entered in Settings, returned as is.
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
    /// The public address the port is forwarded on, when the provider reports one; otherwise
    /// <see langword="null"/>. Compared with the address traffic actually leaves from (see
    /// <see cref="VpnExitAddress"/>): when they differ, peers are given the exit address and cannot
    /// reach the forward.
    /// <para>NAT-PMP takes it from the gateway's external-address reply when the manager is created. PIA
    /// and Static port report none, so the check does not run for them. NAT-PMP's is
    /// <see langword="null"/> when the gateway reports 0.0.0.0, on the instance reused when the gateway
    /// does not answer, and on the stand-in for an adapter with no gateway yet. Callers read it through
    /// <see cref="GetForwardAddressAsync"/>, which a provider that has to ask for it (ProtonVPN) overrides;
    /// ProtonVPN reports none here.</para>
    /// </summary>
    System.Net.IPAddress? ForwardAddress => null;

    /// <summary>
    /// The forward address (see <see cref="ForwardAddress"/>) for the exit-address check; the one member the
    /// sync loop and Diagnostics read it through. Defaults to <see cref="ForwardAddress"/>.
    /// <para>ProtonVPN asks its tunnel gateway over NAT-PMP, the way NAT-PMP mode does, and falls back to the
    /// server address its client logs on each connection; once per instance, so once per cycle. Its log line
    /// is written once per connection and rotates out within hours, so the gateway is the source that lasts.
    /// <see langword="null"/> from a provider that reports a forward address (gateway silent and log lines
    /// gone, or any NAT-PMP case above) is treated by the sync loop as a failed lookup, not as a provider
    /// that reports none.</para>
    /// </summary>
    Task<System.Net.IPAddress?> GetForwardAddressAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ForwardAddress);

    /// <summary>
    /// The VPN adapter's current IPv4 address, which the exit-address lookup is sent from so that it
    /// leaves through the tunnel; <see langword="null"/> when the adapter is not up or has none, and for
    /// providers that report no forward address (see <see cref="GetForwardAddressAsync"/>). Read live on each call, because the VPN hands
    /// the adapter a new address on every reconnect.
    /// </summary>
    System.Net.IPAddress? TunnelAddress => null;

    /// <summary>
    /// How the exit-address messages name where the forward comes from - the NAT-PMP gateway on its
    /// adapter, or the ProtonVPN server - so the user knows what to change. It opens the message, so it
    /// reads as a sentence start. Defaults to <see cref="ProviderName"/>.
    /// </summary>
    string ForwardSource => ProviderName;

    /// <summary>
    /// Whether the tunnel is ProtonVPN's OpenVPN one (adapter <see cref="AppConstants.ProtonVpnOpenVpnAdapterName"/>).
    /// Exit-address mismatches were seen on those sessions and not on Proton WireGuard ones, so the
    /// mismatch messages then suggest switching protocol. Defaults to <see langword="false"/>.
    /// </summary>
    bool IsProtonVpnOverOpenVpn => false;

    /// <summary>
    /// Returns the recovery target passed to <c>AutoRecoveryManager.TriggerRestartAsync</c> or
    /// <c>TriggerCycleAdapterAsync</c>, or <see langword="null"/> if recovery is not supported.
    /// For ProtonVPN and PIA this is the provider token (e.g. "ProtonVPN", "PIA").
    /// For NAT-PMP and Static port this is the provider token when the adapter belongs to a known
    /// provider whose service is installed, or the adapter name otherwise (e.g. a standalone NAT-PMP gateway).
    /// </summary>
    string? GetRecoveryTarget();

    /// <summary>
    /// Returns the auto-recovery action to request from the helper service.
    /// For ProtonVPN and PIA this is always <see cref="qbPortWeaver.Shared.HelperProtocol.ActionRestart"/>.
    /// For NAT-PMP and Static port this is <see cref="qbPortWeaver.Shared.HelperProtocol.ActionRestart"/> when the adapter belongs
    /// to a known provider whose service is installed, or <see cref="qbPortWeaver.Shared.HelperProtocol.ActionCycleAdapter"/> otherwise.
    /// </summary>
    string GetRecoveryAction();

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="interfaceName"/> matches this provider's adapter naming convention.
    /// ProtonVPN and PIA match their registry adapter name(s) case-insensitively: the Windows adapter name
    /// containing the configured one, or the configured one starting with it, so e.g. registry "ProtonVPN"
    /// matches Windows adapter "ProtonVPN TUN" and vice versa, while a short unrelated "VPN" does not.
    /// PIA matches a single configured name; ProtonVPN matches either its legacy name ("ProtonVPN" /
    /// "ProtonVPN TUN") or its in-house tunnel name ("ProTUN"). NAT-PMP and Static port require an exact
    /// case-insensitive match, since their adapter is picked from the live Windows list.
    /// </summary>
    bool IsAdapterMatch(string interfaceName);
}

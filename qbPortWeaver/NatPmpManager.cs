using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver;

/// <summary>
/// NAT-PMP VPN manager. PortSyncService creates instances via TryCreateForAdapterAsync() (sync cycle)
/// or DiscoverAdaptersAsync() (SettingsForm). Renewal state is transferred across cycles via
/// CopyRenewalStateFrom() so port renewal works correctly.
/// </summary>
public sealed class NatPmpManager : IVpnManager
{
    private const int NatPmpPort = 5351;
    private const int InitialTimeoutMs = 1500; // VPN NAT-PMP gateways are remote; 250ms is too aggressive
    private const int MaxAttempts = 3;    // 1500ms -> 3000ms -> 6000ms
    private const uint DefaultMappingLifetime = 3600; // 1 hour

    // RFC 6886 opcodes. UDP and TCP are independent mappings and must be requested separately,
    // even though gateways commonly forward both from a single request. A response carries the
    // request opcode with the high bit set.
    private const byte OpcodeMapUdp = 0x01;
    private const byte OpcodeMapTcp = 0x02;
    private const byte ResponseFlag = 0x80;

    private readonly NetworkInterface _adapter;
    private readonly IPAddress _gateway;
    private readonly uint _mappingLifetime;

    // Cached state for port renewal (persists across sync cycles via PortSyncService._lastKnownNatPmpManager).
    // Not independently locked - access is serialized by PortSyncService's single-concurrency sync semaphore.
    private ushort _lastExternalPort;  // zero until first mapping; suggested to gateway on renewal
    private uint _lastEpochSeconds;  // SSOE (Seconds Since Opened Epoch) from the last successful NAT-PMP response

    private NatPmpManager(NetworkInterface adapter, IPAddress gateway, uint mappingLifetime)
    {
        _adapter = adapter;
        _gateway = gateway;
        _mappingLifetime = mappingLifetime;
    }

    /// <inheritdoc />
    public string ProviderName => _adapter.Name;

    /// <summary>Lease lifetime in seconds granted by the gateway on the last successful port mapping; 0 until first mapping.</summary>
    public uint LastGrantedLifetime { get; private set; }

    /// <inheritdoc />
    // Re-enumerates network interfaces because the stored _adapter object retains its
    // last-seen OperationalStatus even after the adapter is removed on disconnect.
    public bool IsVpnConnected()
    {
        try
        {
            bool connected = NetworkInterface.GetAllNetworkInterfaces()
                .Any(nic => nic.Name.Equals(_adapter.Name, StringComparison.OrdinalIgnoreCase)
                         && nic.OperationalStatus == OperationalStatus.Up);

            LogManager.Instance.LogDebug(connected
                ? $"NatPmpManager.IsVpnConnected: Adapter '{_adapter.Name}' is connected"
                : $"NatPmpManager.IsVpnConnected: Adapter '{_adapter.Name}' is not found or not connected");

            return connected;
        }
        catch (Exception ex)
        {
            return LogManager.LogDebugFalse($"NatPmpManager.IsVpnConnected: {ex.Message}");
        }
    }

    /// <inheritdoc />
    // Logs at INFO/WARN (not DEBUG) because lease time and failure details are not surfaced
    // elsewhere in the sync cycle. On renewal, suggests the previously assigned port
    // (RFC 6886 §3.3) so the gateway keeps the same mapping across cycles.
    public async Task<int?> GetVpnPortAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // _lastExternalPort is 0 on first call (gateway assigns any available port)
            // on renewal it holds the last assigned port so the gateway can keep the same mapping.
            ushort suggested = _lastExternalPort;

            var result = await RequestPortMappingAsync(_gateway, _mappingLifetime, OpcodeMapUdp, suggested, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                LogManager.Instance.LogMessage($"Failed to map NAT-PMP port on '{_adapter.Name}': {result.Error}", LogLevel.Warn);
                return null;
            }

            // Detect NAT-PMP daemon restart: SSOE dropping means all prior mappings are gone.
            // The response is still valid (a fresh mapping was assigned) - log and update state below.
            if (_lastEpochSeconds > 0 && result.EpochSeconds < _lastEpochSeconds)
                LogManager.Instance.LogMessage(
                    $"NAT-PMP epoch reset on '{_adapter.Name}' (was {_lastEpochSeconds}s, now {result.EpochSeconds}s) - gateway restarted, fresh port assigned",
                    LogLevel.Info);

            if (LogManager.Instance.DebugMode)
            {
                string epochDelta = (_lastEpochSeconds > 0 && result.EpochSeconds >= _lastEpochSeconds)
                    ? $" (+{result.EpochSeconds - _lastEpochSeconds}s)" : "";
                LogManager.Instance.LogDebug($"NatPmpManager.GetVpnPortAsync: SSOE {result.EpochSeconds}s{epochDelta}");
            }

            _lastEpochSeconds = result.EpochSeconds;
            _lastExternalPort = result.ExternalPort;
            LastGrantedLifetime = result.LifetimeGranted;

            // Map TCP before reporting, so the one line the user sees states which protocols are
            // actually forwarded. UDP and TCP are separate mappings (RFC 6886) and a gateway can
            // grant one without the other, so naming only one of them - or neither - would leave
            // the reader guessing about the very thing this log line exists to confirm.
            bool tcpMapped = await RequestTcpMappingAsync(result.ExternalPort, cancellationToken).ConfigureAwait(false);
            string protocols = tcpMapped ? "UDP and TCP" : "UDP only";

            // All three cases log at Info deliberately: NAT-PMP leases have finite lifetimes
            // so every renewal is a meaningful event (confirms the gateway is still reachable
            // and the mapping is still active). Renewals are not high-frequency at typical
            // sync intervals (30-60s) relative to lease lifetimes (several minutes).
            if (suggested != 0 && result.ExternalPort == suggested)
                LogManager.Instance.LogMessage($"NAT-PMP lease renewed: port {result.ExternalPort} ({protocols}), lifetime {result.LifetimeGranted}s", LogLevel.Info);
            else if (suggested != 0)
                LogManager.Instance.LogMessage($"NAT-PMP lease granted new port {result.ExternalPort} ({protocols}, suggested {suggested} unavailable), lifetime {result.LifetimeGranted}s", LogLevel.Info);
            else
                LogManager.Instance.LogMessage($"NAT-PMP lease granted: port {result.ExternalPort} ({protocols}), lifetime {result.LifetimeGranted}s", LogLevel.Info);

            return result.ExternalPort;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogManager.Instance.LogMessage($"NAT-PMP error on '{_adapter.Name}': {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    // Maps the same external port for TCP, which RFC 6886 treats as a mapping separate from UDP.
    // Most VPN gateways (ProtonVPN's included) forward both protocols from a single request, in
    // which case this just confirms the mapping that already exists - but a gateway that follows
    // the RFC strictly would otherwise leave TCP unforwarded, and inbound Soulseek connections are
    // TCP-only while BitTorrent needs TCP for peer connections even where uTP covers UDP.
    //
    // Deliberately non-fatal: the UDP mapping is already granted and usable, so a gateway that
    // rejects opcode 2 must not cost the user a working port. Warn rather than Error for the same
    // reason - it is a partial result, not a failed cycle.
    /// <returns><see langword="true"/> when TCP is mapped on the same port as UDP.</returns>
    private async Task<bool> RequestTcpMappingAsync(ushort udpPort, CancellationToken cancellationToken)
    {
        var tcp = await RequestPortMappingAsync(_gateway, _mappingLifetime, OpcodeMapTcp, udpPort, cancellationToken).ConfigureAwait(false);

        if (!tcp.Success)
        {
            LogManager.Instance.LogMessage(
                $"NAT-PMP TCP mapping for port {udpPort} was not granted on '{_adapter.Name}': {tcp.Error} - " +
                "UDP is mapped, so this only matters if the gateway does not forward both protocols together",
                LogLevel.Warn);
            return false;
        }

        if (tcp.ExternalPort != udpPort)
        {
            LogManager.Instance.LogMessage(
                $"NAT-PMP granted TCP port {tcp.ExternalPort} but UDP port {udpPort} on '{_adapter.Name}' - " +
                "the client listens on one port, so inbound TCP may not reach it",
                LogLevel.Warn);
            return false;
        }

        // Track the shorter of the two leases. WarnIfNatPmpLeaseTooShort compares the user's sync
        // interval against LastGrantedLifetime, which until here holds the UDP grant only - a TCP
        // mapping that expired first would stop inbound TCP between cycles with nothing reported.
        uint udpLifetime = LastGrantedLifetime;
        LastGrantedLifetime = Math.Min(udpLifetime, tcp.LifetimeGranted);

        // No success line: the caller's Info entry already reports "(UDP and TCP)" with the
        // lifetime. Only a disagreement is worth logging, because it is what shortens the
        // renewal budget the interval is checked against.
        if (tcp.LifetimeGranted != udpLifetime)
            LogManager.Instance.LogDebug(
                $"NatPmpManager.RequestTcpMappingAsync: TCP lease is {tcp.LifetimeGranted}s against UDP's {udpLifetime}s - renewal follows the shorter one");

        return true;
    }

    /// <inheritdoc />
    // Returns the provider token when the adapter belongs to a known provider (for service restart),
    // or the adapter name for standalone NAT-PMP gateways (for adapter cycling).
    public string? GetRecoveryTarget() => FindProviderToken(_adapter.Name) ?? _adapter.Name;

    /// <inheritdoc />
    public string GetRecoveryAction() =>
        FindProviderToken(_adapter.Name) is not null
            ? HelperProtocol.ActionRestart
            : HelperProtocol.ActionCycleAdapter;

    /// <inheritdoc />
    // Delegates to the shared matcher (AdapterNamesMatch). NAT-PMP and PIA match one configured
    // name; ProtonVpnManager matches either of its two names. The configured name here is the
    // adapter's own name (an instance field), not a registry value.
    public bool IsAdapterMatch(string interfaceName)
        => VpnRegistryConfig.AdapterNamesMatch(_adapter.Name, interfaceName);

    /// <summary>
    /// Returns all network adapters whose gateway actively responds to NAT-PMP,
    /// including TUN/VPN adapters where the gateway is inferred from the unicast address.
    /// All candidates are probed in parallel with a single attempt each; only those whose
    /// gateway responds are returned. Used by SettingsForm to populate the adapter list.
    /// </summary>
    /// <param name="mappingLifetime">Requested port mapping duration in seconds; the gateway may grant less.</param>
    /// <param name="cancellationToken">Cancels in-flight UDP probes so app shutdown is not delayed.</param>
    public static async Task<IReadOnlyList<NatPmpManager>> DiscoverAdaptersAsync(uint mappingLifetime = DefaultMappingLifetime, CancellationToken cancellationToken = default)
    {
        var candidates = new List<(NetworkInterface Nic, IPAddress Gateway)>();

        foreach (NetworkInterface nic in GetActiveNetworkInterfaces())
        {
            IPAddress? gateway = ResolveGateway(nic);
            if (gateway is null)
                continue;

            candidates.Add((nic, gateway));
        }

        // Probe all candidates in parallel to verify NAT-PMP support
        var probeResults = await Task.WhenAll(candidates.Select(async c =>
        {
            IPAddress? externalIp = await RequestExternalAddressAsync(c.Gateway, cancellationToken: cancellationToken).ConfigureAwait(false);
            LogProbeResultDebug("DiscoverAdaptersAsync", c.Nic.Name, c.Gateway, externalIp);
            return (c.Nic, c.Gateway, Supported: externalIp is not null);
        })).ConfigureAwait(false);

        return probeResults
            .Where(r => r.Supported)
            .Select(r => new NatPmpManager(r.Nic, r.Gateway, mappingLifetime))
            .ToList();
    }

    /// <summary>
    /// Probes only the named adapter rather than all adapters. Used by the sync cycle so that
    /// unrelated adapters (e.g. ZeroTier, Ethernet) are never probed unnecessarily.
    /// Unlike <see cref="DiscoverAdaptersAsync"/>, uses <see cref="MaxAttempts"/> retries with
    /// exponential backoff since probing a single known adapter is worth retrying on transient packet loss.
    /// Returns <see langword="null"/> if the adapter is not found or not up, has no resolvable
    /// gateway, or does not respond to a NAT-PMP probe.
    /// </summary>
    /// <param name="adapterName">The adapter name to match (case-insensitive).</param>
    /// <param name="mappingLifetime">Requested port mapping duration in seconds; the gateway may grant less.</param>
    /// <param name="cancellationToken">Cancels in-flight UDP probes so the sync cycle can yield promptly on shutdown.</param>
    public static async Task<NatPmpManager?> TryCreateForAdapterAsync(string adapterName, uint mappingLifetime = DefaultMappingLifetime, CancellationToken cancellationToken = default)
    {
        NetworkInterface? nic = GetActiveNetworkInterfaces()
            .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));

        if (nic is null)
        {
            LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapterAsync: '{adapterName}' not found or not up");
            return null;
        }

        IPAddress? gateway = ResolveGateway(nic);
        if (gateway is null)
        {
            LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapterAsync: '{adapterName}' no resolvable gateway");
            return null;
        }

        IPAddress? externalIp = await RequestExternalAddressAsync(gateway, MaxAttempts, cancellationToken).ConfigureAwait(false);
        LogProbeResultDebug("TryCreateForAdapterAsync", adapterName, gateway, externalIp);

        return externalIp is not null ? new NatPmpManager(nic, gateway, mappingLifetime) : null;
    }

    /// <summary>
    /// Returns the VPN provider token if <paramref name="adapterName"/> matches a known provider keyword,
    /// or <see langword="null"/> if it does not. Used to decide whether to trigger a service restart or an adapter cycle.
    /// Delegates to <see cref="VpnProviderRegistry.FindProviderToken"/> so the provider list stays single-source.
    /// </summary>
    internal static string? FindProviderToken(string adapterName) =>
        VpnProviderRegistry.FindProviderToken(adapterName);

    // Transfers renewal state from a previous instance so that port renewal works correctly
    // when a fresh NatPmpManager instance is created each cycle.
    internal void CopyRenewalStateFrom(NatPmpManager other)
    {
        _lastExternalPort = other._lastExternalPort;
        _lastEpochSeconds = other._lastEpochSeconds;
        LastGrantedLifetime = other.LastGrantedLifetime;
    }

    // Returns all network interfaces that are up and not loopback or protocol-tunnel adapters.
    // NetworkInterfaceType.Tunnel covers Windows IF_TYPE_TUNNEL (Teredo, ISATAP, 6to4) -
    // not VPN adapters. WireGuard/wintun (ProtonVPN) and TAP/OpenVPN adapters report as
    // Unknown or Ethernet and are not excluded by this filter.
    //
    // Guarded the same way both IsVpnConnected implementations guard this call (here and in
    // ProtonVpnManager): GetAllNetworkInterfaces throws NetworkInformationException when the network
    // subsystem is briefly unavailable, which on a machine riding VPN adapter churn is an ordinary
    // transient rather than a fault. An empty sequence hands the callers the answer they already
    // handle - TryCreateForAdapterAsync returns null ("not found or not up") and DiscoverAdaptersAsync
    // an empty list - instead of a throw that RunAsync would report as "An unexpected error occurred"
    // at Error, for what is really just a disconnected adapter. Without this the two static factories
    // were the only unguarded users of an API the two instance methods already treat as fallible.
    //
    // ToList inside the try is load-bearing: a deferred Where would run GetAllNetworkInterfaces at the
    // caller's foreach or FirstOrDefault, outside this catch, and the guard would never fire. The
    // return type is List rather than IEnumerable so the compiler enforces that rather than this
    // comment: returning the lazy Where directly no longer builds. It also lets the foreach in
    // DiscoverAdaptersAsync use List's struct enumerator instead of dispatching through the interface.
    private static List<NetworkInterface> GetActiveNetworkInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                           && nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                                                      and not NetworkInterfaceType.Tunnel)
                .ToList();
        }
        catch (NetworkInformationException ex)
        {
            LogManager.Instance.LogDebug($"NatPmpManager.GetActiveNetworkInterfaces: {ex.Message}");
            return [];
        }
    }

    // Resolves the usable gateway for an adapter.
    // For standard adapters: uses the declared IPv4 gateway.
    // Otherwise (0.0.0.0, empty GatewayAddresses, or all-IPv6 gateways): infers x.x.x.1
    // from the unicast address. This fallback is primarily for VPN/TUN adapters (e.g. ProtonVPN)
    // which Windows does not populate GatewayAddresses for.
    //
    // Takes the adapter rather than its properties so the GetIPProperties call is inside this guard.
    // It is the second thing here that throws NetworkInformationException on a briefly unavailable
    // network subsystem, and it throws per adapter - one disappearing mid-enumeration must not cost
    // the whole scan. Null is "no usable gateway", which both callers already skip.
    private static IPAddress? ResolveGateway(NetworkInterface nic)
    {
        IPInterfaceProperties props;
        try
        {
            props = nic.GetIPProperties();
        }
        catch (NetworkInformationException ex)
        {
            LogManager.Instance.LogDebug($"NatPmpManager.ResolveGateway: '{nic.Name}' - {ex.Message}");
            return null;
        }

        IPAddress? gateway = props.GatewayAddresses
            .Select(gw => gw.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

        return gateway ?? InferGatewayFromUnicast(props);
    }

    // Infers the gateway as x.x.x.1 of the adapter's subnet - a common convention for VPN gateways.
    // Assumes a /24 or wider subnet: for /30 (4 hosts) or /31 (2 hosts) subnets the .1 host
    // may not exist or may not be the gateway. Accepted limitation for the typical VPN gateway case.
    private static IPAddress? InferGatewayFromUnicast(IPInterfaceProperties props)
    {
        foreach (UnicastIPAddressInformation address in props.UnicastAddresses)
        {
            if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                continue;

            // IPv4Mask is documented to return IPAddress.None for non-IPv4 addresses, but some
            // virtual adapters (TAP/TUN before negotiation) have been observed returning null
            // while still reporting InterNetwork. Guard explicitly rather than relying on the
            // outer try/catch to swallow an NRE.
            IPAddress? ipv4Mask = address.IPv4Mask;
            if (ipv4Mask is null || ipv4Mask.Equals(IPAddress.Any))
                continue; // zero mask - cannot infer a meaningful gateway

            byte[] addr = address.Address.GetAddressBytes();
            byte[] mask = ipv4Mask.GetAddressBytes();

            byte[] network = new byte[4];
            for (int i = 0; i < 4; i++)
                network[i] = (byte)(addr[i] & mask[i]);

            network[3] = 1;
            var candidate = new IPAddress(network);

            if (!candidate.Equals(address.Address))
            {
                LogManager.Instance.LogDebug($"NatPmpManager.InferGatewayFromUnicast: No declared gateway for {address.Address}, inferred {candidate}");
                return candidate;
            }
        }
        return null;
    }

    // Sends a NAT-PMP external address request (RFC 6886 opcode 0) and returns the public IP.
    // maxAttempts=1 for discovery (best-effort, all adapters probed in parallel)
    // MaxAttempts for targeted probes where retrying a single adapter is worthwhile.
    private static async Task<IPAddress?> RequestExternalAddressAsync(IPAddress gateway, int maxAttempts = 1, CancellationToken cancellationToken = default)
    {
        // version=0, opcode=0 (external address request) - both zero by default
        byte[] request = new byte[2];

        byte[]? data = await SendReceiveAsync(gateway, request, maxAttempts, cancellationToken).ConfigureAwait(false);
        if (data is null)
            return null;

        // [0] version=0  [1] opcode=0x80  [2-3] result  [4-7] epoch  [8-11] external IP
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x80)
            return null;

        ushort resultCode = ReadUShortBE(data, 2);
        if (resultCode != 0)
            return null;

        return new IPAddress(new byte[] { data[8], data[9], data[10], data[11] });
    }

    // Sends a NAT-PMP port mapping request for one protocol (RFC 6886 opcode 1 = UDP, 2 = TCP).
    // Pass zero as suggestedExternalPort for an initial request, or the previously assigned port to request renewal.
    // Local port is set to 0 - clients that do not bind a specific port let the gateway infer it.
    private static async Task<(bool Success, ushort ExternalPort, uint LifetimeGranted, uint EpochSeconds, string? Error)>
        RequestPortMappingAsync(IPAddress gateway, uint lifetime, byte opcode, ushort suggestedExternalPort = 0, CancellationToken cancellationToken = default)
    {
        // [0] version=0  [1] opcode  [2-3] reserved
        // [4-5] local port=0  [6-7] suggested external port  [8-11] lifetime
        byte[] request = new byte[12];
        request[0] = 0x00;
        request[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), suggestedExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), lifetime);

        byte[]? data = await SendReceiveAsync(gateway, request, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (data is null)
            return (false, 0, 0, 0, "No response from gateway");

        // [0] version=0  [1] opcode|0x80  [2-3] result  [4-7] SSOE
        // [8-9] local port (not read)   [10-11] external port  [12-15] lifetime
        if (data.Length < 16 || data[0] != 0x00 || data[1] != (byte)(opcode | ResponseFlag))
            return (false, 0, 0, 0, "Unexpected response format");

        ushort resultCode = ReadUShortBE(data, 2);
        if (resultCode != 0)
            return (false, 0, 0, 0, $"NAT-PMP result code {resultCode}");

        uint epochSeconds = ReadUIntBE(data, 4);
        ushort externalPort = ReadUShortBE(data, 10);
        uint lifetimeGiven = ReadUIntBE(data, 12);

        if (externalPort == 0)
            return (false, 0, 0, epochSeconds, "Gateway returned external port 0");

        return (true, externalPort, lifetimeGiven, epochSeconds, null);
    }

    private static ushort ReadUShortBE(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));

    private static uint ReadUIntBE(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));

    private static void LogProbeResultDebug(string context, string adapterName, IPAddress gateway, IPAddress? externalIp)
    {
        if (externalIp is not null)
            LogManager.Instance.LogDebug($"NatPmpManager.{context}: '{adapterName}' via gateway {gateway} (external IP: {externalIp})");
        else
            LogManager.Instance.LogDebug($"NatPmpManager.{context}: '{adapterName}' via gateway {gateway} - NAT-PMP probe failed");
    }

    private static void LogTimeoutDebug(int attempt, int maxAttempts, int timeoutMs)
    {
        if (attempt < maxAttempts - 1)
            LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: No response after {timeoutMs}ms, retrying (attempt {attempt + 2}/{maxAttempts})");
        else if (maxAttempts > 1)
            LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: No response after {timeoutMs}ms (all {maxAttempts} attempts exhausted)");
        else
            LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: No response after {timeoutMs}ms");
    }

    // Sends a UDP datagram to the gateway and waits for a response.
    // Retries with exponential backoff per RFC 6886 §3.1 to handle dropped UDP packets.
    // Each retry opens a fresh UdpClient so any stale datagrams from a previous attempt
    // are silently discarded (they arrive on the old socket, which is already disposed).
    // maxAttempts defaults to MaxAttempts; pass 1 for best-effort probes (e.g. discovery).
    private static async Task<byte[]?> SendReceiveAsync(IPAddress gateway, byte[] request, int maxAttempts = MaxAttempts, CancellationToken cancellationToken = default)
    {
        int timeoutMs = InitialTimeoutMs;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var udp = new UdpClient();
                await udp.SendAsync(request, new IPEndPoint(gateway, NatPmpPort), cancellationToken).ConfigureAwait(false);

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                while (true)
                {
                    UdpReceiveResult result = await udp.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);

                    if (!result.RemoteEndPoint.Address.Equals(gateway))
                    {
                        // Discard stray datagrams without consuming a retry slot - keep waiting
                        // on the same socket until the timeout fires or the gateway responds.
                        LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: Response from unexpected sender {result.RemoteEndPoint.Address}, discarding");
                        continue;
                    }

                    return result.Buffer;
                }
            }
            // Real cancellation propagates. Without this arm it falls through the timeout filter below
            // into the generic catch, which returns null - and null means "no response from gateway" to
            // every caller, so shutting down mid-request logged a port detection failure, advanced the
            // auto-recovery streak, and could dispatch a VPN restart on the way out. Listed first so the
            // two filters are exhaustive over OperationCanceledException and none can reach the generic
            // catch. Same arm the clients, PiaVpnManager and ProtonVpnManager already use.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogTimeoutDebug(attempt, maxAttempts, timeoutMs);
                timeoutMs *= 2;
            }
            catch (SocketException ex)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: Gateway {gateway} rejected NAT-PMP probe ({ex.SocketErrorCode}) - NAT-PMP may not be enabled on this gateway");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: {ex.Message}");
                return null;
            }
        }

        return null;
    }
}

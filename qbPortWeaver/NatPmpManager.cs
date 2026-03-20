using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver
{
    /// <summary>
    /// NAT-PMP VPN manager. PortSyncService creates instances via TryCreateForAdapterAsync() (sync cycle)
    /// or DiscoverAdaptersAsync() (SettingsForm). Renewal state is transferred across cycles via
    /// CopyRenewalStateFrom() so port renewal works correctly.
    /// </summary>
    public sealed class NatPmpManager : IVpnManager
    {
        private const int  NatPmpPort             = 5351;
        private const int  InitialTimeoutMs       = 1500; // VPN NAT-PMP gateways are remote; 250ms is too aggressive
        private const int  MaxAttempts            = 3;    // 1500ms → 3000ms → 6000ms
        private const uint DefaultMappingLifetime = 3600; // 1 hour

        private readonly NetworkInterface _adapter;
        private readonly IPAddress        _gateway;
        private readonly uint             _mappingLifetime;

        // Cached state for port renewal (persists across sync cycles via PortSyncService._lastKnownNatPmpManager)
        private ushort _lastExternalPort;  // zero until first mapping; suggested to gateway on renewal
        private uint   _lastEpochSeconds;  // SSOE (Seconds Since Opened Epoch) from the last successful NAT-PMP response

        /// <inheritdoc />
        public string ProviderName => _adapter.Name;

        /// <summary>Lease lifetime in seconds granted by the gateway on the last successful port mapping; 0 until first mapping.</summary>
        public uint LastGrantedLifetime { get; private set; }

        private NatPmpManager(NetworkInterface adapter, IPAddress gateway, uint mappingLifetime)
        {
            _adapter         = adapter;
            _gateway         = gateway;
            _mappingLifetime = mappingLifetime;
        }

        /// <summary>
        /// Returns all network adapters whose gateway actively responds to NAT-PMP,
        /// including TUN/VPN adapters where the gateway is inferred from the unicast address.
        /// All candidates are probed in parallel with a single attempt each; only those whose
        /// gateway responds are returned. Used by SettingsForm to populate the adapter list.
        /// </summary>
        /// <param name="mappingLifetime">Requested port mapping duration in seconds; the gateway may grant less.</param>
        public static async Task<IReadOnlyList<NatPmpManager>> DiscoverAdaptersAsync(uint mappingLifetime = DefaultMappingLifetime)
        {
            var candidates = new List<(NetworkInterface Nic, IPAddress Gateway)>();

            foreach (NetworkInterface nic in GetActiveNetworkInterfaces())
            {
                IPInterfaceProperties props = nic.GetIPProperties();

                IPAddress? gateway = ResolveGateway(props);
                if (gateway is null)
                    continue;

                candidates.Add((nic, gateway));
            }

            // Probe all candidates in parallel to verify NAT-PMP support
            var probeResults = await Task.WhenAll(candidates.Select(async c =>
            {
                IPAddress? externalIp = await RequestExternalAddressAsync(c.Gateway).ConfigureAwait(false);
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
        public static async Task<NatPmpManager?> TryCreateForAdapterAsync(string adapterName, uint mappingLifetime = DefaultMappingLifetime)
        {
            NetworkInterface? nic = GetActiveNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));

            if (nic is null)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapterAsync: '{adapterName}' not found or not up");
                return null;
            }

            IPAddress? gateway = ResolveGateway(nic.GetIPProperties());
            if (gateway is null)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapterAsync: '{adapterName}' - no resolvable gateway");
                return null;
            }

            IPAddress? externalIp = await RequestExternalAddressAsync(gateway, MaxAttempts).ConfigureAwait(false);
            LogProbeResultDebug("TryCreateForAdapterAsync", adapterName, gateway, externalIp);

            return externalIp is not null ? new NatPmpManager(nic, gateway, mappingLifetime) : null;
        }

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
        public async Task<int?> GetVpnPortAsync()
        {
            try
            {
                // _lastExternalPort is 0 on first call (gateway assigns any available port)
                // on renewal it holds the last assigned port so the gateway can keep the same mapping.
                ushort suggested = _lastExternalPort;

                var result = await RequestPortMappingAsync(_gateway, _mappingLifetime, suggested).ConfigureAwait(false);

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

                string epochDelta = (_lastEpochSeconds > 0 && result.EpochSeconds >= _lastEpochSeconds)
                    ? $" (+{result.EpochSeconds - _lastEpochSeconds}s)" : "";
                LogManager.Instance.LogDebug($"NatPmpManager.GetVpnPortAsync: SSOE {result.EpochSeconds}s{epochDelta}");

                _lastEpochSeconds  = result.EpochSeconds;
                _lastExternalPort  = result.ExternalPort;
                LastGrantedLifetime = result.LifetimeGranted;

                if (suggested != 0 && result.ExternalPort == suggested)
                    LogManager.Instance.LogMessage($"NAT-PMP lease renewed: port {result.ExternalPort}, lifetime {result.LifetimeGranted}s", LogLevel.Info);
                else if (suggested != 0)
                    LogManager.Instance.LogMessage($"NAT-PMP lease granted new port {result.ExternalPort} (suggested {suggested} unavailable), lifetime {result.LifetimeGranted}s", LogLevel.Info);
                else
                    LogManager.Instance.LogMessage($"NAT-PMP lease granted: port {result.ExternalPort}, lifetime {result.LifetimeGranted}s", LogLevel.Info);

                return result.ExternalPort;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"NAT-PMP error on '{_adapter.Name}': {ex.Message}", LogLevel.Warn);
                return null;
            }
        }

        /// <inheritdoc />
        // Returns the adapter name. PortSyncService checks if it matches a known provider
        // (ProtonVPN, PIA) and triggers a service restart instead of an adapter cycle.
        public string? GetRecoveryTarget() => _adapter.Name;

        // Transfers renewal state from a previous instance so that port renewal works correctly
        // when a fresh NatPmpManager instance is created each cycle.
        internal void CopyRenewalStateFrom(NatPmpManager other)
        {
            _lastExternalPort  = other._lastExternalPort;
            _lastEpochSeconds  = other._lastEpochSeconds;
        }

        // Returns all network interfaces that are up and not loopback or protocol-tunnel adapters.
        // NetworkInterfaceType.Tunnel covers Windows IF_TYPE_TUNNEL (Teredo, ISATAP, 6to4) -
        // not VPN adapters. WireGuard/wintun (ProtonVPN) and TAP/OpenVPN adapters report as
        // Unknown or Ethernet and are not excluded by this filter.
        private static IEnumerable<NetworkInterface> GetActiveNetworkInterfaces()
            => NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                           && nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                                                      and not NetworkInterfaceType.Tunnel);

        // Resolves the usable gateway for an adapter.
        // For standard adapters: uses the declared IPv4 gateway.
        // Otherwise (0.0.0.0, empty GatewayAddresses, or all-IPv6 gateways): infers x.x.x.1
        // from the unicast address. This fallback is primarily for VPN/TUN adapters (e.g. ProtonVPN)
        // which Windows does not populate GatewayAddresses for.
        private static IPAddress? ResolveGateway(IPInterfaceProperties props)
        {
            IPAddress? gateway = props.GatewayAddresses
                .Select(gw => gw.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            return gateway ?? InferGatewayFromUnicast(props);
        }

        // Infers the gateway as x.x.x.1 of the adapter's subnet - a common convention for VPN gateways.
        private static IPAddress? InferGatewayFromUnicast(IPInterfaceProperties props)
        {
            foreach (UnicastIPAddressInformation address in props.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (address.IPv4Mask.Equals(IPAddress.Any))
                    continue; // zero mask - cannot infer a meaningful gateway

                byte[] addr = address.Address.GetAddressBytes();
                byte[] mask = address.IPv4Mask.GetAddressBytes();

                byte[] network = new byte[4];
                for (int i = 0; i < 4; i++)
                    network[i] = (byte)(addr[i] & mask[i]);

                network[3] = 1;
                var candidate = new IPAddress(network);

                if (!candidate.Equals(address.Address))
                    return candidate;
            }
            return null;
        }

        private static ushort ReadUShortBE(byte[] data, int offset)
            => (ushort)((data[offset] << 8) | data[offset + 1]);

        private static uint ReadUIntBE(byte[] data, int offset)
            => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

        // Sends a NAT-PMP external address request (RFC 6886 opcode 0) and returns the public IP.
        // maxAttempts=1 for discovery (best-effort, all adapters probed in parallel)
        // MaxAttempts for targeted probes where retrying a single adapter is worthwhile.
        private static async Task<IPAddress?> RequestExternalAddressAsync(IPAddress gateway, int maxAttempts = 1)
        {
            // version=0, opcode=0 (external address request) - both zero by default
            byte[] request = new byte[2];

            byte[]? data = await SendReceiveAsync(gateway, request, maxAttempts).ConfigureAwait(false);
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

        // Sends a NAT-PMP UDP port mapping request (RFC 6886 opcode 1).
        // Pass zero as suggestedExternalPort for an initial request, or the previously assigned port to request renewal.
        // Internal port is set to 0 - clients that do not bind a specific port let the gateway infer it.
        private static async Task<(bool Success, ushort ExternalPort, uint LifetimeGranted, uint EpochSeconds, string? Error)>
            RequestPortMappingAsync(IPAddress gateway, uint lifetime, ushort suggestedExternalPort = 0)
        {
            // [0] version=0  [1] opcode=1 (UDP)  [2-3] reserved
            // [4-5] internal port=0  [6-7] suggested external port  [8-11] lifetime
            byte[] request = new byte[12];
            request[0]  = 0x00;
            request[1]  = 0x01;
            request[6]  = (byte)(suggestedExternalPort >> 8);
            request[7]  = (byte)(suggestedExternalPort & 0xFF);
            request[8]  = (byte)(lifetime >> 24);
            request[9]  = (byte)(lifetime >> 16);
            request[10] = (byte)(lifetime >> 8);
            request[11] = (byte)(lifetime & 0xFF);

            byte[]? data = await SendReceiveAsync(gateway, request).ConfigureAwait(false);
            if (data is null)
                return (false, 0, 0, 0, "No response from gateway");

            // [0] version=0  [1] opcode=0x81  [2-3] result  [4-7] SSOE
            // [8-9] internal port (not read)   [10-11] external port  [12-15] lifetime
            if (data.Length < 16 || data[0] != 0x00 || data[1] != 0x81)
                return (false, 0, 0, 0, "Unexpected response format");

            ushort resultCode    = ReadUShortBE(data, 2);
            if (resultCode != 0)
                return (false, 0, 0, 0, $"NAT-PMP result code {resultCode}");

            uint   epochSeconds  = ReadUIntBE(data, 4);
            ushort externalPort  = ReadUShortBE(data, 10);
            uint   lifetimeGiven = ReadUIntBE(data, 12);

            if (externalPort == 0)
                return (false, 0, 0, epochSeconds, "Gateway returned external port 0");

            return (true, externalPort, lifetimeGiven, epochSeconds, null);
        }

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
        // maxAttempts defaults to MaxAttempts; pass 1 for best-effort probes (e.g. discovery).
        private static async Task<byte[]?> SendReceiveAsync(IPAddress gateway, byte[] request, int maxAttempts = MaxAttempts)
        {
            int timeoutMs = InitialTimeoutMs;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    using var udp = new UdpClient();
                    await udp.SendAsync(request, new IPEndPoint(gateway, NatPmpPort)).ConfigureAwait(false);

                    using var cts = new CancellationTokenSource(timeoutMs);
                    while (true)
                    {
                        UdpReceiveResult result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);

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
                catch (OperationCanceledException)
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
}

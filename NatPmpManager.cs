using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver
{
    // NAT-PMP VPN manager. Instances are created by DiscoverAdapters() each cycle; PortSyncService
    // transfers renewal state (_lastExternalPort, _lastEpochSsoe) from the previous instance via
    // CopyRenewalStateFrom() so that port renewal works correctly across cycles.
    public sealed class NatPmpManager : IVpnManager
    {
        private const int  NatPmpPort             = 5351;
        private const int  InitialTimeoutMs       = 1500; // VPN NAT-PMP gateways are remote; 250ms is too aggressive
        private const int  MaxAttempts            = 3;   // 1500ms → 3000ms → 6000ms
        public  const uint DefaultMappingLifetime = 3600; // 1 hour

        private readonly NetworkInterface _adapter;
        private readonly IPAddress        _gateway;
        private readonly uint             _mappingLifetime;

        // Cached state for port renewal (persists across sync cycles via PortSyncService._lastKnownNatPmpManager)
        private ushort _lastExternalPort; // 0 = no prior mapping; sent as suggested port on renewal
        private uint   _lastEpochSsoe;    // seconds-since-start-of-epoch from the last successful response

        public string ProviderName => _adapter.Description;

        // Transfers renewal state from a previous instance for the same adapter so that port renewal
        // works correctly even when DiscoverAdapters() returns a fresh NatPmpManager instance each cycle.
        internal void CopyRenewalStateFrom(NatPmpManager other)
        {
            _lastExternalPort = other._lastExternalPort;
            _lastEpochSsoe    = other._lastEpochSsoe;
        }

        // Returns all network adapters whose gateway actively responds to NAT-PMP,
        // including TUN/VPN adapters where the gateway is inferred from the unicast address.
        // All candidates are probed in parallel; only those with a responding gateway are returned.
        // Logs probe results (success and failure) at DEBUG level — discovery runs every cycle so INFO would be noisy.
        //
        // Called every sync cycle. Cost is bounded: each probe uses maxAttempts=1 (no retry backoff),
        // so the worst-case added latency is InitialTimeoutMs (1500ms) for any non-responding adapter.
        // All adapters are probed in parallel via Task.WhenAll, so cost does not multiply with adapter count.
        //
        // mappingLifetime: requested port mapping duration in seconds (gateway may grant less).
        public static async Task<IReadOnlyList<NatPmpManager>> DiscoverAdapters(uint mappingLifetime = DefaultMappingLifetime)
        {
            var candidates = new List<(NetworkInterface Nic, IPAddress Gateway)>();

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

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
                if (externalIp is not null)
                    LogManager.Instance.LogDebug($"NatPmpManager.DiscoverAdapters: '{c.Nic.Description}' via gateway {c.Gateway} (external IP: {externalIp})");
                else
                    LogManager.Instance.LogDebug($"NatPmpManager.DiscoverAdapters: '{c.Nic.Description}' via gateway {c.Gateway} — NAT-PMP probe failed");
                return (c.Nic, c.Gateway, Supported: externalIp is not null);
            })).ConfigureAwait(false);

            return probeResults
                .Where(r => r.Supported)
                .Select(r => new NatPmpManager(r.Nic, r.Gateway, mappingLifetime))
                .ToList();
        }

        private NatPmpManager(NetworkInterface adapter, IPAddress gateway, uint mappingLifetime)
        {
            _adapter         = adapter;
            _gateway         = gateway;
            _mappingLifetime = mappingLifetime;
        }

        // Re-enumerates network interfaces to check if the adapter is currently present and up.
        // The stored _adapter object retains its last-seen OperationalStatus even after the
        // adapter is removed (e.g. ProtonVPN removes the TUN adapter on disconnect), so a
        // fresh enumeration is required for an accurate result.
        public bool IsVpnConnected()
        {
            try
            {
                bool connected = NetworkInterface.GetAllNetworkInterfaces()
                    .Any(nic => nic.Description.Equals(_adapter.Description, StringComparison.OrdinalIgnoreCase)
                             && nic.OperationalStatus == OperationalStatus.Up);

                LogManager.Instance.LogDebug(connected
                    ? $"NatPmpManager.IsVpnConnected: Adapter '{_adapter.Description}' is up"
                    : $"NatPmpManager.IsVpnConnected: Adapter '{_adapter.Description}' is not found or not up");

                return connected;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.IsVpnConnected: {ex.Message}");
                return false;
            }
        }

        // Sends a NAT-PMP UDP port mapping request and returns the assigned external port.
        // Logs at INFO/WARN level (not DEBUG) — lease time and failure details are not surfaced
        // elsewhere in the sync cycle.
        // On renewal, suggests the previously assigned port (RFC 6886 §3.3) so the gateway keeps
        // the same mapping across cycles, avoiding unnecessary qBittorrent restarts.
        public int? GetVpnPort()
        {
            try
            {
                // On renewal, suggest the previously assigned port so the gateway keeps the same mapping.
                // On first call _lastExternalPort is 0, which tells the gateway to assign any available port.
                ushort suggested = _lastExternalPort;

                var result = RequestPortMappingAsync(_gateway, _mappingLifetime, suggested).GetAwaiter().GetResult();

                if (!result.Success)
                {
                    LogManager.Instance.LogMessage($"NAT-PMP port mapping failed on '{_adapter.Description}': {result.Error}", LogLevel.Warn);
                    return null;
                }

                // Detect NAT-PMP daemon restart: SSOE dropping means all prior mappings are gone.
                // The response is still valid (a fresh mapping was assigned) — log and reset cached state.
                if (_lastEpochSsoe > 0 && result.Ssoe < _lastEpochSsoe)
                    LogManager.Instance.LogMessage(
                        $"NAT-PMP epoch reset on '{_adapter.Description}' (was {_lastEpochSsoe}s, now {result.Ssoe}s) — prior mapping lost, fresh port assigned",
                        LogLevel.Info);

                string epochDelta = (_lastEpochSsoe > 0 && result.Ssoe >= _lastEpochSsoe)
                    ? $" (+{result.Ssoe - _lastEpochSsoe}s)" : "";
                LogManager.Instance.LogDebug($"NatPmpManager.GetVpnPort: SSOE {result.Ssoe}s{epochDelta}");

                _lastEpochSsoe    = result.Ssoe;
                _lastExternalPort = result.ExternalPort;

                if (suggested != 0 && result.ExternalPort == suggested)
                    LogManager.Instance.LogMessage($"NAT-PMP lease renewed: port {result.ExternalPort}, lifetime {result.LifetimeGranted}s", LogLevel.Info);
                else if (suggested != 0)
                    LogManager.Instance.LogMessage($"NAT-PMP lease granted new port {result.ExternalPort} (suggested {suggested} unavailable), lifetime {result.LifetimeGranted}s", LogLevel.Info);
                else
                    LogManager.Instance.LogMessage($"NAT-PMP lease granted: port {result.ExternalPort}, lifetime {result.LifetimeGranted}s", LogLevel.Info);

                int syncInterval = RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
                if (syncInterval > result.LifetimeGranted)
                    LogManager.Instance.LogMessage(
                        $"NAT-PMP sync interval ({syncInterval}s) exceeds lease lifetime ({result.LifetimeGranted}s) — port mapping will expire before the next renewal cycle",
                        LogLevel.Warn);

                return result.ExternalPort;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"NAT-PMP error on '{_adapter.Description}': {ex.Message}", LogLevel.Warn);
                return null;
            }
        }

        // Resolves the usable gateway for an adapter.
        // For standard adapters: uses the declared IPv4 gateway.
        // Otherwise (0.0.0.0, empty GatewayAddresses, or all-IPv6 gateways): infers x.x.x.1
        // from the unicast address. Windows commonly reports an empty GatewayAddresses for
        // DHCP-configured adapters even when a default gateway exists in the routing table.
        private static IPAddress? ResolveGateway(IPInterfaceProperties props)
        {
            IPAddress? gateway = props.GatewayAddresses
                .Select(gw => gw.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            return gateway ?? InferGatewayFromUnicast(props);
        }

        // Infers x.x.x.1 of the subnet from the adapter's unicast address
        private static IPAddress? InferGatewayFromUnicast(IPInterfaceProperties props)
        {
            foreach (UnicastIPAddressInformation address in props.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (address.IPv4Mask.Equals(IPAddress.Any))
                    continue; // zero mask — cannot infer a meaningful gateway

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

        // Sends a NAT-PMP external address request (RFC 6886 opcode 0) and returns the public IP.
        // Single attempt only — discovery is best-effort; a missed adapter can be found via Refresh.
        private static async Task<IPAddress?> RequestExternalAddressAsync(IPAddress gateway)
        {
            // [0] version=0  [1] opcode=0 (external address request)
            byte[] request = new byte[2];
            request[0] = 0x00;
            request[1] = 0x00;

            byte[]? data = await SendReceiveAsync(gateway, request, maxAttempts: 1).ConfigureAwait(false);
            if (data is null)
                return null;

            // [0] version=0  [1] opcode=0x80  [2-3] result  [4-7] epoch  [8-11] external IP
            if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x80)
                return null;

            ushort resultCode = (ushort)((data[2] << 8) | data[3]);
            if (resultCode != 0)
                return null;

            return new IPAddress(new byte[] { data[8], data[9], data[10], data[11] });
        }

        // Sends a NAT-PMP UDP port mapping request (RFC 6886 opcode 1).
        // Pass suggestedExternalPort=0 for an initial request; pass the previously assigned port to request renewal.
        private static async Task<(bool Success, ushort ExternalPort, uint LifetimeGranted, uint Ssoe, string? Error)>
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
            // [8-9] internal port  [10-11] external port  [12-15] lifetime
            if (data.Length < 16 || data[0] != 0x00 || data[1] != 0x81)
                return (false, 0, 0, 0, "Unexpected response format");

            ushort resultCode    = (ushort)((data[2]  << 8)  | data[3]);
            if (resultCode != 0)
                return (false, 0, 0, 0, $"NAT-PMP result code {resultCode}");

            uint   ssoe          = (uint)  ((data[4]  << 24) | (data[5]  << 16) | (data[6]  << 8) | data[7]);
            ushort externalPort  = (ushort)((data[10] << 8)  | data[11]);
            uint   lifetimeGiven = (uint)  ((data[12] << 24) | (data[13] << 16) | (data[14] << 8) | data[15]);

            if (externalPort == 0)
                return (false, 0, 0, ssoe, "Gateway returned external port 0");

            return (true, externalPort, lifetimeGiven, ssoe, null);
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
                    UdpReceiveResult result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);

                    if (!result.RemoteEndPoint.Address.Equals(gateway))
                    {
                        LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: Ignoring response from unexpected sender {result.RemoteEndPoint.Address}");
                        return null;
                    }

                    return result.Buffer;
                }
                catch (OperationCanceledException)
                {
                    if (attempt < maxAttempts - 1)
                        LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: No response after {timeoutMs}ms, retrying (attempt {attempt + 2}/{maxAttempts})");
                    timeoutMs *= 2;
                }
                catch (SocketException ex)
                {
                    LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: gateway {gateway} rejected NAT-PMP probe ({ex.SocketErrorCode}) — NAT-PMP may not be enabled on this gateway");
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

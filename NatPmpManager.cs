using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver
{
    // Generic NAT-PMP VPN manager. Instantiate with an adapter returned by DiscoverAdapters().
    public sealed class NatPmpManager : IVpnManager
    {
        private const int  NatPmpPort             = 5351;
        private const int  TimeoutMs              = 2000;
        public  const uint DefaultMappingLifetime = 3600; // 1 hour

        private readonly NetworkInterface _adapter;
        private readonly IPAddress        _gateway;
        private readonly uint             _mappingLifetime;

        public string ProviderName => _adapter.Description;

        // mappingLifetime: requested port mapping duration in seconds (gateway may grant less).
        // Valid range: 1 – uint.MaxValue (~136 years). Use 0 to delete an existing mapping.
        private NatPmpManager(NetworkInterface adapter, IPAddress gateway, uint mappingLifetime)
        {
            _adapter         = adapter;
            _gateway         = gateway;
            _mappingLifetime = mappingLifetime;
        }

        // Returns all network adapters whose gateway actively responds to NAT-PMP,
        // including TUN/VPN adapters where the gateway is inferred from the unicast address.
        // All candidates are probed in parallel; only those with a responding gateway are returned.
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
                bool supported = await RequestExternalAddressAsync(c.Gateway).ConfigureAwait(false) is not null;
                return (c.Nic, c.Gateway, Supported: supported);
            })).ConfigureAwait(false);

            return probeResults
                .Where(r => r.Supported)
                .Select(r => new NatPmpManager(r.Nic, r.Gateway, mappingLifetime))
                .ToList();
        }

        // Checks if the adapter is up and operational
        public bool IsVpnConnected()
        {
            try
            {
                bool connected = _adapter.OperationalStatus == OperationalStatus.Up;

                LogManager.Instance.LogDebug(connected
                    ? $"NatPmpManager.IsVpnConnected: Adapter '{_adapter.Description}' is up"
                    : $"NatPmpManager.IsVpnConnected: Adapter '{_adapter.Description}' is not up (status: {_adapter.OperationalStatus})");

                return connected;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.IsVpnConnected: {ex.Message}");
                return false;
            }
        }

        // Sends a NAT-PMP UDP port mapping request and returns the assigned external port.
        // Note: unlike other manager classes where all logging is at debug level, this method
        // intentionally logs at INFO/WARN level — the gateway response (or failure) carries
        // diagnostic information (reason, lease time) that is not surfaced elsewhere in the sync cycle.
        public int? GetVpnPort()
        {
            try
            {
                var result = RequestPortMappingAsync(_gateway, _mappingLifetime).GetAwaiter().GetResult();

                if (!result.Success)
                {
                    LogManager.Instance.LogMessage($"NAT-PMP port mapping failed on '{_adapter.Description}': {result.Error}", LogLevel.Warn);
                    return null;
                }

                LogManager.Instance.LogMessage($"NAT-PMP lease granted: port {result.ExternalPort}, lifetime {result.LifetimeGranted}s", LogLevel.Info);

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

        // Sends a NAT-PMP UDP port mapping request (RFC 6886 opcode 1)
        private static async Task<(bool Success, ushort ExternalPort, uint LifetimeGranted, string? Error)>
            RequestPortMappingAsync(IPAddress gateway, uint lifetime)
        {
            // [0] version=0  [1] opcode=1 (UDP)  [2-3] reserved
            // [4-5] internal port=0  [6-7] external port=0  [8-11] lifetime
            byte[] request = new byte[12];
            request[0]  = 0x00;
            request[1]  = 0x01;
            request[8]  = (byte)(lifetime >> 24);
            request[9]  = (byte)(lifetime >> 16);
            request[10] = (byte)(lifetime >> 8);
            request[11] = (byte)(lifetime & 0xFF);

            byte[]? data = await SendReceiveAsync(gateway, request).ConfigureAwait(false);
            if (data is null)
                return (false, 0, 0, "No response from gateway (timeout)");

            // [0] version=0  [1] opcode=0x81  [2-3] result  [4-7] epoch
            // [8-9] internal port  [10-11] external port  [12-15] lifetime
            if (data.Length < 16 || data[0] != 0x00 || data[1] != 0x81)
                return (false, 0, 0, "Unexpected response format");

            ushort resultCode    = (ushort)((data[2]  << 8)  | data[3]);
            if (resultCode != 0)
                return (false, 0, 0, $"NAT-PMP result code {resultCode}");

            ushort externalPort  = (ushort)((data[10] << 8)  | data[11]);
            uint   lifetimeGiven = (uint)  ((data[12] << 24) | (data[13] << 16) | (data[14] << 8) | data[15]);

            if (externalPort == 0)
                return (false, 0, 0, "Gateway returned external port 0");

            return (true, externalPort, lifetimeGiven, null);
        }

        // Sends a NAT-PMP external address request (RFC 6886 opcode 0) and returns the public IP
        private static async Task<IPAddress?> RequestExternalAddressAsync(IPAddress gateway)
        {
            // [0] version=0  [1] opcode=0 (external address request)
            byte[] request = new byte[2];
            request[0] = 0x00;
            request[1] = 0x00;

            byte[]? data = await SendReceiveAsync(gateway, request).ConfigureAwait(false);
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

        // Sends a UDP datagram to the gateway and waits for a response
        private static async Task<byte[]?> SendReceiveAsync(IPAddress gateway, byte[] request)
        {
            try
            {
                using var udp = new UdpClient();
                await udp.SendAsync(request, new IPEndPoint(gateway, NatPmpPort)).ConfigureAwait(false);

                using var cts = new CancellationTokenSource(TimeoutMs);
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
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: {ex.Message}");
                return null;
            }
        }
    }
}

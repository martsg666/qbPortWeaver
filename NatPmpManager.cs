using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver
{
    // Generic NAT-PMP VPN manager. Instantiate with an adapter returned by DiscoverAdapters().
    public sealed class NatPmpManager : IVPNManager
    {
        private const int  NAT_PMP_PORT           = 5351;
        private const int  TIMEOUT_MS             = 2000;
        public  const uint DEFAULT_MAPPING_LIFETIME = 3600; // 1 hour

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

        // Returns all network adapters that have a reachable NAT-PMP gateway,
        // including TUN/VPN adapters where the gateway is inferred from the unicast address.
        // mappingLifetime: requested port mapping duration in seconds (gateway may grant less).
        public static IReadOnlyList<NatPmpManager> DiscoverAdapters(uint mappingLifetime = DEFAULT_MAPPING_LIFETIME)
        {
            var discovered = new List<NatPmpManager>();

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

                discovered.Add(new NatPmpManager(nic, gateway, mappingLifetime));
            }

            return discovered;
        }

        // Checks if the adapter is up and operational
        public bool IsVPNConnected()
        {
            try
            {
                bool connected = _adapter.OperationalStatus == OperationalStatus.Up;

                LogManager.Instance.LogDebug(connected
                    ? $"NatPmpManager.IsVPNConnected: Adapter '{_adapter.Name}' is up"
                    : $"NatPmpManager.IsVPNConnected: Adapter '{_adapter.Name}' is not up (status: {_adapter.OperationalStatus})");

                return connected;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.IsVPNConnected: {ex.Message}");
                return false;
            }
        }

        // Sends a NAT-PMP UDP port mapping request and returns the assigned external port
        public int? GetVPNPort()
        {
            try
            {
                var result = RequestPortMappingAsync(_gateway, _mappingLifetime).GetAwaiter().GetResult();

                if (!result.Success)
                {
                    LogManager.Instance.LogDebug($"NatPmpManager.GetVPNPort: Mapping failed on '{_adapter.Name}': {result.Error}");
                    return null;
                }

                IPAddress? externalIp = RequestExternalAddressAsync(_gateway).GetAwaiter().GetResult();
                if (externalIp != null)
                    LogManager.Instance.LogMessage($"NAT-PMP external IP: {externalIp}", "INFO");
                else
                    LogManager.Instance.LogDebug($"NatPmpManager.GetVPNPort: Could not retrieve external IP for '{_adapter.Name}'");

                LogManager.Instance.LogDebug($"NatPmpManager.GetVPNPort: '{_adapter.Name}' mapped external port {result.ExternalPort} (lifetime {result.LifetimeGranted}s)");
                return result.ExternalPort;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.GetVPNPort: {ex.Message}");
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
            foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
            {
                if (gw.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (!gw.Address.Equals(IPAddress.Any))
                    return gw.Address;

                // 0.0.0.0 — keep scanning; a real gateway may follow
            }

            // No explicit IPv4 gateway found: infer x.x.x.1 from the unicast address
            return InferGatewayFromUnicast(props);
        }

        // Infers x.x.x.1 of the subnet from the adapter's unicast address
        private static IPAddress? InferGatewayFromUnicast(IPInterfaceProperties props)
        {
            foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (uni.IPv4Mask.Equals(IPAddress.Any))
                    continue; // zero mask — cannot infer a meaningful gateway

                byte[] addr = uni.Address.GetAddressBytes();
                byte[] mask = uni.IPv4Mask.GetAddressBytes();

                byte[] network = new byte[4];
                for (int i = 0; i < 4; i++)
                    network[i] = (byte)(addr[i] & mask[i]);

                network[3] = 1;
                var candidate = new IPAddress(network);

                if (!candidate.Equals(uni.Address))
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

            byte[]? data = await SendReceiveAsync(gateway, request);
            if (data is null)
                return (false, 0, 0, "Timeout");

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

            byte[]? data = await SendReceiveAsync(gateway, request);
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
                await udp.SendAsync(request, new IPEndPoint(gateway, NAT_PMP_PORT));

                using var cts = new CancellationTokenSource(TIMEOUT_MS);
                UdpReceiveResult result = await udp.ReceiveAsync(cts.Token);

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

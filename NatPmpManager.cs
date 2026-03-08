using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver
{
    // NAT-PMP VPN manager. PortSyncService creates instances via TryCreateForAdapter() (sync cycle)
    // or DiscoverAdapters() (SettingsForm). Renewal state (_lastExternalPort, _lastEpochSeconds) is
    // transferred across cycles via CopyRenewalStateFrom() so port renewal works correctly.
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

        private NatPmpManager(NetworkInterface adapter, IPAddress gateway, uint mappingLifetime)
        {
            _adapter         = adapter;
            _gateway         = gateway;
            _mappingLifetime = mappingLifetime;
        }

        // Returns the adapter description, used as the VPN provider display name in log messages and status.
        public string ProviderName => _adapter.Description;

        // The lease lifetime (in seconds) granted by the gateway on the last successful port mapping; 0 until first mapping.
        // Read by PortSyncService to warn when the configured sync interval exceeds the lease lifetime.
        public uint LastGrantedLifetime { get; private set; }

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
        // Primarily logs at INFO/WARN (not DEBUG) — lease time and failure details are not surfaced
        // elsewhere in the sync cycle. The epoch delta is the exception, logged at DEBUG only.
        // On renewal, suggests the previously assigned port (RFC 6886 §3.3) so the gateway keeps
        // the same mapping across cycles, avoiding unnecessary qBittorrent restarts.
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
                    LogManager.Instance.LogMessage($"NAT-PMP port mapping failed on '{_adapter.Description}': {result.Error}", LogLevel.Warn);
                    return null;
                }

                // Detect NAT-PMP daemon restart: SSOE dropping means all prior mappings are gone.
                // The response is still valid (a fresh mapping was assigned) — log and update state below.
                if (_lastEpochSeconds > 0 && result.EpochSeconds < _lastEpochSeconds)
                    LogManager.Instance.LogMessage(
                        $"NAT-PMP epoch reset on '{_adapter.Description}' (was {_lastEpochSeconds}s, now {result.EpochSeconds}s) — prior mapping lost, fresh port assigned",
                        LogLevel.Info);

                string epochDelta = (_lastEpochSeconds > 0 && result.EpochSeconds >= _lastEpochSeconds)
                    ? $" (+{result.EpochSeconds - _lastEpochSeconds}s)" : "";
                LogManager.Instance.LogDebug($"NatPmpManager.GetVpnPort: SSOE {result.EpochSeconds}s{epochDelta}");

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
                LogManager.Instance.LogMessage($"NAT-PMP error on '{_adapter.Description}': {ex.Message}", LogLevel.Warn);
                return null;
            }
        }

        // Returns all network adapters whose gateway actively responds to NAT-PMP,
        // including TUN/VPN adapters where the gateway is inferred from the unicast address.
        // All candidates are probed in parallel with a single attempt each, only those with a responding
        // gateway are returned. Used by SettingsForm to populate the adapter list.
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

        // Probes only the named adapter rather than all adapters. Used by the sync cycle so that
        // unrelated adapters (e.g. ZeroTier, Ethernet) are never probed unnecessarily.
        // Unlike DiscoverAdapters (maxAttempts=1), uses MaxAttempts retries with exponential backoff
        // since probing a single known adapter is worth retrying on transient packet loss.
        // Returns null if the adapter is not found/up, has no resolvable gateway, or does not
        // respond to a NAT-PMP probe.
        public static async Task<NatPmpManager?> TryCreateForAdapter(string adapterName, uint mappingLifetime = DefaultMappingLifetime)
        {
            NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Description.Equals(adapterName, StringComparison.OrdinalIgnoreCase)
                                  && n.OperationalStatus == OperationalStatus.Up
                                  && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                                                           and not NetworkInterfaceType.Tunnel);

            if (nic is null)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapter: '{adapterName}' not found or not up");
                return null;
            }

            IPAddress? gateway = ResolveGateway(nic.GetIPProperties());
            if (gateway is null)
            {
                LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapter: '{adapterName}' — no resolvable gateway");
                return null;
            }

            IPAddress? externalIp = await RequestExternalAddressAsync(gateway, MaxAttempts).ConfigureAwait(false);
            if (externalIp is not null)
                LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapter: '{adapterName}' via gateway {gateway} (external IP: {externalIp})");
            else
                LogManager.Instance.LogDebug($"NatPmpManager.TryCreateForAdapter: '{adapterName}' via gateway {gateway} — NAT-PMP probe failed");

            return externalIp is not null ? new NatPmpManager(nic, gateway, mappingLifetime) : null;
        }

        // Transfers renewal state from a previous instance so that port renewal works correctly
        // when a fresh NatPmpManager instance is created each cycle.
        internal void CopyRenewalStateFrom(NatPmpManager other)
        {
            _lastExternalPort  = other._lastExternalPort;
            _lastEpochSeconds  = other._lastEpochSeconds;
        }

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

        // Infers the gateway as x.x.x.1 of the adapter's subnet — a common convention for VPN gateways.
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
        // maxAttempts=1 for discovery (best-effort, all adapters probed in parallel)
        // MaxAttempts for targeted probes where retrying a single adapter is worthwhile.
        private static async Task<IPAddress?> RequestExternalAddressAsync(IPAddress gateway, int maxAttempts = 1)
        {
            // version=0, opcode=0 (external address request) — both zero by default
            byte[] request = new byte[2];

            byte[]? data = await SendReceiveAsync(gateway, request, maxAttempts).ConfigureAwait(false);
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
        // Pass zero as suggestedExternalPort for an initial request, or the previously assigned port to request renewal.
        // Internal port is set to 0 — clients that do not bind a specific port let the gateway infer it.
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

            ushort resultCode    = (ushort)((data[2]  << 8)  | data[3]);
            if (resultCode != 0)
                return (false, 0, 0, 0, $"NAT-PMP result code {resultCode}");

            uint   epochSeconds  = (uint)  ((data[4]  << 24) | (data[5]  << 16) | (data[6]  << 8) | data[7]);
            ushort externalPort  = (ushort)((data[10] << 8)  | data[11]);
            uint   lifetimeGiven = (uint)  ((data[12] << 24) | (data[13] << 16) | (data[14] << 8) | data[15]);

            if (externalPort == 0)
                return (false, 0, 0, epochSeconds, "Gateway returned external port 0");

            return (true, externalPort, lifetimeGiven, epochSeconds, null);
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
                        LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: Response from unexpected sender {result.RemoteEndPoint.Address}, discarding and retrying");
                        continue;
                    }

                    return result.Buffer;
                }
                catch (OperationCanceledException)
                {
                    if (attempt < maxAttempts - 1)
                        LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: No response after {timeoutMs}ms, retrying (attempt {attempt + 2}/{maxAttempts})");
                    else if (maxAttempts > 1)
                        LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: No response after {timeoutMs}ms (all {maxAttempts} attempts exhausted)");
                    else
                        LogManager.Instance.LogDebug($"NatPmpManager.SendReceiveAsync: No response after {timeoutMs}ms");
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

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace qbPortWeaver;

/// <summary>
/// Finds the public address a VPN sends this machine's traffic out on, by asking a STUN server
/// (RFC 5389) which address a request sent from the VPN adapter arrived from. Peers learn a client's
/// address from what other hosts observe - the Soulseek server, other peers - so this is the address
/// they are given. When it differs from the address the NAT-PMP gateway forwards the port on, no peer
/// can ever reach the forward, whatever the client does.
/// </summary>
internal static class VpnExitAddress
{
    private const int TimeoutMs = 2000;
    private const int MaxAttempts = 2;
    private const int HeaderLength = 20;
    private const int TransactionIdLength = 12;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccessResponse = 0x0101;
    private const ushort AttributeMappedAddress = 0x0001;
    private const ushort AttributeXorMappedAddress = 0x0020;
    private const byte FamilyIPv4 = 0x01;
    private const uint MagicCookie = 0x2112A442;

    /// <summary>
    /// Returns the address the STUN server saw a request from <paramref name="localAddress"/> arrive
    /// from, or <see langword="null"/> when it cannot be determined - the server name does not resolve,
    /// no reply arrives, or the reply is not a valid answer to this request. Throws only
    /// <see cref="OperationCanceledException"/>, when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="localAddress">The VPN adapter's own address. Binding to it sends the request
    /// through the tunnel even when the VPN is not the default route (split tunnelling).</param>
    /// <param name="cancellationToken">Cancels the lookup, the send and the wait for a reply.</param>
    internal static async Task<IPAddress?> QueryAsync(IPAddress localAddress, CancellationToken cancellationToken = default)
    {
        IPAddress? server = await ResolveServerAsync(cancellationToken).ConfigureAwait(false);
        if (server is null) return null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            IPAddress? exit = await TryOnceAsync(localAddress, server, cancellationToken).ConfigureAwait(false);
            if (exit is not null) return exit;
        }
        LogManager.Instance.LogDebug($"VpnExitAddress.QueryAsync: no answer from {AppConstants.StunServerHost} after {MaxAttempts} attempts");
        return null;
    }

    /// <summary>
    /// Explains a mismatch between the forward address and the exit address, for the log, the tray
    /// balloon and Diagnostics. Shared so the three cannot describe the same condition differently.
    /// </summary>
    /// <param name="source">Where the forward comes from, as <see cref="IVpnManager.ForwardSource"/> names it.</param>
    /// <param name="port">The forwarded port, or <see langword="null"/> when none is named.</param>
    /// <param name="forward">The address the port is forwarded on.</param>
    /// <param name="exit">The address traffic leaves from.</param>
    internal static string DescribeMismatch(string source, int? port, IPAddress forward, IPAddress exit)
    {
        string portText = port is int p ? $"port {p}" : "the port";
        // A private or shared forward address is the user's own router reporting its WAN side, which
        // sits behind a second NAT (carrier-grade NAT, or another router upstream). No server to switch
        // to there, so the VPN advice would be wrong.
        if (IsBehindAnotherNat(forward))
            return $"The NAT-PMP gateway on {source} forwards {portText} on {forward}, a private address, " +
                   $"while traffic leaves from {exit} - the gateway is itself behind another NAT, so the port " +
                   "cannot be reached from the Internet";
        return $"{source} forwards {portText} on {forward}, but traffic leaves from {exit}. Peers are " +
               $"given {exit}, so they cannot reach the forwarded port - connect the VPN to a different server";
    }

    // RFC 1918 private ranges, RFC 6598 shared address space (carrier-grade NAT) and link-local: none
    // of them is reachable from the Internet, so a gateway reporting one is behind another NAT.
    internal static bool IsBehindAnotherNat(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254);
    }

    private static async Task<IPAddress?> ResolveServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(AppConstants.StunServerHost, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault();
        }
        catch (SocketException ex)
        {
            LogManager.Instance.LogDebug($"VpnExitAddress.ResolveServerAsync: {AppConstants.StunServerHost} did not resolve: {ex.Message}");
            return null;
        }
    }

    private static async Task<IPAddress?> TryOnceAsync(IPAddress localAddress, IPAddress server, CancellationToken cancellationToken)
    {
        byte[] transactionId = RandomNumberGenerator.GetBytes(TransactionIdLength);
        byte[] request = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(request, BindingRequest);
        // [2-3] message length = 0: a Binding request needs no attributes
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
        transactionId.CopyTo(request, 8);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutMs);
        try
        {
            // Bound to the adapter's address, so the request leaves through that adapter. Throws a
            // SocketException if the address went away since it was read (the VPN reconnected).
            using var udp = new UdpClient(new IPEndPoint(localAddress, 0));
            await udp.SendAsync(request, new IPEndPoint(server, AppConstants.StunServerPort), timeout.Token).ConfigureAwait(false);
            UdpReceiveResult reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return ParseMappedAddress(reply.Buffer, transactionId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // timed out; the caller retries
        }
        catch (SocketException ex)
        {
            LogManager.Instance.LogDebug($"VpnExitAddress.TryOnceAsync: {ex.Message}");
            return null;
        }
    }

    // Reads the mapped address from a Binding success response, or null if the response is not one,
    // answers a different request, or carries no IPv4 mapped address. XOR-MAPPED-ADDRESS is preferred:
    // it is the attribute RFC 5389 defines, and the plain MAPPED-ADDRESS is only a fallback for
    // servers built to the older RFC 3489.
    private static IPAddress? ParseMappedAddress(byte[] data, byte[] transactionId)
    {
        if (data.Length < HeaderLength
            || BinaryPrimitives.ReadUInt16BigEndian(data) != BindingSuccessResponse
            || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)) != MagicCookie
            || !data.AsSpan(8, TransactionIdLength).SequenceEqual(transactionId))
            return null;

        int end = Math.Min(data.Length, HeaderLength + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)));
        IPAddress? plain = null;
        int offset = HeaderLength;
        while (offset + 4 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
            int value = offset + 4;
            if (value + length > end) break;

            // [0] reserved  [1] family  [2-3] port  [4-7] IPv4 address
            if (length >= 8 && data[value + 1] == FamilyIPv4)
            {
                if (type == AttributeXorMappedAddress)
                {
                    uint xored = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(value + 4)) ^ MagicCookie;
                    byte[] address = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(address, xored);
                    return new IPAddress(address);
                }
                if (type == AttributeMappedAddress)
                    plain = new IPAddress(data.AsSpan(value + 4, 4));
            }
            offset = value + ((length + 3) & ~3); // attributes are padded to a multiple of 4 bytes
        }
        return plain;
    }
}

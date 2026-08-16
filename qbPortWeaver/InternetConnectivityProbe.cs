using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver;

/// <summary>
/// Answers one question for auto-recovery: does this machine have any upstream connectivity at all?
/// <para>Recovery restarts the VPN service or cycles the adapter, neither of which can help when the
/// internet itself is down - the VPN has nothing to connect to. Without this check a wider outage
/// makes every sync cycle fail, which makes recovery fire again and again for as long as the outage
/// lasts, repeatedly killing a VPN service that was never the problem.</para>
/// <para>Probes public DNS resolvers by ICMP rather than resolving a hostname, deliberately: name
/// resolution is one of the things an outage breaks, so a DNS-dependent probe would report "no
/// internet" for a DNS fault that recovery might legitimately fix. Two independent operators are
/// used so one being unreachable does not suppress recovery on its own.</para>
/// </summary>
internal static class InternetConnectivityProbe
{
    // Cloudflare and Google public DNS. Both are anycast, globally reachable, and answer ICMP.
    // Hardcoded deliberately: the point of the probe is to test raw connectivity without depending on
    // name resolution, which is one of the things an outage breaks. Two operators, so one being
    // unreachable cannot suppress recovery by itself.
    private static readonly IPAddress[] ProbeAddresses =
    [
        IPAddress.Parse("1.1.1.1"), // NOSONAR S1313 - see above
        IPAddress.Parse("8.8.8.8"), // NOSONAR S1313 - see above
    ];

    // Short enough that a probe never delays a sync cycle noticeably, long enough for a slow link.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns <see langword="true"/> if any probe address replies to ICMP, meaning recovery has
    /// something to reconnect to. Both addresses are probed concurrently, so the whole call is
    /// bounded by <see cref="ProbeTimeout"/> rather than the sum. A machine that is genuinely offline
    /// returns <see langword="false"/>; so does one whose network blocks outbound ICMP entirely.
    /// Never throws except <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    internal static async Task<bool> IsInternetReachableAsync(CancellationToken cancellationToken)
    {
        bool[] replies = await Task.WhenAll(
            ProbeAddresses.Select(address => ProbeAddressAsync(address, cancellationToken))).ConfigureAwait(false);

        bool reachable = Array.Exists(replies, replied => replied);
        LogManager.Instance.LogDebug(
            $"InternetConnectivityProbe.IsInternetReachableAsync: reachable={reachable}");
        return reachable;
    }

    // A single ICMP echo. Any failure is reported as "no reply" rather than propagating: the caller
    // treats an unreachable probe and a failed probe identically, and a probe must never be able to
    // break the sync cycle. Cancellation is exempt so shutdown is not swallowed as a failed probe.
    private static async Task<bool> ProbeAddressAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            PingReply reply = await ping.SendPingAsync(address, ProbeTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            LogManager.Instance.LogDebug($"InternetConnectivityProbe.ProbeAddressAsync: {address} - {ex.Message}");
            return false;
        }
    }
}

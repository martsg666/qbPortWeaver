using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace qbPortWeaver;

/// <summary>
/// Adapter rules shared by the VPN managers that work through a network adapter: which adapters are
/// candidates, whether one is up and what its address is, and what auto-recovery does to an adapter
/// chosen in Settings. Kept in one place so the providers cannot drift apart on any of them.
/// </summary>
internal static class VpnAdapter
{
    // Returns all network interfaces that are up and not loopback or protocol-tunnel adapters.
    // NetworkInterfaceType.Tunnel covers Windows IF_TYPE_TUNNEL (Teredo, ISATAP, 6to4) -
    // not VPN adapters. WireGuard/wintun (ProtonVPN) and TAP/OpenVPN adapters report as
    // Unknown or Ethernet and are not excluded by this filter.
    //
    // Every adapter lookup goes through here (IsUp and with it the adapter-based IsVpnConnected checks,
    // the NAT-PMP probes, ProtonVPN's adapter lookup), so this is the one guard: GetAllNetworkInterfaces
    // throws NetworkInformationException when the network subsystem is briefly unavailable, which on a
    // machine riding VPN adapter churn is an ordinary transient rather than a fault. An empty sequence
    // hands the callers the answer they already handle - an adapter that is not up, a null from
    // NatPmpManager.TryCreateForAdapterAsync, both adapter lists in Settings coming back empty - instead
    // of a throw that RunAsync would report as "An unexpected error occurred" at Error, for what is
    // really just a disconnected adapter.
    //
    // ToList inside the try is load-bearing: a deferred Where would run GetAllNetworkInterfaces at the
    // caller's foreach or FirstOrDefault, outside this catch, and the guard would never fire. The
    // return type is List rather than IEnumerable so the compiler enforces that rather than this
    // comment: returning the lazy Where directly no longer builds. It also lets the foreach in
    // NatPmpManager.DiscoverAdaptersAsync use List's struct enumerator instead of dispatching through
    // the interface.
    internal static List<NetworkInterface> GetActiveInterfaces()
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
            LogManager.Instance.LogDebug($"VpnAdapter.GetActiveInterfaces: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if an adapter named <paramref name="adapterName"/> is among the
    /// <see cref="GetActiveInterfaces"/> candidates, so it is up. Re-enumerates on every call, because a
    /// stored <see cref="NetworkInterface"/> keeps its last-seen status after the adapter is removed on
    /// disconnect.
    /// </summary>
    /// <param name="adapterName">The adapter to look for, matched ignoring case.</param>
    /// <param name="caller">Prefix for the debug lines, so the log names the manager that asked.</param>
    internal static bool IsUp(string adapterName, string caller)
    {
        bool connected = GetActiveInterfaces()
            .Exists(nic => nic.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));

        LogManager.Instance.LogDebug(connected
            ? $"{caller}: Adapter '{adapterName}' is connected"
            : $"{caller}: Adapter '{adapterName}' is not found or not connected");

        return connected;
    }

    /// <summary>
    /// The first IPv4 unicast address of the adapter named <paramref name="adapterName"/>, or
    /// <see langword="null"/> if it is not up or has none. Re-enumerates on every call, like
    /// <see cref="IsUp"/>, because the VPN hands the adapter a new address on each reconnect.
    /// </summary>
    internal static IPAddress? GetIPv4Address(string adapterName)
    {
        try
        {
            return GetActiveInterfaces()
                .FirstOrDefault(nic => nic.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase))?
                .GetIPProperties().UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (NetworkInformationException ex)
        {
            LogManager.Instance.LogDebug($"VpnAdapter.GetIPv4Address: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The recovery target for an adapter: the provider token when the adapter belongs to a known
    /// provider whose service is installed (so that service is restarted), otherwise the adapter name
    /// itself (so it is cycled).
    /// </summary>
    /// <remarks>The action that goes with it is a service restart for an installed known provider's
    /// adapter and an adapter cycle for any other. Both come from one lookup, which enumerates the
    /// Windows services, so a caller needing both reads them together rather than looking up twice.</remarks>
    internal static (string Target, string Action) GetRecovery(string adapterName) =>
        FindRestartableProviderToken(adapterName) is { } token
            ? (token, HelperProtocol.ActionRestart)
            : (adapterName, HelperProtocol.ActionCycleAdapter);

    // The adapter name alone is not enough: a provider's WireGuard config run in WireGuard's own app is
    // often named after the provider ("ProtonVPN-NL-12") with that provider's app not installed, and a
    // restart of a service that does not exist skips recovery every time, while cycling the adapter works.
    private static string? FindRestartableProviderToken(string adapterName) =>
        VpnProviderRegistry.FindProviderToken(adapterName) is { } token &&
        VpnProviderRegistry.FindByKeyword(token)?.Config.FindServiceName() is not null
            ? token
            : null;
}

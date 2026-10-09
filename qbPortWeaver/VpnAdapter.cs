using System.Net.NetworkInformation;

namespace qbPortWeaver;

/// <summary>
/// Adapter rules shared by the VPN managers that work through a network adapter chosen in Settings
/// (<see cref="NatPmpManager"/> and <see cref="StaticPortManager"/>): which adapters are candidates,
/// whether the chosen one is up, and what auto-recovery does to it. Kept in one place so the two
/// providers cannot drift apart on any of them.
/// </summary>
internal static class VpnAdapter
{
    // Returns all network interfaces that are up and not loopback or protocol-tunnel adapters.
    // NetworkInterfaceType.Tunnel covers Windows IF_TYPE_TUNNEL (Teredo, ISATAP, 6to4) -
    // not VPN adapters. WireGuard/wintun (ProtonVPN) and TAP/OpenVPN adapters report as
    // Unknown or Ethernet and are not excluded by this filter.
    //
    // Guarded the same way the IsVpnConnected implementations guard this call: GetAllNetworkInterfaces
    // throws NetworkInformationException when the network subsystem is briefly unavailable, which on a
    // machine riding VPN adapter churn is an ordinary transient rather than a fault. An empty sequence
    // hands the callers the answer they already handle - NatPmpManager.TryCreateForAdapterAsync returns
    // null ("not found or not up"), and both adapter lists in Settings come back empty - instead of a
    // throw that RunAsync would report as "An unexpected error occurred" at Error, for what is really
    // just a disconnected adapter.
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
    /// Returns <see langword="true"/> if an adapter named <paramref name="adapterName"/> exists and is up.
    /// Re-enumerates on every call, because a stored <see cref="NetworkInterface"/> keeps its last-seen
    /// status after the adapter is removed on disconnect.
    /// </summary>
    /// <param name="adapterName">The adapter to look for, matched ignoring case.</param>
    /// <param name="caller">Prefix for the debug lines, so the log names the manager that asked.</param>
    internal static bool IsUp(string adapterName, string caller)
    {
        try
        {
            bool connected = NetworkInterface.GetAllNetworkInterfaces()
                .Any(nic => nic.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase)
                         && nic.OperationalStatus == OperationalStatus.Up);

            LogManager.Instance.LogDebug(connected
                ? $"{caller}: Adapter '{adapterName}' is connected"
                : $"{caller}: Adapter '{adapterName}' is not found or not connected");

            return connected;
        }
        catch (Exception ex)
        {
            return LogManager.LogDebugFalse($"{caller}: {ex.Message}");
        }
    }

    /// <summary>
    /// The recovery target for an adapter: the provider token when the adapter belongs to a known
    /// provider (so its service is restarted), otherwise the adapter name itself (so it is cycled).
    /// </summary>
    internal static string GetRecoveryTarget(string adapterName) =>
        VpnProviderRegistry.FindProviderToken(adapterName) ?? adapterName;

    /// <summary>
    /// The recovery action matching <see cref="GetRecoveryTarget"/>: a service restart for a known
    /// provider's adapter, an adapter cycle for any other.
    /// </summary>
    internal static string GetRecoveryAction(string adapterName) =>
        VpnProviderRegistry.FindProviderToken(adapterName) is not null
            ? HelperProtocol.ActionRestart
            : HelperProtocol.ActionCycleAdapter;
}

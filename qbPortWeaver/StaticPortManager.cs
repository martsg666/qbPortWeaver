using System.Net.NetworkInformation;

namespace qbPortWeaver;

/// <summary>
/// VPN manager for providers that assign a permanent forwarded port in their own account settings
/// rather than through NAT-PMP or a local client. The port is entered once in Settings and never
/// changes, so there is nothing to discover: this manager only reports whether the chosen VPN
/// adapter is up, and drives recovery and the interface checks through that adapter.
/// <para>Stateless and cheap to build, so the sync loop creates one per cycle from the saved settings.
/// It is built even while the adapter is down; <see cref="IsVpnConnected"/> then reports false and the
/// sync loop's normal disconnected handling applies.</para>
/// </summary>
public sealed class StaticPortManager(string adapterName, int port) : IVpnManager
{
    /// <inheritdoc />
    // The adapter name, as for NAT-PMP: log lines and the interface-mismatch warning read
    // "'<adapter>' is connected" and "is not a <adapter> adapter".
    public string ProviderName => adapterName;

    /// <inheritdoc />
    // Re-enumerated on every call: a VPN adapter disappears or goes down on disconnect.
    public bool IsVpnConnected()
    {
        try
        {
            bool connected = NetworkInterface.GetAllNetworkInterfaces()
                .Any(nic => nic.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase)
                         && nic.OperationalStatus == OperationalStatus.Up);

            LogManager.Instance.LogDebug(connected
                ? $"StaticPortManager.IsVpnConnected: Adapter '{adapterName}' is connected"
                : $"StaticPortManager.IsVpnConnected: Adapter '{adapterName}' is not found or not connected");

            return connected;
        }
        catch (Exception ex)
        {
            return LogManager.LogDebugFalse($"StaticPortManager.IsVpnConnected: {ex.Message}");
        }
    }

    /// <inheritdoc />
    // The configured value, as is. The sync loop applies the same usable-port rule to it as to a
    // discovered port, so an out-of-range value is reported rather than pushed to the client.
    public Task<int?> GetVpnPortAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(port);

    /// <inheritdoc />
    // Same rule as NAT-PMP: restart the provider's service when the adapter belongs to a known
    // provider, otherwise cycle the adapter itself.
    public string? GetRecoveryTarget() => VpnProviderRegistry.FindProviderToken(adapterName) ?? adapterName;

    /// <inheritdoc />
    public string GetRecoveryAction() =>
        VpnProviderRegistry.FindProviderToken(adapterName) is not null
            ? HelperProtocol.ActionRestart
            : HelperProtocol.ActionCycleAdapter;

    /// <inheritdoc />
    public bool IsAdapterMatch(string interfaceName)
        => VpnRegistryConfig.AdapterNamesMatch(adapterName, interfaceName);

    /// <summary>
    /// Returns the names of the network adapters that are currently up, sorted, for the Settings
    /// adapter list. Unlike NAT-PMP discovery nothing is probed: any adapter can carry a static
    /// forward, so the user picks the VPN's own adapter from everything that is up. Loopback and
    /// tunnel-type pseudo adapters are left out, as NAT-PMP discovery leaves them out.
    /// </summary>
    internal static IReadOnlyList<string> GetActiveAdapterNames()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                           && nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                                                      and not NetworkInterfaceType.Tunnel)
                .Select(nic => nic.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (NetworkInformationException ex)
        {
            LogManager.Instance.LogDebug($"StaticPortManager.GetActiveAdapterNames: {ex.Message}");
            return [];
        }
    }
}

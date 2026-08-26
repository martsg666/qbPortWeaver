using System.ServiceProcess;

namespace qbPortWeaver;

/// <summary>
/// Best-effort detection of which VPN provider is present on this machine, used by the Settings
/// dialog's Detect button to pre-fill the provider selection. Only providers that install a Windows
/// service can be found this way, which is exactly <see cref="VpnProviderRegistry.KnownProviders"/>
/// (ProtonVPN, PIA); NAT-PMP is a gateway protocol with nothing machine-local to look for, so it is
/// never returned and stays a manual choice. A running service is a stronger signal than one that is
/// merely installed, so it wins when both providers are present; providers are probed in
/// <see cref="VpnProviderRegistry.KnownProviders"/> order so the result is deterministic.
/// </summary>
internal static class VpnDetector
{
    internal enum DetectionKind { Running, Installed }

    /// <summary>A provider found on this machine: the keyword to select in Settings (one of
    /// <see cref="RegistrySettingsManager.VpnProviderProtonVpn"/> etc.), the Windows service that
    /// matched, and whether that service is currently running.</summary>
    internal sealed record DetectedVpn(string ProviderKeyword, string ServiceName, DetectionKind Kind);

    /// <summary>
    /// Returns every known VPN provider whose service is installed on this machine, in canonical
    /// order (ProtonVPN, PIA), each marked <see cref="DetectionKind.Running"/> when its service is
    /// running and <see cref="DetectionKind.Installed"/> otherwise. An empty list means none were
    /// found. Never throws.
    /// </summary>
    internal static IReadOnlyList<DetectedVpn> DetectAll()
    {
        ServiceController[]? services = null;
        try
        {
            // One SCM enumeration covers every provider. ServiceLookup.FindServiceName would enumerate
            // once per provider and discard the status we need, so the matching rule is shared with it
            // (ServiceLookup.ServiceMatches) rather than the whole lookup.
            services = ServiceController.GetServices();

            var results = new List<DetectedVpn>();
            foreach (var provider in VpnProviderRegistry.KnownProviders)
            {
                // Registry-driven and therefore user-editable; an emptied search term would match every
                // service (Contains("") is always true), so treat it as "cannot detect this provider".
                string searchTerm = provider.Config.GetServiceSearchTerm();
                if (string.IsNullOrEmpty(searchTerm)) continue;

                var match = Array.Find(services, s => SafeServiceMatches(s, searchTerm));
                if (match is null) continue;

                results.Add(new DetectedVpn(provider.Keyword, match.ServiceName, ReadKind(match)));
            }
            return results;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"VpnDetector.DetectAll: {ex.Message}");
            return [];
        }
        finally
        {
            if (services is not null)
                foreach (var s in services) s.Dispose();
        }
    }

    // Reading ServiceName/DisplayName can throw if the service was removed between the enumeration and
    // this read - the same race ReadKind tolerates below. Contained per-service on purpose: this runs
    // inside a Find over every service on the machine, so letting it escape would abandon the whole
    // detection and report "no provider found" because one unrelated service happened to disappear.
    private static bool SafeServiceMatches(ServiceController service, string searchTerm)
    {
        try
        {
            return ServiceLookup.ServiceMatches(service, searchTerm);
        }
        catch (InvalidOperationException ex)
        {
            LogManager.Instance.LogDebug($"VpnDetector.SafeServiceMatches: {ex.Message}");
            return false;
        }
    }

    // Reading Status can throw if the service was removed between enumeration and this read. That is
    // still a real detection - the service existed a moment ago - so report it as installed rather
    // than dropping the provider from the results.
    private static DetectionKind ReadKind(ServiceController service)
    {
        try
        {
            return service.Status == ServiceControllerStatus.Running
                ? DetectionKind.Running
                : DetectionKind.Installed;
        }
        catch (InvalidOperationException ex)
        {
            LogManager.Instance.LogDebug($"VpnDetector.ReadKind: {service.ServiceName} - {ex.Message}");
            return DetectionKind.Installed;
        }
    }
}

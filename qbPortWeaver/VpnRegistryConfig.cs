namespace qbPortWeaver
{
    /// <summary>
    /// Holds the registry-driven configuration for a VPN provider's service, process, and adapter
    /// names, plus a cached lookup for the client executable path. Each VPN manager owns one
    /// instance configured with its provider-specific registry keys.
    /// </summary>
    internal sealed class VpnRegistryConfig
    {
        private readonly string _serviceSearchTermKey;
        private readonly string _clientProcessNameKey;
        private readonly string _adapterNameKey;
        private readonly string _logPrefix;

        // Cached path; null = not found, string.Empty = not yet resolved.
        // Install paths never change at runtime so we resolve once and reuse.
        // volatile: the field is written at most once (Empty -> resolved path). Without it a reading
        // thread on a different core could observe a stale Empty and trigger one redundant re-resolve.
        // Matches the pattern used by PiaVpnManager._piactlPathCache.
        private volatile string? _clientExePathCache = string.Empty;

        public VpnRegistryConfig(
            string serviceSearchTermKey,
            string clientProcessNameKey,
            string adapterNameKey,
            string logPrefix)
        {
            _serviceSearchTermKey = serviceSearchTermKey;
            _clientProcessNameKey = clientProcessNameKey;
            _adapterNameKey       = adapterNameKey;
            _logPrefix            = logPrefix;
        }

        internal string GetServiceSearchTerm() => RegistrySettingsManager.GetAppValue(_serviceSearchTermKey);
        internal string GetClientProcessName() => RegistrySettingsManager.GetAppValue(_clientProcessNameKey);
        internal string GetAdapterName()       => RegistrySettingsManager.GetAppValue(_adapterNameKey);

        // Case-insensitive bidirectional substring match against the registry-configured adapter name.
        // Bidirectional handles the case where the configured name and the actual Windows adapter name
        // differ in length (e.g. registry "ProtonVPN TUN" vs adapter "ProtonVPN", or the reverse).
        // Mirrors NatPmpManager.IsAdapterMatch so all three VPN managers behave identically.
        // Empty-string guards on both sides: Contains("") returns true for any input, which would
        // falsely match if either the registry value or the interface name was empty.
        internal bool MatchesAdapterName(string interfaceName)
        {
            string adapterName = GetAdapterName();
            if (string.IsNullOrEmpty(adapterName) || string.IsNullOrEmpty(interfaceName)) return false;
            return interfaceName.Contains(adapterName, StringComparison.OrdinalIgnoreCase) ||
                   adapterName.Contains(interfaceName, StringComparison.OrdinalIgnoreCase);
        }

        // Live SCM enumeration; call site caches the result where repeated lookups matter.
        internal string? FindServiceName()  => AppConstants.FindServiceName(GetServiceSearchTerm());
        // Resolved once from the service's ImagePath registry entry; cached via _clientExePathCache sentinel.
        internal string? GetClientExePath()
        {
            // Read/write the volatile field via a local so the ref pass does not strip volatile semantics (CS0420).
            string? cache = _clientExePathCache;
            var result = AppConstants.FindExeInServiceDirectory(ref cache, GetClientProcessName() + ".exe", FindServiceName, _logPrefix);
            _clientExePathCache = cache;
            return result;
        }
    }
}

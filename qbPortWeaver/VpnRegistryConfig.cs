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
        private string? _clientExePathCache = string.Empty;

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

        // Live SCM enumeration; call site caches the result where repeated lookups matter.
        internal string? FindServiceName()  => AppConstants.FindServiceName(GetServiceSearchTerm());
        // Resolved once from the service's ImagePath registry entry; cached via _clientExePathCache sentinel.
        internal string? GetClientExePath() => AppConstants.FindExeInServiceDirectory(
            ref _clientExePathCache, GetClientProcessName() + ".exe", FindServiceName, _logPrefix);
    }
}

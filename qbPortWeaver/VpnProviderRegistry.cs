namespace qbPortWeaver;

/// <summary>Pairs a VPN provider keyword (one of <see cref="RegistrySettingsManager.VpnProviderProtonVpn"/> etc.) with the shared <see cref="VpnRegistryConfig"/> used to resolve its service name, client process name, and exe path.</summary>
internal sealed record VpnProvider(string Keyword, VpnRegistryConfig Config);

/// <summary>
/// Single source of truth for the VPN providers qbPortWeaver knows how to drive in auto-recovery.
/// Adding a 4th provider: add one entry to <see cref="KnownProviders"/> here. Both
/// <see cref="NatPmpManager.FindProviderToken"/> and <see cref="AutoRecoveryManager"/>
/// derive their behaviour from this list, so no other code needs to change.
/// </summary>
internal static class VpnProviderRegistry
{
    internal static readonly IReadOnlyList<VpnProvider> KnownProviders =
    [
        new(RegistrySettingsManager.VpnProviderProtonVpn, ProtonVpnManager.Config),
        new(RegistrySettingsManager.VpnProviderPia,       PiaVpnManager.Config),
    ];

    /// <summary>
    /// Returns the matching provider keyword if <paramref name="adapterName"/> matches a known
    /// provider's configured adapter name(s), or <see langword="null"/> otherwise. Matches against the
    /// provider's registry-driven adapter name(s) via <see cref="VpnRegistryConfig.MatchesAdapterName"/> -
    /// for ProtonVPN that includes both its primary name and its in-house "ProTUN" name - rather than
    /// the bare keyword, so a ProTUN adapter is still recognised as ProtonVPN. Recovery uses this to
    /// decide whether to restart the provider's service (match) or cycle the adapter (no match).
    /// </summary>
    internal static string? FindProviderToken(string adapterName) =>
        KnownProviders.FirstOrDefault(p => p.Config.MatchesAdapterName(adapterName))?.Keyword;

    /// <summary>Returns the provider entry whose <see cref="VpnProvider.Keyword"/> equals <paramref name="keyword"/> (case-insensitive), or <see langword="null"/> if no provider matches.</summary>
    internal static VpnProvider? FindByKeyword(string keyword) =>
        KnownProviders.FirstOrDefault(p => p.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="provider"/> is one of the three VPN providers
    /// selectable in Settings (ProtonVPN, PIA, NAT-PMP). Broader than <see cref="KnownProviders"/>,
    /// which only lists providers with a dedicated service-restart recovery config - NAT-PMP recovers
    /// via generic adapter cycling instead, so it is valid here but absent from <see cref="KnownProviders"/>.
    /// </summary>
    internal static bool IsRecognizedProvider(string? provider) =>
        provider is not null && (
            provider.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase) ||
            provider.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase) ||
            provider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase));
}

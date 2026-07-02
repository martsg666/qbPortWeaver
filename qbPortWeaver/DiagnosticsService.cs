using System.ServiceProcess;

namespace qbPortWeaver;

/// <summary>Outcome of a single diagnostic check.</summary>
public enum DiagnosticStatus
{
    /// <summary>The check succeeded.</summary>
    Pass,
    /// <summary>A recoverable anomaly or degraded-but-working condition.</summary>
    Warn,
    /// <summary>The check failed and needs attention.</summary>
    Fail,
    /// <summary>The check was not applicable this run (e.g. a prerequisite check failed).</summary>
    Skip,
}

/// <summary>One row in the diagnostics report: the check name, its outcome, a detail line, and an optional fix hint.</summary>
public sealed record DiagnosticResult(string Check, DiagnosticStatus Status, string Detail, string? Hint = null);

/// <summary>
/// Runs a read-only health check across the whole port-sync chain (configuration, helper service,
/// VPN, client, port reachability) and returns a per-step report. Safe to run at any time - it builds
/// fresh managers/clients from the saved settings and never changes the port, restarts anything, or
/// mutates sync-loop state. Every underlying probe has its own timeout; the caller bounds the whole run.
/// </summary>
public static class DiagnosticsService
{
    // Check names - each is reused across its check's pass/warn/fail/skip branches; named constants
    // keep the report labels consistent and satisfy Sonar S1192 (no repeated string literals).
    private static class Checks
    {
        public const string VpnProvider = "VPN provider";
        public const string Client = "BitTorrent client";
        public const string HelperService = "Helper service";
        public const string VpnConnection = "VPN connection";
        public const string ForwardedPort = "Forwarded port";
        public const string ClientRunning = "Client running";
        public const string ClientReachable = "Client reachable";
        public const string PortsInSync = "Ports in sync";
        public const string InterfaceBinding = "Interface binding";
        public const string PortReachable = "Port reachable";
    }

    /// <summary>Runs all diagnostic checks in sync-chain order. Throws <see cref="OperationCanceledException"/> only if <paramref name="cancellationToken"/> fires.</summary>
    public static async Task<IReadOnlyList<DiagnosticResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        LogManager.Instance.LogMessage("Diagnostics started", LogLevel.Info);
        var results = new List<DiagnosticResult>();

        string provider = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider);
        string clientSetting = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyBitTorrentClient);
        bool disabled = provider.Equals(RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);

        AddConfigurationResults(results, provider, clientSetting, disabled);
        AddHelperServiceResult(results);
        var (vpn, vpnPort) = await AddVpnResultsAsync(results, disabled, provider, cancellationToken).ConfigureAwait(false);
        await AddClientResultsAsync(results, vpn, vpnPort, cancellationToken).ConfigureAwait(false);

        int pass = results.Count(r => r.Status == DiagnosticStatus.Pass);
        int warn = results.Count(r => r.Status == DiagnosticStatus.Warn);
        int fail = results.Count(r => r.Status == DiagnosticStatus.Fail);
        LogManager.Instance.LogMessage($"Diagnostics completed: {pass} passed, {warn} warning(s), {fail} failed", LogLevel.Info);
        return results;
    }

    // Configuration: VPN provider selected/recognized, and the active client has a URL.
    private static void AddConfigurationResults(List<DiagnosticResult> results, string provider, string clientSetting, bool disabled)
    {
        if (disabled)
            results.Add(new(Checks.VpnProvider, DiagnosticStatus.Warn, "Port sync is disabled",
                "Select a VPN provider in Settings > General to enable port syncing."));
        else if (IsRecognizedProvider(provider))
            results.Add(new(Checks.VpnProvider, DiagnosticStatus.Pass, provider));
        else
            results.Add(new(Checks.VpnProvider, DiagnosticStatus.Fail, $"'{provider}' is not a recognized provider",
                "Reselect the VPN provider in Settings > General."));

        var (section, urlKey, name) = ResolveClient(clientSetting);
        string url = RegistrySettingsManager.GetValue(section, urlKey);
        if (string.IsNullOrWhiteSpace(url))
            results.Add(new(Checks.Client, DiagnosticStatus.Warn, $"{name} selected, but no URL is configured",
                $"Enter the {name} Web UI/RPC URL in Settings."));
        else
            results.Add(new(Checks.Client, DiagnosticStatus.Pass, $"{name} ({url})"));
    }

    private static bool IsRecognizedProvider(string provider) =>
        provider.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase) ||
        provider.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase) ||
        provider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase);

    // Maps the client setting to its registry section, URL key, and display name.
    private static (string Section, string UrlKey, string Name) ResolveClient(string clientSetting)
    {
        if (clientSetting.Equals(RegistrySettingsManager.BitTorrentClientTransmission, StringComparison.OrdinalIgnoreCase))
            return (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUrl, "Transmission");
        if (clientSetting.Equals(RegistrySettingsManager.BitTorrentClientDeluge, StringComparison.OrdinalIgnoreCase))
            return (RegistrySettingsManager.SectionDeluge, RegistrySettingsManager.KeyDelugeUrl, "Deluge");
        return (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl, "qBittorrent");
    }

    // Helper Windows service: needed only for auto-recovery, so a missing/stopped service is a Warn, not a Fail.
    private static void AddHelperServiceResult(List<DiagnosticResult> results)
    {
        try
        {
            using var sc = new ServiceController(HelperProtocol.PipeName);
            ServiceControllerStatus status = sc.Status; // throws InvalidOperationException when the service is not installed
            if (status == ServiceControllerStatus.Running)
                results.Add(new(Checks.HelperService, DiagnosticStatus.Pass, "Installed and running"));
            else
                results.Add(new(Checks.HelperService, DiagnosticStatus.Warn, $"Installed but {status}",
                    "Auto-recovery needs the helper service running."));
        }
        catch (InvalidOperationException)
        {
            results.Add(new(Checks.HelperService, DiagnosticStatus.Warn, "Not installed",
                "Auto-recovery (VPN service restart or adapter cycle) is unavailable. Reinstall qbPortWeaver to add the helper service."));
        }
        catch (Exception ex)
        {
            results.Add(new(Checks.HelperService, DiagnosticStatus.Warn, $"Could not query the helper service: {ex.Message}"));
        }
    }

    // VPN connection + forwarded port. Returns the manager (for the later interface check) and the port (for the in-sync check).
    private static async Task<(IVpnManager? Vpn, int? Port)> AddVpnResultsAsync(List<DiagnosticResult> results, bool disabled, string provider, CancellationToken cancellationToken)
    {
        if (disabled)
        {
            results.Add(new(Checks.VpnConnection, DiagnosticStatus.Skip, "Port sync is disabled"));
            results.Add(new(Checks.ForwardedPort, DiagnosticStatus.Skip, "Port sync is disabled"));
            return (null, null);
        }

        IVpnManager? vpn = await PortSyncService.BuildActiveVpnManagerAsync(cancellationToken).ConfigureAwait(false);
        if (vpn is null)
        {
            bool natPmp = provider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase);
            string hint = natPmp
                ? "Select a NAT-PMP adapter in Settings, and ensure it is up and its gateway responds to NAT-PMP."
                : "Reselect the VPN provider in Settings.";
            results.Add(new(Checks.VpnConnection, DiagnosticStatus.Fail, $"Could not initialize {provider}", hint));
            results.Add(new(Checks.ForwardedPort, DiagnosticStatus.Skip, "VPN unavailable"));
            return (null, null);
        }

        if (!vpn.IsVpnConnected())
        {
            results.Add(new(Checks.VpnConnection, DiagnosticStatus.Fail, $"{vpn.ProviderName} is not connected",
                "Connect your VPN and enable port forwarding on a P2P server."));
            results.Add(new(Checks.ForwardedPort, DiagnosticStatus.Skip, "VPN not connected"));
            return (vpn, null);
        }

        results.Add(new(Checks.VpnConnection, DiagnosticStatus.Pass, $"{vpn.ProviderName} is connected"));

        int? port = await vpn.GetVpnPortAsync(cancellationToken).ConfigureAwait(false);
        if (port is int p)
            results.Add(new(Checks.ForwardedPort, DiagnosticStatus.Pass, $"Port {p}"));
        else
            results.Add(new(Checks.ForwardedPort, DiagnosticStatus.Fail, $"No forwarded port from {vpn.ProviderName}",
                "Ensure port forwarding is enabled on a P2P server."));
        return (vpn, port);
    }

    // Client running, reachable, in sync, correctly bound, and reachable from outside.
    private static async Task AddClientResultsAsync(List<DiagnosticResult> results, IVpnManager? vpn, int? vpnPort, CancellationToken cancellationToken)
    {
        using IBitTorrentClient client = PortSyncService.BuildActiveClient();

        if (client.IsRunning())
            results.Add(new(Checks.ClientRunning, DiagnosticStatus.Pass, $"{client.ClientName} is running"));
        else
            results.Add(new(Checks.ClientRunning, DiagnosticStatus.Warn, $"{client.ClientName} process not detected",
                "Start your client, or enable Force start in Settings. Service/remote setups may still be reachable below."));

        var (clientPort, interfaceName) = await client.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (clientPort is not int cp)
        {
            results.Add(new(Checks.ClientReachable, DiagnosticStatus.Fail, $"Could not reach {client.ClientName}",
                "Check the URL and credentials in Settings and that the Web UI/RPC is enabled. See the log for details."));
            results.Add(new(Checks.PortsInSync, DiagnosticStatus.Skip, "Client unreachable"));
            if (client.SupportsInterfaceMismatchWarning)
                results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Skip, "Client unreachable"));
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Skip, "Client unreachable"));
            return;
        }
        results.Add(new(Checks.ClientReachable, DiagnosticStatus.Pass, $"{client.ClientName} reachable, listening on port {cp}"));

        AddInSyncResult(results, cp, vpnPort);
        if (client.SupportsInterfaceMismatchWarning)
            AddInterfaceResult(results, vpn, interfaceName);
        await AddPortReachableResultAsync(results, client, vpnPort, cancellationToken).ConfigureAwait(false);
    }

    private static void AddInSyncResult(List<DiagnosticResult> results, int clientPort, int? vpnPort)
    {
        if (vpnPort is not int vp)
        {
            results.Add(new(Checks.PortsInSync, DiagnosticStatus.Skip, "No forwarded port to compare against"));
            return;
        }
        if (clientPort == vp)
            results.Add(new(Checks.PortsInSync, DiagnosticStatus.Pass, $"Client matches the forwarded port ({vp})"));
        else
            results.Add(new(Checks.PortsInSync, DiagnosticStatus.Warn, $"Client port {clientPort} does not match forwarded port {vp}",
                "Use Sync Now to align them immediately; the next cycle would do it automatically."));
    }

    private static void AddInterfaceResult(List<DiagnosticResult> results, IVpnManager? vpn, string? interfaceName)
    {
        if (vpn is null)
        {
            results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Skip, "VPN unavailable"));
            return;
        }
        if (interfaceName is null)
        {
            results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Skip, "Client did not report a bound interface"));
            return;
        }
        if (interfaceName.Length == 0)
            results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Warn, "Client is bound to all interfaces - traffic may leak outside the VPN",
                "Bind the client's network interface to your VPN adapter in its settings."));
        else if (vpn.IsAdapterMatch(interfaceName))
            results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Pass, $"Bound to '{interfaceName}'"));
        else
            results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Warn, $"Bound to '{interfaceName}', which is not a {vpn.ProviderName} adapter",
                "Rebind the client's network interface to your active VPN adapter."));
    }

    // Only meaningful when the VPN is connected with a forwarded port - mirrors the sync loop, which
    // skips verification while disconnected (a closed result would be expected noise on the default port).
    private static async Task AddPortReachableResultAsync(List<DiagnosticResult> results, IBitTorrentClient client, int? vpnPort, CancellationToken cancellationToken)
    {
        if (vpnPort is null)
        {
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Skip, "VPN not connected or no forwarded port"));
            return;
        }
        bool? open = await client.TestListeningPortAsync(cancellationToken).ConfigureAwait(false);
        if (open == true)
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Pass, "Listening port is reachable from the Internet"));
        else if (open == false)
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Warn, "Listening port appears closed from the Internet",
                "Allow a moment after a port change. qBittorrent infers this from incoming connections, so an idle client may report closed."));
        else
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Skip, "Could not determine (client, internet, or port-check service unavailable)"));
    }
}

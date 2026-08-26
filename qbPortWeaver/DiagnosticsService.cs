using System.Diagnostics;
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
        public const string Client = "Client";
        public const string ClientPlugin = "Client plugin";
        public const string HelperService = "Helper service";
        public const string VpnConnection = "VPN connection";
        public const string ForwardedPort = "Forwarded port";
        public const string ClientRunning = "Client running";
        public const string ClientReachable = "Client reachable";
        public const string PortsInSync = "Ports in sync";
        public const string InterfaceBinding = "Interface binding";
        public const string ClientSettings = "Client settings";
        public const string PortReachable = "Port reachable";
    }

    // Shared skip reason for every check downstream of the client being reachable. Named for the same
    // reason the check labels above are: one wording for one condition, so the report cannot end up
    // explaining the same skip four different ways (and Sonar S1192 counts the repetition).
    private const string ClientUnreachableSkip = "Client unreachable";

    /// <summary>Runs all diagnostic checks in sync-chain order. Throws <see cref="OperationCanceledException"/> only if <paramref name="cancellationToken"/> fires.</summary>
    public static async Task<IReadOnlyList<DiagnosticResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        LogManager.Instance.LogMessage("Diagnostics started", LogLevel.Info);
        var results = new List<DiagnosticResult>();

        string provider = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyVpnProvider);
        string clientSetting = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyClient);
        bool disabled = provider.Equals(RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);

        AddConfigurationResults(results, provider, clientSetting, disabled);
        AddHelperServiceResult(results);
        var (vpn, vpnPort) = await AddVpnResultsAsync(results, disabled, provider, cancellationToken).ConfigureAwait(false);
        await AddClientResultsAsync(results, vpn, vpnPort, cancellationToken).ConfigureAwait(false);

        // Per-check detail at Debug so the full report is in the log file when debug mode is on,
        // without raising the Warn/Error tray badge (the user is already viewing the results).
        foreach (var r in results)
            LogManager.Instance.LogDebug($"DiagnosticsService.RunAsync: [{r.Status}] {r.Check} - {r.Detail}");

        int pass = results.Count(r => r.Status == DiagnosticStatus.Pass);
        int warn = results.Count(r => r.Status == DiagnosticStatus.Warn);
        int fail = results.Count(r => r.Status == DiagnosticStatus.Fail);
        LogManager.Instance.LogMessage($"Diagnostics completed: {pass} passed, {TextFormat.Pluralize(warn, "warning")}, {fail} failed", LogLevel.Info);
        return results;
    }

    // Configuration: VPN provider selected/recognized, and the active client has a URL.
    private static void AddConfigurationResults(List<DiagnosticResult> results, string provider, string clientSetting, bool disabled)
    {
        if (disabled)
            results.Add(new(Checks.VpnProvider, DiagnosticStatus.Warn, "Port sync is disabled",
                "Select a VPN provider in Settings → General to enable port syncing."));
        else if (VpnProviderRegistry.IsRecognizedProvider(provider))
            results.Add(new(Checks.VpnProvider, DiagnosticStatus.Pass, provider));
        else
            results.Add(new(Checks.VpnProvider, DiagnosticStatus.Fail, $"'{provider}' is not a recognized provider",
                "Reselect the VPN provider in Settings → General."));

        var client = ClientRegistry.Resolve(clientSetting);
        string url = RegistrySettingsManager.GetValue(client.Section, client.UrlKey);
        if (string.IsNullOrWhiteSpace(url))
            results.Add(new(Checks.Client, DiagnosticStatus.Warn, $"{client.Name} selected, but no URL is configured",
                $"Enter the {client.Name} Web UI/RPC URL in Settings."));
        else
            results.Add(new(Checks.Client, DiagnosticStatus.Pass, $"{client.Name} ({url})"));
    }

    // Helper Windows service: needed only for auto-recovery, so a missing/stopped service is a Warn, not a Fail.
    private static void AddHelperServiceResult(List<DiagnosticResult> results)
    {
        try
        {
            using var sc = new ServiceController(HelperProtocol.ServiceName);
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

        // Same usability rule the sync loop applies, so the report cannot pass a port the loop
        // would ignore. An unusable value is discarded here too, leaving the later in-sync check
        // to skip rather than compare the client against nonsense.
        if (port is int reported && !AppConstants.IsUsablePort(reported))
        {
            results.Add(new(Checks.ForwardedPort, DiagnosticStatus.Fail,
                $"{vpn.ProviderName} reported an unusable port ({reported})",
                "The VPN is connected but has not assigned a usable forwarded port. Re-check that port forwarding is enabled on a P2P server."));
            return (vpn, null);
        }

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
        using IManagedClient client = PortSyncService.BuildActiveClient();

        if (client.IsRunning())
            results.Add(new(Checks.ClientRunning, DiagnosticStatus.Pass, $"{client.ClientName} is running"));
        else
            results.Add(new(Checks.ClientRunning, DiagnosticStatus.Warn, $"{client.ClientName} process not detected",
                "Start your client, or enable Force-start in Settings. Service/remote setups may still be reachable below."));

        // Nicotine+ is only reachable through the bridge plugin, so its state is the first thing
        // worth knowing - "not installed", "not enabled", and "Nicotine+ never started" all look
        // identical from the reachability check alone but need different fixes.
        if (client is NicotineClient) AddNicotinePluginResult(results);

        var (clientPort, interfaceName) = await client.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (clientPort is not int cp)
        {
            results.Add(new(Checks.ClientReachable, DiagnosticStatus.Fail, $"Could not reach {client.ClientName}",
                "Check the URL and credentials in Settings and that the Web UI/RPC is enabled. See the log for details."));
            results.Add(new(Checks.PortsInSync, DiagnosticStatus.Skip, ClientUnreachableSkip));
            if (client.SupportsInterfaceMismatchWarning)
                results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Skip, ClientUnreachableSkip));
            results.Add(new(Checks.ClientSettings, DiagnosticStatus.Skip, ClientUnreachableSkip));
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Skip, ClientUnreachableSkip));
            return;
        }
        // A client that reports port 0 is reachable but not listening anywhere useful, and "listening
        // port is 0" reads like a fault in qbPortWeaver rather than a setting in the client. qBittorrent
        // reports exactly this while "use a different port on each startup" is on (verified against a
        // live client), which the Client settings check below then names - so point the reader at it
        // instead of leaving them with a bare zero.
        results.Add(cp == 0
            ? new(Checks.ClientReachable, DiagnosticStatus.Warn,
                $"{client.ClientName} is reachable but reports no listening port",
                "The client is not listening on a port it can report. See the Client settings check below - a randomised listening port causes this.")
            : new DiagnosticResult(Checks.ClientReachable, DiagnosticStatus.Pass, $"{client.ClientName} reachable; listening port is {cp}"));

        AddInSyncResult(results, cp, vpnPort);
        if (client.SupportsInterfaceMismatchWarning)
            await AddInterfaceResultAsync(results, client, vpn, interfaceName, cancellationToken).ConfigureAwait(false);
        await AddClientSettingsResultAsync(results, client, cancellationToken).ConfigureAwait(false);
        await AddPortReachableResultAsync(results, client, vpnPort, cancellationToken).ConfigureAwait(false);
    }

    // Reports the client's own settings that undo the synchronized port. qbPortWeaver writes these to
    // a safe value every time it sets the port, so a conflict here means the user changed it since -
    // the one failure mode where every other check passes and the port is still wrong.
    private static async Task AddClientSettingsResultAsync(List<DiagnosticResult> results, IManagedClient client, CancellationToken cancellationToken)
    {
        var conflicts = await client.GetConflictingSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (conflicts is null)
        {
            // Null is "could not read", which is not the same as "nothing wrong". Reporting Pass here
            // would show a green tick for a check that never ran - and this check exists for exactly
            // the clients where nothing else can see the problem, so a false green is worse than none.
            results.Add(new(Checks.ClientSettings, DiagnosticStatus.Skip, $"Could not read {client.ClientName}'s settings",
                "The client answered the first request but not this one. See the log for details."));
            return;
        }

        if (conflicts.Count == 0)
        {
            results.Add(new(Checks.ClientSettings, DiagnosticStatus.Pass, $"No {client.ClientName} setting is working against the forwarded port"));
            return;
        }

        string names = string.Join(", ", conflicts.Select(c => $"\"{c.SettingName}\""));
        string pronoun = conflicts.Count == 1 ? "it" : "them";
        string hint = $"Turn {pronoun} off in {client.ClientName}'s settings. " +
                      $"{AppIdentity.AppName} switches {pronoun} off each time it sets the port, so this will also clear itself at the next port change.";
        results.Add(new(Checks.ClientSettings, DiagnosticStatus.Warn,
            $"{client.ClientName} has {TextFormat.Pluralize(conflicts.Count, "setting")} working against the forwarded port: {names}", hint));
    }

    // Reports the bridge plugin's state from files alone, so it stays useful precisely when the
    // plugin is unreachable and every other client check has nothing to say.
    private static void AddNicotinePluginResult(List<DiagnosticResult> results)
    {
        string exePath = RegistrySettingsManager.GetValue(
            RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineExePath);
        var status = NicotinePluginInstaller.GetStatus(exePath);

        DiagnosticResult result = status.State switch
        {
            NicotinePluginState.DataFolderMissing => new(Checks.ClientPlugin, DiagnosticStatus.Warn,
                "Nicotine+'s data folder was not found",
                "Start Nicotine+ once, or set the Executable path in Settings for a portable installation."),

            NicotinePluginState.NotInstalled => new(Checks.ClientPlugin, DiagnosticStatus.Fail,
                "The qbPortWeaver bridge plugin is not installed",
                "Nicotine+ has no remote control of its own. Click Install Plugin in Settings, under the Nicotine+ section."),

            // Reuses the summary GetStatus already built rather than rebuilding the sentence from
            // version numbers. Staleness is decided by comparing the installed files against the
            // bundled ones, so the two versions can be identical - which is the whole point of that
            // check, and which made the version-based wording contradict itself ("Bridge plugin 2.6.7
            // is installed; this build ships 2.6.7"). One source for the phrasing also means a later
            // change to it cannot leave this copy behind, which is how that drift happened.
            NicotinePluginState.Outdated => new(Checks.ClientPlugin, DiagnosticStatus.Warn,
                $"The installed bridge plugin differs from the one this build ships ({status.Summary})",
                "Click Update Plugin in Settings, then restart Nicotine+."),

            NicotinePluginState.NotEnabled => new(Checks.ClientPlugin, DiagnosticStatus.Fail,
                "The bridge plugin is installed but not enabled",
                "In Nicotine+, open Preferences → Plugins and tick \"qbPortWeaver Bridge\"."),

            NicotinePluginState.NotRunning => new(Checks.ClientPlugin, DiagnosticStatus.Warn,
                "The bridge plugin is enabled but has not published its connection details",
                "Start Nicotine+. If it is already running, check its log for a qbPortWeaver Bridge error."),

            _ => BuildReadyPluginResult(status)
        };

        results.Add(result);
    }

    // Ready, but the saved connection settings may still point somewhere else - which works (the
    // client re-reads the file) yet costs a failed request every cycle, so it is worth flagging.
    private static DiagnosticResult BuildReadyPluginResult(NicotinePluginStatus status)
    {
        string savedUrl = RegistrySettingsManager.GetValue(
            RegistrySettingsManager.SectionNicotine, RegistrySettingsManager.KeyNicotineUrl);
        string savedToken = RegistrySettingsManager.GetNicotineToken();

        if (status.Handshake is { } handshake &&
            (!string.Equals(savedUrl.TrimEnd('/'), handshake.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(savedToken, handshake.Token, StringComparison.Ordinal)))
        {
            return new(Checks.ClientPlugin, DiagnosticStatus.Warn,
                $"The bridge plugin is on {handshake.Url}, which differs from the saved settings",
                "Open Settings, click the refresh button next to the Plugin token, then save.");
        }

        return new(Checks.ClientPlugin, DiagnosticStatus.Pass,
            $"Bridge plugin {status.InstalledVersion} is installed and active on {status.Handshake?.Url}");
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

    private static async Task AddInterfaceResultAsync(List<DiagnosticResult> results, IManagedClient client,
        IVpnManager? vpn, string? interfaceName, CancellationToken cancellationToken)
    {
        // Checked before anything below, and independently of the VPN: a stale interface token is
        // precisely the case where the name - which is all the other branches compare - still reads
        // correctly while the client listens on nothing. Reporting "Bound to 'X'" as a pass here is
        // what let this go unnoticed, so it outranks every name-based verdict.
        if (client is QBittorrentClient qbClient)
        {
            var (stale, expectedToken) = await qbClient.CheckInterfaceBindingAsync(interfaceName, cancellationToken).ConfigureAwait(false);
            if (stale && expectedToken is not null)
            {
                results.Add(new(Checks.InterfaceBinding, DiagnosticStatus.Fail,
                    $"Bound to '{interfaceName}' by a stale identifier - {client.ClientName} is not listening on that adapter",
                    $"Re-select the network interface in {client.ClientName}, or turn on \"Fix the network interface binding when it goes stale\" " +
                    "in Settings. Restarting the client does not clear it, because the value is stored in its configuration."));
                return;
            }
        }

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
    private static async Task AddPortReachableResultAsync(List<DiagnosticResult> results, IManagedClient client, int? vpnPort, CancellationToken cancellationToken)
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
        {
            // qBittorrent deduces reachability from incoming connections (so an idle client reads
            // closed); the others actively probe through their projects' online check services -
            // Nicotine+ via the bridge plugin, which reports undetermined rather than false when
            // the check cannot complete, so it never reaches this branch on a slow result.
            string hint = client is QBittorrentClient
                ? "Allow a moment after a port change. qBittorrent infers this from incoming connections, so an idle client may report closed."
                : "Allow a moment after a port change, then re-run. A persistently closed port usually means port forwarding is not active on the VPN.";
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Warn, "Listening port appears closed from the Internet", hint));
        }
        else
            results.Add(new(Checks.PortReachable, DiagnosticStatus.Skip, "Could not determine (client, Internet, or port-check service unavailable)"));
    }

    /// <summary>The running app's version (e.g. "X.Y.Z"), for the diagnostics header and report.</summary>
    internal static string AppVersion => AppConstants.AppVersion;

    /// <summary>
    /// The installed helper service's file version, or <see langword="null"/> when the service is not
    /// installed or its executable cannot be read. Read from the service's on-disk ImagePath so it
    /// reflects the actually-installed helper (surfacing a version mismatch after a partial upgrade).
    /// </summary>
    internal static string? GetHelperServiceVersion()
    {
        try
        {
            string? exe = ServiceLookup.GetServiceExePath(HelperProtocol.ServiceName);
            if (exe is null || !File.Exists(exe)) return null;
            string? raw = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            // Normalise to Major.Minor.Build so it matches the app version's format ("2.6.0", not "2.6.0.0").
            return Version.TryParse(raw, out var v) ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : raw;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"DiagnosticsService.GetHelperServiceVersion: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the port-sync-relevant registry settings (general, the active client, and extra),
    /// grouped by section, for the diagnostics report. Sensitive values are masked; sections with no
    /// stored values are omitted.
    /// </summary>
    internal static IReadOnlyList<(string Section, IReadOnlyList<(string Key, string Value)> Values)> GetSettingsSnapshot()
    {
        string clientSetting = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyClient);
        string activeClientSection = ClientRegistry.Resolve(clientSetting).Section;

        string[] sections = [RegistrySettingsManager.SectionGeneral, activeClientSection, RegistrySettingsManager.SectionExtra];
        var snapshot = new List<(string Section, IReadOnlyList<(string Key, string Value)> Values)>();
        foreach (var section in sections)
        {
            var values = RegistrySettingsManager.GetSectionSnapshot(section);
            if (values.Count > 0)
                snapshot.Add((section, values));
        }
        return snapshot;
    }
}

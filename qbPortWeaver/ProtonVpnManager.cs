using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace qbPortWeaver;

/// <summary>Detects ProtonVPN connectivity via network adapter enumeration and reads the forwarded port from the client log file.</summary>
public sealed partial class ProtonVpnManager : IVpnManager
{
    private const int LogReadChunkSize = 4096;

    internal static readonly VpnRegistryConfig Config = new(
        RegistrySettingsManager.KeyProtonVpnServiceSearchTerm,
        RegistrySettingsManager.KeyProtonVpnClientProcessName,
        RegistrySettingsManager.KeyProtonVpnAdapterName,
        "ProtonVpnManager.GetClientExePath",
        nativeAdapterNameKey: RegistrySettingsManager.KeyProtonVpnNativeAdapterName);

    private readonly string _logFilePath;

    // Log format: "Port pair X->Y" where X and Y are always identical (ProtonVPN does not
    // distinguish the two port values). Capture group 1 gives the forwarded port.
    // No nested quantifiers, so no backtracking risk - no match timeout required.
    [GeneratedRegex(@"Port pair\s+(\d+)->(?:\d+)")]
    private static partial Regex PortPairRegex();

    // The server address the client is connected to: "... connected to server with IPv4 'a.b.c.d' ...".
    // Written more than once per connection, the first time sometimes with a different address of the
    // same server, so only the newest line counts - that one has matched the NAT-PMP forward address.
    [GeneratedRegex(@"connected to server with IPv4 '(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})'")]
    private static partial Regex ServerAddressRegex();

    // The server name: "... Connected to server CA#104 ...". Case-sensitive on purpose: the address line
    // above contains "connected to server with", which must not be read as a server named "with".
    [GeneratedRegex(@"Connected to server ([A-Z]{2}(?:-[A-Z]{2})?#\d+[A-Za-z0-9-]*)")]
    private static partial Regex ServerNameRegex();

    // Read lazily, once per instance: only the exit-address check needs them (on a closed or undetermined
    // port test, on an open one while exit reconnects are spent, and in Diagnostics), whereas the port is
    // read every cycle. The server lines are written once per
    // connection, so on a long session they sit far back in the file or in the rotated one, and once both
    // files have lost them they stay null. The forward address is asked from the gateway first anyway
    // (see GetForwardAddressAsync), so the address line is only its fallback; the name line is the one
    // source of the server name in messages, which is left out when it is gone.
    private bool _serverScanned;
    private System.Net.IPAddress? _serverAddress;
    private string? _serverName;

    // The forward address GetForwardAddressAsync settled on, once per instance like the scan above.
    private bool _forwardResolved;
    private System.Net.IPAddress? _forwardAddress;

    /// <inheritdoc />
    public string ProviderName => RegistrySettingsManager.VpnProviderProtonVpn;

    /// <inheritdoc />
    // The tunnel gateway first: ProtonVPN forwards the port through NAT-PMP on it, so its external address
    // is the forward address, the same reply NAT-PMP mode reads (the newest log line has matched it). The
    // log's server line is the fallback for a gateway that does not answer; it is written once per
    // connection and rotates out of both log files within hours, so on its own the check would stop
    // running a few hours into a connection.
    public async Task<System.Net.IPAddress?> GetForwardAddressAsync(CancellationToken cancellationToken = default)
    {
        if (_forwardResolved) return _forwardAddress;
        string? adapter = ActiveAdapterName;
        System.Net.IPAddress? address = adapter is null
            ? null
            : await NatPmpManager.QueryExternalAddressAsync(adapter, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            EnsureServerScanned();
            address = _serverAddress;
        }
        _forwardAddress = address;
        _forwardResolved = true;
        return address;
    }

    /// <inheritdoc />
    public System.Net.IPAddress? TunnelAddress
    {
        get
        {
            string? adapter = ActiveAdapterName;
            return adapter is null ? null : VpnAdapter.GetIPv4Address(adapter);
        }
    }

    /// <inheritdoc />
    public bool IsProtonVpnOverOpenVpn =>
        string.Equals(ActiveAdapterName, AppConstants.ProtonVpnOpenVpnAdapterName, StringComparison.OrdinalIgnoreCase);

    // The first up adapter carrying one of ProtonVPN's names, read live: a protocol switch changes it.
    // The two configured names are read once, not per adapter.
    private static string? ActiveAdapterName
    {
        get
        {
            string legacyName = Config.GetAdapterName();
            string nativeName = Config.GetNativeAdapterName() ?? string.Empty;
            return VpnAdapter.GetActiveInterfaces().Find(nic => MatchesProtonAdapter(nic.Name, legacyName, nativeName))?.Name;
        }
    }

    /// <inheritdoc />
    public string ForwardSource
    {
        get { EnsureServerScanned(); return _serverName is null ? ProviderName : $"{ProviderName} server {_serverName}"; }
    }

    /// <summary>Creates a manager that reads the forwarded port from the ProtonVPN client log file at <paramref name="logFilePath"/>.</summary>
    public ProtonVpnManager(string logFilePath)
    {
        _logFilePath = logFilePath;
    }

    /// <inheritdoc />
    public bool IsVpnConnected()
    {
        // Uses Name (not Description). The legacy stack names its adapter "ProtonVPN" (WireGuard) or
        // "ProtonVPN TUN" (OpenVPN); the new in-house protocols (Proton WireGuard, Proton Stealth) name it
        // "ProTUN". The same lookup as TunnelAddress and IsProtonVpnOverOpenVpn, so "connected" and the
        // exit check can never disagree about which adapter is the tunnel.
        string? matched = ActiveAdapterName;
        LogManager.Instance.LogDebug(matched is not null
            ? $"ProtonVpnManager.IsVpnConnected: Adapter '{matched}' is connected"
            : $"ProtonVpnManager.IsVpnConnected: Adapter '{Config.GetAdapterName()}'/'{Config.GetNativeAdapterName()}' is not found or not connected");
        return matched is not null;
    }

    /// <inheritdoc />
    // cancellationToken only prevents scheduling if cancelled before the task starts; once GetVpnPortCore runs,
    // cancellation cannot interrupt the in-progress log file read (bounded by the file I/O itself).
    public Task<int?> GetVpnPortAsync(CancellationToken cancellationToken = default) => Task.Run(GetVpnPortCore, cancellationToken);

    /// <inheritdoc />
    public string? GetRecoveryTarget() => ProviderName;

    /// <inheritdoc />
    public string GetRecoveryAction() => HelperProtocol.ActionRestart;

    /// <inheritdoc />
    public bool IsAdapterMatch(string interfaceName) => MatchesProtonAdapter(interfaceName);

    // Matches an observed adapter name against either configured Proton adapter name: the legacy
    // "ProtonVPN" (protonVpnAdapterName) or the new in-house tunnel "ProTUN" (protonVpnNativeAdapterName).
    // Delegates to Config so the dual-name rule lives in one place (shared with recovery's
    // provider-token resolution). Callers that match in a loop should read the names once via the
    // overload below to avoid a per-adapter registry read.
    private static bool MatchesProtonAdapter(string interfaceName) => Config.MatchesAdapterName(interfaceName);

    // Matches against pre-read configured names (loop fast-path). The matching rule
    // (AdapterNamesMatch) guards against empty values, so a cleared registry key cannot cause a false
    // match. Kept in step with Config.MatchesAdapterName, which applies the same two-name rule.
    private static bool MatchesProtonAdapter(string interfaceName, string legacyName, string nativeName) =>
        VpnRegistryConfig.AdapterNamesMatch(legacyName, interfaceName) ||
        VpnRegistryConfig.AdapterNamesMatch(nativeName, interfaceName);

    private int? GetVpnPortCore()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logFilePath))
            {
                // Reachable only since AppFiles.GetProtonVpnLogFilePath started returning empty for a
                // blank value - Path.Combine used to mask it as the LocalAppData folder.
                //
                // Only a hand-edit reaches this: there is no Settings field for the path, and
                // GetAppValue falls back to the default when the value is absent or unreadable. An
                // empty string is still a string, so it is the one input that survives to here. The
                // message therefore names the registry value and the recovery, because nothing in the
                // UI can fix it - deleting the value restores the default.
                LogManager.Instance.LogDebug(
                    @"ProtonVpnManager.GetVpnPortCore: The 'protonVpnLogFilePath' value under HKCU\Software\qbPortWeaver is empty - " +
                    "delete the value to restore the default path, or set it to the ProtonVPN log file");
                return null;
            }

            if (!File.Exists(_logFilePath))
            {
                LogManager.Instance.LogDebug($"ProtonVpnManager.GetVpnPortCore: Log file does not exist: {_logFilePath}");
                return null;
            }

            LogManager.Instance.LogDebug($"ProtonVpnManager.GetVpnPortCore: Reading log file: {_logFilePath}");

            int? port = ReadLastPortFromLog();

            if (port.HasValue)
            {
                LogManager.Instance.LogDebug($"ProtonVpnManager.GetVpnPortCore: Found port {port.Value} in log file");
                return port.Value;
            }

            LogManager.Instance.LogDebug("ProtonVpnManager.GetVpnPortCore: No port found in log file");
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"ProtonVpnManager.GetVpnPortCore: {ex.Message}");
            return null;
        }
    }

    // Returns the most recent matched port. Falls back to the rotated log when the current one has no port
    // line yet: ProtonVPN writes one about every 10 seconds, but a file it has just rotated to has none
    // for a moment, and a cycle landing there would otherwise report port detection as failed.
    private int? ReadLastPortFromLog()
    {
        int? port = null;
        bool VisitPortLine(string line)
        {
            var match = PortPairRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int p)) port = p;
            return port is not null;
        }
        ScanLogBackwards(_logFilePath, VisitPortLine);
        string rotated = RotatedLogPath;
        if (port is null && File.Exists(rotated))
            ScanLogBackwards(rotated, VisitPortLine);
        return port;
    }

    // The log ProtonVPN rotated away last: client-logs.1.txt beside client-logs.txt.
    private string RotatedLogPath =>
        Path.Combine(Path.GetDirectoryName(_logFilePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(_logFilePath) + ".1" + Path.GetExtension(_logFilePath));

    // Finds the newest server address and server name lines, once per instance. Any failure leaves
    // them null: the forward address then comes from the gateway alone, and messages leave out the
    // server name.
    // Falls back to the rotated log (client-logs.1.txt) for whichever line the current one lacks:
    // ProtonVPN writes both only when it connects, and rotates its log about every 400 KB, so for much
    // of a long session they are only in the rotated file. The port line needs no such fallback - it is
    // written about every 10 seconds.
    private void EnsureServerScanned()
    {
        if (_serverScanned) return;
        _serverScanned = true;
        if (string.IsNullOrWhiteSpace(_logFilePath) || !File.Exists(_logFilePath)) return;
        try
        {
            ScanLogBackwards(_logFilePath, VisitServerLine);
            string rotated = RotatedLogPath;
            if ((_serverAddress is null || _serverName is null) && File.Exists(rotated))
                ScanLogBackwards(rotated, VisitServerLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogManager.Instance.LogDebug($"ProtonVpnManager.EnsureServerScanned: {ex.Message}");
        }
        LogManager.Instance.LogDebug($"ProtonVpnManager.EnsureServerScanned: server {_serverName ?? "(none)"} at {_serverAddress?.ToString() ?? "(none)"}");
    }

    // Takes the newest of each line not found yet; true once both are known, which ends the scan.
    private bool VisitServerLine(string line)
    {
        if (_serverAddress is null && ServerAddressRegex().Match(line) is { Success: true } address &&
            System.Net.IPAddress.TryParse(address.Groups[1].Value, out var parsed))
            _serverAddress = parsed;
        if (_serverName is null && ServerNameRegex().Match(line) is { Success: true } name)
            _serverName = name.Groups[1].Value;
        return _serverAddress is not null && _serverName is not null;
    }

    // Scans the log file from the end in chunks, passing each line newest first to visit until it
    // returns true. Opens with FileShare.ReadWrite so ProtonVPN can keep writing while we read.
    // Note: if a chunk boundary falls mid-multi-byte UTF-8 character, GetString produces a
    // replacement char on that boundary. This is harmless because the patterns searched for are pure
    // ASCII and ProtonVPN logs use ASCII-only content.

    private static void ScanLogBackwards(string path, Func<string, bool> visit)
    {
        // FileShare.Delete as well as ReadWrite. Windows requires DELETE access on an open handle before
        // another process can rename over it, so without it a log rotation by ProtonVPN *during* this
        // read fails - in ProtonVPN, where we would never see it. Same rule AppFiles.OpenShared applies
        // to our own files, and it matters more here: a sharing violation in a third party's file is one
        // we cannot observe and did not have to cause. Nothing is torn by allowing it - the scan either
        // completes on the handle it opened or fails cleanly, and the enclosing catch turns that into
        // "no port found" for one cycle, which the next cycle retries.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        long bytesRemaining = fs.Length;
        string lineFragment = string.Empty;
        byte[] buffer = new byte[LogReadChunkSize];

        while (bytesRemaining > 0)
        {
            int chunkSize = (int)Math.Min(LogReadChunkSize, bytesRemaining);
            bytesRemaining -= chunkSize;
            fs.Seek(bytesRemaining, SeekOrigin.Begin);
            fs.ReadExactly(buffer, 0, chunkSize);

            // Append the partial-line fragment carried over from the left edge of the next (earlier) chunk
            string text = Encoding.UTF8.GetString(buffer, 0, chunkSize) + lineFragment;
            string[] lines = text.Split('\n');

            // lines[0] may be a partial line whose start is in the next (earlier) chunk
            lineFragment = lines[0];

            // Process complete lines right-to-left; stop as soon as visit is satisfied
            for (int i = lines.Length - 1; i >= 1; i--)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length > 0 && visit(line)) return;
            }
        }

        // The very first line of the file
        if (lineFragment.Length > 0)
            visit(lineFragment.TrimEnd('\r'));
    }
}

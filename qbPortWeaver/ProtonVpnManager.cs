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

    /// <inheritdoc />
    public string ProviderName => RegistrySettingsManager.VpnProviderProtonVpn;

    /// <summary>Creates a manager that reads the forwarded port from the ProtonVPN client log file at <paramref name="logFilePath"/>.</summary>
    public ProtonVpnManager(string logFilePath)
    {
        _logFilePath = logFilePath;
    }

    /// <inheritdoc />
    public bool IsVpnConnected()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            // Uses Name (not Description). The legacy stack names its adapter "ProtonVPN" (WireGuard)
            // or "ProtonVPN TUN" (OpenVPN); the new in-house protocols (Proton WireGuard, Proton Stealth)
            // name it "ProTUN". Both configured names are read once here, then matched per adapter.
            string legacyName = Config.GetAdapterName();
            string nativeName = Config.GetNativeAdapterName() ?? string.Empty;
            NetworkInterface? matched = adapters.FirstOrDefault(adapter =>
                adapter.OperationalStatus == OperationalStatus.Up &&
                MatchesProtonAdapter(adapter.Name, legacyName, nativeName));

            LogManager.Instance.LogDebug(matched is not null
                ? $"ProtonVpnManager.IsVpnConnected: Adapter '{matched.Name}' is connected"
                : $"ProtonVpnManager.IsVpnConnected: Adapter '{legacyName}'/'{nativeName}' is not found or not connected");

            return matched is not null;
        }
        catch (Exception ex)
        {
            return LogManager.LogDebugFalse($"ProtonVpnManager.IsVpnConnected: {ex.Message}");
        }
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

    // Matches against pre-read configured names (loop fast-path). The bidirectional substring rule
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

    // Scans the log file from the end in chunks and returns the most recent matched port.
    // Opens with FileShare.ReadWrite so ProtonVPN can keep writing while we read.
    // Note: if a chunk boundary falls mid-multi-byte UTF-8 character, GetString produces a
    // replacement char on that boundary. This is harmless because the target pattern
    // "Port pair N->N" is pure ASCII and ProtonVPN logs use ASCII-only content.
    private int? ReadLastPortFromLog()
    {
        using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

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

            // Process complete lines right-to-left; stop on first match
            for (int i = lines.Length - 1; i >= 1; i--)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                var match = PortPairRegex().Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
                    return port;
            }
        }

        // Check the very first line of the file
        if (lineFragment.Length > 0)
        {
            var match = PortPairRegex().Match(lineFragment.TrimEnd('\r'));
            if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
                return port;
        }

        return null;
    }
}

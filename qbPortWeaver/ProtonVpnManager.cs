using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace qbPortWeaver
{
    /// <summary>Detects ProtonVPN connectivity via network adapter enumeration and reads the forwarded port from the client log file.</summary>
    public sealed partial class ProtonVpnManager : IVpnManager
    {
        private const int LogReadChunkSize = 4096;

        internal static string GetServiceSearchTerm() => RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyProtonVpnServiceSearchTerm);
        internal static string GetClientProcessName() => RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyProtonVpnClientProcessName);
        internal static string GetAdapterName()       => RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyProtonVpnAdapterName);

        // Cached path; null = not found, string.Empty = not yet resolved.
        // Install paths never change at runtime so we resolve once and reuse.
        private static string? _clientExePathCache = string.Empty;

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
                // Uses Name (not Description) - ProtonVPN's adapter Name contains the configured
                // adapter name substring on all installations: "ProtonVPN" (WireGuard) or "ProtonVPN TUN" (OpenVPN).
                bool isConnected = adapters.Any(adapter =>
                    adapter.Name.Contains(GetAdapterName(), StringComparison.OrdinalIgnoreCase) &&
                    adapter.OperationalStatus == OperationalStatus.Up);

                LogManager.Instance.LogDebug(isConnected
                    ? "ProtonVpnManager.IsVpnConnected: ProtonVPN adapter is connected"
                    : "ProtonVpnManager.IsVpnConnected: ProtonVPN adapter not found or not connected");

                return isConnected;
            }
            catch (Exception ex)
            {
                return LogManager.LogDebugFalse($"ProtonVpnManager.IsVpnConnected: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public Task<int?> GetVpnPortAsync() => Task.FromResult(GetVpnPortCore());

        /// <inheritdoc />
        public string? GetRecoveryTarget() => ProviderName;

        /// <inheritdoc />
        public string GetRecoveryAction() => HelperServiceClient.ActionRestart;

        /// <inheritdoc />
        public bool IsAdapterMatch(string interfaceName)
            => interfaceName.Contains(GetAdapterName(), StringComparison.OrdinalIgnoreCase);

        internal static string? FindServiceName() => AppConstants.FindServiceName(GetServiceSearchTerm());

        internal static string? GetClientExePath()
        {
            if (_clientExePathCache != string.Empty) return _clientExePathCache;
            try
            {
                string? serviceName = FindServiceName();
                string? serviceDir  = serviceName is not null ? AppConstants.GetServiceExeDirectory(serviceName) : null;
                if (serviceDir is null)
                {
                    LogManager.Instance.LogDebug("ProtonVpnManager.GetClientExePath: ProtonVPN service executable directory not found");
                    return _clientExePathCache = null;
                }

                string processName = GetClientProcessName();
                string exePath = Path.Combine(serviceDir, processName + ".exe");
                if (!File.Exists(exePath))
                {
                    LogManager.Instance.LogDebug($"ProtonVpnManager.GetClientExePath: {processName}.exe not found at: {exePath}");
                    return _clientExePathCache = null;
                }

                LogManager.Instance.LogDebug($"ProtonVpnManager.GetClientExePath: Found {processName}.exe at: {exePath}");
                return _clientExePathCache = exePath;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"ProtonVpnManager.GetClientExePath: {ex.Message}");
                return null; // transient error - don't cache, retry next cycle
            }
        }

        private int? GetVpnPortCore()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_logFilePath))
                {
                    LogManager.Instance.LogDebug("ProtonVpnManager.GetVpnPortCore: Logfile path is null or empty");
                    return null;
                }

                if (!File.Exists(_logFilePath))
                {
                    LogManager.Instance.LogDebug($"ProtonVpnManager.GetVpnPortCore: Logfile does not exist: {_logFilePath}");
                    return null;
                }

                LogManager.Instance.LogDebug($"ProtonVpnManager.GetVpnPortCore: Reading logfile: {_logFilePath}");

                int? port = ReadLastPortFromLog();

                if (port.HasValue)
                {
                    LogManager.Instance.LogDebug($"ProtonVpnManager.GetVpnPortCore: Found port {port.Value} in logfile");
                    return port.Value;
                }

                LogManager.Instance.LogDebug("ProtonVpnManager.GetVpnPortCore: No port found in logfile");
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
}

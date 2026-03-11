using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace qbPortWeaver
{
    // Detects ProtonVPN connectivity via network adapter enumeration and reads the forwarded port from ProtonVPN's log file
    public sealed class ProtonVpnManager : IVpnManager
    {
        private const int    LogReadChunkSize = 4096;
        internal const string VpnServiceName    = "ProtonVPN Service";
        internal const string ClientProcessName = "ProtonVPN.Client";

        private readonly string _logFilePath;
        // Log format: "Port pair X->Y" where X and Y are always identical (ProtonVPN does not
        // differentiate external from internal port). Capture group 1 gives the forwarded port.
        private static readonly Regex PortRegex = new Regex(@"Port pair\s+(\d+)->(?:\d+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        public string ProviderName => RegistrySettingsManager.VpnProviderProtonVpn;

        public ProtonVpnManager(string logFilePath)
        {
            _logFilePath = logFilePath;
        }

        public bool IsVpnConnected()
        {
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces();
                // Uses Name (not Description) — ProtonVPN's adapter Name contains "ProtonVPN" on all
                // installations: "ProtonVPN" (WireGuard) or "ProtonVPN TUN" (OpenVPN).
                bool isConnected = adapters.Any(adapter =>
                    adapter.Name.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase) &&
                    adapter.OperationalStatus == OperationalStatus.Up);

                LogManager.Instance.LogDebug(isConnected
                    ? "ProtonVpnManager.IsVpnConnected: ProtonVPN adapter is connected"
                    : "ProtonVpnManager.IsVpnConnected: ProtonVPN adapter not found or not connected");

                return isConnected;
            }
            catch (Exception ex)
            {
                return LogManager.LogDebugExceptionFalse("ProtonVpnManager.IsVpnConnected", ex);
            }
        }

        public Task<int?> GetVpnPortAsync() => Task.FromResult(GetVpnPortCore());

        public string? FindServiceName()
            => VpnManagerHelper.FindServiceByExactName(VpnServiceName, nameof(ProtonVpnManager));

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
                LogManager.LogDebugException("ProtonVpnManager.GetVpnPortCore", ex);
                return null;
            }
        }

        // Scans the log file from the end in chunks and returns the most recent matched port.
        // Opens with FileShare.ReadWrite so ProtonVPN can keep writing while we read.
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
                    var match = PortRegex.Match(line);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
                        return port;
                }
            }

            // Check the very first line of the file
            if (lineFragment.Length > 0)
            {
                var match = PortRegex.Match(lineFragment.TrimEnd('\r'));
                if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
                    return port;
            }

            return null;
        }
    }
}

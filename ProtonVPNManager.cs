using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace qbPortWeaver
{
    // Detects ProtonVPN connectivity via network adapter enumeration and reads the forwarded port from ProtonVPN's log file
    public sealed class ProtonVPNManager : IVpnManager
    {
        private const int LogReadChunkSize = 4096;

        private readonly string _logFilePath;
        // Log format: "Port pair X->Y" where X and Y are always identical (ProtonVPN does not
        // differentiate external from internal port). Capture group 1 gives the forwarded port.
        private static readonly Regex PortRegex = new Regex(@"Port pair\s+(\d+)->(?:\d+)", RegexOptions.Compiled);

        public string ProviderName => RegistrySettingsManager.VpnProviderProtonVpn;

        public ProtonVPNManager(string logFilePath)
        {
            _logFilePath = logFilePath;
        }

        public bool IsVpnConnected()
        {
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces();
                // Uses Name (not Description) — ProtonVPN's adapter Name is reliably "ProtonVPN" on all
                // installations, whereas Description varies by driver version (e.g. "ProtonVPN TUN Tunnel").
                bool isConnected = adapters.Any(adapter =>
                    adapter.Name.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase) &&
                    adapter.OperationalStatus == OperationalStatus.Up);

                LogManager.Instance.LogDebug(isConnected
                    ? "ProtonVPNManager.IsVpnConnected: ProtonVPN adapter is connected"
                    : "ProtonVPNManager.IsVpnConnected: ProtonVPN adapter not found or not connected");

                return isConnected;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"ProtonVPNManager.IsVpnConnected: {ex.Message}");
                return false;
            }
        }

        public Task<int?> GetVpnPortAsync() => Task.FromResult(GetVpnPortCore());

        private int? GetVpnPortCore()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_logFilePath))
                {
                    LogManager.Instance.LogDebug("ProtonVPNManager.GetVpnPortCore: Logfile path is null or empty");
                    return null;
                }

                if (!File.Exists(_logFilePath))
                {
                    LogManager.Instance.LogDebug($"ProtonVPNManager.GetVpnPortCore: Logfile does not exist: {_logFilePath}");
                    return null;
                }

                LogManager.Instance.LogDebug($"ProtonVPNManager.GetVpnPortCore: Reading logfile: {_logFilePath}");

                int? port = ReadLastPortFromLog();

                if (port.HasValue)
                {
                    LogManager.Instance.LogDebug($"ProtonVPNManager.GetVpnPortCore: Found port {port.Value} in logfile");
                    return port.Value;
                }

                LogManager.Instance.LogDebug("ProtonVPNManager.GetVpnPortCore: No port found in logfile");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"ProtonVPNManager.GetVpnPortCore: {ex.Message}");
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

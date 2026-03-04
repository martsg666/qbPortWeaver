using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace qbPortWeaver
{
    // Detects ProtonVPN connectivity via network adapter enumeration and reads the forwarded port from ProtonVPN's log file
    public sealed class ProtonVPNManager : IVpnManager
    {
        private readonly string _logFilePath;
        private static readonly Regex PortRegex = new Regex(@"Port pair\s+(\d+)->(?:\d+)", RegexOptions.Compiled);

        public string ProviderName => "ProtonVPN";

        public ProtonVPNManager(string logFilePath)
        {
            _logFilePath = logFilePath;
        }

        // Checks if ProtonVPN network adapter is connected
        public bool IsVPNConnected()
        {
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces();
                bool isConnected = adapters.Any(adapter =>
                    adapter.Name.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase) &&
                    adapter.OperationalStatus == OperationalStatus.Up);

                LogManager.Instance.LogDebug(isConnected
                    ? "ProtonVPNManager.IsVPNConnected: ProtonVPN adapter is connected"
                    : "ProtonVPNManager.IsVPNConnected: ProtonVPN adapter not found or not connected");

                return isConnected;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"ProtonVPNManager.IsVPNConnected: {ex.Message}");
                return false;
            }
        }

        // Reads the ProtonVPN logfile to find the current port
        public int? GetVPNPort()
        {
            try
            {
                // Validate logfile path
                if (string.IsNullOrWhiteSpace(_logFilePath))
                {
                    LogManager.Instance.LogDebug("ProtonVPNManager.GetVPNPort: Logfile path is null or empty");
                    return null;
                }

                if (!File.Exists(_logFilePath))
                {
                    LogManager.Instance.LogDebug($"ProtonVPNManager.GetVPNPort: Logfile does not exist: {_logFilePath}");
                    return null;
                }

                LogManager.Instance.LogDebug($"ProtonVPNManager.GetVPNPort: Reading logfile: {_logFilePath}");

                int? port = ReadLastPortFromLog();

                if (port.HasValue)
                {
                    LogManager.Instance.LogDebug($"ProtonVPNManager.GetVPNPort: Found port {port.Value} in logfile");
                    return port.Value;
                }

                LogManager.Instance.LogDebug("ProtonVPNManager.GetVPNPort: No port found in logfile");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"ProtonVPNManager.GetVPNPort: {ex.Message}");
                return null;
            }
        }

        // Scans the log file from the end in chunks and returns the most recent matched port.
        // Opens with FileShare.ReadWrite so ProtonVPN can keep writing while we read.
        private int? ReadLastPortFromLog()
        {
            const int CHUNK_SIZE = 4096;
            using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            long bytesRemaining = fs.Length;
            string lineFragment = string.Empty;
            byte[] buffer = new byte[CHUNK_SIZE];

            while (bytesRemaining > 0)
            {
                int chunkSize = (int)Math.Min(CHUNK_SIZE, bytesRemaining);
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

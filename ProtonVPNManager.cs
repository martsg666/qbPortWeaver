using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace qbPortWeaver
{
    // Manages ProtonVPN-related operations
    public class ProtonVPNManager
    {
        private readonly string _logFilePath;

        // Constructor
        public ProtonVPNManager(string logFilePath)
        {
            _logFilePath = logFilePath;
        }

        // Checks if ProtonVPN network adapter is connected
        public bool IsProtonVPNConnected()
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            return adapters.Any(adapter =>
                adapter.Name.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase) &&
                adapter.OperationalStatus == OperationalStatus.Up);
        }

        // Reads the ProtonVPN log file to find the current port
        public int? GetProtonVPNPort()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_logFilePath) || !File.Exists(_logFilePath))
                    return null;

                var regex = new Regex(@"Port pair\s+(\d+)->(\d+)", RegexOptions.Compiled);
                var lines = new List<string>();

                // Open file with shared read/write access
                using (var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                {
                    while (!reader.EndOfStream)
                    {
                        lines.Add(reader.ReadLine());
                    }
                }

                // Search from last line to first
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var match = regex.Match(line);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
                        return port;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProtonVPNManager.GetProtonVPNPort error: {ex.Message}");
                return null;
            }
            return null;
        }
    }
}

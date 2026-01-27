using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
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

    // Manages INI file reading
    public class IniFileManager
    {
        private readonly string _iniFilePath;
        private readonly Dictionary<string, Dictionary<string, string>> _iniData;

        // Constructor
        public IniFileManager(string iniFilePath)
        {
            _iniFilePath = iniFilePath;
            _iniData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        // Loads the INI file into memory
        public bool Load()
        {
            try
            {
                if (!File.Exists(_iniFilePath))
                    return false;

                string currentSection = "global";
                _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int commentCount = 0;

                foreach (var raw in File.ReadAllLines(_iniFilePath))
                {
                    var trimmedLine = raw.Trim();
                    if (string.IsNullOrEmpty(trimmedLine))
                        continue;

                    // Section
                    var sectionMatch = Regex.Match(trimmedLine, @"^\[(.+)\]$");
                    if (sectionMatch.Success)
                    {
                        currentSection = sectionMatch.Groups[1].Value;
                        if (!_iniData.ContainsKey(currentSection))
                            _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        commentCount = 0;
                        continue;
                    }

                    // Comment
                    if (trimmedLine.StartsWith(";"))
                    {
                        commentCount++;
                        if (!_iniData.ContainsKey(currentSection))
                            _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _iniData[currentSection][$"Comment{commentCount}"] = trimmedLine;
                        continue;
                    }

                    // Key=Value
                    var keyValueMatch = Regex.Match(trimmedLine, @"^(.+?)\s*=(.*)$");
                    if (keyValueMatch.Success)
                    {
                        var key = keyValueMatch.Groups[1].Value.Trim();
                        var value = keyValueMatch.Groups[2].Value.Trim();
                        if (!_iniData.ContainsKey(currentSection))
                            _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _iniData[currentSection][key] = value;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IniFileManager.Load error: {ex.Message}");
                return false;
            }
        }

        // Retrieves a value from the INI data
        public string GetValue(string section, string key)
        {
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
                return string.Empty;

            if (_iniData.TryGetValue(section, out var sectionDict) &&
                sectionDict.TryGetValue(key, out var value))
            {
                return value;
            }
            return string.Empty;
        }

        // Creates the INI file if missing
        public bool CreateIniFileIfMissing()
        {
            try
            {
                if (!File.Exists(_iniFilePath))
                {
                    // Default INI content
                    string defaultIniContent = @"
[general]

; Define update interval in seconds 
updateIntervalSeconds = 180

[qBittorrent]

; Define qBittorrent API credentials and URL
qBittorrentURL = http://127.0.0.1:443
qBittorrentUserName = admin
qBittorrentPassword = PASSWORD

; Define qBittorrent executable path
qBittorrentExePath = C:\Program Files\qBittorrent\qbittorrent.exe

; Define qBittorrent process name
qBittorrentProcessName = qbittorrent

; Define if qBittorrent should restart after a port change (recommended)
restartqBittorrent = True
";
                    // Ensure directory exists
                    Directory.CreateDirectory(Path.GetDirectoryName(_iniFilePath));

                    // Write default content
                    File.WriteAllText(_iniFilePath, defaultIniContent.Trim());

                    return true;
                }
                else
                {
                    Debug.WriteLine($"Failed to create INI file. File already exists.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create INI file: {ex.Message}");
                return false;
            }
        }
    }

    //  Manages qBittorrent-related operations via Web API
    public class qBittorrentManager
    {
        private readonly string _qBittorrentURL;
        private readonly string _qBittorrentUserName;
        private readonly string _qBittorrentPassword;
        private readonly string _qBittorrentProcessName;
        private readonly string _qBittorrentExePath;
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookies;

        // Constructor
        public qBittorrentManager(string qBittorrentURL, string qBittorrentUserName, string qBittorrentPassword, string qBittorrentProcessName, string qBittorrentExePath)
        {
            _qBittorrentURL = (qBittorrentURL ?? string.Empty).TrimEnd('/');
            _qBittorrentUserName = qBittorrentUserName;
            _qBittorrentPassword = qBittorrentPassword;
            _qBittorrentProcessName = qBittorrentProcessName;
            _qBittorrentExePath = qBittorrentExePath;
            _cookies = new CookieContainer();
            var handler = new HttpClientHandler { CookieContainer = _cookies };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        // Checks if the qBittorrent process is running
        public bool IsRunning()
        {
            return !string.IsNullOrEmpty(_qBittorrentProcessName) && Process.GetProcessesByName(_qBittorrentProcessName).Length > 0;
        }

        // Authenticates with the qBittorrent Web API
        public async Task<bool> AuthenticateAsync()
        {
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", _qBittorrentUserName ?? string.Empty),
                    new KeyValuePair<string, string>("password", _qBittorrentPassword ?? string.Empty)
                });

                var response = await _httpClient.PostAsync($"{_qBittorrentURL}/api/v2/auth/login", content).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"qBittorrentManager.AuthenticateAsync error: {ex.Message}");
                return false;
            }
        }

        // Gets the current listening port from qBittorrent preferences
        public async Task<int?> GetListeningPortAsync()
        {
            if (!await AuthenticateAsync().ConfigureAwait(false)) return null;

            try
            {
                var response = await _httpClient.GetAsync($"{_qBittorrentURL}/api/v2/app/preferences").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("listen_port", out var portElement))
                {
                    // handle number or string
                    if (portElement.ValueKind == JsonValueKind.Number && portElement.TryGetInt32(out int p1))
                        return p1;

                    if (portElement.ValueKind == JsonValueKind.String && int.TryParse(portElement.GetString(), out int p2))
                        return p2;
                }

                Debug.WriteLine("qBittorrent preferences JSON (listen_port not parsed):");
                Debug.WriteLine(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"qBittorrentManager.GetListeningPortAsync error: {ex.Message}");
                return null;
            }
            return null;
        }

        // Sets the listening port in qBittorrent preferences
        public async Task<bool> SetListeningPortAsync(int port)
        {
            if (!await AuthenticateAsync().ConfigureAwait(false)) return false;

            try
            {
                var jsonBody = $"{{\"listen_port\": {port}}}";
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("json", jsonBody)
                });

                var response = await _httpClient.PostAsync($"{_qBittorrentURL}/api/v2/app/setPreferences", content).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"qBittorrentManager.SetListeningPortAsync error: {ex.Message}");
                return false;
            }
        }

        // Restarts the qBittorrent application directly
        public bool Restart()
        {
            try
            {
                // Kill any running qBittorrent processes
                foreach (var proc in Process.GetProcessesByName(_qBittorrentProcessName))
                {
                    try { proc.Kill(); }
                    catch (Exception ex) { Debug.WriteLine($"Failed to kill process: {ex.Message}"); }
                }

                // Wait a bit to ensure process is terminated
                System.Threading.Thread.Sleep(2000);

                // Start qBittorrent interactively using the executable path
                var psi = new ProcessStartInfo(_qBittorrentExePath)
                {
                    UseShellExecute = true, // true allows interactive mode (shows UI)
                    WorkingDirectory = Path.GetDirectoryName(_qBittorrentExePath)
                };

                var process = Process.Start(psi);

                // Optional: wait a few seconds for the app to initialize
                System.Threading.Thread.Sleep(1000);

                return IsRunning();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"qBittorrentManager.Restart error: {ex.Message}");
                return false;
            }
        }
    }
}

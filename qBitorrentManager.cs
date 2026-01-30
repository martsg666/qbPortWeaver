using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace qbPortWeaver
{
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

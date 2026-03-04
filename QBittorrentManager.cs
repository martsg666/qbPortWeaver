using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace qbPortWeaver
{
    // Manages qBittorrent-related operations via Web API
    public sealed class qBittorrentManager : IDisposable
    {
        private const int HTTP_TIMEOUT_SECONDS = 10;
        private const int PROCESS_START_DELAY_MS = 2000;
        private const int PROCESS_KILL_DELAY_MS = 2000;
        private const int PROCESS_INIT_DELAY_MS = 1000;

        private readonly string _qBittorrentURL;
        private readonly string _qBittorrentUserName;
        private readonly string _qBittorrentPassword;
        private readonly string _qBittorrentProcessName;
        private readonly string _qBittorrentExePath;
        private readonly HttpClient _httpClient;
        private bool _isAuthenticated;

        public qBittorrentManager(string qBittorrentURL, string qBittorrentUserName, string qBittorrentPassword, string qBittorrentProcessName, string qBittorrentExePath)
        {
            _qBittorrentURL = (qBittorrentURL ?? string.Empty).TrimEnd('/');
            _qBittorrentUserName = qBittorrentUserName;
            _qBittorrentPassword = qBittorrentPassword;
            _qBittorrentProcessName = qBittorrentProcessName;
            _qBittorrentExePath = qBittorrentExePath;
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler { CookieContainer = cookies };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS) };
        }

        // Checks if the qBittorrent process is running
        public bool IsRunning()
        {
            if (string.IsNullOrEmpty(_qBittorrentProcessName)) return false;

            var processes = Process.GetProcessesByName(_qBittorrentProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var proc in processes) proc.Dispose();
            }
        }

        // Launches qBittorrent via the configured executable path
        public async Task<bool> ForceStartAsync()
        {
            try
            {
                var psi = new ProcessStartInfo(_qBittorrentExePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(_qBittorrentExePath) ?? string.Empty
                };

                Process.Start(psi)?.Dispose();
                await Task.Delay(PROCESS_START_DELAY_MS);

                return IsRunning();
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to start qBittorrent: {ex.Message} - check the Executable path in Settings ({_qBittorrentExePath})", "ERROR");
                return false;
            }
        }

        // Gets listen_port and current_interface_name from qBittorrent preferences in a single request
        public async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return (null, null);

            try
            {
                using var response = await _httpClient.GetAsync($"{_qBittorrentURL}/api/v2/app/preferences").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent preferences request failed (HTTP {(int)response.StatusCode} {response.StatusCode})", "ERROR");
                    return (null, null);
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                int? listenPort = null;
                if (root.TryGetProperty("listen_port", out var portElement))
                {
                    // listen_port may be a JSON number or string depending on qBittorrent version
                    if (portElement.ValueKind == JsonValueKind.Number && portElement.TryGetInt32(out int p1))
                        listenPort = p1;
                    else if (portElement.ValueKind == JsonValueKind.String && int.TryParse(portElement.GetString(), out int p2))
                        listenPort = p2;
                }

                if (listenPort == null)
                {
                    LogManager.Instance.LogDebug("qBittorrentManager.GetPreferencesAsync: listen_port not parsed in preferences JSON");
                    LogManager.Instance.LogDebug($"qBittorrentManager.GetPreferencesAsync: {json}");
                }

                string? currentInterfaceName = null;
                if (root.TryGetProperty("current_interface_name", out var nameElement))
                    currentInterfaceName = nameElement.GetString();

                return (listenPort, currentInterfaceName);
            }
            catch (TaskCanceledException)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI is not reachable (timed out): check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return (null, null);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI connection failed: {ex.Message} - check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return (null, null);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"qBittorrentManager.GetPreferencesAsync: {ex.Message}");
                return (null, null);
            }
        }

        // Sets the listening port in qBittorrent preferences
        public async Task<bool> SetListeningPortAsync(int port)
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return false;

            try
            {
                var jsonBody = $"{{\"listen_port\": {port}}}";
                using var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("json", jsonBody)
                });

                using var response = await _httpClient.PostAsync($"{_qBittorrentURL}/api/v2/app/setPreferences", content).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent set port failed (HTTP {(int)response.StatusCode} {response.StatusCode})", "ERROR");
                    return false;
                }
                return true;
            }
            catch (TaskCanceledException)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI is not reachable (timed out): check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return false;
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI connection failed: {ex.Message} - check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return false;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"qBittorrentManager.SetListeningPortAsync: {ex.Message}");
                return false;
            }
        }

        // Restarts the qBittorrent application directly
        public async Task<bool> RestartAsync()
        {
            try
            {
                // Kill any running qBittorrent processes
                foreach (var proc in Process.GetProcessesByName(_qBittorrentProcessName))
                {
                    try { proc.Kill(); }
                    catch (Exception ex) { LogManager.Instance.LogDebug($"qBittorrentManager.RestartAsync: Failed to kill process: {ex.Message}"); }
                    finally { proc.Dispose(); }
                }

                // Wait for process to terminate
                await Task.Delay(PROCESS_KILL_DELAY_MS);

                var psi = new ProcessStartInfo(_qBittorrentExePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(_qBittorrentExePath) ?? string.Empty
                };

                Process.Start(psi)?.Dispose();

                // Brief delay to allow the process to register before IsRunning() checks for it
                await Task.Delay(PROCESS_INIT_DELAY_MS);

                return IsRunning();
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to restart qBittorrent: {ex.Message} - check the Executable path in Settings ({_qBittorrentExePath})", "ERROR");
                return false;
            }
        }

        // Returns the connection_status field from /api/v2/transfer/info ("connected", "firewalled", or "disconnected")
        public async Task<string?> GetConnectionStatusAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return null;

            try
            {
                using var response = await _httpClient.GetAsync($"{_qBittorrentURL}/api/v2/transfer/info").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent transfer/info request failed (HTTP {(int)response.StatusCode} {response.StatusCode})", "ERROR");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("connection_status", out var statusElement))
                    return statusElement.GetString();

                LogManager.Instance.LogDebug("qBittorrentManager.GetConnectionStatusAsync: connection_status not found in transfer/info response");
                return null;
            }
            catch (TaskCanceledException)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI is not reachable (timed out): check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return null;
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI connection failed: {ex.Message} - check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"qBittorrentManager.GetConnectionStatusAsync: {ex.Message}");
                return null;
            }
        }

        public void Dispose() => _httpClient.Dispose();

        // Authenticates once per instance; subsequent calls reuse the existing session cookie
        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_isAuthenticated) return true;
            _isAuthenticated = await AuthenticateAsync();
            return _isAuthenticated;
        }

        // Authenticates with the qBittorrent Web API
        private async Task<bool> AuthenticateAsync()
        {
            try
            {
                using var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", _qBittorrentUserName),
                    new KeyValuePair<string, string>("password", _qBittorrentPassword)
                });

                using var response = await _httpClient.PostAsync($"{_qBittorrentURL}/api/v2/auth/login", content).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    LogManager.Instance.LogMessage("qBittorrent authentication failed (HTTP 403 Forbidden): your IP has been banned by qBittorrent due to too many failed login attempts. Restart qBittorrent to clear the ban", "ERROR");
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}): check the URL in Settings ({_qBittorrentURL})", "ERROR");
                    return false;
                }

                // qBittorrent returns 200 for both success and failure - check response body
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!body.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.Instance.LogMessage("qBittorrent authentication failed: wrong username or password. Check the credentials in Settings", "ERROR");
                    return false;
                }

                return true;
            }
            catch (TaskCanceledException)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI is not reachable (timed out): check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return false;
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"qBittorrent Web UI connection failed: {ex.Message} - check the URL in Settings ({_qBittorrentURL})", "ERROR");
                return false;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"qBittorrentManager.AuthenticateAsync: {ex.Message}");
                return false;
            }
        }
    }
}

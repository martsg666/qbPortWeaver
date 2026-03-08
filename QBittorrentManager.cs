using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace qbPortWeaver
{
    // Manages qBittorrent-related operations via Web API
    public sealed class QBittorrentManager : IDisposable
    {
        private const int    ProcessStartDelayMs = 2000;
        private const int    ProcessKillDelayMs  = 2000;
        private const int    ProcessInitDelayMs  = 1000;
        private const string AuthOkResponse      = "Ok.";

        private readonly string _url;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _processName;
        private readonly string _exePath;
        private readonly HttpClient _httpClient;
        private bool _isAuthenticated;

        public QBittorrentManager(string url, string userName, string password, string processName, string exePath)
        {
            _url = (url ?? string.Empty).TrimEnd('/');
            _userName = userName;
            _password = password;
            _processName = processName;
            _exePath = exePath;
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler { CookieContainer = cookies };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
        }

        public bool IsRunning()
        {
            if (string.IsNullOrEmpty(_processName)) return false;

            var processes = Process.GetProcessesByName(_processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var proc in processes) proc.Dispose();
            }
        }

        public async Task<bool> ForceStartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Process.Start(CreateQBittorrentStartInfo())?.Dispose();
                await Task.Delay(ProcessStartDelayMs, cancellationToken);
                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to start qBittorrent: {ex.Message} — check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Kill any running qBittorrent processes and wait for each to exit
                foreach (var proc in Process.GetProcessesByName(_processName))
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(ProcessKillDelayMs);
                    }
                    catch (Exception ex) { LogManager.Instance.LogDebug($"QBittorrentManager.RestartAsync: Failed to kill process: {ex.Message}"); }
                    finally { proc.Dispose(); }
                }

                Process.Start(CreateQBittorrentStartInfo())?.Dispose();

                // Brief delay to allow the process to register before IsRunning() checks for it
                await Task.Delay(ProcessInitDelayMs, cancellationToken);

                // Invalidate the session — the old cookie is dead after the process was killed
                _isAuthenticated = false;

                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart qBittorrent: {ex.Message} — check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        // Gets listen_port and current_interface_name from qBittorrent preferences in a single request
        public async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return (null, null);

            try
            {
                using var response = await _httpClient.GetAsync($"{_url}/api/v2/app/preferences").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent preferences request failed (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return (null, null);
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                int? listenPort = null;
                if (root.TryGetProperty("listen_port", out var portElement))
                {
                    // listen_port may be a JSON number or string depending on qBittorrent version
                    int parsed = 0;
                    if (portElement.ValueKind == JsonValueKind.Number && portElement.TryGetInt32(out parsed))
                        listenPort = parsed;
                    else if (portElement.ValueKind == JsonValueKind.String && int.TryParse(portElement.GetString(), out parsed))
                        listenPort = parsed;
                }

                if (listenPort == null)
                {
                    LogManager.Instance.LogDebug("QBittorrentManager.GetPreferencesAsync: listen_port not parsed in preferences JSON");
                    LogManager.Instance.LogDebug($"QBittorrentManager.GetPreferencesAsync: {json}");
                }

                string? currentInterfaceName = null;
                if (root.TryGetProperty("current_interface_name", out var nameElement))
                    currentInterfaceName = nameElement.GetString();

                return (listenPort, currentInterfaceName);
            }
            catch (Exception ex)
            {
                LogHttpException("GetPreferencesAsync", ex);
                return (null, null);
            }
        }

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

                using var response = await _httpClient.PostAsync($"{_url}/api/v2/app/setPreferences", content).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent set port failed (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LogHttpException("SetListeningPortAsync", ex);
                return false;
            }
        }

        // Returns the connection_status field from /api/v2/transfer/info ("connected", "firewalled", or "disconnected")
        public async Task<string?> GetConnectionStatusAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return null;

            try
            {
                using var response = await _httpClient.GetAsync($"{_url}/api/v2/transfer/info").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent transfer/info request failed (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("connection_status", out var statusElement))
                    return statusElement.GetString();

                LogManager.Instance.LogDebug("QBittorrentManager.GetConnectionStatusAsync: connection_status not found in transfer/info response");
                return null;
            }
            catch (Exception ex)
            {
                LogHttpException("GetConnectionStatusAsync", ex);
                return null;
            }
        }

        public void Dispose() => _httpClient.Dispose();

        // Authenticates once per instance; subsequent calls reuse the existing session cookie
        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_isAuthenticated) return true;
            _isAuthenticated = await AuthenticateAsync().ConfigureAwait(false);
            return _isAuthenticated;
        }

        private async Task<bool> AuthenticateAsync()
        {
            try
            {
                using var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", _userName),
                    new KeyValuePair<string, string>("password", _password)
                });

                using var response = await _httpClient.PostAsync($"{_url}/api/v2/auth/login", content).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    LogManager.Instance.LogMessage("qBittorrent authentication failed (HTTP 403 Forbidden): your IP has been banned by qBittorrent due to too many failed login attempts. Restart qBittorrent to clear the ban", LogLevel.Error);
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}): check the URL in Settings ({_url})", LogLevel.Error);
                    return false;
                }

                // qBittorrent returns 200 for both success and failure - check response body
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!body.Contains(AuthOkResponse, StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.Instance.LogMessage("qBittorrent authentication failed: wrong username or password. Check the credentials in Settings", LogLevel.Error);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogHttpException("AuthenticateAsync", ex);
                return false;
            }
        }

        // Builds the ProcessStartInfo used to launch or re-launch qBittorrent
        private ProcessStartInfo CreateQBittorrentStartInfo() =>
            new ProcessStartInfo(_exePath)
            {
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty
            };

        // Classifies and logs an HTTP-related exception
        private void LogHttpException(string methodName, Exception ex)
        {
            if (ex is TaskCanceledException)
                LogManager.Instance.LogMessage($"qBittorrent Web UI is not reachable (timed out): check the URL in Settings ({_url})", LogLevel.Error);
            else if (ex is HttpRequestException)
                LogManager.Instance.LogMessage($"qBittorrent Web UI connection failed: {ex.Message} — check the URL in Settings ({_url})", LogLevel.Error);
            else
            {
                LogManager.Instance.LogMessage($"qBittorrent request failed unexpectedly in {methodName}: {ex.GetType().Name}", LogLevel.Warn);
                LogManager.Instance.LogDebug($"QBittorrentManager.{methodName}: {ex.Message}");
            }
        }
    }
}

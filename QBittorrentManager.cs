using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace qbPortWeaver
{
    // Manages qBittorrent-related operations via Web API
    public sealed class QBittorrentManager : IDisposable
    {
        private const int ProcessStartDelayMs = 2000;
        private const int ProcessKillDelayMs  = 2000;
        private const int ProcessInitDelayMs  = 1000;

        private readonly string _qBittorrentUrl;
        private readonly string _qBittorrentUserName;
        private readonly string _qBittorrentPassword;
        private readonly string _qBittorrentProcessName;
        private readonly string _qBittorrentExePath;
        private readonly HttpClient _httpClient;
        private bool _isAuthenticated;

        public QBittorrentManager(string qBittorrentUrl, string qBittorrentUserName, string qBittorrentPassword, string qBittorrentProcessName, string qBittorrentExePath)
        {
            _qBittorrentUrl = (qBittorrentUrl ?? string.Empty).TrimEnd('/');
            _qBittorrentUserName = qBittorrentUserName;
            _qBittorrentPassword = qBittorrentPassword;
            _qBittorrentProcessName = qBittorrentProcessName;
            _qBittorrentExePath = qBittorrentExePath;
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler { CookieContainer = cookies };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
        }

        // ── Process operations ────────────────────────────────────────────────────

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
                LogManager.Instance.LogMessage($"Failed to start qBittorrent: {ex.Message} - check the Executable path in Settings ({_qBittorrentExePath})", LogLevel.Error);
                return false;
            }
        }

        public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Kill any running qBittorrent processes
                foreach (var proc in Process.GetProcessesByName(_qBittorrentProcessName))
                {
                    try { proc.Kill(); }
                    catch (Exception ex) { LogManager.Instance.LogDebug($"QBittorrentManager.RestartAsync: Failed to kill process: {ex.Message}"); }
                    finally { proc.Dispose(); }
                }

                // Wait for process to terminate
                await Task.Delay(ProcessKillDelayMs, cancellationToken);

                Process.Start(CreateQBittorrentStartInfo())?.Dispose();

                // Brief delay to allow the process to register before IsRunning() checks for it
                await Task.Delay(ProcessInitDelayMs, cancellationToken);

                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart qBittorrent: {ex.Message} - check the Executable path in Settings ({_qBittorrentExePath})", LogLevel.Error);
                return false;
            }
        }

        // ── API operations ────────────────────────────────────────────────────────

        // Gets listen_port and current_interface_name from qBittorrent preferences in a single request
        public async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return (null, null);

            try
            {
                using var response = await _httpClient.GetAsync($"{_qBittorrentUrl}/api/v2/app/preferences").ConfigureAwait(false);

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

                using var response = await _httpClient.PostAsync($"{_qBittorrentUrl}/api/v2/app/setPreferences", content).ConfigureAwait(false);
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
                using var response = await _httpClient.GetAsync($"{_qBittorrentUrl}/api/v2/transfer/info").ConfigureAwait(false);

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

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        public void Dispose() => _httpClient.Dispose();

        // ── Private helpers ───────────────────────────────────────────────────────

        // Authenticates once per instance; subsequent calls reuse the existing session cookie
        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_isAuthenticated) return true;
            _isAuthenticated = await AuthenticateAsync();
            return _isAuthenticated;
        }

        private async Task<bool> AuthenticateAsync()
        {
            try
            {
                using var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", _qBittorrentUserName),
                    new KeyValuePair<string, string>("password", _qBittorrentPassword)
                });

                using var response = await _httpClient.PostAsync($"{_qBittorrentUrl}/api/v2/auth/login", content).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    LogManager.Instance.LogMessage("qBittorrent authentication failed (HTTP 403 Forbidden): your IP has been banned by qBittorrent due to too many failed login attempts. Restart qBittorrent to clear the ban", LogLevel.Error);
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}): check the URL in Settings ({_qBittorrentUrl})", LogLevel.Error);
                    return false;
                }

                // qBittorrent returns 200 for both success and failure - check response body
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!body.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
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
            new ProcessStartInfo(_qBittorrentExePath)
            {
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(_qBittorrentExePath) ?? string.Empty
            };

        // Classifies and logs an HTTP-related exception; suppresses detail to debug for unexpected types
        private void LogHttpException(string methodName, Exception ex)
        {
            if (ex is TaskCanceledException)
                LogManager.Instance.LogMessage($"qBittorrent Web UI is not reachable (timed out): check the URL in Settings ({_qBittorrentUrl})", LogLevel.Error);
            else if (ex is HttpRequestException)
                LogManager.Instance.LogMessage($"qBittorrent Web UI connection failed: {ex.Message} - check the URL in Settings ({_qBittorrentUrl})", LogLevel.Error);
            else
                LogManager.Instance.LogDebug($"QBittorrentManager.{methodName}: {ex.Message}");
        }
    }
}

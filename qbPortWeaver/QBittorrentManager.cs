using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Manages qBittorrent via its Web API: authentication, port configuration, and process lifecycle.</summary>
    public sealed class QBittorrentManager : IDisposable
    {
        private const int    ProcessStartDelayMs  = 2000;
        private const int    ProcessKillTimeoutMs = 5000;
        private const int    ProcessInitDelayMs   = 1000;
        private const string AuthOkResponse      = "Ok.";
        private const string ApiAuthLogin        = "/api/v2/auth/login";
        private const string ApiAppPreferences   = "/api/v2/app/preferences";
        private const string ApiSetPreferences   = "/api/v2/app/setPreferences";
        private const string ApiTransferInfo     = "/api/v2/transfer/info";

        private readonly string _url;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _processName;
        private readonly string _exePath;
        private readonly HttpClient _httpClient;
        private bool _isAuthenticated;

        /// <summary>Creates a new manager bound to the specified qBittorrent Web API endpoint and local process.</summary>
        /// <param name="url">Base URL of the qBittorrent Web UI (e.g. <c>http://localhost:8080</c>).</param>
        /// <param name="userName">Web UI login username.</param>
        /// <param name="password">Web UI login password.</param>
        /// <param name="processName">Process name used to detect whether qBittorrent is running (e.g. <c>qbittorrent</c>).</param>
        /// <param name="exePath">Full path to the qBittorrent executable, used for force-start.</param>
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

        /// <summary>Disposes the underlying HTTP client.</summary>
        public void Dispose() => _httpClient.Dispose();

        /// <summary>Returns <see langword="true"/> if the qBittorrent process is currently running.</summary>
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

        /// <summary>Launches qBittorrent and returns <see langword="true"/> if it starts successfully.</summary>
        public async Task<bool> ForceStartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Process.Start(CreateQBittorrentStartInfo())?.Dispose();
                await Task.Delay(ProcessStartDelayMs, cancellationToken).ConfigureAwait(false);
                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to start qBittorrent: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        /// <summary>Kills all running qBittorrent processes and launches a new instance. Returns <see langword="true"/> on success.</summary>
        public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Kill all running qBittorrent processes and verify none remain before
                // launching the new instance, to avoid the new process inheriting a port or
                // file lock held by a still-dying instance.
                KillProcessesByName(_processName);
                if (IsRunning())
                {
                    // First pass left survivors - wait briefly and retry with a fresh process list
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    KillProcessesByName(_processName);
                    if (IsRunning())
                    {
                        LogManager.Instance.LogMessage("Failed to kill all qBittorrent processes - aborting restart", LogLevel.Error);
                        return false;
                    }
                }

                Process.Start(CreateQBittorrentStartInfo())?.Dispose();
                await Task.Delay(ProcessInitDelayMs, cancellationToken).ConfigureAwait(false);
                _isAuthenticated = false;

                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart qBittorrent: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        /// <summary>Returns the listen port and current network interface name from qBittorrent preferences.</summary>
        public async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return (null, null);

            try
            {
                using var response = await _httpClient.GetAsync($"{_url}{ApiAppPreferences}").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to get qBittorrent preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
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

                if (listenPort is null)
                {
                    string portDiag = root.TryGetProperty("listen_port", out var diagElement)
                        ? $"listen_port kind={diagElement.ValueKind} value={diagElement}"
                        : "listen_port key absent";
                    LogManager.Instance.LogDebug($"QBittorrentManager.GetPreferencesAsync: listen_port not parsed in preferences JSON ({portDiag})");
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

        /// <summary>Sets qBittorrent's listening port via the Web API. Returns <see langword="true"/> on success.</summary>
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

                using var response = await _httpClient.PostAsync($"{_url}{ApiSetPreferences}", content).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to set qBittorrent port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
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

        /// <summary>Returns the connection status from qBittorrent ("connected", "firewalled", or "disconnected").</summary>
        public async Task<string?> GetConnectionStatusAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return null;

            try
            {
                using var response = await _httpClient.GetAsync($"{_url}{ApiTransferInfo}").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to get qBittorrent transfer info (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
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

        // Attempts to kill all processes matching the given name.
        private static void KillProcessesByName(string processName)
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!AppConstants.KillProcess(proc, ProcessKillTimeoutMs))
                        LogManager.Instance.LogMessage($"qBittorrent process (PID {proc.Id}) still running after kill attempts", LogLevel.Warn);
                }
                catch (Exception ex) { LogManager.Instance.LogDebug($"QBittorrentManager.KillProcessesByName: Failed to kill process: {ex.Message}"); }
                finally { proc.Dispose(); }
            }
        }

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

                using var response = await _httpClient.PostAsync($"{_url}{ApiAuthLogin}", content).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    LogManager.Instance.LogMessage("qBittorrent returned HTTP 403 Forbidden - IP banned due to too many failed login attempts. Restart qBittorrent to clear the ban", LogLevel.Error);
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"qBittorrent authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}) - check the URL in Settings ({_url})", LogLevel.Error);
                    return false;
                }

                // qBittorrent returns 200 for both success and failure - check response body
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!body.Contains(AuthOkResponse, StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.Instance.LogMessage("qBittorrent authentication failed: wrong username or password - check the credentials in Settings", LogLevel.Error);
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
                LogManager.Instance.LogMessage($"qBittorrent Web UI is not reachable (timed out) - check the URL in Settings ({_url})", LogLevel.Error);
            else if (ex is HttpRequestException)
                LogManager.Instance.LogMessage($"Failed to connect to qBittorrent Web UI: {ex.Message} - check the URL in Settings ({_url})", LogLevel.Error);
            else
            {
                LogManager.Instance.LogMessage($"Failed to complete qBittorrent request in {methodName}: {ex.Message}", LogLevel.Error);
                LogManager.Instance.LogDebug($"QBittorrentManager.{methodName}: {ex.GetType().Name}");
            }
        }
    }
}

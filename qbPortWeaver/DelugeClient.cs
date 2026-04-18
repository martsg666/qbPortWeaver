using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Manages Deluge via its Web JSON-RPC API: authentication, port configuration, and process lifecycle.</summary>
    public sealed class DelugeClient : IBitTorrentClient
    {
        private const int    ProcessStartDelayMs     = 2000;
        private const int    ProcessKillTimeoutMs    = 5000;
        private const int    ProcessKillRetryDelayMs = 1000;
        private const int    ProcessInitDelayMs      = 1000;
        // Deluge's config writer debounces disk flushes by 5 s. Waiting longer than
        // that before killing the process ensures core.conf is on disk before restart.
        private const int    ConfigFlushWaitMs       = 6000;
        private const string RpcPath                 = "/json";

        private readonly string _url;
        private readonly string _password;
        private readonly string _processName;
        private readonly string _exePath;
        private readonly HttpClient _httpClient;
        private bool _isAuthenticated;
        private int _nextId = 1;

        /// <inheritdoc/>
        public string ClientName => "Deluge";

        /// <inheritdoc/>
        public bool SupportsInterfaceMismatchWarning => false;

        /// <summary>Creates a new client bound to the specified Deluge Web UI endpoint and local process.</summary>
        /// <param name="url">Base URL of the Deluge Web UI (e.g. <c>http://localhost:8112</c>).</param>
        /// <param name="password">Web UI password.</param>
        /// <param name="processName">Process name used to detect whether Deluge is running (e.g. <c>deluge</c>).</param>
        /// <param name="exePath">Full path to the Deluge executable, used for force-start.</param>
        public DelugeClient(string url, string password, string processName, string exePath)
        {
            _url         = (url ?? string.Empty).TrimEnd('/');
            _password    = password;
            _processName = processName;
            _exePath     = exePath;
            // Per-instance HttpClient (not static) because each client needs its own CookieContainer
            // to hold the Deluge session cookie. The instance is short-lived (one sync cycle).
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
        }

        /// <inheritdoc/>
        public void Dispose() => _httpClient.Dispose();

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public async Task<bool> ForceStartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _isAuthenticated = false;
                Process.Start(CreateStartInfo())?.Dispose();
                await Task.Delay(ProcessStartDelayMs, cancellationToken).ConfigureAwait(false);
                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to start Deluge: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        /// <inheritdoc/>
        /// <remarks>Waits for Deluge's debounced config flush, then kills all running processes and launches a new instance.</remarks>
        public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // core.set_config debounces disk writes by ~5 s. Wait before killing so the
                // new port survives the restart (without this, Deluge reads the old core.conf).
                await Task.Delay(ConfigFlushWaitMs, cancellationToken).ConfigureAwait(false);
                // Kill all running Deluge processes and verify none remain before launching the
                // new instance, to avoid the new process inheriting a port or file lock held by
                // a still-dying instance.
                AppConstants.KillProcessesByName(_processName, ProcessKillTimeoutMs, ClientName);
                if (IsRunning())
                {
                    // First pass left survivors - wait briefly and retry with a fresh process list
                    await Task.Delay(ProcessKillRetryDelayMs, cancellationToken).ConfigureAwait(false);
                    AppConstants.KillProcessesByName(_processName, ProcessKillTimeoutMs, ClientName);
                    if (IsRunning())
                    {
                        LogManager.Instance.LogMessage("Failed to kill all Deluge processes - aborting restart", LogLevel.Error);
                        return false;
                    }
                }

                _isAuthenticated = false;
                Process.Start(CreateStartInfo())?.Dispose();
                await Task.Delay(ProcessInitDelayMs, cancellationToken).ConfigureAwait(false);
                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart Deluge: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return (null, null);

            try
            {
                var body = $$$"""{"method":"core.get_config_values","params":[["listen_ports","random_port","listen_random_port","listen_interface"]],"id":{{{_nextId++}}}}""";
                using var content  = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_url}{RpcPath}", content).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to get Deluge preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return (null, null);
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc  = JsonDocument.Parse(json);
                var root       = doc.RootElement;

                if (!root.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
                {
                    LogManager.Instance.LogDebug("DelugeClient.GetPreferencesAsync: 'result' key missing or null in RPC response");
                    return (null, null);
                }

                int? listenPort = null;
                bool randomPort = result.TryGetProperty("random_port", out var randomPortEl) &&
                                  randomPortEl.ValueKind == JsonValueKind.True;

                if (randomPort)
                {
                    if (result.TryGetProperty("listen_random_port", out var randomPortValEl) &&
                        randomPortValEl.TryGetInt32(out int parsed))
                        listenPort = parsed;
                }
                else
                {
                    if (result.TryGetProperty("listen_ports", out var listenPortsEl) &&
                        listenPortsEl.ValueKind == JsonValueKind.Array &&
                        listenPortsEl.GetArrayLength() > 0 &&
                        listenPortsEl[0].TryGetInt32(out int parsed))
                        listenPort = parsed;
                }

                if (listenPort is null)
                    LogManager.Instance.LogDebug("DelugeClient.GetPreferencesAsync: listen port not parsed in RPC response");

                string? bindAddress = null;
                if (result.TryGetProperty("listen_interface", out var ifaceEl))
                    bindAddress = ifaceEl.GetString();

                return (listenPort, bindAddress);
            }
            catch (Exception ex)
            {
                LogHttpException("GetPreferencesAsync", ex);
                return (null, null);
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SetListeningPortAsync(int port)
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return false;

            try
            {
                // Disable UPnP and NAT-PMP alongside the port change to prevent Deluge's
                // built-in port mapping from overwriting the externally managed port.
                var body = $$$"""{"method":"core.set_config","params":[{"listen_ports":[{{{port}}},{{{port}}}],"random_port":false,"upnp":false,"natpmp":false}],"id":{{{_nextId++}}}}""";
                using var content  = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_url}{RpcPath}", content).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to set Deluge port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                // Deluge returns {"result":null,"error":null,"id":N} on success
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind != JsonValueKind.Null)
                {
                    LogManager.Instance.LogMessage($"Deluge RPC returned an error for core.set_config: {error}", LogLevel.Error);
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

        /// <inheritdoc/>
        /// <remarks>Deluge does not expose a connection status endpoint; always returns <see langword="null"/>.</remarks>
        public Task<string?> GetConnectionStatusAsync() => Task.FromResult<string?>(null);

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
                // Use JsonSerializer to safely embed the password as a JSON string literal
                string encodedPassword = JsonSerializer.Serialize(_password);
                var body = $$$"""{"method":"auth.login","params":[{{{encodedPassword}}}],"id":{{{_nextId++}}}}""";
                using var content  = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_url}{RpcPath}", content).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Deluge authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}) - check the URL in Settings ({_url})", LogLevel.Error);
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc  = JsonDocument.Parse(json);
                var root       = doc.RootElement;

                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    LogManager.Instance.LogMessage("Deluge authentication failed: check the password in Settings", LogLevel.Error);
                    return false;
                }

                if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.True)
                    return true;

                LogManager.Instance.LogMessage("Deluge authentication failed: wrong password - check the credentials in Settings", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                LogHttpException("AuthenticateAsync", ex);
                return false;
            }
        }

        // Builds the ProcessStartInfo used to launch or re-launch Deluge
        private ProcessStartInfo CreateStartInfo() =>
            new ProcessStartInfo(_exePath)
            {
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty
            };

        // Classifies and logs an HTTP-related exception
        private void LogHttpException(string methodName, Exception ex)
        {
            if (ex is TaskCanceledException)
                LogManager.Instance.LogMessage($"Deluge Web UI is not reachable (timed out) - check the URL in Settings ({_url})", LogLevel.Error);
            else if (ex is HttpRequestException)
                LogManager.Instance.LogMessage($"Failed to connect to Deluge Web UI: {ex.Message} - check the URL in Settings ({_url})", LogLevel.Error);
            else
            {
                LogManager.Instance.LogMessage($"Failed to complete Deluge request in {methodName}: {ex.Message}", LogLevel.Error);
                LogManager.Instance.LogDebug($"DelugeClient.{methodName}: {ex.GetType().Name}");
            }
        }
    }
}

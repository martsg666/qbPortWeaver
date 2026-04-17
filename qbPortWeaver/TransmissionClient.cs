using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Manages Transmission via its RPC API: authentication, port configuration, and process lifecycle.</summary>
    public sealed class TransmissionClient : IBitTorrentClient
    {
        private const int    ProcessStartDelayMs     = 2000;
        private const int    ServiceRestartTimeoutMs = 40000;
        private const int    ServiceStopTimeoutMs    = 15000;
        private const int    ServiceRestartPollMs    = 1000;
        private const string RpcPath                 = "/transmission/rpc";
        private const string SessionIdHeader         = "X-Transmission-Session-Id";
        // Stable token sent to the helper service for service-mode restarts.
        // Decoupled from _serviceName so a user-configured service name doesn't break the pipe lookup.
        private const string RestartServiceToken = RegistrySettingsManager.BitTorrentClientTransmission;

        private readonly string _url;
        private readonly string _serviceName;
        private readonly string _processName;
        private readonly string _exePath;
        private readonly HttpClient _httpClient;
        private readonly bool _isServiceMode;
        private string? _sessionId;

        /// <inheritdoc/>
        public string ClientName => "Transmission";

        /// <inheritdoc/>
        public bool SupportsInterfaceMismatchWarning => false;

        /// <summary>Creates a new client bound to the specified Transmission RPC endpoint.</summary>
        /// <param name="url">Base URL of the Transmission RPC endpoint (e.g. <c>http://localhost:9091</c>).</param>
        /// <param name="userName">RPC username.</param>
        /// <param name="password">RPC password.</param>
        /// <param name="serviceName">Windows service name used to detect and restart Transmission when running as a service (e.g. <c>Transmission</c>). Pass an empty string for user-space mode.</param>
        /// <param name="processName">Process name used to detect Transmission when running as a user-space process (e.g. <c>transmission-qt</c>).</param>
        /// <param name="exePath">Full path to the Transmission executable, used for force-start in user-space mode.</param>
        public TransmissionClient(string url, string userName, string password, string serviceName, string processName, string exePath)
        {
            _url         = (url ?? string.Empty).TrimEnd('/');
            _serviceName = serviceName;
            _processName = processName;
            _exePath     = exePath;

            // Detect at construction time whether Transmission is installed as a Windows service.
            // Caches the result so each method can branch without re-querying the SCM on every call.
            // ServiceController.Refresh() throws InvalidOperationException when the service does not exist.
            if (!string.IsNullOrEmpty(serviceName))
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    sc.Refresh();
                    _isServiceMode = true;
                }
                catch (Exception ex)
                {
                    LogManager.Instance.LogDebug($"TransmissionClient: Service '{serviceName}' not found - running in process mode: {ex.Message}");
                    _isServiceMode = false;
                }
            }

            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }

        /// <inheritdoc/>
        public void Dispose() => _httpClient.Dispose();

        /// <inheritdoc/>
        public bool IsRunning()
        {
            if (_isServiceMode)
            {
                try
                {
                    using var sc = new ServiceController(_serviceName);
                    sc.Refresh();
                    return sc.Status == ServiceControllerStatus.Running;
                }
                catch { return false; } // NOSONAR S108
            }

            if (string.IsNullOrEmpty(_processName)) return false;
            var processes = Process.GetProcessesByName(_processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> ForceStartAsync(CancellationToken cancellationToken = default)
        {
            // For service mode, delegate to RestartAsync: the helper service stops (no-op if
            // already stopped) then starts the service, which satisfies both force-start and restart.
            if (_isServiceMode)
                return await RestartAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                Process.Start(CreateStartInfo())?.Dispose();
                await Task.Delay(ProcessStartDelayMs, cancellationToken).ConfigureAwait(false);
                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to start Transmission: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        /// <inheritdoc/>
        /// <remarks>Service mode: stops and restarts the Windows service via the helper service.
        /// Process mode: no-op — the port change applied by <see cref="SetListeningPortAsync"/> is live immediately.</remarks>
        public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            if (_isServiceMode)
            {
                try
                {
                    _sessionId = null;
                    await HelperServiceClient.SendRestartAsync(RestartServiceToken).ConfigureAwait(false);

                    // The helper service restarts the service via named pipe (fire-and-forget from
                    // this side). Phase 1: wait for the service to stop. Phase 2: wait for it to
                    // come back up. Without phase 1, the first poll finds the service still running
                    // (the helper hasn't stopped it yet) and returns a false success.
                    bool wentDown = false;
                    var stopDeadline = DateTimeOffset.UtcNow.AddMilliseconds(ServiceStopTimeoutMs);
                    while (DateTimeOffset.UtcNow < stopDeadline)
                    {
                        await Task.Delay(ServiceRestartPollMs, cancellationToken).ConfigureAwait(false);
                        if (!IsRunning()) { wentDown = true; break; }
                    }

                    if (!wentDown)
                    {
                        LogManager.Instance.LogMessage($"Transmission service '{_serviceName}' did not stop within the expected time", LogLevel.Error);
                        return false;
                    }

                    var upDeadline = DateTimeOffset.UtcNow.AddMilliseconds(ServiceRestartTimeoutMs);
                    while (DateTimeOffset.UtcNow < upDeadline)
                    {
                        await Task.Delay(ServiceRestartPollMs, cancellationToken).ConfigureAwait(false);
                        if (IsRunning()) return true;
                    }

                    LogManager.Instance.LogMessage($"Transmission service '{_serviceName}' did not come back up within the expected time", LogLevel.Error);
                    return false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogManager.Instance.LogMessage($"Failed to restart Transmission service: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }

            // Process mode: session-set applies the port change immediately by rebinding the
            // listening socket in-place (tr_sessionSetPeerPort → tr_session::setSettings).
            // The Qt GUI only writes settings.json after QApplication::exec() returns (i.e.
            // on a user-initiated quit), so killing the process externally would revert the
            // port on next startup. Restart is therefore intentionally skipped in process mode.
            LogManager.Instance.LogMessage("Transmission (process mode): port change is live immediately - no restart required", LogLevel.Info);
            return true;
        }

        /// <inheritdoc/>
        public async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            try
            {
                const string body = """{"method":"session-get","arguments":{"fields":["peer-port","bind-address-ipv4"]}}""";
                using var response = await SendRpcAsync(body).ConfigureAwait(false);
                if (response is null) return (null, null);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to get Transmission preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return (null, null);
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("arguments", out var args))
                {
                    LogManager.Instance.LogDebug("TransmissionClient.GetPreferencesAsync: 'arguments' key missing from RPC response");
                    return (null, null);
                }

                int? listenPort = null;
                if (args.TryGetProperty("peer-port", out var portElement) &&
                    portElement.TryGetInt32(out int parsed))
                    listenPort = parsed;

                if (listenPort is null)
                    LogManager.Instance.LogDebug("TransmissionClient.GetPreferencesAsync: peer-port not parsed in RPC response");

                string? bindAddress = null;
                if (args.TryGetProperty("bind-address-ipv4", out var addrElement))
                    bindAddress = addrElement.GetString();

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
            try
            {
                var body = $$$"""{"method":"session-set","arguments":{"peer-port":{{{port}}},"peer-port-random-on-start":false}}""";
                using var response = await SendRpcAsync(body).ConfigureAwait(false);
                if (response is null) return false;

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to set Transmission port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.GetString()?.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
                    return true;

                LogManager.Instance.LogMessage("Transmission RPC returned non-success result for session-set", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                LogHttpException("SetListeningPortAsync", ex);
                return false;
            }
        }

        /// <inheritdoc/>
        /// <remarks>Transmission does not expose a connection status endpoint; always returns <see langword="null"/>.</remarks>
        public Task<string?> GetConnectionStatusAsync() => Task.FromResult<string?>(null);

        // Sends a Transmission RPC request, handling the CSRF token handshake transparently.
        // Transmission rejects requests without a valid session ID with HTTP 409, including
        // the very first request per session. On 409, the new session ID is extracted from
        // the X-Transmission-Session-Id response header and the request is retried once.
        private async Task<HttpResponseMessage?> SendRpcAsync(string jsonBody)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_url}{RpcPath}")
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };
                if (_sessionId is not null)
                    request.Headers.Add(SessionIdHeader, _sessionId);

                var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.Conflict)
                    return response;

                if (!response.Headers.TryGetValues(SessionIdHeader, out var values))
                {
                    LogManager.Instance.LogMessage("Transmission returned 409 without a session ID header", LogLevel.Error);
                    return response;
                }

                _sessionId = values.First();
                response.Dispose();

                using var retry = new HttpRequestMessage(HttpMethod.Post, $"{_url}{RpcPath}")
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };
                retry.Headers.Add(SessionIdHeader, _sessionId);
                return await _httpClient.SendAsync(retry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHttpException("SendRpcAsync", ex);
                return null;
            }
        }

        // Builds the ProcessStartInfo for user-space process launch
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
                LogManager.Instance.LogMessage($"Transmission RPC is not reachable (timed out) - check the URL in Settings ({_url})", LogLevel.Error);
            else if (ex is HttpRequestException)
                LogManager.Instance.LogMessage($"Failed to connect to Transmission RPC: {ex.Message} - check the URL in Settings ({_url})", LogLevel.Error);
            else
            {
                LogManager.Instance.LogMessage($"Failed to complete Transmission request in {methodName}: {ex.Message}", LogLevel.Error);
                LogManager.Instance.LogDebug($"TransmissionClient.{methodName}: {ex.GetType().Name}");
            }
        }
    }
}

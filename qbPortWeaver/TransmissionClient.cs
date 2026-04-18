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
        private const int    ProcessStartDelayMs      = 2000;
        private const int    ProcessKillTimeoutMs     = 5000;
        private const int    ProcessKillRetryDelayMs  = 1000;
        private const int    ProcessInitDelayMs       = 1000;
        private const int    GracefulShutdownWaitMs   = 5000;
        private const int    WindowCloseWaitMs        = 3000;
        private const int    ServiceRestartTimeoutMs  = 40000;
        private const int    ServiceStopTimeoutMs     = 15000;
        private const int    ServiceRestartPollMs     = 1000;
        private const string RpcPath                 = "/transmission/rpc";
        private const string SessionIdHeader         = "X-Transmission-Session-Id";
        // Stable token sent to the helper service for service-mode restarts.
        // Decoupled from _serviceNameOverride so a user-configured name doesn't break the pipe lookup.
        private const string RestartServiceToken = RegistrySettingsManager.BitTorrentClientTransmission;

        private readonly string _url;
        private readonly string _serviceNameOverride;
        private readonly string _processName;
        private readonly string _exePath;
        private readonly HttpClient _httpClient;
        private string? _sessionId;
        private string? _cachedConfigDir;     // set when TryCacheConfigDirAsync succeeds
        private string? _resolvedServiceName; // lazily discovered: override → search → null
        private bool    _serviceNameResolved;

        /// <inheritdoc/>
        public string ClientName => "Transmission";

        /// <inheritdoc/>
        public bool SupportsInterfaceMismatchWarning => false;

        /// <summary>Creates a new client bound to the specified Transmission RPC endpoint.</summary>
        /// <param name="url">Base URL of the Transmission RPC endpoint (e.g. <c>http://localhost:9091</c>).</param>
        /// <param name="userName">RPC username.</param>
        /// <param name="password">RPC password.</param>
        /// <param name="serviceNameOverride">Windows service name override. Pass an empty string to auto-detect by searching for a service with "transmission" in its name or display name.</param>
        /// <param name="processName">Process name used to detect Transmission when running as a user-space process (e.g. <c>transmission-qt</c>).</param>
        /// <param name="exePath">Full path to the Transmission executable, used for force-start in user-space mode.</param>
        public TransmissionClient(string url, string userName, string password, string serviceNameOverride, string processName, string exePath)
        {
            _url                 = (url ?? string.Empty).TrimEnd('/');
            _serviceNameOverride = serviceNameOverride;
            _processName         = processName;
            _exePath             = exePath;

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
            string? serviceName = GetEffectiveServiceName();
            if (serviceName is not null)
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running) return true;
                }
                catch { } // NOSONAR S108
            }

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
            string? serviceName = GetEffectiveServiceName();
            if (serviceName is not null)
                return await RestartServiceModeAsync(serviceName, cancellationToken).ConfigureAwait(false);

            try
            {
                _sessionId = null;
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
        /// <remarks>Auto-detects mode from <c>config-dir</c> and service discovery. Service mode: stops and
        /// restarts the Windows service via the helper service. Process mode: closes the Qt client window
        /// cleanly (so it saves settings on exit), then relaunches the executable.</remarks>
        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            string? serviceName = GetEffectiveServiceName();
            // Use service mode if a service is found AND config-dir is either unknown (no prior SetListeningPortAsync)
            // or system-wide (ProgramData), confirming the daemon is running rather than the Qt client.
            bool isService = serviceName is not null &&
                             (_cachedConfigDir is null || IsConfigDirSystemWide());
            return isService
                ? RestartServiceModeAsync(serviceName!, cancellationToken)
                : RestartProcessModeAsync(cancellationToken);
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
                var body = $$$"""{"method":"session-set","arguments":{"peer-port":{{{port}}},"peer-port-random-on-start":false,"port-forwarding-enabled":false}}""";
                using var response = await SendRpcAsync(body).ConfigureAwait(false);
                if (response is null) return false;

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to set Transmission port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var result) ||
                    result.GetString()?.Equals("success", StringComparison.OrdinalIgnoreCase) != true)
                {
                    LogManager.Instance.LogMessage("Transmission RPC returned non-success result for session-set", LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogHttpException("SetListeningPortAsync", ex);
                return false;
            }

            await TryCacheConfigDirAsync().ConfigureAwait(false);
            return true;
        }

        /// <inheritdoc/>
        /// <remarks>Transmission does not expose a connection status endpoint; always returns <see langword="null"/>.</remarks>
        public Task<string?> GetConnectionStatusAsync() => Task.FromResult<string?>(null);

        private async Task<bool> RestartServiceModeAsync(string serviceName, CancellationToken cancellationToken)
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
                    LogManager.Instance.LogMessage($"Transmission service '{serviceName}' did not stop within the expected time", LogLevel.Error);
                    return false;
                }

                var upDeadline = DateTimeOffset.UtcNow.AddMilliseconds(ServiceRestartTimeoutMs);
                while (DateTimeOffset.UtcNow < upDeadline)
                {
                    await Task.Delay(ServiceRestartPollMs, cancellationToken).ConfigureAwait(false);
                    if (IsRunning()) return true;
                }

                LogManager.Instance.LogMessage($"Transmission service '{serviceName}' did not come back up within the expected time", LogLevel.Error);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart Transmission service: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        // Process mode: wait for the Qt client to reflect the session-set change, then close
        // the window cleanly so the app saves its settings (including the new port) on exit.
        private async Task<bool> RestartProcessModeAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Wait for the Qt client to poll and reflect the port change before closing
                await Task.Delay(GracefulShutdownWaitMs, cancellationToken).ConfigureAwait(false);

                // Close the main window cleanly — the app saves settings on exit
                foreach (var proc in Process.GetProcessesByName(_processName))
                {
                    try { proc.CloseMainWindow(); }
                    catch { } // NOSONAR S108
                    finally { proc.Dispose(); }
                }

                // Wait for graceful exit, then hard-kill any survivors
                await Task.Delay(WindowCloseWaitMs, cancellationToken).ConfigureAwait(false);
                if (IsRunning())
                {
                    AppConstants.KillProcessesByName(_processName, ProcessKillTimeoutMs, ClientName);
                    if (IsRunning())
                    {
                        await Task.Delay(ProcessKillRetryDelayMs, cancellationToken).ConfigureAwait(false);
                        AppConstants.KillProcessesByName(_processName, ProcessKillTimeoutMs, ClientName);
                        if (IsRunning())
                        {
                            LogManager.Instance.LogMessage("Failed to kill all Transmission processes - aborting restart", LogLevel.Error);
                            return false;
                        }
                    }
                }

                _sessionId = null;
                Process.Start(CreateStartInfo())?.Dispose();
                await Task.Delay(ProcessInitDelayMs, cancellationToken).ConfigureAwait(false);
                return IsRunning();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart Transmission: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

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

        // Fetches config-dir via RPC and caches it for service/process mode detection in RestartAsync.
        private async Task TryCacheConfigDirAsync()
        {
            try
            {
                const string body = """{"method":"session-get","arguments":{"fields":["config-dir"]}}""";
                using var response = await SendRpcAsync(body).ConfigureAwait(false);
                if (response is null || !response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("arguments", out var args) ||
                    !args.TryGetProperty("config-dir", out var configDirEl))
                {
                    LogManager.Instance.LogDebug("TransmissionClient.TryCacheConfigDirAsync: config-dir not found in session-get response");
                    return;
                }

                string? configDir = configDirEl.GetString();
                if (!string.IsNullOrEmpty(configDir))
                    _cachedConfigDir = configDir;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"TransmissionClient.TryCacheConfigDirAsync: {ex.Message}");
            }
        }

        // Returns the effective service name: override if set, otherwise lazily discovered via search.
        private string? GetEffectiveServiceName()
        {
            if (_serviceNameResolved) return _resolvedServiceName;
            _serviceNameResolved = true;
            if (!string.IsNullOrEmpty(_serviceNameOverride))
            {
                _resolvedServiceName = _serviceNameOverride;
                return _resolvedServiceName;
            }
            _resolvedServiceName = FindTransmissionServiceName();
            return _resolvedServiceName;
        }

        // Returns true if the cached config-dir is a system-wide path (ProgramData), indicating
        // the daemon is running rather than the Qt client.
        private bool IsConfigDirSystemWide()
        {
            if (_cachedConfigDir is null) return false;
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return _cachedConfigDir.StartsWith(programData, StringComparison.OrdinalIgnoreCase);
        }

        // Searches all installed Windows services for one with "transmission" in its name or display name.
        private static string? FindTransmissionServiceName()
        {
            ServiceController[]? services = null;
            try
            {
                services = ServiceController.GetServices();
                return services
                    .FirstOrDefault(s =>
                        s.ServiceName.Contains("transmission", StringComparison.OrdinalIgnoreCase) ||
                        s.DisplayName.Contains("transmission", StringComparison.OrdinalIgnoreCase))
                    ?.ServiceName;
            }
            catch { return null; } // NOSONAR S108
            finally
            {
                if (services is not null)
                    foreach (var s in services) s.Dispose();
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

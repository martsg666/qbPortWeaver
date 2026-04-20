using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Manages Transmission via its RPC API: authentication, port configuration, and process lifecycle.</summary>
    public sealed class TransmissionClient : BitTorrentClientBase
    {
        private const int    GracefulShutdownWaitMs  = 5000;
        private const int    WindowCloseWaitMs       = 3000;
        private const int    ServiceRestartTimeoutMs = 40000;
        private const int    ServiceStopTimeoutMs    = 15000;
        private const int    ServiceRestartPollMs    = 1000;
        private const string RpcPath        = "/transmission/rpc";
        private const string SessionIdHeader = "X-Transmission-Session-Id";

        private string? _sessionId;
        private string? _resolvedServiceName; // lazily discovered via search term
        private bool    _serviceNameResolved;

        /// <inheritdoc/>
        public override string ClientName => "Transmission";

        /// <inheritdoc/>
        public override bool SupportsInterfaceMismatchWarning => false;

        /// <inheritdoc/>
        protected override string ApiLabel => "RPC";

        /// <summary>Creates a new client bound to the specified Transmission RPC endpoint.</summary>
        /// <param name="url">Base URL of the Transmission RPC endpoint (e.g. <c>http://localhost:9091</c>).</param>
        /// <param name="userName">RPC username.</param>
        /// <param name="password">RPC password.</param>
        /// <param name="processName">Process name used to detect Transmission when running as a user-space process (e.g. <c>transmission-qt</c>).</param>
        /// <param name="exePath">Full path to the Transmission executable, used for force-start in user-space mode.</param>
        public TransmissionClient(string url, string userName, string password, string processName, string exePath)
            : base(url, processName, exePath, CreateBasicAuthHttpClient(userName, password))
        {
        }

        /// <inheritdoc/>
        public override bool IsRunning()
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
                // ServiceController throws if the service name is invalid or access is denied;
                // fall through to the process-based check in the base class.
                catch { } // NOSONAR S108
            }

            return base.IsRunning();
        }

        /// <inheritdoc/>
        public override async Task<bool> ForceStartAsync(CancellationToken cancellationToken = default)
        {
            string? serviceName = GetEffectiveServiceName();
            if (serviceName is not null)
                return await RestartServiceModeAsync(serviceName, cancellationToken).ConfigureAwait(false);

            return await base.ForceStartAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        /// <remarks>Auto-detects mode from <c>config-dir</c> and service discovery. Service mode: a
        /// Windows service containing "Transmission" is installed and <c>config-dir</c> is under
        /// <c>%ProgramData%</c>, confirming the daemon is active. Process mode: no service found,
        /// or config-dir is user-specific (the Qt desktop client is running instead).</remarks>
        public override async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            string? serviceName = GetEffectiveServiceName();
            bool isService = serviceName is not null && await IsConfigDirSystemWideAsync().ConfigureAwait(false);
            LogManager.Instance.LogMessage(
                $"Transmission restarting in {(isService ? $"service mode. Service name: {serviceName}" : "process mode")}",
                LogLevel.Info);
            return isService
                ? await RestartServiceModeAsync(serviceName!, cancellationToken).ConfigureAwait(false)
                : await RestartProcessModeAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            try
            {
                const string body = """{"method":"session-get","arguments":{"fields":["peer-port","bind-address-ipv4"]}}""";
                using var response = await SendRpcAsync(body).ConfigureAwait(false);
                if (response is null) return (null, null);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to get {ClientName} preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
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
        public override async Task<bool> SetListeningPortAsync(int port)
        {
            try
            {
                var body = $$$"""{"method":"session-set","arguments":{"peer-port":{{{port}}},"peer-port-random-on-start":false,"port-forwarding-enabled":false}}""";
                using var response = await SendRpcAsync(body).ConfigureAwait(false);
                if (response is null) return false;

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to set {ClientName} port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var result) ||
                    !string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.Instance.LogMessage($"{ClientName} RPC returned non-success result for session-set", LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogHttpException("SetListeningPortAsync", ex);
                return false;
            }

            return true;
        }

        /// <inheritdoc/>
        /// <remarks>Transmission does not expose a connection status endpoint; always returns <see langword="null"/>.</remarks>
        public override Task<string?> GetConnectionStatusAsync() => Task.FromResult<string?>(null);

        /// <inheritdoc/>
        protected override void ResetAuthState()
        {
            base.ResetAuthState();
            _sessionId = null;
        }

        // Transmission uses X-Transmission-Session-Id header exchange in SendRpcAsync instead of a login step.
        protected override Task<bool> AuthenticateAsync() => Task.FromResult(true);

        private async Task<bool> RestartServiceModeAsync(string serviceName, CancellationToken cancellationToken)
        {
            try
            {
                ResetAuthState();
                await HelperServiceClient.SendRestartAsync(serviceName).ConfigureAwait(false);

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
                    LogManager.Instance.LogMessage($"{ClientName} service '{serviceName}' did not stop within the expected time", LogLevel.Error);
                    return false;
                }

                var upDeadline = DateTimeOffset.UtcNow.AddMilliseconds(ServiceRestartTimeoutMs);
                while (DateTimeOffset.UtcNow < upDeadline)
                {
                    await Task.Delay(ServiceRestartPollMs, cancellationToken).ConfigureAwait(false);
                    if (IsRunning()) return true;
                }

                LogManager.Instance.LogMessage($"{ClientName} service '{serviceName}' did not come back up within the expected time", LogLevel.Error);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart {ClientName} service: {ex.Message}", LogLevel.Error);
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

                // Close the main window cleanly; the app saves settings on exit
                foreach (var proc in Process.GetProcessesByName(_processName))
                {
                    // CloseMainWindow can fail if the process has already exited; safe to ignore.
                    try { proc.CloseMainWindow(); }
                    catch { } // NOSONAR S108
                    finally { proc.Dispose(); }
                }

                // Wait for graceful exit, then hard-kill any survivors
                await Task.Delay(WindowCloseWaitMs, cancellationToken).ConfigureAwait(false);
                if (IsRunning() && !await KillAndVerifyAsync(cancellationToken).ConfigureAwait(false))
                    return false;

                ResetAuthState();
                return await LaunchAndWaitAsync(ProcessInitDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart {ClientName}: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
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
                    LogManager.Instance.LogMessage($"{ClientName} returned 409 without a session ID header", LogLevel.Error);
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

        // Fetches config-dir live via RPC and returns true if it is NOT under the user profiles
        // root (C:\Users\...), which confirms the daemon is running rather than the Qt desktop client.
        // Covers all system account locations: %ProgramData%, ServiceProfiles\LocalService,
        // ServiceProfiles\NetworkService, system32\config\systemprofile, etc.
        private async Task<bool> IsConfigDirSystemWideAsync()
        {
            try
            {
                const string body = """{"method":"session-get","arguments":{"fields":["config-dir"]}}""";
                using var response = await SendRpcAsync(body).ConfigureAwait(false);
                if (response is null || !response.IsSuccessStatusCode) return false;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("arguments", out var args) ||
                    !args.TryGetProperty("config-dir", out var configDirEl))
                {
                    LogManager.Instance.LogDebug("TransmissionClient.IsConfigDirSystemWideAsync: config-dir not found in session-get response");
                    return false;
                }

                string? configDir = configDirEl.GetString();
                if (string.IsNullOrEmpty(configDir)) return false;

                // Parent of the current user's profile is the users root (e.g. C:\Users)
                string usersRoot = Path.GetDirectoryName(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty;

                return !Path.GetFullPath(configDir).StartsWith(
                    usersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"TransmissionClient.IsConfigDirSystemWideAsync: {ex.Message}");
                return false;
            }
        }

        // Lazily discovers and caches the Transmission Windows service name via the configured search term.
        private string? GetEffectiveServiceName()
        {
            if (_serviceNameResolved) return _resolvedServiceName;
            _serviceNameResolved = true;
            _resolvedServiceName = AppConstants.FindServiceName(RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyTransmissionServiceSearchTerm));
            return _resolvedServiceName;
        }

        // Creates an HttpClient with Basic auth for the Transmission RPC endpoint.
        private static HttpClient CreateBasicAuthHttpClient(string userName, string password)
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            return client;
        }
    }
}

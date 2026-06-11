using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>Manages Transmission via its RPC API: authentication, port configuration, and process lifecycle.</summary>
public sealed class TransmissionClient : BitTorrentClientBase
{
    // Transmission Qt's session refresh interval is 5s by default (see the "Update interval"
    // preference in Edit -> Preferences). After SetListeningPortAsync the new port lives only
    // in the daemon's in-memory session until the Qt client polls; closing the window before
    // that point overwrites the change with the stale Qt-side value. 5s matches the default
    // refresh - if a future Qt build changes the default this delay needs updating.
    private const int GracefulShutdownWaitMs = 5000;
    private const int WindowCloseWaitMs = 3000;
    private const string RpcPath = "/transmission/rpc";
    private const string SessionIdHeader = "X-Transmission-Session-Id";

    private readonly string _userName;
    private string? _sessionId;
    // Sentinel values: string.Empty = not yet resolved or last lookup failed; non-empty = cached name.
    // null is never assigned at runtime - the field starts as Empty and only transitions to non-empty
    // on a successful lookup. The nullable type is required by the volatile reference contract.
    // Static so the SCM enumeration persists across sync-cycle instances.
    // Mirrors the caching pattern used by ProtonVpnManager and PiaVpnManager.
    // volatile ensures writes from one sync-cycle thread are visible to concurrent callers.
    private static volatile string? _resolvedServiceName = string.Empty;

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
        _userName = userName;
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
            catch { } // NOSONAR S108 - ServiceController throws if the service name is invalid or access is denied; fall through to the process-based check in the base class
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
    /// <remarks>Auto-detects mode from <c>config-dir</c> via RPC and service discovery. Service
    /// mode: a Windows service containing "Transmission" is installed AND either <c>config-dir</c>
    /// confirms a system-wide location, or the daemon RPC is unreachable (the most likely cause
    /// of restart being triggered, so service mode is assumed in that case). Process mode: no
    /// service is installed, or <c>config-dir</c> is user-specific (the Qt desktop client is
    /// running instead).</remarks>
    public override async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        string? serviceName = GetEffectiveServiceName();
        // Only query RPC if a service is installed - otherwise mode is unambiguously process mode.
        bool? detected = serviceName is not null
            ? await TryDetectServiceModeAsync(cancellationToken).ConfigureAwait(false)
            : null;
        // When a service is installed but RPC is unreachable, assume service mode: the daemon
        // being hung is the most likely cause of restart being triggered, and a process-mode
        // launch would bypass the service. A wrong guess here is recoverable - the helper-side
        // restart of a dormant service either succeeds or fails cleanly.
        bool isService = serviceName is not null && (detected ?? true);
        string modeDescription;
        if (!isService)
            modeDescription = "process mode";
        else if (detected.HasValue)
            modeDescription = $"service mode. Service name: {serviceName}";
        else
            modeDescription = $"service mode (assumed; daemon RPC unreachable). Service name: {serviceName}";
        LogManager.Instance.LogMessage($"{ClientName} restarting in {modeDescription}", LogLevel.Info);
        return isService
            ? await RestartServiceModeAsync(serviceName!, cancellationToken).ConfigureAwait(false)
            : await RestartProcessModeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            const string body = """{"method":"session-get","arguments":{"fields":["peer-port","bind-address-ipv4"]}}""";
            using var response = await SendRpcAsync(body, cancellationToken).ConfigureAwait(false);
            if (response is null) return (null, null);

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"Failed to get {ClientName} preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                return (null, null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Surface RPC-level errors (e.g. "method not allowed", session conflicts) with the
            // actual server message before falling through to the generic arguments-missing path.
            if (root.TryGetProperty("result", out var rpcResult) &&
                !string.Equals(rpcResult.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Instance.LogMessage($"{ClientName} RPC returned non-success result for session-get: {rpcResult.GetString()}", LogLevel.Error);
                return (null, null);
            }

            if (!root.TryGetProperty("arguments", out var argsElement))
            {
                LogManager.Instance.LogDebug("TransmissionClient.GetPreferencesAsync: 'arguments' key missing from RPC response");
                return (null, null);
            }

            int? listenPort = null;
            if (argsElement.TryGetProperty("peer-port", out var portElement) &&
                portElement.TryGetInt32(out int parsed))
                listenPort = parsed;

            if (listenPort is null)
                LogManager.Instance.LogDebug("TransmissionClient.GetPreferencesAsync: peer-port not parsed in RPC response");

            string? bindAddress = null;
            if (argsElement.TryGetProperty("bind-address-ipv4", out var addrElement))
                bindAddress = addrElement.GetString();

            return (listenPort, bindAddress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("GetPreferencesAsync", ex);
            return (null, null);
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> SetListeningPortAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            // port-forwarding-enabled=false is Transmission's combined UPnP/NAT-PMP off switch,
            // equivalent to qBittorrent's and Deluge's separate upnp=false + natpmp=false fields.
            var body = $$$"""{"method":"session-set","arguments":{"peer-port":{{{port}}},"peer-port-random-on-start":false,"port-forwarding-enabled":false}}""";
            using var response = await SendRpcAsync(body, cancellationToken).ConfigureAwait(false);
            if (response is null) return false;

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"Failed to set {ClientName} port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Instance.LogMessage($"{ClientName} RPC returned non-success result for session-set", LogLevel.Error);
                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("SetListeningPortAsync", ex);
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Transmission does not expose a connection status endpoint; always returns <see langword="null"/>.</remarks>
    public override Task<string?> GetConnectionStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    // Transmission authenticates per request via SendRpcAsync's X-Transmission-Session-Id CSRF
    // handshake, so EnsureAuthenticatedAsync is never called. The base's no-op AuthenticateAsync
    // is inherited unchanged.

    /// <inheritdoc/>
    protected override void ResetAuthState()
    {
        base.ResetAuthState();
        _sessionId = null;
    }

    // Lazily discovers and caches the Transmission Windows service name via the configured search term.
    // Only caches a successful result; a null (not found) result is not cached so the lookup
    // retries each cycle, allowing auto-detection to succeed if the service is installed later.
    private static string? GetEffectiveServiceName()
    {
        if (_resolvedServiceName is { Length: > 0 }) return _resolvedServiceName;
        var found = AppConstants.FindServiceName(RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyTransmissionServiceSearchTerm));
        if (found is not null) _resolvedServiceName = found;
        return found;
    }

    private async Task<bool> RestartServiceModeAsync(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            ResetAuthState();
            // SendRestartAsync blocks until the helper has completed the full stop/start cycle
            // and written back its result. No client-side polling is needed.
            var helperResult = await HelperServiceClient.SendRestartAsync(serviceName, cancellationToken).ConfigureAwait(false);
            helperResult.RaiseLogAlerts();

            if (!helperResult.Completed || helperResult.ErrorCount > 0)
            {
                LogManager.Instance.LogMessage($"{ClientName} service '{serviceName}' restart did not complete cleanly", LogLevel.Error);
                return false;
            }

            if (!IsRunning())
            {
                LogManager.Instance.LogMessage($"{ClientName} service '{serviceName}' did not come back up after restart", LogLevel.Error);
                return false;
            }

            return true;
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
            foreach (var proc in Process.GetProcessesByName(ProcessName))
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
            LogManager.Instance.LogMessage($"Failed to restart {ClientName}: {ex.Message} - check the Executable path in Settings ({ExePath})", LogLevel.Error);
            return false;
        }
    }

    // Sends a Transmission RPC request, handling the CSRF token handshake transparently.
    // Transmission rejects requests without a valid session ID with HTTP 409, including
    // the very first request per session. On 409, the new session ID is extracted from
    // the X-Transmission-Session-Id response header and the request is retried once.
    private async Task<HttpResponseMessage?> SendRpcAsync(string jsonBody, CancellationToken cancellationToken = default)
    {
        try
        {
            var rpcUrl = $"{Url}{RpcPath}";
            using var request = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            if (_sessionId is not null)
                request.Headers.Add(SessionIdHeader, _sessionId);

            var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // 401 is handled here (not per caller) so every RPC call - including the post-409
            // retry below - reports the same actionable credentials message.
            if (IsUnauthorized(response))
                return null;

            if (response.StatusCode != HttpStatusCode.Conflict)
                return response;

            if (!response.Headers.TryGetValues(SessionIdHeader, out var values))
            {
                LogManager.Instance.LogMessage($"{ClientName} returned 409 without a session ID header", LogLevel.Error);
                response.Dispose();
                return null;
            }

            _sessionId = values.FirstOrDefault();
            response.Dispose();
            if (string.IsNullOrEmpty(_sessionId))
            {
                LogManager.Instance.LogMessage($"{ClientName} returned 409 with empty session ID header", LogLevel.Error);
                return null;
            }

            using var retry = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            retry.Headers.Add(SessionIdHeader, _sessionId);
            var retryResponse = await HttpClient.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            return IsUnauthorized(retryResponse) ? null : retryResponse;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("SendRpcAsync", ex);
            return null;
        }
    }

    // Returns true (after logging the actionable credentials message and disposing the response)
    // when the response is HTTP 401, so SendRpcAsync can translate it into its null failure contract.
    private bool IsUnauthorized(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized) return false;
        LogManager.Instance.LogMessage($"{ClientName} authentication failed: wrong username or password (username: '{_userName}') - check the credentials in Settings", LogLevel.Error);
        response.Dispose();
        return true;
    }

    // Fetches config-dir live via RPC to disambiguate service mode from Qt-process mode.
    // Returns true if config-dir is NOT under the user profiles root (C:\Users\...), confirming
    // the daemon is the active process; covers all system account locations: %ProgramData%,
    // ServiceProfiles\LocalService, ServiceProfiles\NetworkService, system32\config\systemprofile.
    // Returns false if config-dir is user-specific (the Qt desktop client is the active process).
    // Returns null if the mode cannot be determined (RPC unreachable, malformed response,
    // missing config-dir, etc.) - callers should fall back to a service-installation heuristic.
    private async Task<bool?> TryDetectServiceModeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            const string body = """{"method":"session-get","arguments":{"fields":["config-dir"]}}""";
            using var response = await SendRpcAsync(body, cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("arguments", out var args) ||
                !args.TryGetProperty("config-dir", out var configDirEl))
            {
                LogManager.Instance.LogDebug("TransmissionClient.TryDetectServiceModeAsync: config-dir not found in session-get response");
                return null;
            }

            string? configDir = configDirEl.GetString();
            if (string.IsNullOrEmpty(configDir)) return null;

            // Parent of the current user's profile is the users root (e.g. C:\Users)
            string usersRoot = Path.GetDirectoryName(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty;

            return !Path.GetFullPath(configDir).StartsWith(
                usersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"TransmissionClient.TryDetectServiceModeAsync: {ex.Message}");
            return null;
        }
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

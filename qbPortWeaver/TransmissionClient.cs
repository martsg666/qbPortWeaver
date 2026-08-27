using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>Manages Transmission via its RPC API: authentication, port configuration, and process lifecycle.</summary>
public sealed class TransmissionClient : ManagedClientBase
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
    private const string JsonContentType = "application/json";
    private const string JsonPropArguments = "arguments";
    private const string JsonPropResult = "result";
    private const string RpcResultSuccess = "success";

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
            using var response = await SendRpcAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            if (root.TryGetProperty(JsonPropResult, out var resultElement) &&
                !string.Equals(resultElement.AsStringOrNull(), RpcResultSuccess, StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Instance.LogMessage($"{ClientName} RPC returned non-success result for session-get: {resultElement}", LogLevel.Error);
                return (null, null);
            }

            if (!root.TryGetProperty(JsonPropArguments, out var argumentsElement))
            {
                LogManager.Instance.LogDebug("TransmissionClient.GetPreferencesAsync: 'arguments' key missing from RPC response");
                return (null, null);
            }

            int? listenPort = null;
            if (argumentsElement.TryGetProperty("peer-port", out var peerPortElement) &&
                peerPortElement.TryGetInt32(out int parsed))
                listenPort = parsed;

            if (listenPort is null)
                LogManager.Instance.LogDebug("TransmissionClient.GetPreferencesAsync: peer-port not parsed in RPC response");

            string? bindAddress = argumentsElement.GetStringOrNull("bind-address-ipv4");

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
            using var response = await SendRpcAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response is null) return false;

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"Failed to set {ClientName} port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!string.Equals(doc.RootElement.GetStringOrNull(JsonPropResult), RpcResultSuccess,
                    StringComparison.OrdinalIgnoreCase))
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

    /// <inheritdoc/>
    /// <remarks>Actively probes the port via Transmission's online port-check service. Transmission
    /// 4.1 renamed the method to <c>port_test</c> with an <c>ip_protocol</c> argument and tests each
    /// stack separately; we pin to IPv4 (the NAT-PMP forward) since the IPv6 stack is usually unmapped
    /// and reports closed. The pre-4.1 hyphen <c>port-test</c> method still answers on 4.1 but is a
    /// dead handler that always returns "No Response", so it is only used as a fallback when the daemon
    /// does not recognize the new method (Transmission &lt; 4.1). Failures log at Debug only - this is a
    /// best-effort probe and the orchestrator treats null as "undeterminable".</remarks>
    public override async Task<bool?> TestListeningPortAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (open, methodUnknown) = await RunPortTestAsync(
                """{"method":"port_test","arguments":{"ip_protocol":"ipv4"}}""", cancellationToken).ConfigureAwait(false);
            if (open is not null) return open;
            // Pre-4.1 daemon: it does not know "port_test" - fall back to the legacy hyphen method.
            if (methodUnknown)
                (open, _) = await RunPortTestAsync("""{"method":"port-test"}""", cancellationToken).ConfigureAwait(false);
            return open;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Debug, not Error: reachability is best-effort, so an unreachable client is
            // "undeterminable" rather than a fault. Routed through the shared classifier so the
            // timeout-vs-refused distinction survives - it is the evidence for why a port
            // verification came back closed.
            LogHttpException(nameof(TestListeningPortAsync), ex, LogLevel.Debug);
            return null;
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<ClientSettingConflict>?> GetConflictingSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            const string body = """{"method":"session-get","arguments":{"fields":["peer-port-random-on-start","port-forwarding-enabled"]}}""";
            using var response = await SendRpcAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode)
            {
                // Every null exit is logged: Diagnostics reports an unread check as Skip and tells the
                // user to consult the log, so a silent return would send them somewhere empty.
                LogManager.Instance.LogDebug(
                    $"TransmissionClient.GetConflictingSettingsAsync: {(response is null ? "no RPC response" : $"HTTP {(int)response.StatusCode}")}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(JsonPropArguments, out var arguments))
            {
                LogManager.Instance.LogDebug("TransmissionClient.GetConflictingSettingsAsync: 'arguments' key missing from RPC response");
                return null;
            }

            var conflicts = new List<ClientSettingConflict>();
            if (arguments.GetBoolOrNull("peer-port-random-on-start") is true)
                conflicts.Add(new("Randomize port on launch", "Transmission picks its own port the next time it starts, abandoning the forwarded one"));
            // Transmission's single switch for both UPnP and NAT-PMP.
            if (arguments.GetBoolOrNull("port-forwarding-enabled") is true)
                conflicts.Add(new("Use port forwarding from my router", "Transmission maps its own port, which can replace the one the VPN forwards"));
            return conflicts;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("GetConflictingSettingsAsync", ex);
            return null;
        }
    }

    // Runs one port-test RPC. Returns the port-is-open result (null when undeterminable) and whether
    // the daemon rejected the method name (so the caller can fall back to the legacy method).
    private async Task<(bool? Open, bool MethodUnknown)> RunPortTestAsync(string body, CancellationToken cancellationToken) // NOSONAR S2325 - calls the instance method SendRpcAsync, so it cannot be static
    {
        using var response = await SendRpcAsync(body, LogLevel.Debug, cancellationToken).ConfigureAwait(false);
        if (response is null) return (null, false);
        if (!response.IsSuccessStatusCode)
        {
            LogManager.Instance.LogDebug($"TransmissionClient.RunPortTestAsync: Failed to test {ClientName} port (HTTP {(int)response.StatusCode} {response.StatusCode})");
            return (null, false);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty(JsonPropArguments, out var argumentsElement) &&
            argumentsElement.TryGetProperty("port-is-open", out var portIsOpenElement) &&
            portIsOpenElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return (portIsOpenElement.GetBoolean(), false);

        string? result = root.GetStringOrNull(JsonPropResult);
        LogManager.Instance.LogDebug($"TransmissionClient.RunPortTestAsync: no port-is-open in response (result: {result})");
        // "no method name" / "method name not recognized" => the daemon lacks this method (old version).
        bool methodUnknown = result is not null && result.Contains("method", StringComparison.OrdinalIgnoreCase);
        return (null, methodUnknown);
    }

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
        var found = ServiceLookup.FindServiceName(RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyTransmissionServiceSearchTerm));
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
    // failureLevel governs the level for transport/protocol/auth failures: Error by default
    // (port sync, set-port - actionable), but the best-effort port verification probe passes
    // Debug so an unreachable daemon during a verify cycle does not raise an Error, matching
    // qBittorrent's and Deluge's verify-path handling.
    // callerName is captured automatically so the transport-failure log below names the public
    // method that actually failed (GetPreferencesAsync, SetListeningPortAsync, etc.), matching
    // how QBittorrentClient and DelugeClient report their own method name at each call site.
    private async Task<HttpResponseMessage?> SendRpcAsync(string jsonBody, LogLevel failureLevel = LogLevel.Error,
        CancellationToken cancellationToken = default, [CallerMemberName] string callerName = "")
    {
        try
        {
            var rpcUrl = $"{Url}{RpcPath}";
            using var request = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, JsonContentType)
            };
            if (_sessionId is not null)
                request.Headers.Add(SessionIdHeader, _sessionId);

            var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // 401 is handled here (not per caller) so every RPC call - including the post-409
            // retry below - reports the same actionable credentials message.
            if (IsUnauthorized(response, failureLevel))
                return null;

            if (response.StatusCode != HttpStatusCode.Conflict)
                return response;

            if (!response.Headers.TryGetValues(SessionIdHeader, out var values))
            {
                LogManager.Instance.LogMessage($"{ClientName} returned 409 without a session ID header", failureLevel);
                response.Dispose();
                return null;
            }

            _sessionId = values.FirstOrDefault();
            response.Dispose();
            if (string.IsNullOrEmpty(_sessionId))
            {
                LogManager.Instance.LogMessage($"{ClientName} returned 409 with empty session ID header", failureLevel);
                return null;
            }

            using var retry = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, JsonContentType)
            };
            retry.Headers.Add(SessionIdHeader, _sessionId);
            var retryResponse = await HttpClient.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            return IsUnauthorized(retryResponse, failureLevel) ? null : retryResponse;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException(callerName, ex, failureLevel);
            return null;
        }
    }

    // Returns true (after logging the actionable credentials message and disposing the response)
    // when the response is HTTP 401, so SendRpcAsync can translate it into its null failure contract.
    // NOTE: Disposes the response internally so the 409-retry path in SendRpcAsync stays clean.
    // Callers of THIS method must not wrap the response they hand in inside a using block - it is
    // already disposed once this returns true. SendRpcAsync's own callers are the opposite case and
    // must use one: a non-null return from it is a live response they take ownership of, and `using`
    // on the null failure return is a no-op. Spelled out because the two rules are adjacent and
    // opposite, and following the wrong one leaks a response on every successful RPC.
    private bool IsUnauthorized(HttpResponseMessage response, LogLevel failureLevel = LogLevel.Error)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized) return false;
        LogManager.Instance.LogMessage($"{ClientName} authentication failed: wrong username or password (username: '{_userName}') - check the credentials in Settings", failureLevel);
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
            using var response = await SendRpcAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? configDir = doc.RootElement.TryGetProperty(JsonPropArguments, out var argumentsElement)
                ? argumentsElement.GetStringOrNull("config-dir")
                : null;

            if (string.IsNullOrEmpty(configDir))
            {
                LogManager.Instance.LogDebug("TransmissionClient.TryDetectServiceModeAsync: config-dir not found in session-get response");
                return null;
            }

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

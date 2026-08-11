using System.Net;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>Manages qBittorrent via its Web API: authentication, port configuration, and process lifecycle.</summary>
public sealed class QBittorrentClient : ManagedClientBase
{
    private const string AuthOkResponse = "Ok.";
    private const string ConnectionStatusConnected = "connected";
    private const string ConnectionStatusFirewalled = "firewalled";
    private const string ApiAuthLogin = "/api/v2/auth/login";
    private const string ApiAppPreferences = "/api/v2/app/preferences";
    private const string ApiSetPreferences = "/api/v2/app/setPreferences";
    private const string ApiNetworkInterfaceList = "/api/v2/app/networkInterfaceList";
    private const string ApiTransferInfo = "/api/v2/transfer/info";

    private readonly string _userName;
    private readonly string _password;
    // The interface token qBittorrent last reported (current_network_interface), captured by
    // GetPreferencesAsync. Null until the first successful read, or when the key is absent.
    private string? _storedInterfaceToken;

    /// <inheritdoc/>
    public override string ClientName => "qBittorrent";

    /// <inheritdoc/>
    public override bool SupportsInterfaceMismatchWarning => true;

    /// <summary>Creates a new client bound to the specified qBittorrent Web API endpoint and local process.</summary>
    /// <param name="url">Base URL of the qBittorrent Web UI (e.g. <c>http://localhost:8080</c>).</param>
    /// <param name="userName">Web UI login username.</param>
    /// <param name="password">Web UI login password.</param>
    /// <param name="processName">Process name used to detect whether qBittorrent is running (e.g. <c>qbittorrent</c>).</param>
    /// <param name="exePath">Full path to the qBittorrent executable, used for force-start.</param>
    public QBittorrentClient(string url, string userName, string password, string processName, string exePath)
        : base(url, processName, exePath, CreateCookieHttpClient())
    {
        _userName = userName;
        _password = password;
    }

    /// <inheritdoc/>
    public override async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return (null, null);

        try
        {
            using var response = await HttpClient.GetAsync($"{Url}{ApiAppPreferences}", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"Failed to get {ClientName} preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                return (null, null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool hasListenPort = root.TryGetProperty("listen_port", out var listenPortElement);

            int? listenPort = null;
            if (hasListenPort)
            {
                // listen_port may be a JSON number or string depending on qBittorrent version
                int parsed;
                if (listenPortElement.ValueKind == JsonValueKind.Number && listenPortElement.TryGetInt32(out parsed))
                    listenPort = parsed;
                else if (int.TryParse(listenPortElement.AsStringOrNull(), out parsed))
                    listenPort = parsed;
            }

            if (listenPort is null)
            {
                string portDiag = hasListenPort
                    ? $"listen_port kind={listenPortElement.ValueKind} value={listenPortElement}"
                    : "listen_port key absent";
                LogManager.Instance.LogDebug($"QBittorrentClient.GetPreferencesAsync: listen_port not parsed in preferences JSON ({portDiag})");
            }

            string? currentInterfaceName = root.GetStringOrNull("current_interface_name");
            // Captured here rather than re-fetched: the binding check needs the token, and this is
            // the same response that carries the name, so it costs nothing.
            _storedInterfaceToken = root.GetStringOrNull("current_network_interface");

            return (listenPort, currentInterfaceName);
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
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return false;

        try
        {
            var jsonBody = $$$"""{"listen_port":{{{port}}},"upnp":false,"natpmp":false}""";
            using var content = new FormUrlEncodedContent([new("json", jsonBody)]);

            using var response = await HttpClient.PostAsync($"{Url}{ApiSetPreferences}", content, cancellationToken).ConfigureAwait(false);
            // qBittorrent returns HTTP 200 with an empty body on success - no JSON error envelope to check.
            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"Failed to set {ClientName} port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("SetListeningPortAsync", ex);
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Returns one of <c>"connected"</c>, <c>"firewalled"</c>, or <c>"disconnected"</c>.
    /// Used by the restart-on-disconnect check, so transport failures log at Error (an unreachable
    /// client is actionable here).</remarks>
    public override Task<string?> GetConnectionStatusAsync(CancellationToken cancellationToken = default) =>
        GetConnectionStatusCoreAsync(LogLevel.Error, nameof(GetConnectionStatusAsync), cancellationToken);

    // Core implementation shared by GetConnectionStatusAsync (restart-on-disconnect, failureLevel
    // = Error) and TestListeningPortAsync (best-effort port verification, failureLevel = Debug).
    // The failure level keeps the verification path's logging symmetric with Transmission/Deluge,
    // which log their best-effort port-test failures at Debug. callerName labels the Debug-level
    // lines with the public method that initiated the call, so a verify failure reads
    // TestListeningPortAsync; Error-level lines stay plain prose like every other client error.
    private async Task<string?> GetConnectionStatusCoreAsync(LogLevel failureLevel, string callerName, CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return null;

        try
        {
            using var response = await HttpClient.GetAsync($"{Url}{ApiTransferInfo}", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Class.Method prefix only at Debug: user-facing Warn/Error entries are plain prose
                // app-wide; the greppable prefix convention belongs to Debug entries.
                string prefix = failureLevel == LogLevel.Debug ? $"QBittorrentClient.{callerName}: " : string.Empty;
                LogManager.Instance.LogMessage($"{prefix}Failed to get {ClientName} transfer info (HTTP {(int)response.StatusCode} {response.StatusCode})", failureLevel);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetStringOrNull("connection_status") is { } connectionStatus)
                return connectionStatus;

            LogManager.Instance.LogDebug($"QBittorrentClient.{callerName}: connection_status not found in transfer/info response");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException(callerName, ex, failureLevel);
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Derived from <c>connection_status</c>: "connected" = open, "firewalled" = closed,
    /// "disconnected" or unreachable = <see langword="null"/> (no internet is not a port problem).
    /// The status is inferred from incoming peer activity, so an idle client may report closed
    /// even when the port is open - callers should confirm before alerting.</remarks>
    public override async Task<bool?> TestListeningPortAsync(CancellationToken cancellationToken = default)
    {
        // Best-effort probe: transport failures log at Debug (failureLevel), not Error, so an
        // unreachable client during verification matches Transmission/Deluge's Debug-level handling.
        string? status = await GetConnectionStatusCoreAsync(LogLevel.Debug, nameof(TestListeningPortAsync), cancellationToken).ConfigureAwait(false);
        if (string.Equals(status, ConnectionStatusConnected, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(status, ConnectionStatusFirewalled, StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>
    /// Checks the stored network-interface binding against the adapters qBittorrent can currently see.
    /// </summary>
    /// <remarks>
    /// <para>qBittorrent keeps the binding in two independent preferences: <c>current_network_interface</c>,
    /// an opaque token of the form <c>&lt;type&gt;_&lt;index&gt;</c> (<c>iftype53_32768</c>, <c>ethernet_32768</c>)
    /// that libtorrent actually binds to, and <c>current_interface_name</c>, the display name. When a VPN
    /// destroys and recreates its adapter the index changes while the name is reused, so the stored token
    /// stops resolving while every human-readable signal still looks correct: no listener, no warning, and
    /// a restart cannot help because the token is persisted configuration.</para>
    /// <para>Resolution is by name, not by parsing the token, so the differing token formats do not matter.</para>
    /// </remarks>
    /// <returns>
    /// <c>Stale</c> is <see langword="true"/> only when the stored token disagrees with the live token for
    /// the bound adapter. <c>ExpectedToken</c> is that live token, or <see langword="null"/> when the answer
    /// is unknown - no name reported, bound to all interfaces, the adapter is absent (VPN likely down), or
    /// the endpoint is unavailable on this version. Callers must treat a null expected token as "do nothing".
    /// </returns>
    internal async Task<(bool Stale, string? ExpectedToken)> CheckInterfaceBindingAsync(
        string? interfaceName, CancellationToken cancellationToken = default)
    {
        // An empty name is "bound to all interfaces", which CheckInterfaceMatch already warns about as a
        // leak - there is no per-adapter token to validate, so this check has nothing to say.
        if (string.IsNullOrEmpty(interfaceName) || _storedInterfaceToken is null) return (false, null);

        var live = await GetNetworkInterfacesAsync(cancellationToken).ConfigureAwait(false);
        if (live is null) return (false, null);

        string? expected = live.FirstOrDefault(i => string.Equals(i.Name, interfaceName, StringComparison.Ordinal)).Value;
        if (expected is null)
        {
            // The bound adapter is not present right now. That is the ordinary VPN-disconnected state,
            // not a stale binding, and re-pointing it at something else would be actively wrong.
            LogManager.Instance.LogDebug(
                $"QBittorrentClient.CheckInterfaceBindingAsync: '{interfaceName}' is not in the live adapter list - leaving the binding alone");
            return (false, null);
        }

        return (!string.Equals(_storedInterfaceToken, expected, StringComparison.Ordinal), expected);
    }

    /// <summary>
    /// Re-points the network-interface binding at <paramref name="expectedToken"/>, the live token for the
    /// adapter qBittorrent already names. Returns <see langword="true"/> when the write succeeded.
    /// </summary>
    /// <remarks>Writes the name alongside the token, as the Web UI does when an adapter is picked, so the
    /// two preferences cannot drift apart in the other direction. Never writes an empty token: empty means
    /// "bind to every interface", which would replace a client that cannot connect with one that reaches
    /// the internet outside the tunnel.</remarks>
    internal async Task<bool> RepairInterfaceBindingAsync(string interfaceName, string expectedToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(interfaceName))
            return false;

        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return false;

        try
        {
            string jsonBody = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["current_network_interface"] = expectedToken,
                ["current_interface_name"] = interfaceName,
            });
            using var content = new FormUrlEncodedContent([new("json", jsonBody)]);
            using var response = await HttpClient.PostAsync($"{Url}{ApiSetPreferences}", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage(
                    $"Failed to re-apply the {ClientName} network interface binding (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Warn);
                return false;
            }

            _storedInterfaceToken = expectedToken;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("RepairInterfaceBindingAsync", ex, LogLevel.Warn);
            return false;
        }
    }

    // Live adapters as (name, token) pairs, or null when the list cannot be read - which includes
    // qBittorrent versions predating the endpoint, so callers degrade to doing nothing.
    private async Task<List<(string Name, string Value)>?> GetNetworkInterfacesAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return null;

        try
        {
            using var response = await HttpClient.GetAsync($"{Url}{ApiNetworkInterfaceList}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogDebug(
                    $"QBittorrentClient.GetNetworkInterfacesAsync: HTTP {(int)response.StatusCode} {response.StatusCode} - interface binding not checked");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<(string Name, string Value)>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.GetStringOrNull("name") is { } name && entry.GetStringOrNull("value") is { } value)
                    result.Add((name, value));
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"QBittorrentClient.GetNetworkInterfacesAsync: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new FormUrlEncodedContent(
            [
                new("username", _userName),
                new("password", _password)
            ]);

            using var response = await HttpClient.PostAsync($"{Url}{ApiAuthLogin}", content, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                LogManager.Instance.LogMessage($"{ClientName} returned HTTP 403 Forbidden - IP banned due to too many failed login attempts. Restart {ClientName} to clear the ban", LogLevel.Error);
                return false;
            }

            // qBittorrent >= 5.2.0 returns 204 on success (WebAPI no-data responses changed from 200+"Ok." to 204)
            if (response.StatusCode == HttpStatusCode.NoContent)
                return true;

            // qBittorrent >= 5.2.0 returns 401 for wrong credentials; older versions with a reverse proxy also return 401
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                LogManager.Instance.LogMessage($"{ClientName} authentication failed: wrong username or password (username: '{_userName}') - check the credentials in Settings. If a reverse proxy is in front of {ClientName}, verify it is not requiring additional authentication", LogLevel.Error);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"{ClientName} authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}) - check the URL in Settings ({Url})", LogLevel.Error);
                return false;
            }

            // qBittorrent < 5.2.0 returns 200 for both success ("Ok.") and failure ("Fails.")
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(body.Trim(), AuthOkResponse, StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Instance.LogMessage($"{ClientName} authentication failed: wrong username or password (username: '{_userName}') - check the credentials in Settings", LogLevel.Error);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("AuthenticateAsync", ex);
            return false;
        }
    }
}

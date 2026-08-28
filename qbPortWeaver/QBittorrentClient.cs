using System.Net;
using System.Runtime.CompilerServices;
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
    private const string ApiNetworkInterfaceAddressList = "/api/v2/app/networkInterfaceAddressList";
    private const string ApiTransferInfo = "/api/v2/transfer/info";

    private readonly string _userName;
    private readonly string _password;
    // The interface token qBittorrent last reported (current_network_interface), captured by
    // GetPreferencesAsync. Null until the first successful read, or when the key is absent.
    private string? _storedInterfaceToken;
    // The address qBittorrent is configured to bind to (current_interface_address), captured by
    // GetPreferencesAsync alongside the token. Empty means "all addresses on that interface", which
    // is qBittorrent's default and a materially different case from a specific address - see
    // GetInterfaceAddressStateAsync. Null until the first successful read, or when the key is absent.
    private string? _storedInterfaceAddress;

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
            // Same reasoning: the address check needs it, and this response already carries it.
            _storedInterfaceAddress = root.GetStringOrNull("current_interface_address");

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
    // random_port is written alongside upnp/natpmp for the same reason they are: each would undo the
    // port we just set. "Use a different port on each startup" makes qBittorrent pick its own on the
    // next launch, silently stranding the VPN's forwarded port while every other check still passes.
    // Written unconditionally and without a setting, matching upnp/natpmp - a toggle for one of the
    // three and not the others would be a convention nobody could predict.
    public override Task<bool> SetListeningPortAsync(int port, CancellationToken cancellationToken = default) =>
        PostPreferencesAsync(
            $$$"""{"listen_port":{{{port}}},"random_port":false,"upnp":false,"natpmp":false}""",
            $"Failed to set {ClientName} port", LogLevel.Error, cancellationToken);

    // Both preference writes use the same envelope: a JSON object in a "json" form field POSTed to
    // setPreferences, which answers HTTP 200 with an empty body on success - there is no JSON error
    // envelope to inspect, so the status code is the whole result. What differs between the two
    // callers is the wording and the severity (a failed port write is an Error the user must act on;
    // a failed binding repair is a Warn the next cycle retries), so those are parameters.
    // [CallerMemberName] keeps the transport log line attributed to the public method, as the
    // equivalent helpers in TransmissionClient and NicotineClient do.
    private async Task<bool> PostPreferencesAsync(string jsonBody, string failureMessage, LogLevel failureLevel,
        CancellationToken cancellationToken, [CallerMemberName] string callerName = "")
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return false;

        try
        {
            using var content = new FormUrlEncodedContent([new("json", jsonBody)]);
            using var response = await HttpClient.PostAsync($"{Url}{ApiSetPreferences}", content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return true;

            LogManager.Instance.LogMessage($"{failureMessage} (HTTP {(int)response.StatusCode} {response.StatusCode})", failureLevel);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException(callerName, ex, failureLevel);
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

    /// <inheritdoc/>
    // Re-reads preferences rather than reusing the sync cycle's values: this runs from Diagnostics,
    // on demand and long after that read, and the whole point is to see the setting as it is now.
    public override async Task<IReadOnlyList<ClientSettingConflict>?> GetConflictingSettingsAsync(CancellationToken cancellationToken = default)
    {
        // Every null exit below is logged: Diagnostics reports an unread check as Skip and tells the
        // user to consult the log, so a silent return would send them somewhere empty.
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
        {
            LogManager.Instance.LogDebug("QBittorrentClient.GetConflictingSettingsAsync: not authenticated");
            return null;
        }

        try
        {
            using var response = await HttpClient.GetAsync($"{Url}{ApiAppPreferences}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogDebug($"QBittorrentClient.GetConflictingSettingsAsync: HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var conflicts = new List<ClientSettingConflict>();
            if (root.GetBoolOrNull("random_port") is true)
                conflicts.Add(new("Use a different port on each startup", "qBittorrent picks its own port on the next launch, abandoning the forwarded one"));
            if (root.GetBoolOrNull("upnp") is true)
                conflicts.Add(new("Use UPnP / NAT-PMP port forwarding from my router", "qBittorrent maps its own port, which can replace the one the VPN forwards"));
            return conflicts;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("GetConflictingSettingsAsync", ex);
            return null;
        }
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
    /// What qBittorrent is bound to by address, and what addresses its chosen adapter actually has.
    /// Facts only - the caller decides what they mean, because the interesting comparison is against the
    /// previous cycle and this client is constructed fresh each cycle.
    /// </summary>
    /// <param name="LiveAddresses">Addresses qBittorrent currently sees on the bound adapter, or
    /// <see langword="null"/> when they could not be read at all (a qBittorrent predating the endpoint,
    /// an unreachable Web API, or no interface bound). Null means "do not draw any conclusion".</param>
    /// <param name="PinnedAddress">The configured <c>current_interface_address</c>. Empty or null means
    /// qBittorrent binds to every address on the adapter, which is its default.</param>
    internal readonly record struct InterfaceAddressInfo(
        IReadOnlyList<string>? LiveAddresses,
        string? PinnedAddress);

    /// <summary>
    /// Reads the address side of the network-interface binding, the half <see cref="CheckInterfaceBindingAsync"/>
    /// cannot see. The token check compares an identifier that survives a VPN reconnect unchanged; the
    /// address underneath it does not, and a client left listening on an address the adapter no longer
    /// carries accepts no connections while every other check in the cycle reports it healthy.
    /// <para>Addresses come from qBittorrent's own endpoint rather than from <c>NetworkInterface</c>, for
    /// the same reason the token check uses qBittorrent's own adapter list: it is the view the client
    /// binds against, and it sidesteps the enumeration quirks of tunnel adapters mid-negotiation.</para>
    /// </summary>
    internal async Task<InterfaceAddressInfo> GetInterfaceAddressStateAsync(
        string? interfaceName, CancellationToken cancellationToken = default)
    {
        // An empty name is "bound to all interfaces". There is no adapter to compare against, and this
        // check must never be the thing that talks anyone into binding to one.
        if (string.IsNullOrEmpty(interfaceName) || string.IsNullOrEmpty(_storedInterfaceToken))
            return new InterfaceAddressInfo(null, _storedInterfaceAddress);

        var live = await GetInterfaceAddressesAsync(_storedInterfaceToken, cancellationToken).ConfigureAwait(false);
        return new InterfaceAddressInfo(live, _storedInterfaceAddress);
    }

    // Addresses qBittorrent reports for one interface token, or null when the list cannot be read -
    // which includes qBittorrent versions predating the endpoint, so callers degrade to doing nothing.
    // Mirrors GetNetworkInterfacesAsync exactly, including its logging levels.
    private async Task<List<string>?> GetInterfaceAddressesAsync(string interfaceToken, CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return null;

        try
        {
            string url = $"{Url}{ApiNetworkInterfaceAddressList}?iface={Uri.EscapeDataString(interfaceToken)}";
            using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogDebug(
                    $"QBittorrentClient.GetInterfaceAddressesAsync: HTTP {(int)response.StatusCode} {response.StatusCode} - interface address not checked");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } address)
                    result.Add(address);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException(nameof(GetInterfaceAddressesAsync), ex, LogLevel.Debug);
            return null;
        }
    }

    /// <summary>
    /// Forces libtorrent to rebuild its listen sockets by changing <c>current_interface_address</c>, then
    /// leaving it at <paramref name="finalAddress"/>. Returns <see langword="true"/> when every write
    /// succeeded.
    /// </summary>
    /// <remarks>
    /// <para>qBittorrent acts on a preference only when its value actually <em>changes</em>, so writing the
    /// binding back unchanged is a no-op and cannot fix a socket left on a previous address. A change is
    /// therefore required, which is why this pins an address rather than re-writing the interface.</para>
    /// <para>When <paramref name="finalAddress"/> differs from <paramref name="pinAddress"/> the pin is
    /// released again in a second write, so the stored configuration ends exactly as it started. That
    /// matters: pinning permanently would convert qBittorrent's default "all addresses" into a value this
    /// app has to maintain on every reconnect, and which goes stale and breaks the client if this app is
    /// ever removed. The intermediate pin is *stricter* than "all addresses" - it is one address on the
    /// same adapter - so unlike clearing the interface token it opens no window for traffic outside the
    /// tunnel.</para>
    /// <para>If the process dies between the two writes the client is left pinned to what was then a
    /// valid address. That is benign until the adapter's address next moves, at which point the pinned-
    /// address check reports it and repairs it like any other stale pin.</para>
    /// </remarks>
    internal async Task<bool> ForceInterfaceRebindAsync(string pinAddress, string finalAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pinAddress)) return false;

        if (!await WriteInterfaceAddressAsync(pinAddress, cancellationToken).ConfigureAwait(false)) return false;
        _storedInterfaceAddress = pinAddress;

        // Equal means the pin *is* the intended end state (a stale pin corrected to a live address), so
        // there is nothing to release and a second write would be the no-op this method exists to avoid.
        if (string.Equals(pinAddress, finalAddress, StringComparison.OrdinalIgnoreCase)) return true;

        if (!await WriteInterfaceAddressAsync(finalAddress, cancellationToken).ConfigureAwait(false)) return false;
        _storedInterfaceAddress = finalAddress;
        return true;
    }

    // One setPreferences write of current_interface_address. Empty is a legitimate value here - it means
    // "all addresses on the bound adapter" - and is the opposite of an empty interface *token*, which
    // would mean every adapter on the machine and is refused in RepairInterfaceBindingAsync.
    private Task<bool> WriteInterfaceAddressAsync(string address, CancellationToken cancellationToken)
    {
        string jsonBody = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["current_interface_address"] = address,
        });

        return PostPreferencesAsync(jsonBody, $"Failed to set the {ClientName} network interface address",
            LogLevel.Warn, cancellationToken);
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

        // JsonSerializer rather than interpolation because both values are qBittorrent-supplied
        // strings that need escaping - the same rule the other JSON bodies in these clients follow.
        string jsonBody = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["current_network_interface"] = expectedToken,
            ["current_interface_name"] = interfaceName,
        });

        if (!await PostPreferencesAsync(jsonBody, $"Failed to re-apply the {ClientName} network interface binding",
                LogLevel.Warn, cancellationToken).ConfigureAwait(false))
            return false;

        // Only after the write is confirmed: the stored token is what the next cycle's staleness
        // check compares against, so recording an unwritten value would suppress a real repair.
        _storedInterfaceToken = expectedToken;
        return true;
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
            // Debug via the shared classifier: this catch sees transport failures as well as a
            // malformed body, and the classifier covers both - timeout and connection-refused get
            // their own wording (the same reachability question the port checks ask), and anything
            // else falls to its generic branch, which names the method and the exception type.
            LogHttpException(nameof(GetNetworkInterfacesAsync), ex, LogLevel.Debug);
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

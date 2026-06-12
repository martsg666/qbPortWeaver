using System.Text;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>Manages Deluge via its Web JSON-RPC API: authentication, port configuration, and process lifecycle.</summary>
public sealed class DelugeClient : BitTorrentClientBase
{
    // Deluge's config writer debounces disk flushes by ~5 s; waiting 6 s ensures
    // core.conf is on disk before the process is killed on restart.
    private const int ConfigFlushWaitMs = 6000;
    private const string RpcPath = "/json";
    private const string JsonContentType = "application/json";
    private const string JsonPropError = "error";
    private const string JsonPropResult = "result";

    private readonly string _password;
    private int _rpcId = 1; // sequential - DelugeClient is not shared across threads; no atomic increment needed

    /// <inheritdoc/>
    public override string ClientName => "Deluge";

    /// <inheritdoc/>
    public override bool SupportsInterfaceMismatchWarning => false;

    /// <summary>Creates a new client bound to the specified Deluge Web UI endpoint and local process.</summary>
    /// <param name="url">Base URL of the Deluge Web UI (e.g. <c>http://localhost:8112</c>).</param>
    /// <param name="password">Web UI password.</param>
    /// <param name="processName">Process name used to detect whether Deluge is running (e.g. <c>deluge</c>).</param>
    /// <param name="exePath">Full path to the Deluge executable, used for force-start.</param>
    public DelugeClient(string url, string password, string processName, string exePath)
        : base(url, processName, exePath, CreateCookieHttpClient())
    {
        _password = password;
    }

    /// <inheritdoc/>
    public override async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return (null, null);

        try
        {
            var body = $$$"""{"method":"core.get_config_values","params":[["listen_ports","random_port","listen_random_port","listen_interface"]],"id":{{{_rpcId++}}}}""";
            using var content = new StringContent(body, Encoding.UTF8, JsonContentType);
            using var response = await HttpClient.PostAsync($"{Url}{RpcPath}", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"Failed to get {ClientName} preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                return (null, null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Surface RPC-level errors (e.g. "Not Authenticated", "No daemon connected") with the
            // actual server message before falling through to the generic result-missing path.
            if (root.TryGetProperty(JsonPropError, out var error) && error.ValueKind != JsonValueKind.Null)
            {
                LogManager.Instance.LogMessage($"{ClientName} RPC returned an error for core.get_config_values: {error}", LogLevel.Error);
                return (null, null);
            }

            if (!root.TryGetProperty(JsonPropResult, out var result) || result.ValueKind == JsonValueKind.Null)
            {
                LogManager.Instance.LogDebug("DelugeClient.GetPreferencesAsync: 'result' key missing or null in RPC response");
                return (null, null);
            }

            int? listenPort = ParseListenPort(result);

            if (listenPort is null)
                LogManager.Instance.LogDebug("DelugeClient.GetPreferencesAsync: listen_ports not parsed in RPC response");

            string? bindAddress = null;
            if (result.TryGetProperty("listen_interface", out var ifaceElement))
                bindAddress = ifaceElement.GetString();

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
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return false;

        try
        {
            // Disable UPnP and NAT-PMP alongside the port change to prevent Deluge's
            // built-in port mapping from overwriting the externally managed port.
            var body = $$$"""{"method":"core.set_config","params":[{"listen_ports":[{{{port}}},{{{port}}}],"random_port":false,"upnp":false,"natpmp":false}],"id":{{{_rpcId++}}}}""";
            using var content = new StringContent(body, Encoding.UTF8, JsonContentType);
            using var response = await HttpClient.PostAsync($"{Url}{RpcPath}", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"Failed to set {ClientName} port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            // Deluge returns {"result":null,"error":null,"id":N} on success
            if (doc.RootElement.TryGetProperty(JsonPropError, out var error) &&
                error.ValueKind != JsonValueKind.Null)
            {
                LogManager.Instance.LogMessage($"{ClientName} RPC returned an error for core.set_config: {error}", LogLevel.Error);
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
    /// <remarks>Deluge does not expose a connection status endpoint; always returns <see langword="null"/>.</remarks>
    public override Task<string?> GetConnectionStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <inheritdoc/>
    /// <remarks>Uses <c>core.test_listen_port</c>, which actively probes the port via Deluge's
    /// online port-check service. A null RPC result means the daemon could not determine it
    /// (e.g. check service unreachable). Failures log at Debug only - this is a best-effort
    /// probe and the orchestrator treats null as "undeterminable".</remarks>
    public override async Task<bool?> TestListeningPortAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return null;

        try
        {
            var body = $$$"""{"method":"core.test_listen_port","params":[],"id":{{{_rpcId++}}}}""";
            using var content = new StringContent(body, Encoding.UTF8, JsonContentType);
            using var response = await HttpClient.PostAsync($"{Url}{RpcPath}", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty(JsonPropError, out var error) && error.ValueKind != JsonValueKind.Null)
            {
                LogManager.Instance.LogDebug($"DelugeClient.TestListeningPortAsync: RPC returned an error: {error}");
                return null;
            }

            if (root.TryGetProperty(JsonPropResult, out var result) &&
                result.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return result.GetBoolean();

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"DelugeClient.TestListeningPortAsync: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Waits for Deluge's ~5 s config-flush debounce before the kill step so the new port survives the restart.</remarks>
    protected override Task PreRestartAsync(CancellationToken cancellationToken) =>
        Task.Delay(ConfigFlushWaitMs, cancellationToken);

    /// <inheritdoc/>
    protected override async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Use JsonSerializer to safely embed the password as a JSON string literal
            string encodedPassword = JsonSerializer.Serialize(_password);
            var body = $$$"""{"method":"auth.login","params":[{{{encodedPassword}}}],"id":{{{_rpcId++}}}}""";
            using var content = new StringContent(body, Encoding.UTF8, JsonContentType);
            using var response = await HttpClient.PostAsync($"{Url}{RpcPath}", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogManager.Instance.LogMessage($"{ClientName} authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}) - check the URL in Settings ({Url})", LogLevel.Error);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty(JsonPropError, out var error) && error.ValueKind != JsonValueKind.Null)
            {
                LogManager.Instance.LogMessage($"{ClientName} authentication failed: {error} - wrong password - check the credentials in Settings", LogLevel.Error);
                return false;
            }

            if (root.TryGetProperty(JsonPropResult, out var result) && result.ValueKind == JsonValueKind.True)
                return true;

            LogManager.Instance.LogMessage($"{ClientName} authentication failed: wrong password - check the credentials in Settings", LogLevel.Error);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("AuthenticateAsync", ex);
            return false;
        }
    }

    private static int? ParseListenPort(JsonElement result)
    {
        bool randomPort = result.TryGetProperty("random_port", out var randomPortElement) &&
                          randomPortElement.ValueKind == JsonValueKind.True;

        if (randomPort)
        {
            if (result.TryGetProperty("listen_random_port", out var randomPortValElement) &&
                randomPortValElement.TryGetInt32(out int parsed))
                return parsed;
        }
        else
        {
            if (result.TryGetProperty("listen_ports", out var listenPortsElement) &&
                listenPortsElement.ValueKind == JsonValueKind.Array &&
                listenPortsElement.GetArrayLength() > 0 &&
                listenPortsElement[0].TryGetInt32(out int parsed))
                return parsed;
        }
        return null;
    }
}

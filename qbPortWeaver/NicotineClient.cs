using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>
/// Manages Nicotine+ through the qbPortWeaver bridge plugin's local JSON API: token
/// authentication, port configuration, and process lifecycle.
/// </summary>
/// <remarks>Nicotine+ exposes no remote-control interface of its own, so the bridge plugin
/// supplies one. It applies a port change to the running client and reconnects, so the new port
/// is live within a few seconds without restarting Nicotine+.</remarks>
public sealed class NicotineClient : ManagedClientBase
{
    private const string PathPreferences = "/v1/preferences";
    private const string PathPort = "/v1/port";
    private const string PathStatus = "/v1/status";
    private const string PathPortTest = "/v1/porttest";
    private const string JsonContentType = "application/json";
    private const string AuthorizationHeader = "Authorization";

    private const string JsonPropOk = "ok";
    private const string JsonPropError = "error";
    private const string JsonPropCode = "code";
    private const string JsonPropMessage = "message";
    private const string JsonPropState = "state";
    private const string JsonPropResult = "result";

    private const string ErrorPortLocked = "port_locked_by_cli";
    private const string StateDone = "done";

    // Long enough for the plugin's own capped wait plus a round trip, short enough that the
    // shared 10 s HttpClient timeout still bounds the request.
    private const int PortTestWaitMs = 8000;

    // Nicotine+ reports "connecting" while the bridge's own reconnect is in flight. Treated as
    // still-connected so a port change is never mistaken for the client dropping offline.
    private const string StatusConnecting = "connecting";

    private readonly string _exePathHint;
    private string _endpoint;
    private string _token;
    private bool _rediscoveryAttempted;

    // Deduplicates the --port guidance. The condition lasts for the life of the Nicotine+
    // process and each sync cycle builds a fresh client, so the latch has to outlive the
    // instance or every cycle would repeat the same warning. Cleared on a successful port
    // change (see SetListeningPortAsync): the lock is then gone, and a later one must warn
    // again rather than be suppressed as a repeat of a condition that has since been fixed.
    private static string? _lastLockedWarning; // NOSONAR S2696 - one sync loop; the dedupe is deliberately process-wide

    /// <inheritdoc/>
    public override string ClientName => "Nicotine+";

    /// <inheritdoc/>
    /// <remarks>Nicotine+ stores a network adapter's friendly name, the same form qBittorrent
    /// reports, so the mismatch check works against it directly.</remarks>
    public override bool SupportsInterfaceMismatchWarning => true;

    /// <inheritdoc/>
    protected override string ApiLabel => "bridge plugin";

    /// <summary>Creates a new client bound to the Nicotine+ bridge plugin's local API.</summary>
    /// <param name="url">Base URL of the bridge plugin (e.g. <c>http://127.0.0.1:38472</c>).</param>
    /// <param name="token">Bearer token issued by the plugin.</param>
    /// <param name="processName">Process name used to detect whether Nicotine+ is running (e.g. <c>Nicotine+</c>).</param>
    /// <param name="exePath">Full path to the Nicotine+ executable, used for force-start and to locate a portable data folder.</param>
    public NicotineClient(string url, string token, string processName, string exePath)
        : base(url, processName, exePath, CreatePlainHttpClient())
    {
        _exePathHint = exePath ?? string.Empty;
        _endpoint = Url;
        _token = token ?? string.Empty;

        // The connection file is authoritative: the plugin may have bound elsewhere if its
        // configured port was taken. Prefer it over the saved settings from the start, so the
        // common case needs no failed request to correct itself.
        ApplyHandshake(NicotinePluginDiscovery.TryRead(_exePathHint), logChange: false);
    }

    /// <inheritdoc/>
    /// <remarks>A cold start makes the plugin publish a fresh connection file; re-read it so the
    /// rest of this cycle talks to the endpoint it actually bound.</remarks>
    public override async Task<bool> ForceStartAsync(CancellationToken cancellationToken = default)
    {
        bool started = await base.ForceStartAsync(cancellationToken).ConfigureAwait(false);
        if (started) TryRediscover();
        return started;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately a no-op. The bridge plugin applies the port to the running client and
    /// reconnects, so the change is already live and a restart would fix nothing.
    /// <para>It would also do harm: Nicotine+ only writes its configuration on a graceful
    /// shutdown, which killing the process does not produce on Windows, so the base class's
    /// kill-and-relaunch would discard the user's settings. Closing the main window is no better
    /// - Nicotine+ normally sits minimised to the tray with no window to close, so that path
    /// falls through to the same hard kill.</para>
    /// </remarks>
    public override Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        LogManager.Instance.LogMessage(
            $"{ClientName} applies port changes to the running client, so no restart is needed", LogLevel.Info);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public override async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, PathPreferences, null,
            LogLevel.Error, cancellationToken).ConfigureAwait(false);
        if (response is null) return (null, null);

        try
        {
            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("listen_port", out var listenPortElement) ||
                !listenPortElement.TryGetInt32(out int listenPort))
            {
                LogManager.Instance.LogDebug("NicotineClient.GetPreferencesAsync: 'listen_port' missing or not an integer in the plugin response");
                return (null, null);
            }

            // Empty and absent mean different things and must not be conflated: an empty string
            // is "bound to every adapter", which the caller warns about as a possible leak
            // outside the VPN, while null is "this client cannot report an interface" and skips
            // the check entirely. Same convention as qBittorrent.
            string? interfaceName = root.GetStringOrNull("interface");

            return (listenPort, interfaceName);
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
        // Turning off Nicotine+'s own port forwarding alongside the change, as the other clients
        // do, so its port mapper cannot fight the externally managed port.
        //
        // No random-port field here, and that is not an omission. The other three each disable a
        // random-port setting alongside the write - qBittorrent and Deluge `random_port:false`,
        // Transmission `peer-port-random-on-start:false` - because those clients offer a toggle that
        // would otherwise re-pick a port behind us. Nicotine+ has no such toggle: it picks within a
        // configured *range*, and the plugin's set_port collapses that range to a single port
        // (`portrange = (port, port)`), which removes the choice by construction. Same protection,
        // expressed the way this client's config allows. See bridge/core_io.py.
        var body = $$"""{"port":{{port}},"disable_upnp":true}""";

        using var response = await SendAsync(HttpMethod.Post, PathPort, body,
            LogLevel.Error, cancellationToken).ConfigureAwait(false);
        if (response is null) return false;

        try
        {
            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty(JsonPropOk, out var okElement) && okElement.ValueKind == JsonValueKind.True)
            {
                // The plugin rejects a locked port before any write, so a success means the lock
                // is gone. Re-arm the warning latch so a later one is reported instead of being
                // swallowed as a repeat - same rule as the interface latches in PortSyncService.
                _lastLockedWarning = null;
                return true;
            }

            LogSetPortFailure(root);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Debug, like the other three read paths: SendAsync has already returned a successful
            // response and reported any transport failure itself, so anything landing here is a
            // malformed body. The user-facing Error comes from ApplyPortUpdateAsync, which turns
            // the false below into "Failed to set {client} port to {n}" - this line only records why.
            LogHttpException("SetListeningPortAsync", ex);
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Reports <c>"connected"</c> or <c>"disconnected"</c>. The plugin's own
    /// <c>"connecting"</c> state - which covers the few seconds after a port change while the
    /// client rebinds - is reported as connected, so a sync never reads its own work as an outage.</remarks>
    public override async Task<string?> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, PathStatus, null,
            LogLevel.Error, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;

        try
        {
            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            string? status = doc.RootElement.GetStringOrNull("connection");
            return status == StatusConnecting ? "connected" : status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("GetConnectionStatusAsync", ex);
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Runs Nicotine+'s own reachability check, which probes the port through the
    /// Soulseek project's online check service. The plugin caps how long it will hold the request,
    /// so a slow check comes back as still pending and is reported here as undeterminable rather
    /// than as a closed port. Failures log at Debug only - this is a best-effort probe.</remarks>
    public override async Task<bool?> TestListeningPortAsync(CancellationToken cancellationToken = default)
    {
        var body = $$"""{"wait_ms":{{PortTestWaitMs}}}""";

        using var response = await SendAsync(HttpMethod.Post, PathPortTest, body,
            LogLevel.Debug, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;

        try
        {
            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            string? state = root.GetStringOrNull(JsonPropState);

            if (state != StateDone)
            {
                LogManager.Instance.LogDebug(
                    $"NicotineClient.TestListeningPortAsync: The check is '{state ?? "unknown"}' - treating the result as undetermined");
                return null;
            }

            if (root.TryGetProperty(JsonPropResult, out var resultElement) &&
                resultElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return resultElement.GetBoolean();

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Debug like the other clients' test methods: an unreachable client makes the result
            // undeterminable rather than faulty, so this must not badge the tray on every reconnect.
            LogHttpException("TestListeningPortAsync", ex, LogLevel.Debug);
            return null;
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<ClientSettingConflict>?> GetConflictingSettingsAsync(CancellationToken cancellationToken = default)
    {
        // Debug, not Error: this runs from Diagnostics, which reports an unreachable client itself.
        using var response = await SendAsync(HttpMethod.Get, PathPreferences, null,
            LogLevel.Debug, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;

        try
        {
            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            // The bridge reports null for a setting the running Nicotine+ version does not expose, so
            // only an explicit true is a conflict - "unknown" must never be reported as "on".
            return doc.RootElement.GetBoolOrNull("upnp") is true
                ? [new ClientSettingConflict("Use UPnP to forward the listening port", "Nicotine+ maps its own port, which can replace the one the VPN forwards")]
                : [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogHttpException("GetConflictingSettingsAsync", ex);
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>The endpoint is discovered rather than configured, so the readiness probe after a
    /// launch must use the current value rather than the one saved in Settings.</remarks>
    protected override string ResolveUrl() => _endpoint;

    // ------------------------------------------------------------------ transport

    // One request, with a single retry after re-reading the connection file. The plugin keeps its
    // port and token across restarts, so a failure that rediscovery fixes means it was reinstalled
    // or had to fall back to a different port - worth one extra round trip rather than failing the
    // whole cycle. Caller disposes the response.
    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, string? jsonBody,
        LogLevel failureLevel, CancellationToken cancellationToken,
        [CallerMemberName] string callerName = "")
    {
        var (response, exception) = await SendOnceAsync(method, path, jsonBody, cancellationToken).ConfigureAwait(false);

        bool worthRetrying = response is null || response.StatusCode == HttpStatusCode.Unauthorized;
        if (worthRetrying && TryRediscover())
        {
            response?.Dispose();
            (response, exception) = await SendOnceAsync(method, path, jsonBody, cancellationToken).ConfigureAwait(false);
        }

        if (response is null)
        {
            LogTransportFailure(callerName, exception, failureLevel);
            return null;
        }

        if (response.IsSuccessStatusCode) return response;

        await LogHttpFailureAsync(response, path, failureLevel, cancellationToken).ConfigureAwait(false);
        response.Dispose();
        return null;
    }

    private async Task<(HttpResponseMessage? Response, Exception? Error)> SendOnceAsync(
        HttpMethod method, string path, string? jsonBody, CancellationToken cancellationToken)
    {
        StringContent? content = null;
        try
        {
            using var request = new HttpRequestMessage(method, $"{_endpoint}{path}");

            // TryAddWithoutValidation rather than AuthenticationHeaderValue: a token pasted with
            // stray whitespace would make the latter throw, and the constructor must never fail
            // on user input. An unusable token becomes a clean 401 instead.
            request.Headers.TryAddWithoutValidation(AuthorizationHeader, $"Bearer {_token}");

            if (jsonBody is not null)
            {
                content = new StringContent(jsonBody, Encoding.UTF8, JsonContentType);
                request.Content = content;
            }

            var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (null, ex);
        }
        finally
        {
            // Belt-and-braces, and deliberately kept: once assigned, the request owns the content
            // and `using var request` disposes it on every path out of the try - so this second
            // Dispose is a no-op (HttpContent.Dispose is idempotent). It covers only the window
            // between constructing the content and assigning it, and it keeps the ownership
            // explicit for a reader who does not know HttpRequestMessage disposes its Content.
            content?.Dispose();
        }
    }

    // Re-reads the connection file, adopting a changed endpoint or token. Runs at most once per
    // instance, and each sync cycle builds a fresh instance, so a persistently missing plugin
    // costs one file check per cycle rather than one per request.
    private bool TryRediscover()
    {
        if (_rediscoveryAttempted) return false;
        _rediscoveryAttempted = true;

        return ApplyHandshake(NicotinePluginDiscovery.TryRead(_exePathHint), logChange: true);
    }

    private bool ApplyHandshake(NicotinePluginHandshake? handshake, bool logChange)
    {
        if (handshake is null) return false;
        if (handshake.Url == _endpoint && handshake.Token == _token) return false;

        if (logChange && handshake.Url != _endpoint)
        {
            LogManager.Instance.LogMessage(
                $"{ClientName} bridge plugin moved to {handshake.Url} - update the URL in Settings to skip this lookup each cycle",
                LogLevel.Info);
        }

        _endpoint = handshake.Url;
        _token = handshake.Token;
        return true;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    // ------------------------------------------------------------------- logging

    private void LogSetPortFailure(JsonElement root)
    {
        var (code, message) = ReadError(root);

        // A plugin built against a different version of this client can report a failure in a
        // shape ReadError does not recognise, leaving no message. Say that plainly instead of
        // trailing a colon with nothing after it, on the one log line that explains the failure.
        string detail = message.Length > 0 ? $": {message}" : " (the plugin gave no reason)";

        if (code == ErrorPortLocked)
        {
            // Not a transient failure: the port cannot change until Nicotine+ is restarted
            // without the option. Warn rather than Error, once per distinct message.
            string warning = $"Cannot set the {ClientName} port{detail}";
            if (_lastLockedWarning != warning)
            {
                _lastLockedWarning = warning;
                LogManager.Instance.LogMessage(warning, LogLevel.Warn);
            }
            return;
        }

        LogManager.Instance.LogMessage($"Failed to set {ClientName} port{detail}", LogLevel.Error);
    }

    private async Task LogHttpFailureAsync(HttpResponseMessage response, string path,
        LogLevel failureLevel, CancellationToken cancellationToken)
    {
        string detail = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);

        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"{ClientName} bridge plugin rejected the token - use Refresh next to the Plugin token in Settings to read the current one",
            HttpStatusCode.NotFound =>
                $"{ClientName} bridge plugin does not provide {path} - reinstall it from Settings to match this version of qbPortWeaver",
            _ => $"{ClientName} bridge plugin returned HTTP {(int)response.StatusCode} {response.StatusCode} for {path}{detail}"
        };

        Log(message, failureLevel);
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var (_, message) = ReadError(doc.RootElement);
            return message.Length > 0 ? $": {message}" : string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // An error body is a nicety; its absence must not mask the status code.
            return string.Empty;
        }
    }

    private static (string Code, string Message) ReadError(JsonElement root)
    {
        if (!root.TryGetProperty(JsonPropError, out var errorElement) || errorElement.ValueKind != JsonValueKind.Object)
            return (string.Empty, string.Empty);

        // Read independently: a code in an unexpected shape must not cost us the message, which is
        // the half the user actually reads.
        string code = errorElement.GetStringOrNull(JsonPropCode) ?? string.Empty;
        string message = errorElement.GetStringOrNull(JsonPropMessage) ?? string.Empty;

        // The plugin terminates its messages with a full stop - correct for an API field shown on
        // its own, and for the chat commands that echo it. Our log lines never end in one, and this
        // message is always appended to one, so strip it at the boundary rather than asking the
        // plugin to punctuate for our format. Only the trailing stop goes; internal ones stay.
        return (code, message.TrimEnd().TrimEnd('.'));
    }

    private void LogTransportFailure(string callerName, Exception? exception, LogLevel failureLevel)
    {
        if (exception is not null)
        {
            LogHttpException(callerName, exception, failureLevel);
        }
        else
        {
            Log($"{ClientName} bridge plugin is not reachable at {_endpoint}", failureLevel);
        }

        // Distinguish "plugin missing" from "Nicotine+ down", which need different fixes.
        if (failureLevel == LogLevel.Error && !NicotinePluginDiscovery.IsPluginInstalled(_exePathHint))
        {
            LogManager.Instance.LogMessage(
                $"The qbPortWeaver bridge plugin is not installed in {ClientName} - install it from Settings, then enable it in Nicotine+ under Preferences → Plugins",
                LogLevel.Error);
        }
    }

    private static void Log(string message, LogLevel level)
    {
        if (level == LogLevel.Debug) LogManager.Instance.LogDebug(message);
        else LogManager.Instance.LogMessage(message, level);
    }

    // No cookie container and no default headers: the bridge authenticates per request with a
    // bearer token, which is attached in SendOnceAsync so rediscovery can swap it mid-instance.
    private static HttpClient CreatePlainHttpClient() =>
        new() { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
}

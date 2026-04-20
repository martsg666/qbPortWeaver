using System.Net;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Manages qBittorrent via its Web API: authentication, port configuration, and process lifecycle.</summary>
    public sealed class QBittorrentClient : BitTorrentClientBase
    {
        private const string AuthOkResponse      = "Ok.";
        private const string ApiAuthLogin        = "/api/v2/auth/login";
        private const string ApiAppPreferences   = "/api/v2/app/preferences";
        private const string ApiSetPreferences   = "/api/v2/app/setPreferences";
        private const string ApiTransferInfo     = "/api/v2/transfer/info";

        private readonly string _userName;
        private readonly string _password;

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
        public override async Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return (null, null);

            try
            {
                using var response = await _httpClient.GetAsync($"{_url}{ApiAppPreferences}").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to get {ClientName} preferences (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return (null, null);
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                int? listenPort = null;
                if (root.TryGetProperty("listen_port", out var portElement))
                {
                    // listen_port may be a JSON number or string depending on qBittorrent version
                    int parsed;
                    if (portElement.ValueKind == JsonValueKind.Number && portElement.TryGetInt32(out parsed))
                        listenPort = parsed;
                    else if (portElement.ValueKind == JsonValueKind.String && int.TryParse(portElement.GetString(), out parsed))
                        listenPort = parsed;
                }

                if (listenPort is null)
                {
                    string portDiag = root.TryGetProperty("listen_port", out var diagElement)
                        ? $"listen_port kind={diagElement.ValueKind} value={diagElement}"
                        : "listen_port key absent";
                    LogManager.Instance.LogDebug($"QBittorrentClient.GetPreferencesAsync: listen_port not parsed in preferences JSON ({portDiag})");
                }

                string? currentInterfaceName = null;
                if (root.TryGetProperty("current_interface_name", out var nameElement))
                    currentInterfaceName = nameElement.GetString();

                return (listenPort, currentInterfaceName);
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
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return false;

            try
            {
                var jsonBody = $"{{\"listen_port\":{port},\"upnp\":false,\"natpmp\":false}}";
                using var content = new FormUrlEncodedContent([new("json", jsonBody)]);

                using var response = await _httpClient.PostAsync($"{_url}{ApiSetPreferences}", content).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to set {ClientName} port (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
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
        /// <remarks>Returns one of <c>"connected"</c>, <c>"firewalled"</c>, or <c>"disconnected"</c>.</remarks>
        public override async Task<string?> GetConnectionStatusAsync()
        {
            if (!await EnsureAuthenticatedAsync().ConfigureAwait(false)) return null;

            try
            {
                using var response = await _httpClient.GetAsync($"{_url}{ApiTransferInfo}").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"Failed to get {ClientName} transfer info (HTTP {(int)response.StatusCode} {response.StatusCode})", LogLevel.Error);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("connection_status", out var statusElement))
                    return statusElement.GetString();

                LogManager.Instance.LogDebug("QBittorrentClient.GetConnectionStatusAsync: connection_status not found in transfer/info response");
                return null;
            }
            catch (Exception ex)
            {
                LogHttpException("GetConnectionStatusAsync", ex);
                return null;
            }
        }

        protected override async Task<bool> AuthenticateAsync()
        {
            try
            {
                using var content = new FormUrlEncodedContent(
                [
                    new("username", _userName),
                    new("password", _password)
                ]);

                using var response = await _httpClient.PostAsync($"{_url}{ApiAuthLogin}", content).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    LogManager.Instance.LogMessage("qBittorrent returned HTTP 403 Forbidden - IP banned due to too many failed login attempts. Restart qBittorrent to clear the ban", LogLevel.Error);
                    return false;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    LogManager.Instance.LogMessage("qBittorrent returned HTTP 401 Unauthorized - check whether a reverse proxy with authentication is running in front of qBittorrent", LogLevel.Error);
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.Instance.LogMessage($"{ClientName} authentication failed (HTTP {(int)response.StatusCode} {response.StatusCode}) - check the URL in Settings ({_url})", LogLevel.Error);
                    return false;
                }

                // qBittorrent returns 200 for both success and failure - check response body
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!body.Contains(AuthOkResponse, StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.Instance.LogMessage($"{ClientName} authentication failed: wrong username or password (username: '{_userName}') - check the credentials in Settings", LogLevel.Error);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogHttpException("AuthenticateAsync", ex);
                return false;
            }
        }
    }
}

using System.Diagnostics;
using System.Net;

namespace qbPortWeaver
{
    /// <summary>Base class providing shared process-lifecycle and HTTP infrastructure for BitTorrent clients.</summary>
    public abstract class BitTorrentClientBase : IBitTorrentClient // NOSONAR S3881 - all subclasses are sealed with no additional disposable resources
    {
        protected const int ProcessStartDelayMs     = 2000;
        protected const int ProcessKillTimeoutMs    = 5000;
        protected const int ProcessKillRetryDelayMs = 1000;
        protected const int ProcessInitDelayMs      = 1000;
        private   const int ApiReadyPollIntervalMs  = 500;
        private   const int ApiReadyTimeoutSeconds  = 30;
        private   const int ApiProbeTimeoutSeconds  = 2;

        protected readonly string _url;
        protected readonly string _processName;
        protected readonly string _exePath;
        protected readonly HttpClient _httpClient;
        protected bool _isAuthenticated;

        /// <summary>Initialises the shared fields used by all BitTorrent client implementations.</summary>
        /// <param name="url">Base URL of the client's Web UI or RPC endpoint.</param>
        /// <param name="processName">Process name used for <see cref="IsRunning"/> checks. Pass an empty string if process mode is not used.</param>
        /// <param name="exePath">Full path to the client executable, used for force-start and restart.</param>
        /// <param name="httpClient">Pre-configured <see cref="HttpClient"/> (cookie-based or header-based auth depending on the client).</param>
        protected BitTorrentClientBase(string url, string processName, string exePath, HttpClient httpClient)
        {
            _url         = (url ?? string.Empty).TrimEnd('/');
            _processName = processName;
            _exePath     = exePath;
            _httpClient  = httpClient;
        }

        /// <inheritdoc/>
        public abstract string ClientName { get; }

        /// <inheritdoc/>
        public abstract bool SupportsInterfaceMismatchWarning { get; }

        /// <inheritdoc/>
        public void Dispose() => _httpClient.Dispose();

        /// <inheritdoc/>
        public virtual bool IsRunning()
        {
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
        public virtual async Task<bool> ForceStartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                ResetAuthState();
                return await LaunchAndWaitAsync(ProcessStartDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to start {ClientName}: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        /// <inheritdoc/>
        /// <remarks>Kills all running processes and launches a new instance. Subclasses may override
        /// <see cref="PreRestartAsync"/> to inject work (e.g. a config-flush wait) before the kill step.</remarks>
        public virtual async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await PreRestartAsync(cancellationToken).ConfigureAwait(false);
                // Kill all running processes and verify none remain before launching the new instance,
                // to avoid the new process inheriting a port or file lock held by a still-dying instance.
                if (!await KillAndVerifyAsync(cancellationToken).ConfigureAwait(false)) return false;
                ResetAuthState();
                return await LaunchAndWaitAsync(ProcessInitDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogManager.Instance.LogMessage($"Failed to restart {ClientName}: {ex.Message} - check the Executable path in Settings ({_exePath})", LogLevel.Error);
                return false;
            }
        }

        // Override to inject work before the kill step in RestartAsync (e.g. waiting for a config flush).
        protected virtual Task PreRestartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // Launches the process, waits for the OS to register it, confirms it is running,
        // then polls the API URL until it accepts connections or the timeout elapses.
        protected async Task<bool> LaunchAndWaitAsync(int initialDelayMs, CancellationToken cancellationToken)
        {
            Process.Start(CreateStartInfo())?.Dispose();
            await Task.Delay(initialDelayMs, cancellationToken).ConfigureAwait(false);
            if (!IsRunning()) return false;
            await WaitForApiReadyAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Probes the API URL until it returns any HTTP response (port is open) or the timeout elapses.
        // A short per-probe cancellation avoids blocking on a slow response mid-startup.
        private async Task WaitForApiReadyAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(ApiReadyTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(ApiProbeTimeoutSeconds));
                    using var response = await _httpClient.GetAsync(_url, probeCts.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { } // Connection refused or per-probe timeout - not ready yet
                await Task.Delay(ApiReadyPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            LogManager.Instance.LogDebug($"{ClientName} API did not respond within {ApiReadyTimeoutSeconds}s after start");
        }

        /// <inheritdoc/>
        public abstract Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync();

        /// <inheritdoc/>
        public abstract Task<bool> SetListeningPortAsync(int port);

        /// <inheritdoc/>
        public abstract Task<string?> GetConnectionStatusAsync();

        // Resets the per-instance auth token so the next API call re-authenticates against the freshly started process.
        protected virtual void ResetAuthState() => _isAuthenticated = false;

        // Performs the client-specific authentication handshake. Called once per instance by EnsureAuthenticatedAsync.
        protected abstract Task<bool> AuthenticateAsync();

        // Authenticates once per instance; subsequent calls reuse the existing session.
        protected async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_isAuthenticated) return true;
            _isAuthenticated = await AuthenticateAsync().ConfigureAwait(false);
            return _isAuthenticated;
        }

        // Label inserted into HTTP error messages to identify the endpoint type ("Web UI" or "RPC").
        protected virtual string ApiLabel => "Web UI";

        // Kills all processes matching _processName, waits for stragglers, then retries once.
        // Returns false (and logs an error) if processes remain after both passes.
        protected async Task<bool> KillAndVerifyAsync(CancellationToken cancellationToken)
        {
            AppConstants.KillProcessesByName(_processName, ProcessKillTimeoutMs, ClientName);
            if (!IsRunning()) return true;
            await Task.Delay(ProcessKillRetryDelayMs, cancellationToken).ConfigureAwait(false);
            AppConstants.KillProcessesByName(_processName, ProcessKillTimeoutMs, ClientName);
            if (!IsRunning()) return true;
            LogManager.Instance.LogMessage($"Failed to kill all {ClientName} processes - aborting restart", LogLevel.Error);
            return false;
        }

        // Builds the ProcessStartInfo for launching the client executable.
        protected ProcessStartInfo CreateStartInfo() =>
            new ProcessStartInfo(_exePath)
            {
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty
            };

        // Classifies and logs an HTTP-related exception using ClientName and ApiLabel.
        protected void LogHttpException(string methodName, Exception ex)
        {
            if (ex is TaskCanceledException)
                LogManager.Instance.LogMessage($"{ClientName} {ApiLabel} is not reachable (timed out) - check the URL in Settings ({_url})", LogLevel.Error);
            else if (ex is HttpRequestException)
                LogManager.Instance.LogMessage($"Failed to connect to {ClientName} {ApiLabel}: {ex.Message} - check the URL in Settings ({_url})", LogLevel.Error);
            else
            {
                LogManager.Instance.LogMessage($"Failed to complete {ClientName} request in {methodName}: {ex.Message}", LogLevel.Error);
                LogManager.Instance.LogDebug($"{GetType().Name}.{methodName}: {ex.GetType().Name}");
            }
        }

        // Creates an HttpClient with a per-instance CookieContainer for cookie-based auth (qBittorrent, Deluge).
        // Per-instance (not static) because each sync-cycle instance needs its own cookie jar for the session cookie.
        protected static HttpClient CreateCookieHttpClient()
        {
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
        }
    }
}

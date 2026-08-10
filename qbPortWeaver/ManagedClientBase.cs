using System.Diagnostics;
using System.Net;

namespace qbPortWeaver;

/// <summary>Base class providing shared process-lifecycle and HTTP infrastructure for the peer-to-peer clients.</summary>
public abstract class ManagedClientBase : IManagedClient // NOSONAR S3881 - all subclasses are sealed with no additional disposable resources
{
    // Two launch settle delays, differing only in how warm the start is. A cold start (ForceStart:
    // the client was not running) waits longer because the executable and its dependencies are being
    // paged in for the first time; a restart (the image is already in the file cache, and the process
    // was alive moments ago) registers with the OS sooner. Both are only the wait before the
    // IsRunning check - API readiness is polled separately by WaitForApiReadyAsync.
    protected const int ProcessStartDelayMs = 2000;   // cold start, via ForceStartAsync
    protected const int ProcessInitDelayMs = 1000;    // warm restart, via RestartAsync
    protected const int ProcessKillTimeoutMs = 5000;
    protected const int ProcessKillRetryDelayMs = 1000;
    private const int ApiReadyPollIntervalMs = 500;
    private const int ApiReadyTimeoutSeconds = 30;
    private const int ApiProbeTimeoutSeconds = 2;

    protected readonly string Url;
    protected readonly string ProcessName;
    protected readonly string ExePath;
    protected readonly HttpClient HttpClient;
    // Not volatile: IsAuthenticated is only accessed on the single-threaded sync loop so no
    // cross-thread visibility guarantee is needed. Unlike the static TransmissionClient._resolvedServiceName,
    // which is shared across sync-cycle instances (hence volatile), this field is confined to a single
    // client instance and is always read and written on the same thread.
    protected bool IsAuthenticated;
    private bool _disposed;

    /// <summary>Initialises the shared fields used by all client implementations.</summary>
    /// <param name="url">Base URL of the client's Web UI or RPC endpoint.</param>
    /// <param name="processName">Process name used for <see cref="IsRunning"/> checks. Pass an empty string if process mode is not used.</param>
    /// <param name="exePath">Full path to the client executable, used for force-start and restart.</param>
    /// <param name="httpClient">Pre-configured <see cref="HttpClient"/> (cookie-based or header-based auth depending on the client).</param>
    protected ManagedClientBase(string url, string processName, string exePath, HttpClient httpClient)
    {
        Url = (url ?? string.Empty).TrimEnd('/');
        ProcessName = processName;
        ExePath = exePath;
        HttpClient = httpClient;
    }

    /// <inheritdoc/>
    public abstract string ClientName { get; }

    /// <inheritdoc/>
    public abstract bool SupportsInterfaceMismatchWarning { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HttpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public virtual bool IsRunning()
    {
        if (string.IsNullOrEmpty(ProcessName)) return false;

        var processes = Process.GetProcessesByName(ProcessName);
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
            LogManager.Instance.LogMessage($"Failed to start {ClientName}: {ex.Message} - check the Executable path in Settings ({ExePath})", LogLevel.Error);
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
            LogManager.Instance.LogMessage($"Failed to restart {ClientName}: {ex.Message} - check the Executable path in Settings ({ExePath})", LogLevel.Error);
            return false;
        }
    }

    /// <inheritdoc/>
    public abstract Task<(int? ListenPort, string? CurrentInterfaceName)> GetPreferencesAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract Task<bool> SetListeningPortAsync(int port, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract Task<string?> GetConnectionStatusAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract Task<bool?> TestListeningPortAsync(CancellationToken cancellationToken = default);

    /// <summary>Called by <see cref="RestartAsync"/> before the kill step. Override to inject pre-kill work (e.g. waiting for a config flush).</summary>
    protected virtual Task PreRestartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Launches the process, waits for the OS to register it, confirms it is running, then polls the
    // API URL until it accepts connections or the timeout elapses.
    //
    // Only the process check gates the result: API readiness is best-effort and its outcome is
    // deliberately not propagated. A client whose process is up but whose Web UI is still warming
    // still returns true, because the alternative - reporting a start failure for a slow-but-healthy
    // client - is the worse error, and the first real request produces a far better-targeted message
    // than this probe could. A probe timeout is recorded at Debug and nowhere else.
    protected async Task<bool> LaunchAndWaitAsync(int initialDelayMs, CancellationToken cancellationToken)
    {
        Process.Start(CreateStartInfo())?.Dispose();
        await Task.Delay(initialDelayMs, cancellationToken).ConfigureAwait(false);
        if (!IsRunning())
        {
            // The launch itself succeeded, so the most likely cause is a ProcessName that does
            // not match the actual executable - without this hint the caller's generic failure
            // message points the user at the exe path instead of the Process name field.
            LogManager.Instance.LogMessage(
                $"{ClientName} was launched but no process named '{ProcessName}' was found - check the Process name in Settings",
                LogLevel.Error);
            return false;
        }
        await WaitForApiReadyAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Probes the API URL until it returns any HTTP response (API is accepting requests) or the timeout elapses.
    // A short per-probe cancellation avoids blocking on a slow response mid-startup.
    private async Task WaitForApiReadyAsync(CancellationToken cancellationToken)
    {
        // Monotonic: a clock correction mid-startup must not cut the probe short or extend it.
        var probeTimer = Stopwatch.StartNew();
        while (probeTimer.Elapsed.TotalSeconds < ApiReadyTimeoutSeconds)
        {
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(TimeSpan.FromSeconds(ApiProbeTimeoutSeconds));
                using var response = await HttpClient.GetAsync(ResolveUrl(), probeCts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            // HttpRequestException (connection refused) and OperationCanceledException from the
            // per-probe CTS are both expected while the process is still starting up - swallow
            // and retry after the poll interval.
            catch { } // NOSONAR S108
            await Task.Delay(ApiReadyPollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
        LogManager.Instance.LogDebug($"{GetType().Name}.WaitForApiReadyAsync: {ClientName} API did not respond within {ApiReadyTimeoutSeconds}s after start");
    }

    /// <summary>
    /// The base URL to use for requests right now. Defaults to the configured <see cref="Url"/>.
    /// Overridden by clients whose endpoint is discovered at runtime rather than configured, so
    /// the readiness probe follows the endpoint the client actually moved to.
    /// </summary>
    protected virtual string ResolveUrl() => Url;

    /// <summary>Resets the per-instance auth state so the next API call triggers a fresh authentication handshake.</summary>
    protected virtual void ResetAuthState() => IsAuthenticated = false;

    /// <summary>
    /// Performs the client-specific authentication handshake. Returns <see langword="true"/> on success.
    /// Default implementation is a no-op for clients that authenticate per-request (e.g. Transmission's
    /// X-Transmission-Session-Id CSRF handshake). Cookie-based clients (qBittorrent, Deluge) override
    /// this with their login flow.
    /// </summary>
    protected virtual Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    // Authenticates once per instance; subsequent calls reuse the existing session.
    protected async Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        if (IsAuthenticated) return true;
        IsAuthenticated = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        return IsAuthenticated;
    }

    /// <summary>Label identifying the endpoint type used in HTTP error messages (e.g. <c>"Web UI"</c> or <c>"RPC"</c>).</summary>
    protected virtual string ApiLabel => "Web UI";

    // Kills all processes matching ProcessName, waits for stragglers, then retries once.
    // Returns false (and logs an error) if processes remain after both passes.
    protected async Task<bool> KillAndVerifyAsync(CancellationToken cancellationToken)
    {
        AppConstants.KillProcessesByName(ProcessName, ProcessKillTimeoutMs, ClientName);
        if (!IsRunning()) return true;
        await Task.Delay(ProcessKillRetryDelayMs, cancellationToken).ConfigureAwait(false);
        AppConstants.KillProcessesByName(ProcessName, ProcessKillTimeoutMs, ClientName);
        if (!IsRunning()) return true;
        LogManager.Instance.LogMessage($"Failed to kill all {ClientName} processes - aborting restart", LogLevel.Error);
        return false;
    }

    // Builds the ProcessStartInfo for launching the client executable.
    protected ProcessStartInfo CreateStartInfo() =>
        new ProcessStartInfo(ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(ExePath) ?? string.Empty
        };

    // Classifies and logs an HTTP-related exception using ClientName and ApiLabel. Defaults to
    // Error; best-effort callers (e.g. port verification, where a failure is "undeterminable"
    // rather than a fault) pass LogLevel.Debug so an unreachable client does not raise an Error -
    // matching the Debug-level handling in the other clients' test methods.
    protected void LogHttpException(string methodName, Exception ex, LogLevel level = LogLevel.Error)
    {
        if (ex is TaskCanceledException)
            LogManager.Instance.LogMessage($"{ClientName} {ApiLabel} is not reachable (timed out) - check the URL in Settings ({Url})", level);
        else if (ex is HttpRequestException)
            LogManager.Instance.LogMessage($"Failed to connect to {ClientName} {ApiLabel}: {ex.Message} - check the URL in Settings ({Url})", level);
        else
        {
            LogManager.Instance.LogMessage($"Failed to complete {ClientName} request in {methodName}: {ex.Message}", level);
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

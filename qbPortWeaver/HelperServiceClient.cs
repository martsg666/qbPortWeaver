using System.IO.Pipes;

namespace qbPortWeaver
{
    /// <summary>WARN/ERROR counts the helper service wrote to the shared log file while handling a request.</summary>
    internal readonly record struct HelperResult(int WarnCount, int ErrorCount)
    {
        public static HelperResult Empty => default;
    }

    /// <summary>Sends privileged action requests to the helper Windows service via named pipe.</summary>
    internal static class HelperServiceClient
    {
        internal const string ActionRestart      = "restart";       // Must match HelperPipeServer.ActionRestart in HelperService
        internal const string ActionCycleAdapter = "cycle-adapter"; // Must match HelperPipeServer.ActionCycleAdapter in HelperService

        // Result line keys returned by the helper service. Must match HelperPipeServer.
        internal const string ResultWarnKey  = "warn";
        internal const string ResultErrorKey = "error";

        private const int PipeConnectTimeoutMs = 5000;
        // Bound for awaiting the helper's response. Covers the helper's pathological
        // restart path (stop ~45s + 5s pause + start ~30s = ~80s) with headroom. The
        // helper writes its response only after the action completes.
        private const int PipeReadTimeoutMs    = 120_000;

        // Resolved once per session; the helper validates it against HKCU to reject unauthorized pipe connections.
        private static readonly Lazy<string> _sessionToken = new(() => RegistrySettingsManager.GetOrCreatePipeSessionToken());

        /// <summary>Asks the helper service to stop and restart the Windows service with the given <paramref name="serviceName"/>.</summary>
        internal static Task<HelperResult> SendRestartAsync(string serviceName, CancellationToken ct = default) =>
            SendAsync(ActionRestart, serviceName, ct);

        /// <summary>Asks the helper service to disable and re-enable the named network adapter.</summary>
        internal static Task<HelperResult> SendCycleAdapterAsync(string adapterName, CancellationToken ct = default) =>
            SendAsync(ActionCycleAdapter, adapterName, ct);

        // Sends a pipe-delimited command to the helper service: action|target|sessionToken.
        // Reads back a single result line: warn=N|error=M (helper-side WARN/ERROR counts).
        // Honors <paramref name="ct"/> so app shutdown is not blocked while waiting for the helper.
        private static async Task<HelperResult> SendAsync(string action, string target, CancellationToken ct)
        {
            if (target.Contains('|'))
            {
                LogManager.Instance.LogMessage($"Cannot send '{action}' request: target '{target}' contains an invalid character", LogLevel.Warn);
                return HelperResult.Empty;
            }

            if (string.IsNullOrEmpty(_sessionToken.Value))
            {
                LogManager.Instance.LogMessage($"Cannot send '{action}' request: session token unavailable (registry error)", LogLevel.Warn);
                return HelperResult.Empty;
            }

            try
            {
                await using var pipe = new NamedPipeClientStream(".", AppConstants.HelperServicePipeName, PipeDirection.InOut);
                await pipe.ConnectAsync(PipeConnectTimeoutMs, ct).ConfigureAwait(false);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using       var reader = new StreamReader(pipe, leaveOpen: true);

                await writer.WriteLineAsync($"{action}|{target}|{_sessionToken.Value}".AsMemory(), ct).ConfigureAwait(false);
                LogManager.Instance.LogMessage($"Sent '{action}' request for '{target}'", LogLevel.Info);

                return ParseResult(await ReadResponseAsync(reader, action, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // app shutdown - let the caller observe cancellation
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to reach helper service: {ex.Message}", LogLevel.Warn);
                return HelperResult.Empty;
            }
        }

        // Reads one line from the pipe with two cancellation sources: the caller's <paramref name="ct"/>
        // (shutdown) and an internal timeout. Distinguishes the two so timeout returns null while
        // shutdown propagates as OperationCanceledException.
        private static async Task<string?> ReadResponseAsync(StreamReader reader, string action, CancellationToken ct)
        {
            using var timeoutCts = new CancellationTokenSource(PipeReadTimeoutMs);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                return await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogManager.Instance.LogDebug($"HelperServiceClient.ReadResponseAsync: Helper response timed out for '{action}'");
                return null;
            }
        }

        // Parses "warn=N|error=M" from the helper. Returns Empty if the line is missing or malformed.
        private static HelperResult ParseResult(string? response)
        {
            if (string.IsNullOrWhiteSpace(response)) return HelperResult.Empty;

            int warn = 0, error = 0;
            foreach (var part in response.Split('|'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || !int.TryParse(kv[1], out int value)) continue;
                if      (kv[0] == ResultWarnKey)  warn  = value;
                else if (kv[0] == ResultErrorKey) error = value;
            }
            return new HelperResult(warn, error);
        }
    }
}

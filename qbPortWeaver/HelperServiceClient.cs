using System.IO.Pipes;

namespace qbPortWeaver
{
    /// <summary>Sends privileged action requests to the helper Windows service via named pipe.</summary>
    internal static class HelperServiceClient
    {
        internal const string ActionRestart      = "restart";       // Must match HelperPipeServer.ActionRestart in HelperService
        internal const string ActionCycleAdapter = "cycle-adapter"; // Must match HelperPipeServer.ActionCycleAdapter in HelperService

        private const int PipeConnectTimeoutMs = 5000;

        // Resolved once per session; the helper validates it against HKCU to reject unauthorized pipe connections.
        private static readonly Lazy<string> _sessionToken = new(() => RegistrySettingsManager.GetOrCreatePipeSessionToken());

        /// <summary>Asks the helper service to stop and restart the Windows service with the given <paramref name="serviceName"/>.</summary>
        internal static Task SendRestartAsync(string serviceName) =>
            SendAsync(ActionRestart, serviceName);

        /// <summary>Asks the helper service to disable and re-enable the named network adapter.</summary>
        internal static Task SendCycleAdapterAsync(string adapterName) =>
            SendAsync(ActionCycleAdapter, adapterName);

        // Sends a pipe-delimited command to the helper service: action|target|sessionToken
        private static async Task SendAsync(string action, string target)
        {
            if (target.Contains('|'))
            {
                LogManager.Instance.LogMessage($"Cannot send '{action}' request: target '{target}' contains an invalid character", LogLevel.Warn);
                return;
            }

            try
            {
                using var pipe = new NamedPipeClientStream(".", AppConstants.HelperServicePipeName, PipeDirection.Out);
                await pipe.ConnectAsync(PipeConnectTimeoutMs).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync($"{action}|{target}|{_sessionToken.Value}").ConfigureAwait(false);
                LogManager.Instance.LogMessage($"Sent '{action}' request for '{target}'", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to reach helper service: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}

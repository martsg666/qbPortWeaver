using System.Diagnostics;

namespace qbPortWeaver
{
    /// <summary>Detects PIA (Private Internet Access) connectivity and reads the forwarded port via <c>piactl</c>.</summary>
    public sealed class PiaVpnManager : IVpnManager
    {
        internal static readonly VpnRegistryConfig Config = new(
            RegistrySettingsManager.KeyPiaServiceSearchTerm,
            RegistrySettingsManager.KeyPiaClientProcessName,
            RegistrySettingsManager.KeyPiaAdapterName,
            "PiaVpnManager.GetClientExePath");

        private static string GetPiactlProcessName() => RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyPiactlProcessName);
        private const int    ProcessTimeoutMs = 5000;

        // Cached path for piactl; null = not found, string.Empty = not yet resolved.
        // Install paths never change at runtime so we resolve once and reuse.
        private static string? _piactlPathCache = string.Empty;

        /// <inheritdoc />
        public string ProviderName => RegistrySettingsManager.VpnProviderPia;

        /// <inheritdoc />
        public bool IsVpnConnected()
        {
            try
            {
                string? output = RunPiactl("get connectionstate");
                if (output is null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.IsVpnConnected: piactl returned no output");
                    return false;
                }

                bool isConnected = output.Equals("Connected", StringComparison.OrdinalIgnoreCase);

                LogManager.Instance.LogDebug(isConnected
                    ? "PiaVpnManager.IsVpnConnected: PIA VPN is connected"
                    : $"PiaVpnManager.IsVpnConnected: PIA VPN is not connected (state: {output})");

                return isConnected;
            }
            catch (Exception ex)
            {
                return LogManager.LogDebugFalse($"PiaVpnManager.IsVpnConnected: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public Task<int?> GetVpnPortAsync() => Task.FromResult(GetVpnPortCore());

        /// <inheritdoc />
        public string? GetRecoveryTarget() => ProviderName;

        /// <inheritdoc />
        public string GetRecoveryAction() => HelperServiceClient.ActionRestart;

        /// <inheritdoc />
        public bool IsAdapterMatch(string interfaceName)
            => interfaceName.Contains(Config.GetAdapterName(), StringComparison.OrdinalIgnoreCase);

        private static int? GetVpnPortCore()
        {
            try
            {
                string? output = RunPiactl("get portforward");
                if (output is null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.GetVpnPortCore: piactl returned no output");
                    return null;
                }

                if (int.TryParse(output, out int port) && port > 0)
                {
                    LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPortCore: Found port {port}");
                    return port;
                }

                LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPortCore: Failed to parse port from piactl output: {output}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPortCore: {ex.Message}");
                return null;
            }
        }

        // Runs a piactl command and returns the trimmed stdout output
        private static string? RunPiactl(string arguments)
        {
            try
            {
                string? piactlPath = GetPiactlPath();
                if (piactlPath is null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: Failed to resolve piactl path");
                    return null;
                }

                var startInfo = AppConstants.CreateHiddenStartInfo(piactlPath, arguments);
                startInfo.RedirectStandardOutput = true;

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: Failed to start piactl process");
                    return null;
                }

                if (!process.WaitForExit(ProcessTimeoutMs))
                {
                    // Cleanup only - no new process follows, so KillProcess's retry wait is not needed here.
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* already exited between timeout and Kill() */ }
                    LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: piactl timed out and was killed");
                    return null;
                }

                // piactl output is always tiny (a few characters); stdout buffer overflow is not a concern,
                // so synchronous ReadToEnd() after WaitForExit() is safe and simpler than async.
                string output = process.StandardOutput.ReadToEnd().Trim();

                LogManager.Instance.LogDebug($"PiaVpnManager.RunPiactl: '{arguments}' returned: {output}");
                return output;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PiaVpnManager.RunPiactl: Failed to run '{arguments}': {ex.Message}");
                return null;
            }
        }

        private static string? GetPiactlPath() => AppConstants.FindExeInServiceDirectory(ref _piactlPathCache, GetPiactlProcessName() + ".exe", Config.FindServiceName, "PiaVpnManager.GetPiactlPath");
    }
}

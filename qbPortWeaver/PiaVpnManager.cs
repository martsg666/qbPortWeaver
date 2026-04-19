using System.Diagnostics;

namespace qbPortWeaver
{
    /// <summary>Detects PIA (Private Internet Access) connectivity and reads the forwarded port via <c>piactl</c>.</summary>
    public sealed class PiaVpnManager : IVpnManager
    {
        private const string ServiceSearchTerm = "PrivateInternetAccess";
        private const string PiactlFileName       = "piactl.exe";
        internal const string ClientProcessName   = "pia-client";
        private const int    ProcessTimeoutMs = 5000;

        // Cached paths; null = not found, string.Empty = not yet resolved.
        // Install paths never change at runtime so we resolve once and reuse.
        private static string? _piactlPathCache    = string.Empty;
        private static string? _clientExePathCache = string.Empty;

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
            => interfaceName.Contains("PIA", StringComparison.OrdinalIgnoreCase);

        internal static string? FindServiceName() => AppConstants.FindServiceName(ServiceSearchTerm);

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

        // Resolves a PIA exe path from the service executable directory.
        private static string? ResolveExePath(ref string? cache, string exeFileName, string logLabel)
        {
            if (cache != string.Empty) return cache;
            try
            {
                string? serviceName = FindServiceName();
                string? serviceDir  = serviceName is not null ? AppConstants.GetServiceExeDirectory(serviceName) : null;
                if (serviceDir is null)
                {
                    LogManager.Instance.LogDebug($"PiaVpnManager.{logLabel}: PIA service executable directory not found");
                    return cache = null;
                }

                string exePath = Path.Combine(serviceDir, exeFileName);
                if (!File.Exists(exePath))
                {
                    LogManager.Instance.LogDebug($"PiaVpnManager.{logLabel}: {exeFileName} not found at: {exePath}");
                    return cache = null;
                }

                LogManager.Instance.LogDebug($"PiaVpnManager.{logLabel}: Found {exeFileName} at: {exePath}");
                return cache = exePath;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PiaVpnManager.{logLabel}: {ex.Message}");
                return null; // transient error - don't cache, retry next cycle
            }
        }

        private static string? GetPiactlPath()    => ResolveExePath(ref _piactlPathCache,    PiactlFileName,              "GetPiactlPath");
        internal static string? GetClientExePath() => ResolveExePath(ref _clientExePathCache, ClientProcessName + ".exe",  "GetClientExePath");
    }
}

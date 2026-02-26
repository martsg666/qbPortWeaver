using System.Diagnostics;

namespace qbPortWeaver
{
    public enum SyncState { OK, VpnDisconnected, Error }

    public sealed record TrayStatus(SyncState State, int? Port, string Message);

    public sealed class PortSyncService
    {
        // Event raised when a sync cycle completes (success or failure)
        public event Action<TrayStatus>? SyncCompleted;

        // Event raised when qBittorrent's network interface does not match the configured VPN provider
        public event Action<string>? InterfaceMismatchDetected;

        // All values read from the registry for a single sync cycle
        private sealed record AppConfig(
            string VpnProvider,
            int UpdateInterval,
            string QBittorrentURL,
            string QBittorrentUserName,
            string QBittorrentPassword,
            string QBittorrentExePath,
            string QBittorrentProcessName,
            bool RestartQBittorrent,
            bool ForceStartQBittorrent,
            int DefaultPort,
            bool WarnOnInterfaceMismatch,
            string PostUpdateCmd
        );

        // Groups qBittorrent behaviour settings passed to EnsureRunningAndUpdatePortAsync
        private sealed record SyncConfig(
            bool ForceStart,
            bool Restart,
            string PostUpdateCmd,
            string? VpnProviderName,
            bool WarnOnInterfaceMismatch
        );

        // Main port update logic, returns update interval in seconds
        public async Task<int> RunAsync()
        {
            // Initialize status with default values. This is written to the status file at the end of the method (in finally)
            // so it captures the final state even if an exception occurs.
            // The RunCoreAsync method updates this dictionary as it progresses.
            var status = new Dictionary<string, object?>
            {
                ["appVersion"] = AppConstants.APP_VERSION,
                ["timestamp"] = DateTimeOffset.Now,
                ["vpnProvider"] = null,
                ["vpnConnected"] = false,
                ["vpnPort"] = null,
                ["qBittorrentRunning"] = false,
                ["qBittorrentPreviousPort"] = null,
                ["qBittorrentPort"] = null,
                ["portChanged"] = false,
                ["updateIntervalSeconds"] = AppConstants.DEFAULT_UPDATE_INTERVAL_SECONDS,
                ["status"] = "error",
                ["message"] = null
            };

            try
            {
                return await RunCoreAsync(status);
            }
            catch (Exception ex)
            {
                SetCompleted(status, false, $"An unexpected error occurred: {ex.Message}");
                return AppConstants.DEFAULT_UPDATE_INTERVAL_SECONDS;
            }
            finally
            {
                StatusManager.Write(status);

                bool success      = status["status"]       as string == "success";
                bool vpnConnected = status["vpnConnected"] is true;
                int? port         = status["qBittorrentPort"] as int?;
                string message    = status["message"] as string ?? string.Empty;

                SyncState state;
                if (!vpnConnected)     state = SyncState.VpnDisconnected;
                else if (success)      state = SyncState.OK;
                else                   state = SyncState.Error;

                SyncCompleted?.Invoke(new TrayStatus(state, port, message));
            }
        }

        // Core logic separated so the outer method handles status writing via finally
        private async Task<int> RunCoreAsync(Dictionary<string, object?> status)
        {
            LogManager.Instance.LogMessage($"Starting {AppConstants.APP_NAME} {AppConstants.APP_VERSION}", "INFO");

            // Set debug mode as early as possible (reads fresh from registry each loop)
            bool.TryParse(RegistrySettingsManager.GetValue("extra", "debugMode"), out bool debugMode);
            LogManager.Instance.DebugMode = debugMode;

            var cfg = ReadConfig();
            status["vpnProvider"] = cfg.VpnProvider;
            status["updateIntervalSeconds"] = cfg.UpdateInterval;

            // Instantiate VPN manager based on configured provider
            IVPNManager vpnManager;
            if (cfg.VpnProvider.Equals("PIA", StringComparison.OrdinalIgnoreCase))
            {
                vpnManager = new PIAVPNManager();
            }
            else
            {
                if (!cfg.VpnProvider.Equals("ProtonVPN", StringComparison.OrdinalIgnoreCase))
                    LogManager.Instance.LogMessage($"Unknown VPN provider '{cfg.VpnProvider}', defaulting to ProtonVPN", "WARN");
                vpnManager = new ProtonVPNManager(AppConstants.GetProtonVPNLogFilePath());
            }

            int targetPort;
            string? vpnProviderName;
            bool warnOnInterfaceMismatch;

            if (!vpnManager.IsVPNConnected())
            {
                if (cfg.DefaultPort == 0)
                {
                    status["status"]  = "skipped";
                    status["message"] = $"{vpnManager.ProviderName} is not connected";
                    LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is not connected, defaultPort is 0 — skipping port update", "INFO");
                    return cfg.UpdateInterval;
                }
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is not connected, applying default port {cfg.DefaultPort}", "INFO");
                targetPort            = cfg.DefaultPort;
                vpnProviderName       = null;
                warnOnInterfaceMismatch = false;
            }
            else
            {
                status["vpnConnected"] = true;
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is connected", "INFO");

                int? vpnPort = vpnManager.GetVPNPort();
                if (!vpnPort.HasValue)
                {
                    SetCompleted(status, false, $"Could not determine {vpnManager.ProviderName} port");
                    return cfg.UpdateInterval;
                }
                status["vpnPort"] = vpnPort.Value;
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} port found: {vpnPort.Value}", "INFO");
                targetPort            = vpnPort.Value;
                vpnProviderName       = vpnManager.ProviderName;
                warnOnInterfaceMismatch = cfg.WarnOnInterfaceMismatch;
            }

            using var qBittorrentMgr = new qBittorrentManager(
                cfg.QBittorrentURL, cfg.QBittorrentUserName, cfg.QBittorrentPassword,
                cfg.QBittorrentProcessName, cfg.QBittorrentExePath);

            await EnsureRunningAndUpdatePortAsync(qBittorrentMgr, targetPort,
                new SyncConfig(
                    ForceStart:              cfg.ForceStartQBittorrent,
                    Restart:                 cfg.RestartQBittorrent,
                    PostUpdateCmd:           cfg.PostUpdateCmd,
                    VpnProviderName:         vpnProviderName,
                    WarnOnInterfaceMismatch: warnOnInterfaceMismatch),
                status);

            return cfg.UpdateInterval;
        }

        // Reads all configuration values from the registry into a single AppConfig record
        private static AppConfig ReadConfig()
        {
            if (!int.TryParse(RegistrySettingsManager.GetValue("general", "updateIntervalSeconds"), out int updateInterval) || updateInterval < 10)
                updateInterval = AppConstants.DEFAULT_UPDATE_INTERVAL_SECONDS;
            if (!bool.TryParse(RegistrySettingsManager.GetValue("qBittorrent", "restartqBittorrent"), out bool restartQBittorrent))
                restartQBittorrent = true;
            if (!bool.TryParse(RegistrySettingsManager.GetValue("qBittorrent", "forceStartqBittorrent"), out bool forceStartQBittorrent))
                forceStartQBittorrent = false;
            if (!int.TryParse(RegistrySettingsManager.GetValue("qBittorrent", "defaultPort"), out int defaultPort))
                defaultPort = 0;
            if (!bool.TryParse(RegistrySettingsManager.GetValue("qBittorrent", "warnOnInterfaceMismatch"), out bool warnOnInterfaceMismatch))
                warnOnInterfaceMismatch = true;

            return new AppConfig(
                VpnProvider:            RegistrySettingsManager.GetValue("general",     "vpnProvider"),
                UpdateInterval:         updateInterval,
                QBittorrentURL:         RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentURL"),
                QBittorrentUserName:    RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentUserName"),
                QBittorrentPassword:    RegistrySettingsManager.GetPassword(),
                QBittorrentExePath:     RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentExePath"),
                QBittorrentProcessName: RegistrySettingsManager.GetValue("qBittorrent", "qBittorrentProcessName"),
                RestartQBittorrent:     restartQBittorrent,
                ForceStartQBittorrent:  forceStartQBittorrent,
                DefaultPort:            defaultPort,
                WarnOnInterfaceMismatch: warnOnInterfaceMismatch,
                PostUpdateCmd:          RegistrySettingsManager.GetValue("extra",        "postUpdateCmd")
            );
        }

        // Ensures qBittorrent is running, then updates its port if it differs from the target port
        private async Task EnsureRunningAndUpdatePortAsync(qBittorrentManager qBittorrentMgr, int targetPort, SyncConfig config, Dictionary<string, object?> status)
        {
            if (!await EnsureQBittorrentRunningAsync(qBittorrentMgr, config, status))
                return;
            status["qBittorrentRunning"] = true;

            // Get current preferences (listening port and network interface) in a single request
            var (currentPort, currentInterfaceName) = await qBittorrentMgr.GetPreferencesAsync();
            if (!currentPort.HasValue)
            {
                SetCompleted(status, false, "Could not determine qBittorrent port");
                return;
            }
            status["qBittorrentPreviousPort"] = currentPort.Value;
            LogManager.Instance.LogMessage($"qBittorrent port found: {currentPort.Value}", "INFO");

            // Warn if qBittorrent's network interface doesn't match the configured VPN provider
            if (config.VpnProviderName != null && config.WarnOnInterfaceMismatch)
                CheckInterfaceMatch(currentInterfaceName, config.VpnProviderName);

            if (currentPort.Value == targetPort)
            {
                status["qBittorrentPort"] = currentPort.Value;
                LogManager.Instance.LogMessage("Ports match, no update needed", "INFO");
            }
            else
            {
                if (!await ApplyPortUpdateAsync(qBittorrentMgr, targetPort, config, status))
                    return;
            }

            SetCompleted(status, true, "Completed successfully");
        }

        // Returns true if qBittorrent is running (or was successfully force-started), false otherwise
        private static async Task<bool> EnsureQBittorrentRunningAsync(qBittorrentManager qBittorrentMgr, SyncConfig config, Dictionary<string, object?> status)
        {
            if (qBittorrentMgr.IsRunning())
            {
                LogManager.Instance.LogMessage("qBittorrent is running", "INFO");
                return true;
            }

            if (!config.ForceStart)
            {
                SetCompleted(status, false, "qBittorrent is not running");
                return false;
            }

            LogManager.Instance.LogMessage("qBittorrent is not running, attempting to force start", "INFO");
            if (!await qBittorrentMgr.ForceStartAsync())
            {
                SetCompleted(status, false, "Failed to force start qBittorrent");
                return false;
            }
            LogManager.Instance.LogMessage("Successfully force started qBittorrent", "INFO");
            return true;
        }

        // Checks if qBittorrent's network interface matches the expected VPN provider and logs a warning if not
        private void CheckInterfaceMatch(string? interfaceName, string vpnProviderName)
        {
            if (interfaceName == null)
            {
                LogManager.Instance.LogDebug("PortSyncService.CheckInterfaceMatch: current_interface_name not returned by qBittorrent, skipping check");
                return;
            }

            if (interfaceName.Length == 0)
            {
                LogManager.Instance.LogMessage("qBittorrent is bound to all network interfaces — traffic may leak outside the VPN", "WARN");
                InterfaceMismatchDetected?.Invoke("No VPN interface bound — traffic may leak.");
                return;
            }

            bool isMatch;

            if (vpnProviderName.Equals("PIA", StringComparison.OrdinalIgnoreCase))
            {
                isMatch = interfaceName.Contains("Private Internet Access", StringComparison.OrdinalIgnoreCase) ||
                          interfaceName.Contains("PIA", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                isMatch = interfaceName.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase);
            }

            if (!isMatch)
            {
                LogManager.Instance.LogMessage($"qBittorrent network interface '{interfaceName}' does not match '{vpnProviderName}'", "WARN");
                InterfaceMismatchDetected?.Invoke($"Interface mismatch — '{interfaceName}' is not a {vpnProviderName} adapter.");
            }
            else
            {
                LogManager.Instance.LogMessage($"qBittorrent network interface '{interfaceName}' matches the configured VPN provider '{vpnProviderName}'", "INFO");
            }
        }

        // Sets the listening port, optionally restarts qBittorrent and runs the post-update command.
        // Returns false if any step fails.
        private static async Task<bool> ApplyPortUpdateAsync(qBittorrentManager qBittorrentMgr, int targetPort, SyncConfig config, Dictionary<string, object?> status)
        {
            LogManager.Instance.LogMessage($"Ports do not match, updating qBittorrent port to: {targetPort}", "INFO");
            if (!await qBittorrentMgr.SetListeningPortAsync(targetPort))
            {
                SetCompleted(status, false, $"Failed to set qBittorrent port to: {targetPort}");
                return false;
            }
            LogManager.Instance.LogMessage($"Successfully set qBittorrent port to: {targetPort}", "INFO");

            status["qBittorrentPort"] = targetPort;
            status["portChanged"] = true;

            if (config.Restart)
            {
                LogManager.Instance.LogMessage("Attempting to restart qBittorrent", "INFO");
                if (!await qBittorrentMgr.RestartAsync())
                {
                    SetCompleted(status, false, "Failed to restart qBittorrent");
                    return false;
                }
                LogManager.Instance.LogMessage("Successfully restarted qBittorrent", "INFO");
            }

            // Run post-update command if configured (fire-and-forget)
            if (!string.IsNullOrWhiteSpace(config.PostUpdateCmd))
                RunPostUpdateCommand(config.PostUpdateCmd);

            return true;
        }

        // Launches the post-update shell command (fire-and-forget)
        private static void RunPostUpdateCommand(string cmd)
        {
            LogManager.Instance.LogMessage($"Running post-update command: {cmd}", "INFO");
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/C \"{cmd}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi)?.Dispose();
                LogManager.Instance.LogMessage("Successfully launched post-update command", "INFO");
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Post-update command failed: {ex.Message}", "ERROR");
            }
        }

        // Sets the completion status and logs the message
        private static void SetCompleted(Dictionary<string, object?> status, bool success, string message)
        {
            status["status"] = success ? "success" : "error";
            status["message"] = message;
            LogManager.Instance.LogMessage(message, success ? "INFO" : "ERROR");
        }
    }
}

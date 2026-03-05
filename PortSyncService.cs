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
            string NatPmpAdapterName,
            int UpdateInterval,
            string QBittorrentUrl,
            string QBittorrentUserName,
            string QBittorrentPassword,
            string QBittorrentExePath,
            string QBittorrentProcessName,
            bool RestartQBittorrent,
            bool ForceStartQBittorrent,
            int DefaultPort,
            bool WarnOnInterfaceMismatch,
            bool RestartOnDisconnect,
            string PostUpdateCommand
        );

        // Groups qBittorrent behaviour settings passed to EnsureRunningAndUpdatePortAsync
        private sealed record SyncConfig(
            bool ForceStart,
            bool Restart,
            string PostUpdateCommand,
            string? VpnProviderName,
            bool WarnOnInterfaceMismatch,
            bool RestartOnDisconnect
        );

        // Main port update logic, returns update interval in seconds
        public async Task<int> RunAsync(CancellationToken cancellationToken = default)
        {
            // Initialize status with default values. This is written to the status file at the end of the method (in finally)
            // so it captures the final state even if an exception occurs.
            // The RunCoreAsync method updates this dictionary as it progresses.
            var status = new Dictionary<string, object?>
            {
                [StatusKeys.AppVersion]              = AppConstants.AppVersion,
                [StatusKeys.Timestamp]               = DateTimeOffset.Now,
                [StatusKeys.VpnProvider]             = null,
                [StatusKeys.VpnConnected]            = false,
                [StatusKeys.VpnPort]                 = null,
                [StatusKeys.QBittorrentRunning]      = false,
                [StatusKeys.QBittorrentPreviousPort] = null,
                [StatusKeys.QBittorrentPort]         = null,
                [StatusKeys.PortChanged]             = false,
                [StatusKeys.UpdateIntervalSeconds]   = AppConstants.DefaultUpdateIntervalSeconds,
                [StatusKeys.Status]                  = "error",
                [StatusKeys.Message]                 = null
            };

            try
            {
                return await RunCoreAsync(status, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetCompleted(status, false, $"An unexpected error occurred: {ex.Message}");
                return AppConstants.DefaultUpdateIntervalSeconds;
            }
            finally
            {
                StatusManager.Write(status);

                bool success      = status[StatusKeys.Status]          as string == "success";
                bool vpnConnected = status[StatusKeys.VpnConnected]   is true;
                int? port         = status[StatusKeys.QBittorrentPort] as int?;
                string message    = status[StatusKeys.Message]         as string ?? string.Empty;

                SyncState state;
                if (!vpnConnected)     state = SyncState.VpnDisconnected;
                else if (success)      state = SyncState.OK;
                else                   state = SyncState.Error;

                SyncCompleted?.Invoke(new TrayStatus(state, port, message));
            }
        }

        // Core logic separated so the outer method handles status writing via finally
        private async Task<int> RunCoreAsync(Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            LogManager.Instance.LogMessage("Sync cycle started", LogLevel.Info);

            // Set debug mode as early as possible (reads fresh from registry each loop)
            bool.TryParse(RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode), out bool debugMode);
            LogManager.Instance.DebugMode = debugMode;

            var cfg = ReadConfig();
            status[StatusKeys.VpnProvider]           = cfg.VpnProvider;
            status[StatusKeys.UpdateIntervalSeconds] = cfg.UpdateInterval;

            // Instantiate VPN manager based on configured provider
            IVpnManager? vpnManager = await CreateVpnManager(cfg, status);
            if (vpnManager is null)
                return cfg.UpdateInterval;

            int targetPort;
            string? vpnProviderName;
            bool warnOnInterfaceMismatch;

            if (!vpnManager.IsVpnConnected())
            {
                if (cfg.DefaultPort == 0)
                {
                    status[StatusKeys.Status]  = "skipped";
                    status[StatusKeys.Message] = $"{vpnManager.ProviderName} is not connected";
                    LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is not connected, default port is 0 — skipping port update", LogLevel.Info);
                    return cfg.UpdateInterval;
                }
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is not connected, applying default port {cfg.DefaultPort}", LogLevel.Info);
                targetPort              = cfg.DefaultPort;
                vpnProviderName         = null;
                warnOnInterfaceMismatch = false;
            }
            else
            {
                status[StatusKeys.VpnConnected] = true;
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is connected", LogLevel.Info);

                int? vpnPort = vpnManager.GetVpnPort();
                if (!vpnPort.HasValue)
                {
                    SetCompleted(status, false, $"Failed to determine {vpnManager.ProviderName} port");
                    return cfg.UpdateInterval;
                }
                status[StatusKeys.VpnPort] = vpnPort.Value;
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} port found: {vpnPort.Value}", LogLevel.Info);
                targetPort              = vpnPort.Value;
                vpnProviderName         = vpnManager.ProviderName;
                warnOnInterfaceMismatch = cfg.WarnOnInterfaceMismatch;
            }

            using var manager = new QBittorrentManager(
                cfg.QBittorrentUrl, cfg.QBittorrentUserName, cfg.QBittorrentPassword,
                cfg.QBittorrentProcessName, cfg.QBittorrentExePath);

            await EnsureRunningAndUpdatePortAsync(manager, targetPort,
                new SyncConfig(
                    ForceStart:              cfg.ForceStartQBittorrent,
                    Restart:                 cfg.RestartQBittorrent,
                    PostUpdateCommand:       cfg.PostUpdateCommand,
                    VpnProviderName:         vpnProviderName,
                    WarnOnInterfaceMismatch: warnOnInterfaceMismatch,
                    RestartOnDisconnect:     cfg.RestartOnDisconnect),
                status,
                cancellationToken);

            return cfg.UpdateInterval;
        }

        // Reads all configuration values from the registry into a single AppConfig record
        private static AppConfig ReadConfig()
        {
            int updateInterval = RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
            if (updateInterval < 10) updateInterval = AppConstants.DefaultUpdateIntervalSeconds;

            bool restartQBittorrent      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent);
            bool forceStartQBittorrent   = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent);
            int  defaultPort             = RegistrySettingsManager.GetInt (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort);
            bool warnOnInterfaceMismatch = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch);
            bool restartOnDisconnect     = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect);

            return new AppConfig(
                VpnProvider:            RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral,     RegistrySettingsManager.KeyVpnProvider),
                NatPmpAdapterName:      RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral,     RegistrySettingsManager.KeyNatPmpAdapterName),
                UpdateInterval:         updateInterval,
                QBittorrentUrl:         RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl),
                QBittorrentUserName:    RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName),
                QBittorrentPassword:    RegistrySettingsManager.GetPassword(),
                QBittorrentExePath:     RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath),
                QBittorrentProcessName: RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName),
                RestartQBittorrent:     restartQBittorrent,
                ForceStartQBittorrent:  forceStartQBittorrent,
                DefaultPort:            defaultPort,
                WarnOnInterfaceMismatch: warnOnInterfaceMismatch,
                RestartOnDisconnect:    restartOnDisconnect,
                PostUpdateCommand:      RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra,        RegistrySettingsManager.KeyPostUpdateCmd)
            );
        }

        // Instantiates the appropriate VPN manager for the configured provider.
        // Returns null (with status already set) if the provider cannot be initialised.
        private static async Task<IVpnManager?> CreateVpnManager(AppConfig cfg, Dictionary<string, object?> status)
        {
            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase))
                return new PiaVpnManager();

            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
            {
                var adapters = await NatPmpManager.DiscoverAdapters().ConfigureAwait(false);
                if (adapters.Count == 0)
                {
                    SetCompleted(status, false, "No NAT-PMP capable adapters found");
                    return null;
                }
                // Use the configured adapter if set; otherwise fall back to the first connected one
                if (!string.IsNullOrWhiteSpace(cfg.NatPmpAdapterName))
                {
                    var configured = adapters.FirstOrDefault(a => a.ProviderName.Equals(cfg.NatPmpAdapterName, StringComparison.OrdinalIgnoreCase));
                    if (configured is null)
                        LogManager.Instance.LogMessage($"Configured NAT-PMP adapter '{cfg.NatPmpAdapterName}' not found, falling back to first available", LogLevel.Warn);
                    return configured ?? adapters.FirstOrDefault(a => a.IsVpnConnected()) ?? adapters[0];
                }
                return adapters.FirstOrDefault(a => a.IsVpnConnected()) ?? adapters[0];
            }

            if (!cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase))
                LogManager.Instance.LogMessage($"Unknown VPN provider '{cfg.VpnProvider}', defaulting to ProtonVPN", LogLevel.Warn);
            return new ProtonVPNManager(AppConstants.GetProtonVPNLogFilePath());
        }

        // Ensures qBittorrent is running, then updates its port if it differs from the target port
        private async Task EnsureRunningAndUpdatePortAsync(QBittorrentManager manager, int targetPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            if (!await EnsureQBittorrentRunningAsync(manager, config, status, cancellationToken))
                return;
            status[StatusKeys.QBittorrentRunning] = true;

            // Get current preferences (listening port and network interface) in a single request
            var (currentPort, currentInterfaceName) = await manager.GetPreferencesAsync();
            if (!currentPort.HasValue)
            {
                SetCompleted(status, false, "Failed to determine qBittorrent port");
                return;
            }
            status[StatusKeys.QBittorrentPreviousPort] = currentPort.Value;
            LogManager.Instance.LogMessage($"qBittorrent port found: {currentPort.Value}", LogLevel.Info);

            // Warn if qBittorrent's network interface doesn't match the configured VPN provider
            if (config.VpnProviderName != null && config.WarnOnInterfaceMismatch)
                CheckInterfaceMatch(currentInterfaceName, config.VpnProviderName);

            if (currentPort.Value == targetPort)
            {
                status[StatusKeys.QBittorrentPort] = currentPort.Value;
                LogManager.Instance.LogMessage("Ports match, no update needed", LogLevel.Info);
            }
            else
            {
                if (!await ApplyPortUpdateAsync(manager, targetPort, config, status, cancellationToken))
                    return;
            }

            // Check connection status and restart if offline — skip if a restart was already performed
            // by ApplyPortUpdateAsync (port changed + restart enabled) to avoid a redundant cycle.
            bool alreadyRestarted = config.Restart && status[StatusKeys.PortChanged] is true;
            if (config.RestartOnDisconnect && !alreadyRestarted)
                await CheckAndRestartIfDisconnectedAsync(manager, cancellationToken);

            SetCompleted(status, true, "Completed successfully");
        }

        // Returns true if qBittorrent is running (or was successfully force-started), false otherwise
        private static async Task<bool> EnsureQBittorrentRunningAsync(QBittorrentManager manager, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            if (manager.IsRunning())
            {
                LogManager.Instance.LogMessage("qBittorrent is running", LogLevel.Info);
                return true;
            }

            if (!config.ForceStart)
            {
                SetCompleted(status, false, "qBittorrent is not running");
                return false;
            }

            LogManager.Instance.LogMessage("qBittorrent is not running, attempting to force start", LogLevel.Info);
            if (!await manager.ForceStartAsync(cancellationToken))
            {
                SetCompleted(status, false, "Failed to force start qBittorrent");
                return false;
            }
            LogManager.Instance.LogMessage("Successfully force started qBittorrent", LogLevel.Info);
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
                LogManager.Instance.LogMessage("qBittorrent is bound to all network interfaces — traffic may leak outside the VPN", LogLevel.Warn);
                InterfaceMismatchDetected?.Invoke("No VPN interface bound — traffic may leak.");
                return;
            }

            bool isMatch;

            if (vpnProviderName.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase))
            {
                isMatch = interfaceName.Contains("Private Internet Access", StringComparison.OrdinalIgnoreCase) ||
                          interfaceName.Contains("PIA", StringComparison.OrdinalIgnoreCase);
            }
            else if (vpnProviderName.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase))
            {
                isMatch = interfaceName.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // NAT-PMP: vpnProviderName is the adapter description configured by the user.
                // qBittorrent may return either the adapter name or description, so check both directions.
                isMatch = interfaceName.Contains(vpnProviderName, StringComparison.OrdinalIgnoreCase) ||
                          vpnProviderName.Contains(interfaceName, StringComparison.OrdinalIgnoreCase);
            }

            if (!isMatch)
            {
                LogManager.Instance.LogMessage($"qBittorrent network interface '{interfaceName}' does not match '{vpnProviderName}'", LogLevel.Warn);
                InterfaceMismatchDetected?.Invoke($"Interface mismatch — '{interfaceName}' is not a {vpnProviderName} adapter.");
            }
            else
            {
                LogManager.Instance.LogMessage($"qBittorrent network interface '{interfaceName}' matches the configured VPN provider '{vpnProviderName}'", LogLevel.Info);
            }
        }

        // Sets the listening port, optionally restarts qBittorrent and runs the post-update command.
        // Returns false if any step fails.
        private static async Task<bool> ApplyPortUpdateAsync(QBittorrentManager manager, int targetPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            LogManager.Instance.LogMessage($"Ports do not match, updating qBittorrent port to {targetPort}", LogLevel.Info);
            if (!await manager.SetListeningPortAsync(targetPort))
            {
                SetCompleted(status, false, $"Failed to set qBittorrent port to {targetPort}");
                return false;
            }
            LogManager.Instance.LogMessage($"Successfully set qBittorrent port to {targetPort}", LogLevel.Info);

            status[StatusKeys.QBittorrentPort] = targetPort;
            status[StatusKeys.PortChanged]     = true;

            if (config.Restart)
            {
                LogManager.Instance.LogMessage("Attempting to restart qBittorrent", LogLevel.Info);
                if (!await manager.RestartAsync(cancellationToken))
                {
                    SetCompleted(status, false, "Failed to restart qBittorrent");
                    return false;
                }
                LogManager.Instance.LogMessage("Successfully restarted qBittorrent", LogLevel.Info);
            }

            // Run post-update command if configured (fire-and-forget)
            if (!string.IsNullOrWhiteSpace(config.PostUpdateCommand))
                RunPostUpdateCommand(config.PostUpdateCommand);

            return true;
        }

        // Launches the post-update shell command (fire-and-forget)
        private static void RunPostUpdateCommand(string cmd)
        {
            LogManager.Instance.LogMessage($"Running post-update command: {cmd}", LogLevel.Info);
            try
            {
                string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var startInfo = new ProcessStartInfo(cmdExe, $"/C \"{cmd}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true
                };
                Process.Start(startInfo)?.Dispose();
                LogManager.Instance.LogMessage("Successfully launched post-update command", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Post-update command failed: {ex.Message}", LogLevel.Error);
            }
        }

        // Polls /api/v2/transfer/info and restarts qBittorrent if connection_status is "disconnected"
        private static async Task CheckAndRestartIfDisconnectedAsync(QBittorrentManager manager, CancellationToken cancellationToken)
        {
            string? connectionStatus = await manager.GetConnectionStatusAsync();
            if (connectionStatus == null)
                return;

            LogManager.Instance.LogMessage($"qBittorrent connection status: {connectionStatus}", LogLevel.Info);

            if (!connectionStatus.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
                return;

            LogManager.Instance.LogMessage("qBittorrent connection status is disconnected — restarting", LogLevel.Warn);
            if (!await manager.RestartAsync(cancellationToken))
                LogManager.Instance.LogMessage("Failed to restart qBittorrent after connection disconnect", LogLevel.Error);
            else
                LogManager.Instance.LogMessage("Successfully restarted qBittorrent after connection disconnect", LogLevel.Info);
        }

        // Sets the completion status and logs the message
        private static void SetCompleted(Dictionary<string, object?> status, bool success, string message)
        {
            status[StatusKeys.Status]  = success ? "success" : "error";
            status[StatusKeys.Message] = message;
            LogManager.Instance.LogMessage(message, success ? LogLevel.Info : LogLevel.Error);
        }

        // Compile-time–safe keys for the status dictionary written to the JSON status file
        private static class StatusKeys
        {
            public const string AppVersion              = "appVersion";
            public const string Timestamp               = "timestamp";
            public const string VpnProvider             = "vpnProvider";
            public const string VpnConnected            = "vpnConnected";
            public const string VpnPort                 = "vpnPort";
            public const string QBittorrentRunning      = "qBittorrentRunning";
            public const string QBittorrentPreviousPort = "qBittorrentPreviousPort";
            public const string QBittorrentPort         = "qBittorrentPort";
            public const string PortChanged             = "portChanged";
            public const string UpdateIntervalSeconds   = "updateIntervalSeconds";
            public const string Status                  = "status";
            public const string Message                 = "message";
        }
    }
}

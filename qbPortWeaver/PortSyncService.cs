using System.Diagnostics;

namespace qbPortWeaver
{
    public enum SyncState { Synced, VpnDisconnected, Error }

    public sealed record TrayStatus(SyncState State, int? Port, string Message);

    public sealed class PortSyncService
    {
        // qBittorrent API value returned by /api/v2/transfer/info when the client has no active connections
        private const string QBittorrentDisconnectedStatus = "disconnected";

        // Event raised when a sync cycle completes (success or failure)
        public event Action<TrayStatus>? SyncCompleted;

        // Event raised when qBittorrent's network interface does not match the configured VPN provider
        public event Action<string>? InterfaceMismatchDetected;

        // Consecutive sync cycles in which the VPN was disconnected or port detection failed.
        // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
        private int _consecutiveFailedCycles;

        // Fallback for when TryCreateForAdapter cannot reach the configured adapter (e.g. VPN is
        // between disconnect and reconnect) - returned so IsVpnConnected() reports false and
        // RunCoreAsync handles disconnection gracefully. Cleared when the adapter name changes in settings.
        // Thread-safety: only accessed inside RunCoreAsync, serialised by MainForm._updateSemaphore.
        private NatPmpManager? _lastKnownNatPmpManager;

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
            string PostUpdateCommand,
            bool VpnAutoRecoveryEnabled,
            int VpnAutoRecoveryTriggerCycles
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

        // Compile-time–safe keys and values for the status dictionary written to the JSON status file
        private static class StatusKeys
        {
            // Keys
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

            // Values for the Status key - "skipped" means VPN disconnected with no default port configured (cycle is a no-op)
            public const string StatusSuccess = "success";
            public const string StatusError   = "error";
            public const string StatusSkipped = "skipped";
        }

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
                [StatusKeys.Status]                  = StatusKeys.StatusError,
                [StatusKeys.Message]                 = null
            };

            try
            {
                return await RunCoreAsync(status, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetCompleted(status, false, $"An unexpected error occurred: {ex.Message}");
                return AppConstants.DefaultUpdateIntervalSeconds;
            }
            finally
            {
                StatusManager.Write(status);

                bool success      = status[StatusKeys.Status]          as string == StatusKeys.StatusSuccess;
                bool vpnConnected = status[StatusKeys.VpnConnected]   is true;
                int? port         = status[StatusKeys.QBittorrentPort] as int?;
                string message    = status[StatusKeys.Message]         as string ?? string.Empty;

                SyncState state;
                if (!vpnConnected)     state = SyncState.VpnDisconnected;
                else if (success)      state = SyncState.Synced;
                else                   state = SyncState.Error;

                SyncCompleted?.Invoke(new TrayStatus(state, port, message));
            }
        }

        // Core logic separated so the outer method handles status writing via finally
        private async Task<int> RunCoreAsync(Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            LogManager.Instance.LogMessage("Sync cycle started", LogLevel.Info);

            // Set debug mode as early as possible (reads fresh from registry each loop)
            LogManager.Instance.DebugMode = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode);

            var cfg = ReadConfig();
            status[StatusKeys.VpnProvider]           = cfg.VpnProvider;
            status[StatusKeys.UpdateIntervalSeconds] = cfg.UpdateInterval;

            // Instantiate VPN manager based on configured provider
            IVpnManager? vpnManager = await CreateVpnManager(cfg, status).ConfigureAwait(false);
            if (vpnManager is null)
                return cfg.UpdateInterval;

            int targetPort;
            string? vpnProviderName;
            bool warnOnInterfaceMismatch;

            if (!vpnManager.IsVpnConnected())
            {
                _consecutiveFailedCycles++;
                int disconnectedCount = _consecutiveFailedCycles;
                string disconnectedMsg = $"{vpnManager.ProviderName} is not connected";
                LogManager.Instance.LogMessage(BuildCycleCountMessage(disconnectedMsg, disconnectedCount, cfg), LogLevel.Info);
                await TryTriggerVpnRecoveryAsync(vpnManager.FindProviderToken(), vpnManager.ProviderName, cfg).ConfigureAwait(false);

                if (cfg.DefaultPort == 0)
                {
                    status[StatusKeys.Status]  = StatusKeys.StatusSkipped;
                    status[StatusKeys.Message] = disconnectedMsg;
                    LogManager.Instance.LogMessage($"{vpnManager.ProviderName} default port is 0 - skipping port update", LogLevel.Info);
                    return cfg.UpdateInterval;
                }
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} applying default port {cfg.DefaultPort}", LogLevel.Info);
                targetPort              = cfg.DefaultPort;
                vpnProviderName         = null;
                warnOnInterfaceMismatch = false;
            }
            else
            {
                // Counter is only reset after a successful port detection (see below) so that
                // port detection failures also accumulate toward the auto-recovery threshold.
                status[StatusKeys.VpnConnected] = true;

                // Cache VPN client EXE paths while the process is running so auto-recovery
                // can restart the client even if it was killed externally before we inspect it.
                if (cfg.VpnAutoRecoveryEnabled)
                    VpnAutoRecoveryManager.CacheRunningClientExePaths();
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is connected", LogLevel.Info);

                int? vpnPort = await vpnManager.GetVpnPortAsync().ConfigureAwait(false);
                if (!vpnPort.HasValue)
                {
                    await HandlePortDetectionFailureAsync(vpnManager, cfg).ConfigureAwait(false);
                    SetCompleted(status, false, $"Failed to determine {vpnManager.ProviderName} port", LogLevel.Warn);
                    return cfg.UpdateInterval;
                }
                _consecutiveFailedCycles = 0; // Reset only after a successful port fetch
                status[StatusKeys.VpnPort] = vpnPort.Value;
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} port found: {vpnPort.Value}", LogLevel.Info);

                // Warn if the NAT-PMP lease will expire before the next sync cycle renews it
                if (vpnManager is NatPmpManager natPmp &&
                    natPmp.LastGrantedLifetime > 0 &&
                    cfg.UpdateInterval > natPmp.LastGrantedLifetime)
                    LogManager.Instance.LogMessage(
                        $"NAT-PMP sync interval ({cfg.UpdateInterval}s) exceeds lease lifetime ({natPmp.LastGrantedLifetime}s) - port mapping will expire before the next sync cycle",
                        LogLevel.Warn);

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
                cancellationToken).ConfigureAwait(false);

            return cfg.UpdateInterval;
        }

        // Reads all configuration values from the registry into a single AppConfig record
        private static AppConfig ReadConfig()
        {
            int updateInterval = RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
            if (updateInterval < AppConstants.MinUpdateIntervalSeconds) updateInterval = AppConstants.DefaultUpdateIntervalSeconds;

            return new AppConfig(
                VpnProvider:                  RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral,     RegistrySettingsManager.KeyVpnProvider),
                NatPmpAdapterName:            RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral,     RegistrySettingsManager.KeyNatPmpAdapterName),
                UpdateInterval:               updateInterval,
                QBittorrentUrl:               RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUrl),
                QBittorrentUserName:          RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentUserName),
                QBittorrentPassword:          RegistrySettingsManager.GetPassword(),
                QBittorrentExePath:           RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentExePath),
                QBittorrentProcessName:       RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyQBittorrentProcessName),
                RestartQBittorrent:           RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartQBittorrent),
                ForceStartQBittorrent:        RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyForceStartQBittorrent),
                DefaultPort:                  RegistrySettingsManager.GetInt  (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyDefaultPort),
                WarnOnInterfaceMismatch:      RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch),
                RestartOnDisconnect:          RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect),
                PostUpdateCommand:            RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra,       RegistrySettingsManager.KeyPostUpdateCmd),
                VpnAutoRecoveryEnabled:       RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionGeneral,     RegistrySettingsManager.KeyVpnAutoRecoveryEnabled),
                VpnAutoRecoveryTriggerCycles: RegistrySettingsManager.GetInt  (RegistrySettingsManager.SectionGeneral,     RegistrySettingsManager.KeyVpnAutoRecoveryTriggerCycles)
            );
        }

        // Instantiates the appropriate VPN manager for the configured provider.
        // Returns null (with status already set) if the provider cannot be initialised.
        private async Task<IVpnManager?> CreateVpnManager(AppConfig cfg, Dictionary<string, object?> status)
        {
            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase))
                return new PiaVpnManager();

            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
                return await CreateNatPmpVpnManager(cfg, status).ConfigureAwait(false);

            if (!cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase))
                LogManager.Instance.LogMessage($"Unknown VPN provider '{cfg.VpnProvider}', defaulting to ProtonVPN", LogLevel.Warn);
            return new ProtonVpnManager(AppConstants.GetProtonVPNLogFilePath());
        }

        // Resolves the NAT-PMP VPN manager for the configured adapter, handling the disconnected
        // fallback cases and auto-recovery triggering when no adapter is reachable.
        private async Task<IVpnManager?> CreateNatPmpVpnManager(AppConfig cfg, Dictionary<string, object?> status)
        {
            if (string.IsNullOrWhiteSpace(cfg.NatPmpAdapterName))
            {
                SetCompleted(status, false, "No NAT-PMP adapter configured - open Settings and select an adapter");
                return null;
            }

            // Discard the fallback if the adapter name changed in settings
            if (_lastKnownNatPmpManager is not null &&
                !_lastKnownNatPmpManager.ProviderName.Equals(cfg.NatPmpAdapterName, StringComparison.OrdinalIgnoreCase))
                _lastKnownNatPmpManager = null;

            var selected = await NatPmpManager.TryCreateForAdapter(cfg.NatPmpAdapterName).ConfigureAwait(false);

            if (selected is not null)
            {
                // Transfer renewal state from the previous instance so port renewal works correctly
                // when TryCreateForAdapter() returns a fresh NatPmpManager instance each cycle.
                if (_lastKnownNatPmpManager is not null)
                    selected.CopyRenewalStateFrom(_lastKnownNatPmpManager);
                _lastKnownNatPmpManager = selected;
                return selected;
            }

            // Adapter not found - likely down between disconnect and reconnect.
            // Return the last known manager so IsVpnConnected() reports false and
            // RunCoreAsync handles disconnection gracefully (apply default port or skip).
            if (_lastKnownNatPmpManager is not null)
            {
                LogManager.Instance.LogDebug("PortSyncService.CreateNatPmpVpnManager: adapter not discoverable, using last known manager for disconnection handling");
                return _lastKnownNatPmpManager;
            }

            // No previous knowledge of this adapter - VPN likely just disconnected for the first time.
            // Treat as disconnected so the consecutive-cycle counter increments and auto-recovery can fire.
            _consecutiveFailedCycles++;
            int count = _consecutiveFailedCycles;
            string disconnectedMsg = $"NAT-PMP adapter '{cfg.NatPmpAdapterName}' not found - VPN may be disconnected";
            LogManager.Instance.LogMessage(BuildCycleCountMessage(disconnectedMsg, count, cfg), LogLevel.Info);

            await TryTriggerVpnRecoveryAsync(
                NatPmpManager.InferProviderToken(cfg.NatPmpAdapterName),
                $"NAT-PMP adapter '{cfg.NatPmpAdapterName}'",
                cfg).ConfigureAwait(false);

            status[StatusKeys.Status]  = StatusKeys.StatusSkipped;
            status[StatusKeys.Message] = disconnectedMsg;
            return null;
        }

        // Ensures qBittorrent is running, then updates its port if it differs from the target port
        private async Task EnsureRunningAndUpdatePortAsync(QBittorrentManager manager, int targetPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            if (!await EnsureQBittorrentRunningAsync(manager, config, status, cancellationToken).ConfigureAwait(false))
                return;
            status[StatusKeys.QBittorrentRunning] = true;

            // Get current preferences (listening port and network interface) in a single request
            var (currentPort, currentInterfaceName) = await manager.GetPreferencesAsync().ConfigureAwait(false);
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
                if (!await ApplyPortUpdateAsync(manager, targetPort, config, status, cancellationToken).ConfigureAwait(false))
                    return;
            }

            // Check connection status and restart if offline - skip if a restart was already performed
            // by ApplyPortUpdateAsync (port changed + restart enabled) to avoid a redundant cycle.
            bool alreadyRestarted = config.Restart && status[StatusKeys.PortChanged] is true;
            if (config.RestartOnDisconnect && !alreadyRestarted)
                await CheckAndRestartIfDisconnectedAsync(manager, cancellationToken).ConfigureAwait(false);

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
                SetCompleted(status, false, "qBittorrent is not running", LogLevel.Warn);
                return false;
            }

            LogManager.Instance.LogMessage("qBittorrent is not running, attempting to force start", LogLevel.Info);
            if (!await manager.ForceStartAsync(cancellationToken).ConfigureAwait(false))
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
                LogManager.Instance.LogMessage("qBittorrent is bound to all network interfaces - traffic may leak outside the VPN", LogLevel.Warn);
                InterfaceMismatchDetected?.Invoke("No VPN interface bound - traffic may leak.");
                return;
            }

            bool isMatch;

            if (vpnProviderName.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase))
            {
                isMatch = interfaceName.Contains("PIA", StringComparison.OrdinalIgnoreCase);
            }
            else if (vpnProviderName.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase))
            {
                isMatch = interfaceName.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // NAT-PMP: vpnProviderName is the adapter name configured in qbPortWeaver settings
                // (e.g. "ProtonVPN TUN", "ProtonVPN", "wgpia0"). qBittorrent stores the interface by
                // its Windows connection name. A bidirectional Contains handles cases where the names
                // differ in length (e.g. "ProtonVPN TUN" vs "ProtonVPN" across OpenVPN/WireGuard).
                isMatch = interfaceName.Contains(vpnProviderName, StringComparison.OrdinalIgnoreCase) ||
                          vpnProviderName.Contains(interfaceName, StringComparison.OrdinalIgnoreCase);
            }

            if (!isMatch)
            {
                LogManager.Instance.LogMessage($"qBittorrent network interface '{interfaceName}' does not match '{vpnProviderName}'", LogLevel.Warn);
                InterfaceMismatchDetected?.Invoke($"Interface mismatch - '{interfaceName}' is not a {vpnProviderName} adapter.");
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
            if (!await manager.SetListeningPortAsync(targetPort).ConfigureAwait(false))
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
                if (!await manager.RestartAsync(cancellationToken).ConfigureAwait(false))
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

        // Launches the post-update shell command (fire-and-forget).
        // The command string is passed through directly without sanitisation - this is intentional.
        // It is a user-configured value (stored in the registry under HKCU) so the user already
        // controls execution in their own context; no external or untrusted input reaches this path.
        private static void RunPostUpdateCommand(string cmd)
        {
            LogManager.Instance.LogDebug($"PortSyncService.RunPostUpdateCommand: {cmd}");
            try
            {
                string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var startInfo = new ProcessStartInfo(cmdExe, $"/C \"{cmd}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true
                };
                Process.Start(startInfo)?.Dispose(); // NOSONAR S4721 - cmd is a user-configured registry value; execution of arbitrary commands is the intended behaviour
                LogManager.Instance.LogMessage("Post-update command launched (fire-and-forget; result not tracked)", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Post-update command failed: {ex.Message}", LogLevel.Error);
            }
        }

        // Polls /api/v2/transfer/info and restarts qBittorrent if connection_status is "disconnected"
        private static async Task CheckAndRestartIfDisconnectedAsync(QBittorrentManager manager, CancellationToken cancellationToken)
        {
            string? connectionStatus = await manager.GetConnectionStatusAsync().ConfigureAwait(false);
            if (connectionStatus is null)
                return;

            LogManager.Instance.LogMessage($"qBittorrent connection status: {connectionStatus}", LogLevel.Info);

            if (!connectionStatus.Equals(QBittorrentDisconnectedStatus, StringComparison.OrdinalIgnoreCase))
                return;

            LogManager.Instance.LogMessage("qBittorrent connection status is disconnected - restarting", LogLevel.Warn);
            if (!await manager.RestartAsync(cancellationToken).ConfigureAwait(false))
                LogManager.Instance.LogMessage("Failed to restart qBittorrent after connection disconnect", LogLevel.Error);
            else
                LogManager.Instance.LogMessage("Successfully restarted qBittorrent after connection disconnect", LogLevel.Info);
        }

        // Increments the failure counter and triggers recovery when port detection
        // fails despite the VPN being connected (applies to all providers).
        private async Task HandlePortDetectionFailureAsync(IVpnManager vpnManager, AppConfig cfg)
        {
            _consecutiveFailedCycles++;
            int failedCount = _consecutiveFailedCycles;
            LogManager.Instance.LogMessage(
                BuildCycleCountMessage($"Port detection failed on '{vpnManager.ProviderName}'", failedCount, cfg),
                LogLevel.Info);
            await TryTriggerVpnRecoveryAsync(vpnManager.FindProviderToken(), vpnManager.ProviderName, cfg).ConfigureAwait(false);
        }

        // Triggers VPN auto-recovery if enabled and the failure cycle threshold is reached.
        // Resets the counter before the token check so the warning does not fire every cycle
        // when no provider token is found.
        private async Task TryTriggerVpnRecoveryAsync(string? providerToken, string providerName, AppConfig cfg)
        {
            if (!cfg.VpnAutoRecoveryEnabled) return;
            if (_consecutiveFailedCycles < cfg.VpnAutoRecoveryTriggerCycles) return;

            int count = _consecutiveFailedCycles;
            _consecutiveFailedCycles = 0;

            if (providerToken is null)
            {
                LogManager.Instance.LogMessage($"VPN auto-recovery: no provider token found for '{providerName}'", LogLevel.Warn);
                return;
            }

            LogManager.Instance.LogMessage(
                $"VPN auto-recovery: triggering recovery for '{providerName}' after {count} consecutive failed {(count == 1 ? "cycle" : "cycles")}",
                LogLevel.Info);
            await VpnAutoRecoveryManager.TriggerRecoveryAsync(providerToken).ConfigureAwait(false);
        }

        // Builds a failure log message with cycle count and optional recovery trigger suffix
        private static string BuildCycleCountMessage(string prefix, int count, AppConfig cfg)
        {
            string cycles = count == 1 ? "cycle" : "cycles";
            string recoverySuffix = cfg.VpnAutoRecoveryEnabled
                ? $", recovery triggers after {cfg.VpnAutoRecoveryTriggerCycles} consecutive failures"
                : string.Empty;
            return $"{prefix} ({count} consecutive {cycles}{recoverySuffix})";
        }

        // Sets the completion status and logs the message.
        // Pass an explicit level to override the default (Info on success, Error on failure).
        private static void SetCompleted(Dictionary<string, object?> status, bool success, string message, LogLevel? level = null)
        {
            status[StatusKeys.Status]  = success ? StatusKeys.StatusSuccess : StatusKeys.StatusError;
            status[StatusKeys.Message] = message;
            LogManager.Instance.LogMessage(message, level ?? (success ? LogLevel.Info : LogLevel.Error));
        }

    }
}

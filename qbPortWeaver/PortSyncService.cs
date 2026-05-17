using System.Diagnostics;

namespace qbPortWeaver
{
    /// <summary>Outcome of a port sync cycle, used to drive the tray icon color and tooltip.</summary>
    public enum SyncState
    {
        /// <summary>Port was successfully detected and applied to the BitTorrent client.</summary>
        Synced,
        /// <summary>VPN is not connected; no port is available to sync.</summary>
        VpnDisconnected,
        /// <summary>Sync is paused because the BitTorrent client or VPN provider is not configured.</summary>
        Disabled,
        /// <summary>An error occurred during the sync cycle (e.g. client unreachable, port update failed).</summary>
        Error,
    }

    /// <summary>Snapshot of the tray icon state after a sync cycle, raised via <see cref="PortSyncService.SyncCompleted"/>.</summary>
    public sealed record TrayStatus(SyncState State, int? Port, string Message);

    /// <summary>Background service that syncs the BitTorrent client's listening port with the VPN-assigned port on each cycle.</summary>
    public sealed class PortSyncService
    {
        // Connection status value returned by clients that support GetConnectionStatusAsync
        private const string ClientDisconnectedStatus = "disconnected";

        /// <summary>Raised when a sync cycle completes (success or failure) with the resulting tray status.</summary>
        public event Action<TrayStatus>? SyncCompleted;

        /// <summary>Raised when the BitTorrent client's network interface does not match the configured VPN provider.</summary>
        public event Action<string>? InterfaceMismatchDetected;

        /// <summary>Raised when the BitTorrent client's listening port is successfully updated to a new value.</summary>
        public event Action<string>? PortUpdated;

        // Consecutive sync cycles in which the VPN was disconnected or port detection failed.
        // Serialised by MainForm._updateSemaphore (same guarantee as _lastKnownNatPmpManager).
        private int _consecutiveFailedCycles;
        // Tracks the last interface-mismatch message shown as a balloon tip to suppress repeat invocations
        // for the same persistent mismatch. Cleared when the mismatch resolves so the balloon re-fires if it returns.
        // Thread-safety: only read/written inside CheckInterfaceMatch via EnsureRunningAndUpdatePortAsync,
        // serialised by MainForm._updateSemaphore (same guarantee as _consecutiveFailedCycles and _lastKnownNatPmpManager).
        private string? _lastInterfaceMismatchMessage;

        // Fallback for when TryCreateForAdapterAsync cannot reach the configured adapter (e.g. VPN is
        // between disconnect and reconnect) - returned so IsVpnConnected() reports false and
        // RunCoreAsync handles disconnection gracefully. Cleared when the adapter name changes in settings.
        // Thread-safety: only accessed inside RunCoreAsync, serialised by MainForm._updateSemaphore.
        private NatPmpManager? _lastKnownNatPmpManager;

        // All values read from the registry for a single sync cycle.
        // Known extensibility bottleneck: adding a 4th BitTorrent client requires changes in
        // several places (AppConfig parameters, ReadAppConfig, GetActiveClientSection,
        // GetClientRestartConfig, GetClientBehaviorConfig). Consider per-client config sub-records
        // if a 4th client is ever added.
        private sealed record AppConfig(
            string VpnProvider,
            string NatPmpAdapterName,
            int UpdateInterval,
            string BitTorrentClient,
            string QBittorrentUrl,
            string QBittorrentUserName,
            string QBittorrentPassword,
            string QBittorrentProcessName,
            string QBittorrentExePath,
            bool RestartQBittorrent,
            bool ForceStartQBittorrent,
            int QBittorrentDefaultPort,
            bool QBittorrentWarnOnInterfaceMismatch,
            bool QBittorrentRestartOnDisconnect,
            string TransmissionUrl,
            string TransmissionUserName,
            string TransmissionPassword,
            string TransmissionProcessName,
            string TransmissionExePath,
            bool RestartTransmission,
            bool ForceStartTransmission,
            int TransmissionDefaultPort,
            string DelugeUrl,
            string DelugePassword,
            string DelugeProcessName,
            string DelugeExePath,
            bool RestartDeluge,
            bool ForceStartDeluge,
            int DelugeDefaultPort,
            string PostUpdateCommand,
            bool AutoRecoveryEnabled,
            int AutoRecoveryTriggerCycles,
            bool NotifyOnPortUpdate
        );

        // Groups client behaviour settings passed to EnsureRunningAndUpdatePortAsync
        private sealed record SyncConfig(
            bool ForceStart,
            bool Restart,
            string PostUpdateCommand,
            IVpnManager? VpnManager,
            bool WarnOnInterfaceMismatch,
            bool RestartOnDisconnect,
            bool NotifyOnPortUpdate
        );

        // Compile-time-safe keys and values for the status dictionary written to the JSON status file.
        private static class StatusKeys
        {
            // Keys
            public const string AppVersion              = "appVersion";
            public const string Timestamp               = "timestamp";
            public const string VpnProvider             = "vpnProvider";
            public const string VpnConnected            = "vpnConnected";
            public const string VpnPort                 = "vpnPort";
            public const string Client                  = "client";
            public const string ClientRunning           = "clientRunning";
            public const string ClientPreviousPort      = "clientPreviousPort";
            public const string ClientPort              = "clientPort";
            public const string PortChanged             = "portChanged";
            public const string UpdateIntervalSeconds   = "updateIntervalSeconds";
            public const string Status                  = "status";
            public const string Message                 = "message";

            // Values for the Status key - "skipped" means port sync was disabled or VPN disconnected with no default port (cycle is a no-op)
            public const string Success = "success";
            public const string Error   = "error";
            public const string Skipped = "skipped";
        }

        /// <summary>Runs one port sync cycle and returns the configured update interval in seconds.</summary>
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
                [StatusKeys.Client]                  = null,
                [StatusKeys.ClientRunning]           = false,
                [StatusKeys.ClientPreviousPort]      = null,
                [StatusKeys.ClientPort]              = null,
                [StatusKeys.PortChanged]             = false,
                [StatusKeys.UpdateIntervalSeconds]   = AppConstants.DefaultUpdateIntervalSeconds,
                [StatusKeys.Status]                  = StatusKeys.Error,
                [StatusKeys.Message]                 = null
            };

            try
            {
                return await RunCoreAsync(status, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                SetSyncResult(status, false, $"An unexpected error occurred: {ex.Message}");
                return AppConstants.DefaultUpdateIntervalSeconds;
            }
            finally
            {
                // Skip status write and tray update on clean shutdown - the cycle was cancelled,
                // not failed. Writing an error/disconnected state here would flicker the tray icon
                // and leave a misleading error JSON file on every exit.
                if (!cancellationToken.IsCancellationRequested)
                {
                    StatusManager.Write(status);

                    bool success      = status[StatusKeys.Status]          as string == StatusKeys.Success;
                    bool vpnConnected = status[StatusKeys.VpnConnected]   is true;
                    int? port         = status[StatusKeys.ClientPort] as int?;
                    string message    = status[StatusKeys.Message]         as string ?? string.Empty;
                    bool isDisabled   = string.Equals(status[StatusKeys.VpnProvider] as string, RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase);

                    SyncState state;
                    if (isDisabled)            state = SyncState.Disabled;
                    else if (!vpnConnected)    state = SyncState.VpnDisconnected;
                    else if (success)          state = SyncState.Synced;
                    else                       state = SyncState.Error;

                    try { SyncCompleted?.Invoke(new TrayStatus(state, port, message)); }
                    catch (Exception ex) { LogManager.Instance.LogMessage($"SyncCompleted handler failed: {ex.Message}", LogLevel.Warn); }
                }
            }
        }

        // Core logic separated so the outer method handles status writing via finally
        private async Task<int> RunCoreAsync(Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            // Set debug mode as early as possible (reads fresh from registry each loop)
            LogManager.Instance.DebugMode = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionExtra, RegistrySettingsManager.KeyDebugMode);

            var (cfg, activeSection) = ReadConfig();
            int defaultPort = GetDefaultPort(cfg, activeSection);
            LogConfigDebug(cfg, activeSection);
            status[StatusKeys.VpnProvider]           = cfg.VpnProvider;
            status[StatusKeys.UpdateIntervalSeconds] = cfg.UpdateInterval;

            // Instantiate VPN manager based on configured provider
            IVpnManager? vpnManager = await CreateVpnManager(cfg, status, cancellationToken).ConfigureAwait(false);
            if (vpnManager is null)
                return cfg.UpdateInterval;

            var (forceStart, restart, restartOnDisconnect, warnOnInterfaceMismatch) = GetClientBehaviorConfig(cfg, activeSection);

            int targetPort;
            IVpnManager? syncVpnManager;

            if (!vpnManager.IsVpnConnected())
            {
                string disconnectedMsg = $"{vpnManager.ProviderName} is not connected";
                await RegisterFailureAndTryRecoveryAsync(
                    disconnectedMsg, LogLevel.Info,
                    vpnManager.GetRecoveryAction(), vpnManager.GetRecoveryTarget(), vpnManager.ProviderName,
                    cfg, cancellationToken).ConfigureAwait(false);

                if (defaultPort == 0)
                {
                    status[StatusKeys.Status]  = StatusKeys.Skipped;
                    status[StatusKeys.Message] = disconnectedMsg;
                    LogManager.Instance.LogMessage($"{vpnManager.ProviderName} default port is 0 - skipping port update", LogLevel.Info);
                    return cfg.UpdateInterval;
                }
                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} default port is {defaultPort} - applying to {cfg.BitTorrentClient}", LogLevel.Info);
                targetPort     = defaultPort;
                syncVpnManager = null;
            }
            else
            {
                // Counter is only reset after a successful port detection (see below) so that
                // port detection failures also accumulate toward the auto-recovery threshold.
                status[StatusKeys.VpnConnected] = true;

                LogManager.Instance.LogMessage($"{vpnManager.ProviderName} is connected", LogLevel.Info);

                int? vpnPort = await vpnManager.GetVpnPortAsync(cancellationToken).ConfigureAwait(false);
                if (!vpnPort.HasValue)
                {
                    await HandlePortDetectionFailureAsync(vpnManager, cfg, cancellationToken).ConfigureAwait(false);
                    SetSyncResult(status, false, $"Failed to determine {vpnManager.ProviderName} port", LogLevel.Warn);
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

                targetPort     = vpnPort.Value;
                syncVpnManager = vpnManager;
            }

            using var manager = CreateBitTorrentClient(cfg);
            status[StatusKeys.Client] = manager.ClientName;

            await EnsureRunningAndUpdatePortAsync(manager, targetPort,
                new SyncConfig(
                    ForceStart:              forceStart,
                    Restart:                 restart,
                    PostUpdateCommand:       cfg.PostUpdateCommand,
                    VpnManager:              syncVpnManager,
                    WarnOnInterfaceMismatch: warnOnInterfaceMismatch,
                    RestartOnDisconnect:     restartOnDisconnect,
                    NotifyOnPortUpdate:      cfg.NotifyOnPortUpdate),
                status,
                cancellationToken).ConfigureAwait(false);

            return cfg.UpdateInterval;
        }

        // Reads all configuration values from the registry into a single AppConfig record
        private static (AppConfig Config, string ActiveSection) ReadConfig()
        {
            int updateInterval = RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
            if (updateInterval < AppConstants.MinUpdateIntervalSeconds) updateInterval = AppConstants.DefaultUpdateIntervalSeconds;

            int autoRecoveryTriggerCycles = Math.Max(1, RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyAutoRecoveryTriggerCycles));

            string bitTorrentClient = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyBitTorrentClient);
            string activeSection = GetActiveClientSection(bitTorrentClient);

            return (new AppConfig(
                VpnProvider:               RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral,      RegistrySettingsManager.KeyVpnProvider),
                NatPmpAdapterName:         RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionGeneral,      RegistrySettingsManager.KeyNatPmpAdapterName),
                UpdateInterval:            updateInterval,
                BitTorrentClient:          bitTorrentClient,
                QBittorrentUrl:            RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyQBittorrentUrl),
                QBittorrentUserName:       RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyQBittorrentUserName),
                QBittorrentPassword:       RegistrySettingsManager.GetQBittorrentPassword(),
                QBittorrentProcessName:    RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyQBittorrentProcessName),
                QBittorrentExePath:        RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyQBittorrentExePath),
                RestartQBittorrent:        RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyRestartQBittorrent),
                ForceStartQBittorrent:     RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyForceStartQBittorrent),
                QBittorrentDefaultPort:    RegistrySettingsManager.GetInt  (RegistrySettingsManager.SectionQBittorrent,  RegistrySettingsManager.KeyDefaultPort),
                QBittorrentWarnOnInterfaceMismatch: RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyWarnOnInterfaceMismatch),
                QBittorrentRestartOnDisconnect:     RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionQBittorrent, RegistrySettingsManager.KeyRestartOnDisconnect),
                TransmissionUrl:           RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUrl),
                TransmissionUserName:      RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionUserName),
                TransmissionPassword:      RegistrySettingsManager.GetTransmissionPassword(),
                TransmissionProcessName:   RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionProcessName),
                TransmissionExePath:       RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyTransmissionExePath),
                RestartTransmission:       RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyRestartTransmission),
                ForceStartTransmission:    RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyForceStartTransmission),
                TransmissionDefaultPort:   RegistrySettingsManager.GetInt  (RegistrySettingsManager.SectionTransmission, RegistrySettingsManager.KeyDefaultPort),
                DelugeUrl:                 RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge,       RegistrySettingsManager.KeyDelugeUrl),
                DelugePassword:            RegistrySettingsManager.GetDelugePassword(),
                DelugeProcessName:         RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge,       RegistrySettingsManager.KeyDelugeProcessName),
                DelugeExePath:             RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionDeluge,       RegistrySettingsManager.KeyDelugeExePath),
                RestartDeluge:             RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionDeluge,       RegistrySettingsManager.KeyRestartDeluge),
                ForceStartDeluge:          RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionDeluge,       RegistrySettingsManager.KeyForceStartDeluge),
                DelugeDefaultPort:         RegistrySettingsManager.GetInt  (RegistrySettingsManager.SectionDeluge,       RegistrySettingsManager.KeyDefaultPort),
                PostUpdateCommand:         RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionExtra,        RegistrySettingsManager.KeyPostUpdateCmd),
                AutoRecoveryEnabled:       RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionGeneral,      RegistrySettingsManager.KeyAutoRecoveryEnabled),
                AutoRecoveryTriggerCycles: autoRecoveryTriggerCycles,
                NotifyOnPortUpdate:        RegistrySettingsManager.GetBool (RegistrySettingsManager.SectionGeneral,      RegistrySettingsManager.KeyNotifyOnPortUpdate)
            ), activeSection);
        }

        // Dumps the active AppConfig to the log file when debug mode is enabled.
        // Three lines (general / active client / extra) keep each section independently greppable.
        private static void LogConfigDebug(AppConfig cfg, string activeSection)
        {
            if (!LogManager.Instance.DebugMode) return;

            LogManager.Instance.LogDebug(
                $"PortSyncService.RunCoreAsync [general]: {RegistrySettingsManager.KeyVpnProvider}={cfg.VpnProvider}, " +
                $"{RegistrySettingsManager.KeyNatPmpAdapterName}={cfg.NatPmpAdapterName}, " +
                $"{RegistrySettingsManager.KeyUpdateIntervalSeconds}={cfg.UpdateInterval}s, " +
                $"{RegistrySettingsManager.KeyAutoRecoveryEnabled}={cfg.AutoRecoveryEnabled}, " +
                $"{RegistrySettingsManager.KeyAutoRecoveryTriggerCycles}={cfg.AutoRecoveryTriggerCycles}, " +
                $"{RegistrySettingsManager.KeyBitTorrentClient}={cfg.BitTorrentClient}");

            if (activeSection == RegistrySettingsManager.SectionTransmission)
                LogManager.Instance.LogDebug(
                    $"PortSyncService.RunCoreAsync [transmission]: {RegistrySettingsManager.KeyTransmissionUrl}={cfg.TransmissionUrl}, " +
                    $"{RegistrySettingsManager.KeyTransmissionUserName}={cfg.TransmissionUserName}, " +
                    $"{RegistrySettingsManager.KeyTransmissionPassword}=***, " + // NOSONAR S2068 - value is masked, not a real credential
                    $"{RegistrySettingsManager.KeyTransmissionProcessName}={cfg.TransmissionProcessName}, " +
                    $"{RegistrySettingsManager.KeyTransmissionExePath}={cfg.TransmissionExePath}, " +
                    $"{RegistrySettingsManager.KeyRestartTransmission}={cfg.RestartTransmission}, " +
                    $"{RegistrySettingsManager.KeyForceStartTransmission}={cfg.ForceStartTransmission}, " +
                    $"{RegistrySettingsManager.KeyDefaultPort}={cfg.TransmissionDefaultPort}");
            else if (activeSection == RegistrySettingsManager.SectionDeluge)
                LogManager.Instance.LogDebug(
                    $"PortSyncService.RunCoreAsync [deluge]: {RegistrySettingsManager.KeyDelugeUrl}={cfg.DelugeUrl}, " +
                    $"{RegistrySettingsManager.KeyDelugePassword}=***, " + // NOSONAR S2068 - value is masked, not a real credential
                    $"{RegistrySettingsManager.KeyDelugeProcessName}={cfg.DelugeProcessName}, " +
                    $"{RegistrySettingsManager.KeyDelugeExePath}={cfg.DelugeExePath}, " +
                    $"{RegistrySettingsManager.KeyRestartDeluge}={cfg.RestartDeluge}, " +
                    $"{RegistrySettingsManager.KeyForceStartDeluge}={cfg.ForceStartDeluge}, " +
                    $"{RegistrySettingsManager.KeyDefaultPort}={cfg.DelugeDefaultPort}");
            else
                LogManager.Instance.LogDebug(
                    $"PortSyncService.RunCoreAsync [qBittorrent]: {RegistrySettingsManager.KeyQBittorrentUrl}={cfg.QBittorrentUrl}, " +
                    $"{RegistrySettingsManager.KeyQBittorrentUserName}={cfg.QBittorrentUserName}, " +
                    $"{RegistrySettingsManager.KeyQBittorrentPassword}=***, " + // NOSONAR S2068 - value is masked, not a real credential
                    $"{RegistrySettingsManager.KeyQBittorrentProcessName}={cfg.QBittorrentProcessName}, " +
                    $"{RegistrySettingsManager.KeyQBittorrentExePath}={cfg.QBittorrentExePath}, " +
                    $"{RegistrySettingsManager.KeyRestartQBittorrent}={cfg.RestartQBittorrent}, " +
                    $"{RegistrySettingsManager.KeyForceStartQBittorrent}={cfg.ForceStartQBittorrent}, " +
                    $"{RegistrySettingsManager.KeyDefaultPort}={cfg.QBittorrentDefaultPort}, " +
                    $"{RegistrySettingsManager.KeyWarnOnInterfaceMismatch}={cfg.QBittorrentWarnOnInterfaceMismatch}, " +
                    $"{RegistrySettingsManager.KeyRestartOnDisconnect}={cfg.QBittorrentRestartOnDisconnect}");

            LogManager.Instance.LogDebug(
                $"PortSyncService.RunCoreAsync [extra]: {RegistrySettingsManager.KeyPostUpdateCmd}={cfg.PostUpdateCommand}, " +
                $"{RegistrySettingsManager.KeyDebugMode}={LogManager.Instance.DebugMode}");
        }

        // Instantiates the appropriate VPN manager for the configured provider.
        // Returns null (with status already set) if the provider is disabled or cannot be initialised.
        private async Task<IVpnManager?> CreateVpnManager(AppConfig cfg, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderDisabled, StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Instance.LogMessage("Port sync disabled", LogLevel.Info);
                status[StatusKeys.Status]  = StatusKeys.Skipped;
                status[StatusKeys.Message] = "Port sync disabled";
                return null;
            }

            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderPia, StringComparison.OrdinalIgnoreCase))
                return new PiaVpnManager();

            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderNatPmp, StringComparison.OrdinalIgnoreCase))
                return await CreateNatPmpVpnManager(cfg, status, cancellationToken).ConfigureAwait(false);

            if (cfg.VpnProvider.Equals(RegistrySettingsManager.VpnProviderProtonVpn, StringComparison.OrdinalIgnoreCase))
                return new ProtonVpnManager(AppConstants.GetProtonVpnLogFilePath());

            LogManager.Instance.LogMessage($"VPN provider '{cfg.VpnProvider}' is not recognized, port sync skipped", LogLevel.Warn);
            status[StatusKeys.Status]  = StatusKeys.Skipped;
            status[StatusKeys.Message] = $"VPN provider '{cfg.VpnProvider}' is not recognized";
            return null;
        }

        // Resolves the NAT-PMP VPN manager for the configured adapter, handling the disconnected
        // fallback cases and auto-recovery triggering when no adapter is reachable.
        private async Task<IVpnManager?> CreateNatPmpVpnManager(AppConfig cfg, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cfg.NatPmpAdapterName))
            {
                SetSyncResult(status, false, "No NAT-PMP adapter configured - open Settings and select an adapter");
                return null;
            }

            // Discard the fallback if the adapter name changed in settings
            if (_lastKnownNatPmpManager is not null &&
                !_lastKnownNatPmpManager.ProviderName.Equals(cfg.NatPmpAdapterName, StringComparison.OrdinalIgnoreCase))
                _lastKnownNatPmpManager = null;

            var selected = await NatPmpManager.TryCreateForAdapterAsync(cfg.NatPmpAdapterName).ConfigureAwait(false);

            if (selected is not null)
            {
                // Transfer renewal state from the previous instance so port renewal works correctly
                // when TryCreateForAdapterAsync() returns a fresh NatPmpManager instance each cycle.
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
                LogManager.Instance.LogDebug("PortSyncService.CreateNatPmpVpnManager: Adapter not discoverable, using last known manager for disconnection handling");
                return _lastKnownNatPmpManager;
            }

            // No previous knowledge of this adapter - VPN likely just disconnected for the first time.
            // No IVpnManager instance is available here (adapter not found, no fallback manager),
            // so we resolve the recovery action and target directly instead of going through the interface.
            string adapterName     = cfg.NatPmpAdapterName;
            string? providerToken  = NatPmpManager.FindProviderToken(adapterName);
            string disconnectedMsg = $"NAT-PMP adapter '{adapterName}' not found - VPN may be disconnected";
            await RegisterFailureAndTryRecoveryAsync(
                disconnectedMsg, LogLevel.Info,
                providerToken is not null ? HelperServiceClient.ActionRestart : HelperServiceClient.ActionCycleAdapter,
                providerToken ?? adapterName,
                $"NAT-PMP adapter '{adapterName}'",
                cfg, cancellationToken).ConfigureAwait(false);

            status[StatusKeys.Status]  = StatusKeys.Skipped;
            status[StatusKeys.Message] = disconnectedMsg;
            return null;
        }

        // Creates the active IBitTorrentClient based on the BitTorrentClient config value.
        // Defaults to QBittorrentClient when the value is unrecognized.
        private static IBitTorrentClient CreateBitTorrentClient(AppConfig cfg)
        {
            if (cfg.BitTorrentClient.Equals(RegistrySettingsManager.BitTorrentClientTransmission, StringComparison.OrdinalIgnoreCase))
                return new TransmissionClient(
                    cfg.TransmissionUrl, cfg.TransmissionUserName, cfg.TransmissionPassword,
                    cfg.TransmissionProcessName, cfg.TransmissionExePath);

            if (cfg.BitTorrentClient.Equals(RegistrySettingsManager.BitTorrentClientDeluge, StringComparison.OrdinalIgnoreCase))
                return new DelugeClient(cfg.DelugeUrl, cfg.DelugePassword, cfg.DelugeProcessName, cfg.DelugeExePath);

            return new QBittorrentClient(
                cfg.QBittorrentUrl, cfg.QBittorrentUserName, cfg.QBittorrentPassword,
                cfg.QBittorrentProcessName, cfg.QBittorrentExePath);
        }

        // Ensures the BitTorrent client is running, then updates its port if it differs from the target port
        private async Task EnsureRunningAndUpdatePortAsync(IBitTorrentClient manager, int targetPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            if (!await EnsureClientRunningAsync(manager, config, status, cancellationToken).ConfigureAwait(false))
                return;
            status[StatusKeys.ClientRunning] = true;

            // Get current preferences (listening port and network interface) in a single request
            var (currentPort, currentInterfaceName) = await manager.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
            if (!currentPort.HasValue)
            {
                SetSyncResult(status, false, $"Failed to determine {manager.ClientName} port");
                return;
            }
            status[StatusKeys.ClientPreviousPort] = currentPort.Value;
            LogManager.Instance.LogMessage($"{manager.ClientName} port found: {currentPort.Value}", LogLevel.Info);

            // Warn if the client's network interface doesn't match the configured VPN provider
            if (config.VpnManager is not null && config.WarnOnInterfaceMismatch && manager.SupportsInterfaceMismatchWarning)
                CheckInterfaceMatch(manager.ClientName, currentInterfaceName, config.VpnManager);

            if (currentPort.Value == targetPort)
            {
                status[StatusKeys.ClientPort] = currentPort.Value;
                LogManager.Instance.LogMessage($"{manager.ClientName} ports match - no update needed", LogLevel.Info);
            }
            else
            {
                if (!await ApplyPortUpdateAsync(manager, targetPort, config, status, cancellationToken).ConfigureAwait(false))
                    return; // port update failed - skip RestartOnDisconnect check; next cycle will retry
                if (config.NotifyOnPortUpdate)
                    NotifyPortUpdated(manager.ClientName, targetPort);
            }

            // Check connection status and restart if offline - skip if a restart was already performed
            // by ApplyPortUpdateAsync (port changed + restart enabled) to avoid a redundant cycle.
            bool restartAttemptedThisCycle = config.Restart && status[StatusKeys.PortChanged] is true;
            if (config.RestartOnDisconnect && !restartAttemptedThisCycle)
                await CheckAndRestartIfDisconnectedAsync(manager, cancellationToken).ConfigureAwait(false);

            SetSyncResult(status, true, "Sync cycle completed");
        }

        // Returns true if the BitTorrent client is running (or was successfully force-started), false otherwise
        private static async Task<bool> EnsureClientRunningAsync(IBitTorrentClient manager, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            if (manager.IsRunning())
            {
                LogManager.Instance.LogMessage($"{manager.ClientName} is running", LogLevel.Info);
                return true;
            }

            if (!config.ForceStart)
            {
                SetSyncResult(status, false, $"{manager.ClientName} is not running", LogLevel.Warn);
                return false;
            }

            LogManager.Instance.LogMessage($"{manager.ClientName} is not running - attempting to force start", LogLevel.Info);
            if (!await manager.ForceStartAsync(cancellationToken).ConfigureAwait(false))
            {
                SetSyncResult(status, false, $"Failed to force start {manager.ClientName}");
                return false;
            }
            LogManager.Instance.LogMessage($"Force-started {manager.ClientName}", LogLevel.Info);
            return true;
        }

        // Checks if the client's network interface matches the expected VPN provider and logs a warning if not.
        // The warn log fires every cycle so the log alert badge tracks the persistent condition.
        // The InterfaceMismatchDetected balloon fires only on transition (new or changed mismatch) to avoid
        // spamming the user each cycle; it re-fires if the mismatch clears and then returns.
        private void CheckInterfaceMatch(string clientName, string? interfaceName, IVpnManager vpnManager)
        {
            if (interfaceName is null)
            {
                LogManager.Instance.LogDebug($"PortSyncService.CheckInterfaceMatch: {clientName} did not return an interface name, skipping check");
                return;
            }

            string? balloonMessage = null;

            if (interfaceName.Length == 0)
            {
                LogManager.Instance.LogMessage($"{clientName} is bound to all network interfaces - traffic may leak outside the VPN", LogLevel.Warn);
                balloonMessage = $"{clientName}: no VPN interface bound - traffic may leak.";
            }
            else if (!vpnManager.IsAdapterMatch(interfaceName))
            {
                LogManager.Instance.LogMessage($"{clientName} network interface '{interfaceName}' does not match '{vpnManager.ProviderName}'", LogLevel.Warn);
                balloonMessage = $"{clientName} interface mismatch - '{interfaceName}' is not a {vpnManager.ProviderName} adapter.";
            }
            else
            {
                LogManager.Instance.LogDebug($"PortSyncService.CheckInterfaceMatch: {clientName} interface '{interfaceName}' matches '{vpnManager.ProviderName}'");
            }

            if (balloonMessage is null)
            {
                _lastInterfaceMismatchMessage = null;
                return;
            }

            if (balloonMessage == _lastInterfaceMismatchMessage) return;
            _lastInterfaceMismatchMessage = balloonMessage;
            try { InterfaceMismatchDetected?.Invoke(balloonMessage); }
            catch (Exception ex) { LogManager.Instance.LogMessage($"InterfaceMismatchDetected handler failed: {ex.Message}", LogLevel.Warn); }
        }

        private void NotifyPortUpdated(string clientName, int port)
        {
            try { PortUpdated?.Invoke($"{clientName} port updated to {port}"); }
            catch (Exception ex) { LogManager.Instance.LogMessage($"PortUpdated handler failed: {ex.Message}", LogLevel.Warn); }
        }

        // Sets the listening port, optionally restarts the client and runs the post-update command.
        // Returns false if any step fails.
        private static async Task<bool> ApplyPortUpdateAsync(IBitTorrentClient manager, int targetPort, SyncConfig config, Dictionary<string, object?> status, CancellationToken cancellationToken)
        {
            LogManager.Instance.LogMessage($"Ports do not match - updating {manager.ClientName} port to {targetPort}", LogLevel.Info);
            if (!await manager.SetListeningPortAsync(targetPort, cancellationToken).ConfigureAwait(false))
            {
                SetSyncResult(status, false, $"Failed to set {manager.ClientName} port to {targetPort}");
                return false;
            }
            LogManager.Instance.LogMessage($"{manager.ClientName} port set to {targetPort}", LogLevel.Info);

            status[StatusKeys.ClientPort] = targetPort;
            status[StatusKeys.PortChanged] = true;

            if (config.Restart)
            {
                LogManager.Instance.LogMessage($"Attempting to restart {manager.ClientName}", LogLevel.Info);
                if (!await manager.RestartAsync(cancellationToken).ConfigureAwait(false))
                {
                    SetSyncResult(status, false, $"Failed to restart {manager.ClientName}");
                    return false;
                }
                LogManager.Instance.LogMessage($"Restarted {manager.ClientName}", LogLevel.Info);
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
                Process.Start(AppConstants.CreateHiddenStartInfo(cmdExe, $"/C \"{cmd}\""))?.Dispose(); // NOSONAR S4721 - cmd is a user-configured registry value; execution of arbitrary commands is the intended behaviour
                LogManager.Instance.LogMessage("Post-update command launched", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to run post-update command: {ex.Message}", LogLevel.Warn);
            }
        }

        // Checks connection status and restarts the client if it reports as disconnected.
        // Clients that do not support connection status (GetConnectionStatusAsync returns null) are skipped.
        private static async Task CheckAndRestartIfDisconnectedAsync(IBitTorrentClient manager, CancellationToken cancellationToken)
        {
            string? connectionStatus = await manager.GetConnectionStatusAsync(cancellationToken).ConfigureAwait(false);
            if (connectionStatus is null)
                return;

            LogManager.Instance.LogDebug($"PortSyncService.CheckAndRestartIfDisconnectedAsync: {manager.ClientName} connection status: {connectionStatus}");

            if (!connectionStatus.Equals(ClientDisconnectedStatus, StringComparison.OrdinalIgnoreCase))
                return;

            LogManager.Instance.LogMessage($"{manager.ClientName} connection status is disconnected - restarting", LogLevel.Warn);
            if (!await manager.RestartAsync(cancellationToken).ConfigureAwait(false))
                LogManager.Instance.LogMessage($"Failed to restart {manager.ClientName} after connection disconnect", LogLevel.Error);
            else
                LogManager.Instance.LogMessage($"Restarted {manager.ClientName} after connection disconnect", LogLevel.Info);
        }

        // Builds a failure log message with cycle count and optional recovery trigger suffix
        private static string BuildCycleCountMessage(string prefix, int count, AppConfig cfg)
        {
            string cycles = count == 1 ? "cycle" : "cycles";
            string recoverySuffix = cfg.AutoRecoveryEnabled
                ? $", recovery triggers after {cfg.AutoRecoveryTriggerCycles} consecutive failures"
                : string.Empty;
            return $"{prefix} ({count} consecutive {cycles}{recoverySuffix})";
        }

        // Port detection failed despite the VPN being connected. Logs at Warn (the other two
        // failure paths use Info because they correspond to expected disconnection states).
        private Task HandlePortDetectionFailureAsync(IVpnManager vpnManager, AppConfig cfg, CancellationToken cancellationToken) =>
            RegisterFailureAndTryRecoveryAsync(
                $"Port detection failed on '{vpnManager.ProviderName}'", LogLevel.Warn,
                vpnManager.GetRecoveryAction(), vpnManager.GetRecoveryTarget(), vpnManager.ProviderName,
                cfg, cancellationToken);

        // Single increment site for _consecutiveFailedCycles. Every failure path that contributes
        // to the auto-recovery threshold flows through here: VPN disconnected, port detection
        // failed, and NAT-PMP adapter not found. Logs the cycle count message and then dispatches
        // recovery (which may no-op if the threshold has not been reached).
        private async Task RegisterFailureAndTryRecoveryAsync(
            string reason, LogLevel logLevel,
            string recoveryAction, string? recoveryTarget, string displayName,
            AppConfig cfg, CancellationToken cancellationToken)
        {
            _consecutiveFailedCycles++;
            int count = _consecutiveFailedCycles;
            LogManager.Instance.LogMessage(BuildCycleCountMessage(reason, count, cfg), logLevel);
            await TryTriggerRecoveryAsync(recoveryAction, recoveryTarget, displayName, cfg, cancellationToken).ConfigureAwait(false);
        }

        // Triggers auto-recovery if enabled and the failure cycle threshold is reached.
        // Resets the counter before the target check so the warning does not fire every cycle
        // when no recovery target is found.
        private async Task TryTriggerRecoveryAsync(string action, string? recoveryTarget, string displayName, AppConfig cfg, CancellationToken cancellationToken)
        {
            if (!cfg.AutoRecoveryEnabled)
            {
                _consecutiveFailedCycles = 0;
                return;
            }
            if (_consecutiveFailedCycles < cfg.AutoRecoveryTriggerCycles) return;

            int count = _consecutiveFailedCycles;

            if (recoveryTarget is null)
            {
                _consecutiveFailedCycles = 0;
                LogManager.Instance.LogMessage($"No recovery target found for '{displayName}'", LogLevel.Warn);
                return;
            }

            _consecutiveFailedCycles = 0;

            LogManager.Instance.LogMessage(
                $"Triggering '{action}' for '{displayName}' after {count} consecutive failed {(count == 1 ? "cycle" : "cycles")}",
                LogLevel.Info);
            if (action == HelperServiceClient.ActionRestart)
                await AutoRecoveryManager.TriggerRestartAsync(recoveryTarget, cancellationToken).ConfigureAwait(false);
            else
                await AutoRecoveryManager.TriggerCycleAdapterAsync(recoveryTarget, cancellationToken).ConfigureAwait(false);
        }

        // Returns the registry settings section for the active BitTorrent client.
        // Used to read DefaultPort and to determine which restart options to apply.
        private static string GetActiveClientSection(string client)
        {
            if (client.Equals(RegistrySettingsManager.BitTorrentClientTransmission, StringComparison.OrdinalIgnoreCase))
                return RegistrySettingsManager.SectionTransmission;
            if (client.Equals(RegistrySettingsManager.BitTorrentClientDeluge, StringComparison.OrdinalIgnoreCase))
                return RegistrySettingsManager.SectionDeluge;
            return RegistrySettingsManager.SectionQBittorrent;
        }

        private static (bool ForceStart, bool Restart, bool RestartOnDisconnect, bool WarnOnInterfaceMismatch) GetClientBehaviorConfig(AppConfig cfg, string activeSection) =>
            activeSection switch
            {
                // RestartOnDisconnect and WarnOnInterfaceMismatch are qBittorrent-only: Transmission and Deluge
                // do not expose a connection-state API, so neither feature can be implemented for them.
                RegistrySettingsManager.SectionTransmission => (cfg.ForceStartTransmission, cfg.RestartTransmission, false, false),
                RegistrySettingsManager.SectionDeluge       => (cfg.ForceStartDeluge,       cfg.RestartDeluge,       false, false),
                _                                           => (cfg.ForceStartQBittorrent,  cfg.RestartQBittorrent,  cfg.QBittorrentRestartOnDisconnect, cfg.QBittorrentWarnOnInterfaceMismatch),
            };

        private static int GetDefaultPort(AppConfig cfg, string activeSection) =>
            activeSection switch
            {
                RegistrySettingsManager.SectionTransmission => cfg.TransmissionDefaultPort,
                RegistrySettingsManager.SectionDeluge       => cfg.DelugeDefaultPort,
                _                                           => cfg.QBittorrentDefaultPort,
            };

        // Sets the cycle status and message in the status dict, logs the message, and adds a closing bookend on failure.
        // Pass an explicit level to override the default (Info on success, Error on failure).
        // The bookend uses the same effective level so a Warn-level soft failure does not escalate to Error.
        private static void SetSyncResult(Dictionary<string, object?> status, bool success, string message, LogLevel? level = null)
        {
            status[StatusKeys.Status]  = success ? StatusKeys.Success : StatusKeys.Error;
            status[StatusKeys.Message] = message;
            LogLevel effectiveLevel = level ?? (success ? LogLevel.Info : LogLevel.Error);
            LogManager.Instance.LogMessage(message, effectiveLevel);
            if (!success)
                LogManager.Instance.LogMessage("Sync cycle failed", effectiveLevel);
        }
    }
}

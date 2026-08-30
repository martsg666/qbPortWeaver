using System.Text.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver;

/// <summary>
/// String values written to the "status" field of the status file (and surfaced to the Status
/// panel and external scripts). Kept stable across releases like the status keys. "skipped" means
/// port sync was disabled or the VPN was disconnected with no default port (the cycle is a no-op).
/// </summary>
public static class SyncStatusValues
{
    public const string Success = "success";
    public const string Skipped = "skipped";
    public const string Error = "error";
}

/// <summary>
/// Typed view of the last sync cycle written to the JSON status file. Property names map to the
/// literal status keys (PortSyncService's StatusKeys constants); only a subset of those keys are
/// modelled here. Unmapped keys in the file are ignored.
/// </summary>
public sealed record StatusSnapshot
{
    [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; init; }
    [JsonPropertyName("vpnProvider")] public string? VpnProvider { get; init; }
    [JsonPropertyName("vpnConnected")] public bool VpnConnected { get; init; }
    [JsonPropertyName("vpnPort")] public int? VpnPort { get; init; }
    [JsonPropertyName("client")] public string? Client { get; init; }
    [JsonPropertyName("clientRunning")] public bool ClientRunning { get; init; }
    [JsonPropertyName("clientPort")] public int? ClientPort { get; init; }
    [JsonPropertyName("clientPreviousPort")] public int? ClientPreviousPort { get; init; }
    [JsonPropertyName("portChanged")] public bool PortChanged { get; init; }
    [JsonPropertyName("portVerified")] public bool? PortVerified { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("waitingForVpn")] public bool WaitingForVpn { get; init; }
    /// <summary>When the next cycle is due. Absolute rather than a duration for the same reason as
    /// <see cref="RecoveryHoldUntil"/>: the wait starts when the cycle ends, while
    /// <see cref="Timestamp"/> is stamped at the start, so deriving it from the two would run the
    /// countdown out early by the cycle's length. Null on a status file written before this key
    /// existed, where callers fall back to the old derivation.</summary>
    [JsonPropertyName("nextSyncAt")] public DateTimeOffset? NextSyncAt { get; init; }
    /// <summary>When auto-recovery may next attempt, while it is being held back because connectivity
    /// could not be confirmed. Null whenever nothing is being held. Absolute rather than a duration so
    /// it does not have to be read against the cycle timestamp, which is stamped at a different moment.</summary>
    [JsonPropertyName("recoveryHoldUntil")] public DateTimeOffset? RecoveryHoldUntil { get; init; }
    /// <summary>Whether auto-recovery is switched on.</summary>
    [JsonPropertyName("recoveryEnabled")] public bool RecoveryEnabled { get; init; }
    /// <summary>Consecutive failed cycles accumulated so far, 0 when the last cycle succeeded.</summary>
    [JsonPropertyName("recoveryFailedCycles")] public int RecoveryFailedCycles { get; init; }
    /// <summary>Consecutive failed cycles required before auto-recovery triggers.</summary>
    [JsonPropertyName("recoveryTriggerCycles")] public int RecoveryTriggerCycles { get; init; }
    /// <summary>When the sustained-failure floor clears, while it is holding recovery back; null
    /// otherwise. Distinct from <see cref="RecoveryHoldUntil"/>: that one waits on connectivity,
    /// this one waits for the failures to have persisted long enough to rule out a brief blip.</summary>
    [JsonPropertyName("recoverySustainedUntil")] public DateTimeOffset? RecoverySustainedUntil { get; init; }
    /// <summary>Whether the failed-cycle trigger is suspended because the consecutive-recovery cap was
    /// reached: recoveries ran but none restored a forwarded port. Unlike the two holds above this one
    /// carries no deadline - it ends on the next successful port read, which nothing can predict - so it
    /// is a flag rather than an instant.</summary>
    [JsonPropertyName("recoverySuspended")] public bool RecoverySuspended { get; init; }
    /// <summary>Whether the port-closed recovery trigger can actually fire. Independent of
    /// <see cref="RecoveryEnabled"/>: either trigger can restart the VPN with the other off. This is
    /// the effective value, not the raw setting - the trigger runs inside port verification, so it is
    /// false whenever verification is off, however its own checkbox was left.</summary>
    [JsonPropertyName("portClosedRecoveryEnabled")] public bool PortClosedRecoveryEnabled { get; init; }
    /// <summary>Consecutive confirmed-closed checks accumulated so far, 0 when the port last verified open.</summary>
    [JsonPropertyName("portClosedRecoveryChecks")] public int PortClosedRecoveryChecks { get; init; }
    /// <summary>Consecutive confirmed-closed checks required before port-closed recovery triggers.</summary>
    [JsonPropertyName("portClosedRecoveryTriggerChecks")] public int PortClosedRecoveryTriggerChecks { get; init; }
    /// <summary>False once the one-shot port-closed trigger has fired, until a verification reports the
    /// port open again. While false that trigger cannot fire however long the port stays closed.</summary>
    [JsonPropertyName("portClosedRecoveryArmed")] public bool PortClosedRecoveryArmed { get; init; }
}

/// <summary>
/// Reads and writes the sync cycle status snapshot JSON file. Object keys are the literal string
/// values of PortSyncService's private StatusKeys constants - external consumers should rely on
/// those names, which are kept stable across releases.
/// </summary>
public static class StatusManager
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Serializes <paramref name="status"/> to the JSON status file using an atomic temp-file write.</summary>
    public static void Write(IReadOnlyDictionary<string, object?> status)
    {
        string filePath = AppFiles.GetStatusFilePath();

        try
        {
            string json = JsonSerializer.Serialize(status, _jsonOptions);
            AppFiles.WriteAtomic(filePath, json);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to write status file: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Reads the last status snapshot from the JSON file. Returns null when the file does not yet
    /// exist (no cycle has run) or cannot be read/parsed - callers keep their current display in
    /// that case rather than blanking it. Writes are atomic (temp-file rename), so a read always
    /// sees a complete file or fails cleanly.
    /// </summary>
    public static StatusSnapshot? TryRead()
    {
        string filePath = AppFiles.GetStatusFilePath();

        try
        {
            if (!File.Exists(filePath)) return null;
            string json = AppFiles.ReadAllTextShared(filePath);
            return JsonSerializer.Deserialize<StatusSnapshot>(json, _readOptions);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"StatusManager.TryRead: {ex.Message}");
            return null;
        }
    }
}

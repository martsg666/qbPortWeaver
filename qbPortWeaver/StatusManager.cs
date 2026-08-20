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
        string filePath = AppConstants.GetStatusFilePath();

        try
        {
            string json = JsonSerializer.Serialize(status, _jsonOptions);
            AppConstants.WriteAtomic(filePath, json);
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
        string filePath = AppConstants.GetStatusFilePath();

        try
        {
            if (!File.Exists(filePath)) return null;
            string json = AppConstants.ReadAllTextShared(filePath);
            return JsonSerializer.Deserialize<StatusSnapshot>(json, _readOptions);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"StatusManager.TryRead: {ex.Message}");
            return null;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver;

/// <summary>Category of a port history event, driving the row accent in the Status panel.</summary>
public enum PortHistoryKind
{
    /// <summary>A normal port change (VPN assigned a new port, or the default port was applied).</summary>
    PortChanged,
    /// <summary>The forwarded port was confirmed unreachable from outside.</summary>
    PortClosed,
    /// <summary>An auto-recovery action was dispatched (VPN service restart or adapter cycle).</summary>
    Recovery,
}

/// <summary>One recorded port history event. Serialized to the history JSON file.</summary>
public sealed record PortHistoryEntry
{
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("port")] public int? Port { get; init; }
    [JsonPropertyName("event")] public string Event { get; init; } = string.Empty;
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PortHistoryKind Kind { get; init; }
}

/// <summary>
/// Persisted, bounded history of port-affecting events (port changes, confirmed-closed results,
/// auto-recovery dispatches), shown in the Status panel. Port changes are rare (VPN reconnects),
/// so the history is persisted across restarts - a session-only list would usually be empty.
/// Events are appended by the sync loop (background thread) and read by the Status panel (UI
/// thread); appends are serialized by a lock and the file write is atomic, so a concurrent read
/// sees either the previous or the new complete file.
/// </summary>
public static class PortHistoryManager
{
    private const string HistoryFileName = "qbPortWeaver.history.json";
    private const int MaxEntries = 50;

    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Appends one event, trimming the history to the most recent <see cref="MaxEntries"/>.
    /// Never throws - a failed write costs one history entry, never a sync cycle.</summary>
    public static void Append(PortHistoryKind kind, int? port, string eventText)
    {
        lock (_lock)
        {
            try
            {
                var entries = ReadCore();
                entries.Add(new PortHistoryEntry
                {
                    Timestamp = DateTimeOffset.Now,
                    Port = port,
                    Event = eventText,
                    Kind = kind,
                });
                if (entries.Count > MaxEntries)
                    entries.RemoveRange(0, entries.Count - MaxEntries);
                AppConstants.WriteAtomic(
                    AppConstants.GetDataFilePath(HistoryFileName),
                    JsonSerializer.Serialize(entries, _jsonOptions));
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PortHistoryManager.Append: {ex.Message}");
            }
        }
    }

    /// <summary>Returns the recorded events, oldest first. Empty when no history exists yet or the
    /// file cannot be read (a corrupt file is discarded on the next append).</summary>
    public static IReadOnlyList<PortHistoryEntry> Read()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"PortHistoryManager.Read: {ex.Message}");
            return [];
        }
    }

    /// <summary>Deletes the persisted history. Never throws.</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            AppConstants.DeleteFileSafely(AppConstants.GetDataFilePath(HistoryFileName));
        }
    }

    private static List<PortHistoryEntry> ReadCore()
    {
        string path = AppConstants.GetDataFilePath(HistoryFileName);
        if (!File.Exists(path))
            return [];
        return JsonSerializer.Deserialize<List<PortHistoryEntry>>(AppConstants.ReadAllTextShared(path), _readOptions) ?? [];
    }
}

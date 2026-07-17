using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>
/// In-memory counters for the current app session, shown in the Status panel's Statistics group.
/// Written by the sync loop (background thread) and read by the Status panel (UI thread) via
/// Interlocked/Volatile, so the panel always sees whole values. Deliberately not persisted:
/// "this session" is the scope that makes these numbers meaningful, and they reset naturally on
/// app restart. Port-derived figures (current port held, changes today) come from
/// PortHistoryManager instead - that history is persisted and already carries the timestamps.
/// </summary>
public static class SessionStats
{
    /// <summary>When monitoring started. Uses the process start time rather than a class-load
    /// timestamp, so the value is accurate even though this class is first touched later (first
    /// sync cycle or Status panel open).</summary>
    public static DateTimeOffset StartedAt { get; }

    static SessionStats()
    {
        using var process = Process.GetCurrentProcess();
        StartedAt = process.StartTime;
    }

    private static int _syncCount;
    private static int _syncOkCount;
    private static int _recoveryCount;

    /// <summary>Completed sync cycles this session. Skipped cycles are not counted - they are
    /// no-ops (sync disabled or VPN off), not attempts.</summary>
    public static int SyncCount => Volatile.Read(ref _syncCount);

    /// <summary>Successful sync cycles this session.</summary>
    public static int SyncOkCount => Volatile.Read(ref _syncOkCount);

    /// <summary>Auto-recovery actions dispatched this session.</summary>
    public static int RecoveryCount => Volatile.Read(ref _recoveryCount);

    /// <summary>Records one completed (non-skipped) sync cycle.</summary>
    public static void RecordSync(bool success)
    {
        Interlocked.Increment(ref _syncCount);
        if (success)
            Interlocked.Increment(ref _syncOkCount);
    }

    /// <summary>Records one dispatched auto-recovery action.</summary>
    public static void RecordRecovery() => Interlocked.Increment(ref _recoveryCount);
}

using System.Diagnostics;
using System.Text;

namespace qbPortWeaver;

/// <summary>Subsystem identifiers used as the source column in log entries.</summary>
public static class Subsystem
{
    public const string MainApp = "MainApp";
    public const string MediaManager = "MediaManager";
    public const string HelperService = LoggingConstants.HelperServiceSubsystem;
}

/// <summary>Singleton file-based logger with size-based rotation. Thread-safe.</summary>
public sealed class LogManager
{
    private const long MaxSize = 20 * 1024 * 1024; // 20 MB
    private const int MaxLogFiles = 5;   // Keep only 5 logfiles total (including current)
    private const int RotationCheckInterval = 100; // Check rotation every N writes

    // Pre-padded level labels indexed by LogLevel enum value (Info=0, Warn=1, Error=2, Debug=3).
    // Labels come from Shared.LoggingConstants so the helper service uses the same strings.
    private static readonly string[] _levelLabels =
    [
        LoggingConstants.LevelInfoLabel,
        LoggingConstants.LevelWarnLabel,
        LoggingConstants.LevelErrorLabel,
        LoggingConstants.LevelDebugLabel,
    ];

    // Static instance for global access - null until Initialize() is called
    private static LogManager? _instance;

    /// <summary>Raised after a Warn or Error entry is written, outside the write lock. Fired from background threads.</summary>
    public event Action<LogLevel>? WarnOrErrorLogged;

    /// <summary>
    /// Raised when writing to the log file fails, so the tray can surface a failure that by
    /// definition cannot be reported through the log. Fired outside the write lock, from background
    /// threads.
    /// <para>Latched: it fires once per failure episode and re-arms only after a later write
    /// succeeds. A failing log file usually keeps failing, so an unlatched event would fire on every
    /// entry the app tries to write - unbounded, and from the one path that cannot log about it.</para>
    /// <para><b>Subscribers must not log at Warn or Error.</b> Doing so re-enters this path and, if
    /// the write is still failing, raises the event again. Show it to the user instead, as MainForm
    /// does with a tray balloon.</para>
    /// </summary>
    public event Action? LogWriteFailed;

    /// <summary>Returns <see langword="true"/> after <see cref="Initialize"/> has been called.</summary>
    public static bool IsInitialized => _instance is not null;

    /// <summary>Returns the singleton instance. Throws <see cref="InvalidOperationException"/> if not yet initialized.</summary>
    public static LogManager Instance =>
        _instance ?? throw new InvalidOperationException(
            $"{nameof(LogManager)} has not been initialized. Call {nameof(Initialize)} first.");

    /// <summary>Absolute path to the active log file.</summary>
    public string LogFilePath { get; }
    private readonly object _lock = new object();
    private int _writeCount;
    // Whether the current write-failure episode has already been announced. Guarded by _lock,
    // cleared by the next successful write. See LogWriteFailed for why this is latched.
    private bool _writeFailureReported;

    private volatile bool _debugMode;

    /// <summary>When <see langword="true"/>, <see cref="LogDebug"/> writes entries; when <see langword="false"/>, debug calls are no-ops.</summary>
    public bool DebugMode
    {
        get => _debugMode;
        set => _debugMode = value;
    }

    /// <summary>
    /// Initializes the singleton with the given log file path. Throws if called more than once.
    /// Call exactly once during startup, before any background tasks are started.
    /// Atomic via <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> so a race between two
    /// startup paths reliably gives one winner and one thrower instead of overwriting silently.
    /// </summary>
    public static LogManager Initialize(string logFilePath)
    {
        // Build first, swap atomically. If the swap loses, the just-created instance is unreachable
        // and gets GC'd - one wasted allocation in the throw path, which is the exceptional case.
        var instance = new LogManager(logFilePath);
        if (Interlocked.CompareExchange(ref _instance, instance, null) is not null)
            throw new InvalidOperationException($"{nameof(LogManager)} has already been initialized");
        return instance;
    }

    private LogManager(string logFilePath)
    {
        LogFilePath = logFilePath;
    }

    /// <summary>Writes a log entry at the given level. Thread-safe.</summary>
    /// <returns><see langword="true"/> when the entry reached the file.</returns>
    /// <remarks>Almost every caller ignores this - a log write is best-effort and nothing should
    /// change course because one failed. <see cref="LogStateChange"/> is the exception: it must not
    /// record having reported a condition that was never written.
    /// <para>Deliberately not derived from the <c>writeFailed</c> flag below, which is only raised on
    /// the <i>first</i> failure of an episode and stays false for the rest - so it answers "should the
    /// user be told" rather than "did this reach the file", and inverting it would report the second
    /// and later failed writes as successes.</para></remarks>
    public bool LogMessage(string message, LogLevel level, string subsystem = Subsystem.MainApp)
    {
        bool shouldNotify = false;
        bool writeFailed = false;
        bool wrote = false;
        lock (_lock)
        {
            try
            {
                // Check if rotation is needed periodically (every N writes)
                _writeCount++;
                if (_writeCount >= RotationCheckInterval)
                {
                    _writeCount = 0;
                    RotateIfNeeded();
                }

                WriteRaw(FormatEntry(message, level, subsystem));
                wrote = true;
                shouldNotify = level is LogLevel.Warn or LogLevel.Error;
                // A successful write re-arms the failure report, so a later episode is announced
                // rather than swallowed as a duplicate of one the user has already dealt with.
                _writeFailureReported = false;
            }
            catch (Exception ex)
            {
                // Debug.WriteLine rather than any logging call: this IS the logging path, and it has
                // just failed. Everything below exists because that makes the failure invisible -
                // nothing reaches the file, and shouldNotify stays false, so the tray badge and
                // balloon that normally mark a problem never fire either.
                Debug.WriteLine($"LogManager.LogMessage: {ex.Message}");
                if (!_writeFailureReported)
                {
                    _writeFailureReported = true;
                    writeFailed = true;
                }
            }
        }

        if (shouldNotify)
            RaiseWarnOrErrorLogged(level);
        if (writeFailed)
            RaiseLogWriteFailed();
        return wrote;
    }

    /// <summary>
    /// Raises <see cref="WarnOrErrorLogged"/> for an entry written to the shared log file
    /// by an external writer (the helper service), so the tray UI can surface it as a log
    /// alert. Does not write a new entry. No-op for non-Warn/Error levels.
    /// </summary>
    public void NotifyExternalWarnOrError(LogLevel level)
    {
        if (level is LogLevel.Warn or LogLevel.Error)
            RaiseWarnOrErrorLogged(level);
    }

    // Invokes WarnOrErrorLogged subscribers under a try/catch so a throwing subscriber cannot
    // propagate back into the caller of LogMessage (which may be in the middle of unrelated
    // sync logic) and disrupt it. Logging the failure via Debug.WriteLine avoids recursing
    // back through LogMessage.
    private void RaiseWarnOrErrorLogged(LogLevel level)
    {
        try
        {
            WarnOrErrorLogged?.Invoke(level);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LogManager.RaiseWarnOrErrorLogged: subscriber threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Mirrors RaiseWarnOrErrorLogged: a throwing subscriber must not propagate back into the caller
    // of LogMessage, and the failure is reported through Debug.WriteLine rather than the log, which
    // has just proven it cannot be written to.
    private void RaiseLogWriteFailed()
    {
        try
        {
            LogWriteFailed?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LogManager.RaiseLogWriteFailed: subscriber threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Writes a blank line to the log file. Thread-safe.</summary>
    public void LogBlankLine()
    {
        lock (_lock)
        {
            try
            {
                WriteRaw(Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LogManager.LogBlankLine: {ex.Message}");
            }
        }
    }

    /// <summary>Writes a debug entry only when <see cref="DebugMode"/> is enabled. Thread-safe.</summary>
    public void LogDebug(string message, string subsystem = Subsystem.MainApp)
    {
        if (!DebugMode) return;
        LogMessage(message, LogLevel.Debug, subsystem);
    }

    // Last message written under each state key. Concurrent because the sync loop, the media import
    // task and the UI thread can all report state.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastStateMessage = new();

    /// <summary>
    /// Writes <paramref name="message"/> only when it differs from the last message logged under
    /// <paramref name="key"/>. Thread-safe.
    /// <para>For conditions re-evaluated every cycle that stay true until the user fixes them - an
    /// unconfigured setting, an offline network share. Logging those per cycle buries the entries that
    /// matter and, at <see cref="LogLevel.Warn"/> or above, drives the tray warning badge up
    /// indefinitely. Comparing the message rather than just the key means a condition that changes
    /// (a different folder goes offline) still gets announced.</para>
    /// <para>Call <see cref="ClearLogState"/> once the condition clears, so a later recurrence is
    /// reported instead of being swallowed as a duplicate.</para>
    /// <para><b>The check-then-set is deliberately not atomic.</b> The dictionary is concurrent, so each
    /// step is safe on its own, but two threads could pass the guard for the same key and message and
    /// both write. The cost of that is exactly one duplicate entry before they converge - no state is
    /// lost and the next call suppresses correctly - and every key in use has a single writing site, so
    /// it is not reachable today. Closing it would mean either holding a lock across the file write, in
    /// the one path where contention is least acceptable, or latching before the write - and the latch
    /// must stay after it, for the reason given at the assignment below.</para>
    /// </summary>
    public void LogStateChange(string key, string message, LogLevel level, string subsystem = Subsystem.MainApp)
    {
        if (_lastStateMessage.TryGetValue(key, out string? previous) && previous == message) return;

        // Latched only once the entry is on disk. Recording it first meant a failed write left the
        // condition marked as reported and suppressed from then on, even after the log recovered -
        // losing exactly the standing conditions (an unreachable share, a stale binding) that a
        // disk-full or permissions fault makes most worth having.
        if (LogMessage(message, level, subsystem))
            _lastStateMessage[key] = message;
    }

    /// <summary>Forgets the last message logged under <paramref name="key"/> so the next
    /// <see cref="LogStateChange"/> for it writes again. Call when the condition clears.</summary>
    public void ClearLogState(string key) => _lastStateMessage.TryRemove(key, out _);

    /// <summary>Deletes all log files and starts a fresh log. Thread-safe.</summary>
    public void ClearLogs()
    {
        lock (_lock)
        {
            try
            {
                // Delete rotated backup files
                for (int i = 1; i < MaxLogFiles; i++)
                {
                    string backup = $"{LogFilePath}.{i}";
                    if (File.Exists(backup))
                        File.Delete(backup);
                }

                if (File.Exists(LogFilePath))
                    File.Delete(LogFilePath);

                _writeCount = 0;

                // Re-arm every LogStateChange latch. Those entries are written once per condition
                // and then suppressed while the message stays the same, so without this a standing
                // condition - an interface mismatch, an unreachable share, a stale plugin binding -
                // is simply absent from the new log: still true, still suppressed as a duplicate of
                // an entry that no longer exists. The user clears the log to capture a clean
                // reproduction, and those are precisely the lines that explain it.
                _lastStateMessage.Clear();

                // Write the sentinel while still holding the lock so no concurrent LogMessage
                // can interleave between the delete and this entry.
                WriteRaw(FormatEntry("Logs cleared by user", LogLevel.Info, Subsystem.MainApp));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LogManager.ClearLogs: {ex.Message}");
            }
        }
    }

    /// <summary>Checks the log file size and rotates it if it exceeds the maximum. Thread-safe.</summary>
    internal void CheckAndRotateLogFile()
    {
        lock (_lock)
        {
            RotateIfNeeded();
        }
    }

    /// <summary>Logs a debug message and returns <see langword="false"/>, enabling single-line catch blocks.</summary>
    internal static bool LogDebugFalse(string message, string subsystem = Subsystem.MainApp)
    {
        Instance.LogDebug(message, subsystem);
        return false;
    }

    private static string FormatEntry(string message, LogLevel level, string subsystem) =>
        LoggingConstants.FormatLogEntry(DateTime.Now, _levelLabels[(int)level], subsystem, message);

    // Appends text to the log file. Must be called while holding _lock.
    // Opens a new stream per call intentionally - no persistent stream to manage across threads or rotation events.
    private void WriteRaw(string text)
    {
        using var fs = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(fs, Encoding.UTF8);
        writer.Write(text);
    }

    // Must be called while holding _lock
    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogFilePath))
                return;

            var fileInfo = new FileInfo(LogFilePath);
            if (fileInfo.Length > MaxSize)
            {
                // Shift existing backups up (.1 -> .2, etc.). The highest-numbered backup
                // is overwritten by the shift, so the oldest entries are dropped.
                RotateBackupFiles();

                // Move current log to .1
                string backupPath = $"{LogFilePath}.1";
                File.Move(LogFilePath, backupPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LogManager.RotateIfNeeded: {ex.Message}");
        }
    }

    // Shifts existing backup files up by one (.1 -> .2, etc.)
    private void RotateBackupFiles()
    {
        for (int i = MaxLogFiles - 2; i >= 1; i--)
        {
            string currentBackup = $"{LogFilePath}.{i}";
            string nextBackup = $"{LogFilePath}.{i + 1}";

            if (File.Exists(currentBackup))
                File.Move(currentBackup, nextBackup, overwrite: true);
        }
    }
}

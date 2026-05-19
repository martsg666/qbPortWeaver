using qbPortWeaver.Shared;
using System.Diagnostics;
using System.Text;

namespace qbPortWeaver
{
    /// <summary>Severity level for log entries.</summary>
    public enum LogLevel { Info, Warn, Error, Debug }

    /// <summary>Subsystem identifiers used as the source column in log entries.</summary>
    public static class Subsystem
    {
        public const string MainApp       = "MainApp";
        public const string MediaManager  = "MediaManager";
        public const string HelperService = LoggingConstants.HelperServiceSubsystem;
    }

    /// <summary>Singleton file-based logger with size-based rotation. Thread-safe.</summary>
    public sealed class LogManager
    {
        private const long MaxSize              = 5 * 1024 * 1024; // 5 MB
        private const int  MaxLogFiles          = 5;   // Keep only 5 logfiles total (including current)
        private const int  RotationCheckInterval = 100; // Check rotation every N writes

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

        /// <summary>Returns <see langword="true"/> after <see cref="Initialize"/> has been called.</summary>
        public static bool IsInitialized => _instance is not null;

        /// <summary>Returns the singleton instance. Throws <see cref="InvalidOperationException"/> if not yet initialized.</summary>
        public static LogManager Instance =>
            _instance ?? throw new InvalidOperationException(
                $"{nameof(LogManager)} has not been initialized. Call {nameof(Initialize)} first.");

        /// <summary>Absolute path to the active log file.</summary>
        public  string LogFilePath { get; }
        private readonly object _lock = new object();
        private int _writeCount;

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
        /// Not internally synchronized; relies on single-threaded startup sequencing.
        /// </summary>
        public static LogManager Initialize(string logFilePath)
        {
            if (_instance is not null)
                throw new InvalidOperationException($"{nameof(LogManager)} has already been initialized");
            _instance = new LogManager(logFilePath);
            return _instance;
        }

        private LogManager(string logFilePath)
        {
            LogFilePath = logFilePath;
        }

        /// <summary>Writes a log entry at the given level. Thread-safe.</summary>
        public void LogMessage(string message, LogLevel level, string subsystem = Subsystem.MainApp)
        {
            bool shouldNotify = false;
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
                    shouldNotify = level is LogLevel.Warn or LogLevel.Error;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LogManager.LogMessage: {ex.Message}");
                }
            }

            if (shouldNotify)
                RaiseWarnOrErrorLogged(level);
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
                    // Shift existing backups up (.1 → .2, etc.). The highest-numbered backup
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
}

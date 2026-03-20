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
        public const string HelperService = "HelperService";

        /// <summary>Length of the longest subsystem name, used for column padding.</summary>
        public const int MaxLength = 13; // "HelperService".Length
    }

    /// <summary>Singleton file-based logger with size-based rotation. Thread-safe.</summary>
    public sealed class LogManager
    {
        private const long MaxSize              = 5 * 1024 * 1024; // 5 MB
        private const int  MaxLogFiles          = 3;   // Keep only 3 logfiles total (including current)
        private const int  RotationCheckInterval = 100; // Check rotation every N writes

        // Static instance for global access - null until Initialize() is called
        private static LogManager? _instance;

        /// <summary>Returns <see langword="true"/> after <see cref="Initialize"/> has been called.</summary>
        public static bool IsInitialized => _instance != null;

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
            if (_instance != null)
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

                    string paddedType = level.ToString().ToUpperInvariant().PadRight(5);
                    string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {paddedType} | {subsystem.PadRight(Subsystem.MaxLength)} | {message}{Environment.NewLine}";

                    using var fs = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(fs, Encoding.UTF8);
                    writer.Write(logEntry);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LogManager.LogMessage: {ex.Message}");
                }
            }
        }

        /// <summary>Writes a blank line to the log file. Thread-safe.</summary>
        public void LogBlankLine()
        {
            lock (_lock)
            {
                try
                {
                    using var fs = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(fs, Encoding.UTF8);
                    writer.Write(Environment.NewLine);
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
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LogManager.ClearLogs: {ex.Message}");
                }
            }

            // Write fresh entry outside the lock (LogMessage acquires its own lock)
            LogMessage("Logs cleared by user", LogLevel.Info);
        }

        // Checks the log file size and rotates it if it exceeds the maximum. Thread-safe.
        internal void CheckAndRotateLogFile()
        {
            lock (_lock)
            {
                RotateIfNeeded();
            }
        }

        // Logs message at debug level and returns false, enabling single-line catch blocks
        internal static bool LogDebugFalse(string message, string subsystem = Subsystem.MainApp)
        {
            Instance.LogDebug(message, subsystem);
            return false;
        }

        // Internal rotation check - must be called while holding _lock
        private void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(LogFilePath))
                    return;

                var fileInfo = new FileInfo(LogFilePath);
                if (fileInfo.Length > MaxSize)
                {
                    // Delete oldest backup if we already have max files
                    string oldestBackup = $"{LogFilePath}.{MaxLogFiles - 1}";
                    if (File.Exists(oldestBackup))
                        File.Delete(oldestBackup);

                    // Shift existing backups up: .1 → .2
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

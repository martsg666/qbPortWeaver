using System.Diagnostics;
using System.Text;

namespace qbPortWeaver
{
    public enum LogLevel { Info, Warn, Error, Debug }

    public sealed class LogManager
    {
        private const long MaxSize              = 5 * 1024 * 1024; // 5 MB
        private const int  MaxLogFiles          = 3;   // Keep only 3 logfiles total (including current)
        private const int  RotationCheckInterval = 100; // Check rotation every N writes

        // Static instance for global access
        public static LogManager Instance { get; private set; } = null!;

        private readonly string _logFilePath;
        private readonly object _lock = new object();
        private int _writeCount;

        private volatile bool _debugMode;
        public bool DebugMode
        {
            get => _debugMode;
            set => _debugMode = value;
        }

        // Initializes the singleton; throws if called more than once
        public static LogManager Initialize(string logFilePath)
        {
            if (Instance != null)
                throw new InvalidOperationException($"{nameof(LogManager)} has already been initialized");
            return new LogManager(logFilePath);
        }

        private LogManager(string logFilePath)
        {
            _logFilePath = logFilePath;
            Instance     = this;
        }

        // Writes a log entry at the given level (thread-safe)
        public void LogMessage(string message, LogLevel level)
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
                    string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {paddedType} | {message}{Environment.NewLine}";

                    using var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(fs, Encoding.UTF8);
                    writer.Write(logEntry);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LogManager.LogMessage: {ex.Message}");
                }
            }
        }

        // Writes a debug entry only when DebugMode is enabled (thread-safe)
        public void LogDebug(string message)
        {
            if (!DebugMode) return;
            LogMessage(message, LogLevel.Debug);
        }

        // Clears all log files and starts fresh (thread-safe)
        public void ClearLogs()
        {
            lock (_lock)
            {
                try
                {
                    // Delete rotated backup files
                    for (int i = 1; i < MaxLogFiles; i++)
                    {
                        string backup = $"{_logFilePath}.{i}";
                        if (File.Exists(backup))
                            File.Delete(backup);
                    }

                    if (File.Exists(_logFilePath))
                        File.Delete(_logFilePath);

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

        // Checks log file size and rotates if it exceeds the maximum
        public void CheckAndRotateLogFile()
        {
            lock (_lock)
            {
                RotateIfNeeded();
            }
        }

        // Internal rotation check — must be called while holding _lock
        private void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(_logFilePath))
                    return;

                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Length > MaxSize)
                {
                    // Delete oldest backup if we already have max files
                    string oldestBackup = $"{_logFilePath}.{MaxLogFiles - 1}";
                    if (File.Exists(oldestBackup))
                        File.Delete(oldestBackup);

                    // Rotate existing backup files (.1 -> .2, current -> .1)
                    RotateBackupFiles();

                    string backupPath = $"{_logFilePath}.1";
                    File.Move(_logFilePath, backupPath, overwrite: true);
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
                string currentBackup = $"{_logFilePath}.{i}";
                string nextBackup = $"{_logFilePath}.{i + 1}";

                if (File.Exists(currentBackup))
                    File.Move(currentBackup, nextBackup, overwrite: true);
            }
        }
    }
}

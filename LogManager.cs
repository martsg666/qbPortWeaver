using System.Diagnostics;

namespace qbPortWeaver
{
    public class LogManager
    {

        private readonly string _logFilePath;
        private const long MaxSize = 5 * 1024 * 1024; // 5 MB

        // Constructor
        public LogManager(string logFilePath)
        {
            _logFilePath = logFilePath;
        }

        // Check log file size and delete if exceeds 5 MB
        public void CheckAndDeleteLogFile()
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    FileInfo fileInfo = new FileInfo(_logFilePath);

                    if (fileInfo.Length > MaxSize)
                    {
                        File.Delete(_logFilePath);
                        Debug.WriteLine($"Logfile deleted because it exceeded 5 MB: {_logFilePath}");
                    }
                    else
                    {
                        Debug.WriteLine($"Logfile size is OK: {fileInfo.Length} bytes");
                    }
                }
                else
                {
                    Debug.WriteLine($"Logfile does not exist: {_logFilePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking or deleting logfile: {ex.Message}");
            }
        }


        // Logging message to logfile
        public void LogMessage(string message, string type)
        {
            try
            {
                type = type.PadRight(5);
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {type} | {message}{Environment.NewLine}";
                File.AppendAllText(_logFilePath, logEntry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write to logfile: {ex.Message}");
            }
        }
    }
}

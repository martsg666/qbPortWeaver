using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Writes the sync cycle status snapshot to a JSON file for external tooling.</summary>
    public static class StatusManager
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

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
    }
}

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
            string tempPath = filePath + ".tmp";

            try
            {
                string json = JsonSerializer.Serialize(status, _jsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to write status file: {ex.Message}", LogLevel.Warn);
                try { File.Delete(tempPath); }
                catch (Exception) { /* Best-effort cleanup; ignore if temp file cannot be deleted. */ }
            }
        }
    }
}

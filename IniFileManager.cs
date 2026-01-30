using System.Diagnostics;
using System.Text.RegularExpressions;

namespace qbPortWeaver
{
    public class IniFileManager
    {
        private readonly string _iniFilePath;
        private readonly Dictionary<string, Dictionary<string, string>> _iniData;

        // Constructor
        public IniFileManager(string iniFilePath)
        {
            _iniFilePath = iniFilePath;
            _iniData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        // Loads the INI file into memory
        public bool Load()
        {
            try
            {
                if (!File.Exists(_iniFilePath))
                    return false;

                string currentSection = "global";
                _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int commentCount = 0;

                foreach (var raw in File.ReadAllLines(_iniFilePath))
                {
                    var trimmedLine = raw.Trim();
                    if (string.IsNullOrEmpty(trimmedLine))
                        continue;

                    // Section
                    var sectionMatch = Regex.Match(trimmedLine, @"^\[(.+)\]$");
                    if (sectionMatch.Success)
                    {
                        currentSection = sectionMatch.Groups[1].Value;
                        if (!_iniData.ContainsKey(currentSection))
                            _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        commentCount = 0;
                        continue;
                    }

                    // Comment
                    if (trimmedLine.StartsWith(";"))
                    {
                        commentCount++;
                        if (!_iniData.ContainsKey(currentSection))
                            _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _iniData[currentSection][$"Comment{commentCount}"] = trimmedLine;
                        continue;
                    }

                    // Key=Value
                    var keyValueMatch = Regex.Match(trimmedLine, @"^(.+?)\s*=(.*)$");
                    if (keyValueMatch.Success)
                    {
                        var key = keyValueMatch.Groups[1].Value.Trim();
                        var value = keyValueMatch.Groups[2].Value.Trim();
                        if (!_iniData.ContainsKey(currentSection))
                            _iniData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _iniData[currentSection][key] = value;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IniFileManager.Load error: {ex.Message}");
                return false;
            }
        }

        // Retrieves a value from the INI data
        public string GetValue(string section, string key)
        {
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
                return string.Empty;

            if (_iniData.TryGetValue(section, out var sectionDict) &&
                sectionDict.TryGetValue(key, out var value))
            {
                return value;
            }
            return string.Empty;
        }

        // Creates the INI file if missing
        public bool CreateIniFileIfMissing()
        {
            try
            {
                if (!File.Exists(_iniFilePath))
                {
                    // Default INI content
                    string defaultIniContent = @"
[general]

; Define update interval in seconds 
updateIntervalSeconds = 180

[qBittorrent]

; Define qBittorrent API credentials and URL
qBittorrentURL = http://127.0.0.1:443
qBittorrentUserName = admin
qBittorrentPassword = PASSWORD

; Define qBittorrent executable path
qBittorrentExePath = C:\Program Files\qBittorrent\qbittorrent.exe

; Define qBittorrent process name
qBittorrentProcessName = qbittorrent

; Define if qBittorrent should restart after a port change (recommended)
restartqBittorrent = True
";
                    // Ensure directory exists
                    Directory.CreateDirectory(Path.GetDirectoryName(_iniFilePath));

                    // Write default content
                    File.WriteAllText(_iniFilePath, defaultIniContent.Trim());

                    return true;
                }
                else
                {
                    Debug.WriteLine($"Failed to create INI file. File already exists.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create INI file: {ex.Message}");
                return false;
            }
        }
    }
}

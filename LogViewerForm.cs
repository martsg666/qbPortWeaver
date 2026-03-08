using System.Text;
using Microsoft.Win32;

namespace qbPortWeaver
{
    // Modeless log viewer with live tail updates and log-level colour coding.
    // Opened via the tray menu or tray icon double-click; only one instance is allowed at a time
    // (enforced by MainForm.ShowLogViewer).
    public partial class LogViewerForm : Form
    {
        private readonly string    _logFilePath;
        private readonly object    _readLock = new();
        private long               _lastReadPosition;
        private FileSystemWatcher? _watcher;
        private bool               _isDarkMode;
        private Color[]            _themeColors = null!; // initialized in OnLoad after _isDarkMode is set

        public LogViewerForm(string logFilePath)
        {
            InitializeComponent();
            _logFilePath = logFilePath;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _isDarkMode  = IsDarkModeEnabled();
            _themeColors = _isDarkMode
                ? [Color.OrangeRed, Color.Gold, Color.DodgerBlue, Color.DarkOrange, Color.Gainsboro]
                : [Color.Crimson, Color.Goldenrod, Color.SteelBlue, Color.DarkOrange, SystemColors.WindowText];
            _ = LoadInitialContentAsync(); // fire-and-forget: exceptions are caught within the method
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Disable watcher events before the form is fully disposed to prevent callbacks on a dead form.
            // Disposal is handled in Dispose(bool) in the Designer file.
            if (_watcher != null)
                _watcher.EnableRaisingEvents = false;
            base.OnFormClosed(e);
        }

        // Returns true if the user has enabled dark mode in Windows personalisation settings
        private static bool IsDarkModeEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("AppsUseLightTheme") as int?) == 0;
        }

        // Reads the full log file and builds its RTF representation on a background thread,
        // then sets rtbLog.Rtf in a single UI-thread operation for near-instant rendering.
        // StartWatcher is called in the finally block so _lastReadPosition is set before
        // any live-update events can fire.
        private async Task LoadInitialContentAsync()
        {
            try
            {
                if (!File.Exists(_logFilePath))
                {
                    AppendLine("(No log entries yet)", MetaColor);
                    return;
                }

                // Capture on UI thread — _themeColors must not be read from the background thread
                Color[] colors = _themeColors;

                (string rtf, long position) = await Task.Run(() =>
                {
                    lock (_readLock)
                    {
                        using var fs     = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        var lines        = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        return (BuildRtf(lines, colors), fs.Position);
                    }
                });

                if (IsDisposed) return;

                _lastReadPosition = position;
                rtbLog.Rtf = rtf;
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    AppendLine($"(Error reading log: {ex.Message})", Color.OrangeRed);
            }
            finally
            {
                StartWatcher();
            }
        }

        // Starts a FileSystemWatcher to detect new log entries and file rotation/clearing
        private void StartWatcher()
        {
            try
            {
                string? dir  = Path.GetDirectoryName(_logFilePath);
                string? file = Path.GetFileName(_logFilePath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                    return;

                _watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += (_, _) => OnLogFileUpdated();
                _watcher.Created += (_, _) => OnLogFileUpdated();
                _watcher.Deleted += (_, _) => OnLogFileDeleted();
            }
            catch (Exception ex)
            {
                AppendLine($"(Live updates unavailable: {ex.Message})", MetaColor);
            }
        }

        // Reads any new content appended since the last read and appends it to the RichTextBox
        private void OnLogFileUpdated()
        {
            try
            {
                string[] newLines;
                lock (_readLock)
                {
                    if (!File.Exists(_logFilePath))
                        return;

                    using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    // File shorter than expected — it was rotated; read from the start
                    if (fs.Length < _lastReadPosition)
                        _lastReadPosition = 0;

                    if (fs.Length == _lastReadPosition)
                        return;

                    fs.Seek(_lastReadPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    newLines          = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    _lastReadPosition = fs.Position;
                }

                if (newLines.Length == 0 || IsDisposed)
                    return;

                Invoke(() =>
                {
                    foreach (string line in newLines)
                        AppendColoredLine(line.TrimEnd('\r'));
                    ScrollToBottom();
                });
            }
            catch (Exception ex)
            {
                // Best-effort live update; transient errors during rotation or clear are expected
                LogManager.Instance.LogDebug($"LogViewerForm.OnLogFileUpdated: {ex.Message}");
            }
        }

        // Called when the log file is deleted (e.g. Clear Logs); resets the read position and clears the display
        private void OnLogFileDeleted()
        {
            lock (_readLock)
                _lastReadPosition = 0;

            if (IsDisposed) return;
            try { Invoke(() => rtbLog.Clear()); }
            catch (Exception) { /* ObjectDisposedException if form was disposed between the IsDisposed check and Invoke */ }
        }

        private void AppendColoredLine(string line) =>
            AppendLine(line, GetLineColor(line));

        // Maps a log line to its display colour using the shared colour index
        private Color GetLineColor(string line) => _themeColors[GetLineColorIndex(line)];

        // Returns the 0-based colour index for a log line, shared by the RTF builder and live-update renderer
        // Log format: "yyyy-MM-dd HH:mm:ss | LEVEL | message" (level padded to 5 chars)
        private static int GetLineColorIndex(string line)
        {
            if (line.Contains("| ERROR |", StringComparison.Ordinal)) return 0;
            if (line.Contains("| WARN  |", StringComparison.Ordinal)) return 1;
            if (line.Contains("| INFO  |", StringComparison.Ordinal)) return 2;
            if (line.Contains("| DEBUG |", StringComparison.Ordinal)) return 3;
            return 4;
        }

        // Convenience colour for meta/status messages (not log entries)
        private Color MetaColor => _isDarkMode ? Color.DimGray : SystemColors.GrayText;

        // Builds an RTF document from log lines using the provided colour palette.
        // Runs on a background thread — must not access any UI elements.
        private static string BuildRtf(string[] lines, Color[] colors)
        {
            var sb = new StringBuilder(lines.Length * 100);

            // RTF header: Unicode-safe, Consolas monospace font, colour table
            sb.Append("{\\rtf1\\ansi\\uc0\\deff0");
            sb.Append("{\\fonttbl{\\f0\\fmodern\\fprq1\\fcharset0 Consolas;}}");
            sb.Append("{\\colortbl ;");
            foreach (var c in colors)
                sb.Append($"\\red{c.R}\\green{c.G}\\blue{c.B};");
            sb.Append('}');

            // Consolas 9pt (18 half-points), no paragraph spacing
            sb.Append("\\f0\\fs18\\sb0\\sa0 ");

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');
                int    cf   = GetLineColorIndex(line) + 1; // RTF colour table is 1-based
                sb.Append($"\\cf{cf} ");
                AppendRtfText(sb, line);
                sb.Append("\\par ");
            }

            sb.Append('}');
            return sb.ToString();
        }

        // Appends RTF-escaped text, encoding RTF special characters and non-ASCII as Unicode escapes
        private static void AppendRtfText(StringBuilder sb, string text)
        {
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '{':  sb.Append("\\{");  break;
                    case '}':  sb.Append("\\}");  break;
                    default:
                        if (c > 127) sb.Append($"\\u{(int)c}?");
                        else sb.Append(c);
                        break;
                }
            }
        }

        private void AppendLine(string text, Color color)
        {
            rtbLog.SelectionStart  = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor  = color;
            rtbLog.AppendText(text + Environment.NewLine);
        }

        private void ScrollToBottom()
        {
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }
    }
}

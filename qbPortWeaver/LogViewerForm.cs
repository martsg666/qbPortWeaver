using System.ComponentModel;
using System.Text;
using Microsoft.Win32;

namespace qbPortWeaver
{
    // Modeless log viewer with live tail updates and log-level colour coding.
    // Opened via the tray menu or tray icon double-click; only one instance is allowed at a time
    // (enforced by MainForm.ShowLogViewer).
    public partial class LogViewerForm : Form
    {
        private readonly string      _logFilePath;
        private readonly object      _readLock  = new();
        private readonly List<string> _allLines = new(); // all raw lines in memory; rebuilt on filter change without re-reading the file
        private readonly List<int>   _searchMatches = new(); // character indices of current search hits in rtbLog
        private int                  _searchIndex = -1;
        private long                 _lastReadPosition;
        private FileSystemWatcher?   _watcher;
        private bool                 _isDarkMode;
        private Color[]              _themeColors    = null!; // initialized in OnLoad after _isDarkMode is set

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
            Text = $"{AppConstants.AppName} | Log Viewer";
            ApplyTheme();
            // Vertically center the search box — single-line TextBox auto-sizes its height from the font,
            // so the actual height is only known after layout; compute the top offset here.
            int searchTop = (pnlToolbar.Height - txtSearch.Height) / 2;
            txtSearch.Top = searchTop;

            // Size nav buttons and match count label to the search box height so everything is visually aligned
            btnPrev.Size      = new Size(btnPrev.Width, txtSearch.Height);
            btnNext.Size      = new Size(btnNext.Width, txtSearch.Height);
            btnPrev.Top       = searchTop;
            btnNext.Top       = searchTop;
            lblMatchCount.Top = searchTop + (txtSearch.Height - lblMatchCount.Height) / 2;

            // Position the × button inside the right edge of the search box.
            // Done here so the button tracks the auto-sized TextBox height and right-anchor position.
            int cbSize = txtSearch.Height - 4;
            btnClearSearch.Size     = new Size(cbSize, cbSize);
            btnClearSearch.Location = new Point(txtSearch.Right - cbSize - 2, searchTop + 2);
            // Must be in front of the native TextBox HWND or it will be hidden behind it
            btnClearSearch.BringToFront();
            _ = LoadInitialContentAsync(); // fire-and-forget; exceptions are handled inside LoadInitialContentAsync
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Disable watcher events before the form is fully disposed to prevent callbacks on a dead form.
            // Disposal is handled in Dispose(bool) in the Designer file.
            if (_watcher != null)
                _watcher.EnableRaisingEvents = false;
            base.OnFormClosed(e);
        }

        // Applies theme colors to the background, filter buttons, and search controls
        private void ApplyTheme()
        {
            Color bg   = _isDarkMode ? Color.FromArgb(30, 30, 30) : SystemColors.Window;
            Color fg   = _isDarkMode ? Color.Gainsboro : SystemColors.WindowText;
            Color border = _isDarkMode ? Color.FromArgb(80, 80, 80) : SystemColors.ControlDark;

            BackColor            = bg;
            pnlToolbar.BackColor = bg;
            rtbLog.BackColor     = bg;

            ApplyFilterButtonStyle(chkError, _themeColors[0]);
            ApplyFilterButtonStyle(chkWarn,  _themeColors[1]);
            ApplyFilterButtonStyle(chkInfo,  _themeColors[2]);
            ApplyFilterButtonStyle(chkDebug, _themeColors[3]);

            txtSearch.BackColor = bg;
            txtSearch.ForeColor = fg;

            foreach (var btn in new[] { btnPrev, btnNext })
            {
                btn.BackColor                  = bg;
                btn.ForeColor                  = fg;
                btn.FlatAppearance.BorderColor = border;
            }

            // Clear button sits inside the search box — blend it in rather than styling it like the nav buttons
            btnClearSearch.BackColor                 = txtSearch.BackColor;
            btnClearSearch.ForeColor                 = _isDarkMode ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText;
            btnClearSearch.FlatAppearance.BorderSize = 0;

            lblMatchCount.BackColor = bg;
            lblMatchCount.ForeColor = _isDarkMode ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText;
        }

        // Sets filter button foreground and border to the level colour when active, dimmed when inactive
        private void ApplyFilterButtonStyle(CheckBox chk, Color levelColor)
        {
            Color dimmed = _isDarkMode ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);
            chk.ForeColor                       = chk.Checked ? levelColor : dimmed;
            chk.FlatAppearance.BorderColor      = chk.Checked ? levelColor : dimmed;
            chk.FlatAppearance.CheckedBackColor = _isDarkMode ? Color.FromArgb(55, 55, 55) : Color.FromArgb(225, 225, 235);
            chk.BackColor                       = pnlToolbar.BackColor;
        }

        // Returns true if the user has enabled dark mode in Windows personalisation settings
        private static bool IsDarkModeEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("AppsUseLightTheme") as int?) == 0;
        }

        // Called when any filter CheckBox changes — updates its style and rebuilds the display
        private void FilterButton_CheckedChanged(object? sender, EventArgs e)
        {
            if (sender is CheckBox chk)
                ApplyFilterButtonStyle(chk, GetButtonLevelColor(chk));
            RebuildDisplay();
        }

        private Color GetButtonLevelColor(CheckBox chk)
        {
            if (chk == chkError) return _themeColors[0];
            if (chk == chkWarn)  return _themeColors[1];
            if (chk == chkInfo)  return _themeColors[2];
            return _themeColors[3];
        }

        private void CtxLog_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
            => ctxCopy.Enabled = rtbLog.SelectionLength > 0;

        private void BtnClearSearch_Click(object? sender, EventArgs e) => txtSearch.Clear();
        private void BtnPrev_Click(object? sender, EventArgs e)        => SearchPrev();
        private void BtnNext_Click(object? sender, EventArgs e)        => SearchNext();

        // Triggered when the search text changes — shows/hides the clear button, then refreshes matches
        private void TxtSearch_TextChanged(object? sender, EventArgs e)
        {
            btnClearSearch.Visible = txtSearch.Text.Length > 0;
            RefreshSearch(navigateToFirst: true);
        }

        // Handles Enter (next), Shift+Enter (prev), and Escape (clear) in the search box
        private void TxtSearch_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (e.Shift) SearchPrev(); else SearchNext();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                txtSearch.Clear();
                e.SuppressKeyPress = true;
            }
        }

        private void RefreshSearch(bool navigateToFirst = false)
        {
            _searchMatches.Clear();

            string query = txtSearch.Text;
            if (string.IsNullOrEmpty(query))
            {
                lblMatchCount.Text     = string.Empty;
                rtbLog.SelectionLength = 0;
                return;
            }

            // Scan plain text with IndexOf — avoids calling rtbLog.Find() which changes the
            // RichTextBox selection on every call and causes visible flashing.
            string text  = rtbLog.Text;
            int    start = 0;
            while (true)
            {
                int found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                _searchMatches.Add(found);
                start = found + 1;
            }

            if (_searchMatches.Count == 0)
            {
                _searchIndex           = -1;
                lblMatchCount.Text     = "No matches";
                rtbLog.SelectionLength = 0;
                return;
            }

            if (navigateToFirst || _searchIndex < 0 || _searchIndex >= _searchMatches.Count)
                _searchIndex = 0;

            NavigateToMatch(_searchIndex);
        }

        private void SearchNext()
        {
            if (_searchMatches.Count == 0) return;
            NavigateToMatch((_searchIndex + 1) % _searchMatches.Count);
        }

        private void SearchPrev()
        {
            if (_searchMatches.Count == 0) return;
            NavigateToMatch(_searchIndex <= 0 ? _searchMatches.Count - 1 : _searchIndex - 1);
        }

        private void NavigateToMatch(int index)
        {
            _searchIndex = index;
            rtbLog.Select(_searchMatches[index], txtSearch.Text.Length);
            rtbLog.ScrollToCaret();
            lblMatchCount.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
        }

        // Rebuilds the RTF display from the in-memory line store, applying the current filter.
        // Preserves the scroll position: only scrolls to bottom if the user was already there.
        private void RebuildDisplay()
        {
            bool    wasAtBottom = IsAtBottom();
            bool[]  filters     = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];
            Color[] colors      = _themeColors;
            string[] filtered   = _allLines.Where(l => IsLineVisibleWithFilters(l, filters)).ToArray();
            rtbLog.Rtf = BuildRtf(filtered, colors);
            RefreshSearch(navigateToFirst: true);
            if (wasAtBottom) ScrollToBottom();
        }

        // Returns true if the user is scrolled to the bottom of the log.
        // Compares line numbers rather than char indices: GetCharIndexFromPosition at the
        // bottom-left of the viewport returns the *first* char of the bottom-visible line,
        // which for a long last line is far below TextLength-2, making a char-index comparison
        // return false even when fully scrolled to the end.
        private bool IsAtBottom()
        {
            if (rtbLog.TextLength == 0) return true;
            int lastVisibleLine = rtbLog.GetLineFromCharIndex(
                rtbLog.GetCharIndexFromPosition(new Point(0, rtbLog.ClientSize.Height - 1)));
            int totalLines = rtbLog.GetLineFromCharIndex(rtbLog.TextLength);
            return lastVisibleLine >= totalLines - 1;
        }

        // Static — safe to call from background threads (no UI state access).
        // Meta/unclassified lines (index >= 4) are always shown.
        private static bool IsLineVisibleWithFilters(string line, bool[] filters)
        {
            int idx = GetLineColorIndex(line);
            return idx >= filters.Length || filters[idx];
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

                // Capture UI-thread state before entering the background task
                Color[] colors  = _themeColors;
                bool[]  filters = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];

                (string rtf, long position, string[] allLines) = await Task.Run(() =>
                {
                    lock (_readLock)
                    {
                        using var fs     = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        string[] lines   = reader.ReadToEnd()
                                                 .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(l => l.TrimEnd('\r'))
                                                 .ToArray();
                        string[] filtered = lines.Where(l => IsLineVisibleWithFilters(l, filters)).ToArray();
                        return (BuildRtf(filtered, colors), fs.Position, lines);
                    }
                });

                if (IsDisposed) return;

                _allLines.AddRange(allLines);
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
            if (IsDisposed) return;
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

        // Reads any new content appended since the last read and appends visible lines to the display.
        // Only scrolls to the bottom if the user was already there before the update.
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
                    string    raw    = reader.ReadToEnd();

                    // Only process content up to the last complete line. The FileSystemWatcher
                    // fires as soon as the OS flushes a write, which can happen before the logger
                    // has finished writing the full line. Reading past the last '\n' would capture
                    // a partial line, store it in _allLines, and advance _lastReadPosition past it —
                    // leaving an orphaned fragment in the display that can never be corrected.
                    int lastNl = raw.LastIndexOf('\n');
                    if (lastNl < 0) return; // no complete line yet; wait for the next cycle

                    string complete = raw[..(lastNl + 1)];
                    // Use fs.Position (actual file offset, includes any BOM bytes) minus the tail
                    // byte count so the tail is re-read next cycle. += GetByteCount(complete) would
                    // be 3 bytes short after a StreamReader-consumed UTF-8 BOM, producing a stray
                    // 'r' line in the viewer (last bytes of the prior entry re-read as a new line).
                    _lastReadPosition = fs.Position - Encoding.UTF8.GetByteCount(raw[(lastNl + 1)..]);

                    newLines = complete.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                       .Select(l => l.TrimEnd('\r'))
                                       .ToArray();
                }

                if (newLines.Length == 0 || IsDisposed)
                    return;

                try
                {
                    Invoke(() => AppendNewLines(newLines));
                }
                catch (ObjectDisposedException) { /* form disposed between IsDisposed check and Invoke — expected on close */ }
            }
            catch (Exception ex)
            {
                // Best-effort live update; transient errors during rotation or clear are expected
                LogManager.Instance.LogDebug($"LogViewerForm.OnLogFileUpdated: {ex.Message}");
            }
        }

        // Called when the log file is deleted (e.g. Clear Logs); resets state and clears the display
        private void OnLogFileDeleted()
        {
            lock (_readLock)
                _lastReadPosition = 0;

            if (IsDisposed) return;
            try
            {
                Invoke(() =>
                {
                    _allLines.Clear();
                    rtbLog.Clear();
                });
            }
            catch (ObjectDisposedException) { /* form disposed between IsDisposed check and Invoke — expected on close */ }
        }

        // Appends new lines to the in-memory store and rebuilds the display from scratch.
        // Rebuilding is simpler and more reliable than SelectedRtf line-by-line appending:
        // Win32 RichEdit can merge the last paragraph of one Invoke batch with the first of
        // the next when a repaint occurs between calls, regardless of the \par mechanism used.
        // Must be called on the UI thread.
        private void AppendNewLines(string[] newLines)
        {
            bool wasAtBottom = IsAtBottom();
            bool[] filters   = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];

            // Capture the first visible line before the rebuild so we can restore the scroll
            // position when the user is not at the bottom. Setting .Rtf resets scroll to the top.
            int firstVisibleLine = wasAtBottom ? 0 :
                rtbLog.GetLineFromCharIndex(rtbLog.GetCharIndexFromPosition(new Point(0, 0)));

            foreach (string line in newLines)
                _allLines.Add(line);

            string[] filtered = _allLines.Where(l => IsLineVisibleWithFilters(l, filters)).ToArray();
            rtbLog.Rtf = BuildRtf(filtered, _themeColors);

            if (wasAtBottom)
            {
                ScrollToBottom();
            }
            else
            {
                int charIdx = rtbLog.GetFirstCharIndexFromLine(firstVisibleLine);
                if (charIdx >= 0)
                {
                    rtbLog.SelectionStart = charIdx;
                    rtbLog.ScrollToCaret();
                }
            }

            // Update match count if a search is active — new lines may contain additional hits
            if (!string.IsNullOrEmpty(txtSearch.Text))
                RefreshSearch(navigateToFirst: false);
        }

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

        // Writes the RTF document header shared by BuildRtf and AppendLine:
        // Unicode-safe, Consolas 9pt (18 half-points), no paragraph spacing, colour table.
        private static void AppendRtfHeader(StringBuilder sb, Color[] colors)
        {
            sb.Append("{\\rtf1\\ansi\\uc0\\deff0");
            sb.Append("{\\fonttbl{\\f0\\fmodern\\fprq1\\fcharset0 Consolas;}}");
            sb.Append("{\\colortbl ;");
            foreach (var c in colors)
                sb.Append($"\\red{c.R}\\green{c.G}\\blue{c.B};");
            sb.Append('}');
            sb.Append("\\f0\\fs18\\sb0\\sa0 ");
        }

        // Builds an RTF document from log lines using the provided colour palette.
        // Runs on a background thread — must not access any UI elements.
        private static string BuildRtf(string[] lines, Color[] colors)
        {
            var sb = new StringBuilder(lines.Length * 100);
            AppendRtfHeader(sb, colors);

            foreach (string line in lines)
            {
                int cf = GetLineColorIndex(line) + 1; // RTF colour table is 1-based
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
                        if (c > 127) sb.Append($"\\u{(int)c} ");
                        else sb.Append(c);
                        break;
                }
            }
        }

        // Used only for one-off meta/error messages (e.g. "No log entries yet", watcher errors).
        // Replaces the entire RTF content with a single styled line — safe because meta messages
        // are shown before any real log content, or when the watcher has already failed.
        private void AppendLine(string text, Color color)
        {
            // Map color to a 1-based RTF colour-table index.
            // Falls back to the last entry for colours not in the theme palette (e.g. MetaColor).
            int colorIdx = _themeColors.Length;
            for (int i = 0; i < _themeColors.Length; i++)
            {
                if (_themeColors[i] == color) { colorIdx = i + 1; break; }
            }

            var sb = new StringBuilder();
            AppendRtfHeader(sb, _themeColors);
            sb.Append($"\\cf{colorIdx} ");
            AppendRtfText(sb, text);
            sb.Append("\\par}");
            rtbLog.Rtf = sb.ToString();
        }

        private void ScrollToBottom()
        {
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }

        // Custom TextBox that draws a vertically-centered placeholder.
        // Shadowing PlaceholderText prevents the base class from sending EM_SETCUEBANNER,
        // which renders the native cue at the top-left regardless of the control height.
        private sealed class PlaceholderTextBox : TextBox
        {
            private string _placeholderText = string.Empty;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public new string PlaceholderText
            {
                get => _placeholderText;
                set { _placeholderText = value; Invalidate(); }
            }

            protected override void OnGotFocus(EventArgs e)  { base.OnGotFocus(e);  Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override void WndProc(ref Message m)
            {
                const int WM_PAINT = 0x000F;
                base.WndProc(ref m);
                if (m.Msg == WM_PAINT && TextLength == 0 && !Focused && _placeholderText.Length > 0)
                {
                    using var g    = Graphics.FromHwnd(Handle);
                    var       rect = ClientRectangle;
                    rect.Inflate(-2, 0);
                    TextRenderer.DrawText(g, _placeholderText, Font, rect, SystemColors.GrayText,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                        TextFormatFlags.SingleLine     | TextFormatFlags.NoPadding);
                }
            }
        }
    }
}

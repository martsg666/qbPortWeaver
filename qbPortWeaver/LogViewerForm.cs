using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace qbPortWeaver;

/// <summary>Modeless log viewer with live tail updates and log-level colour coding. Opened via the tray menu or tray icon double-click; only one instance is allowed at a time (enforced by MainForm.ShowOrActivate).</summary>
public partial class LogViewerForm : Form
{
    // One parsed log line: the raw text and its level classified once at parse time
    // (indexes match _themeColors; LevelMeta = unclassified, e.g. lines without a level column).
    private readonly record struct LogLine(string Text, byte Level);

    private const byte LevelError = 0;
    private const byte LevelWarn = 1;
    private const byte LevelInfo = 2;
    private const byte LevelDebug = 3;
    private const byte LevelMeta = 4;

    private readonly string _logFilePath;
    private string _activeLogFilePath;
    private bool _navigateToLatestIssue;
    private readonly object _readLock = new();
    // Source of truth: every parsed line of the active file, in order. The virtual list view
    // renders rows on demand from this store via _visibleRows, so no second copy of the log
    // exists inside a native control and a filter change never re-renders the document.
    private readonly List<LogLine> _allLines = new();
    // Indices into _allLines that pass the current level/subsystem filters, in order. This is
    // what the list view shows: row r displays _allLines[_visibleRows[r]]. Rebuilt in one pass
    // on any filter change (milliseconds even for very large logs).
    private readonly List<int> _visibleRows = new();
    // Search hits over the visible rows, sorted by (Row, Offset). Painted per visible row in
    // lvLog_DrawItem, so no highlight cap is needed - offscreen matches cost nothing.
    private readonly List<(int Row, int Offset)> _searchMatches = new();
    private int _searchIndex = -1;
    private long _lastReadPosition;
    private FileSystemWatcher? _watcher;
    // Incremented under _readLock whenever the active log file changes so in-flight
    // FileSystemWatcher events from the prior watcher can detect they are stale and bail.
    // FileSystemWatcher.Dispose() does not synchronously wait for handlers already queued
    // on the threadpool, so without this guard a stale event would read the newly-active
    // file at a freshly-reset offset and duplicate content against LoadInitialContentAsync.
    private int _watcherGeneration;
    private Color[] _themeColors = []; // per-level line palette; resolved for the active theme in OnLoad
    // Longest visible line in characters; drives the single column's width so the horizontal
    // scrollbar covers the widest line (monospace font, so chars * _charWidth is exact).
    private int _maxLineLength;
    private float _charWidth;   // measured monospace character width, device pixels
    private int _textPadding;   // left text inset inside a row, device pixels
    // Overlay shown for status/placeholder text (loading, empty, error). A ListView cannot
    // vertically centre a message, so these live on a Label that covers the log area.
    private Label? _metaLabel;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativeListViewItem lParam);

    // Asks Windows to trim the calling process's working set so Task Manager reflects live
    // memory. Used only on close (see OnFormClosed) - trimming pages out live memory too, so
    // running it while the viewer is open would make the next interaction pay a page-fault storm.
    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    // Returns the current-process pseudo-handle (a constant, not a real handle - no open/close
    // needed), used for self-targeted calls like EmptyWorkingSet.
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    // Minimal LVITEM layout for LVM_SETITEMSTATE (only mask/state fields are read for state changes).
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeListViewItem
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr LParam;
    }

    private static class WinMsg
    {
        public const int WM_PAINT = 0x000F;
        public const int LVM_SETITEMSTATE = 0x102B;
        public const int LVM_GETITEMSTATE = 0x102C;
        public const uint LVIF_STATE = 0x0008;
        public const uint LVIS_FOCUSED = 0x0001;
        public const uint LVIS_SELECTED = 0x0002;
    }

    // Log column markers (format: "| LEVEL | ") used to classify lines once at parse time
    private const string ColError = "| ERROR |";
    private const string ColWarn = "| WARN  |";
    private const string ColInfo = "| INFO  |";
    private const string ColDebug = "| DEBUG |";
    private const long LoadingIndicatorMinBytes = 1_000_000; // show "Loading..." only for logs large enough that the read + parse is perceptible
    private const int ClearButtonInset = 4; // shrinks button to fit inside the TextBox border (2 px top + 2 px bottom)
    private const int ClearButtonMargin = 2; // inner gap from TextBox right edge and top

    public LogViewerForm() : this(string.Empty) { } // designer support only

    /// <summary>Opens the log viewer for the specified log file.</summary>
    /// <param name="logFilePath">Path to the log file to display.</param>
    /// <param name="navigateToLatestIssue">When <see langword="true"/>, scrolls to the most recent WARN or ERROR entry on open.</param>
    public LogViewerForm(string logFilePath, bool navigateToLatestIssue = false)
    {
        InitializeComponent();
        _logFilePath = logFilePath;
        _activeLogFilePath = logFilePath;
        _navigateToLatestIssue = navigateToLatestIssue;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _themeColors = [AppConstants.LogError, AppConstants.LogWarning, AppConstants.LogInfo, AppConstants.LogDebug, SystemColors.WindowText];
        Text = $"{AppIdentity.AppName} | Log Viewer";
        KeyPreview = true; // form sees keys before the focused control - see OnKeyDown (Escape to close)
        ApplyTheme();
        // Measure the monospace character width once for the active font/DPI. Averaged over a
        // block of characters so GDI padding rounding does not skew the per-char value; used for
        // the column width and to position search-highlight runs within a row.
        const int MeasureChars = 64;
        _charWidth = TextRenderer.MeasureText(new string('0', MeasureChars), lvLog.Font,
            Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width / (float)MeasureChars;
        _textPadding = LogicalToDeviceUnits(4);
        lvLog.ClientSizeChanged += (_, _) => UpdateColumnWidth();

        // Vertically center the search box - single-line TextBox auto-sizes its height from the font,
        // so the actual height is only known after layout; compute the top offset here.
        int searchTop = (pnlToolbar.Height - txtSearch.Height) / 2;
        txtSearch.Top = searchTop;
        cboSubsystem.Top = (pnlToolbar.Height - cboSubsystem.Height) / 2;
        cboLogFile.Top = (pnlToolbar.Height - cboLogFile.Height) / 2;

        // Size all nav buttons to match the search box height. Their up/down chevrons are owner-drawn
        // in NavButton_Paint (crisp, perfectly centered, using the button's ForeColor) rather than a
        // font glyph, which never centered cleanly.
        int navH = txtSearch.Height;
        foreach (var btn in new[] { btnPrev, btnNext, btnIssuePrev, btnIssueNext })
        {
            btn.Height = navH;
            btn.Top = searchTop;
            btn.Paint += NavButton_Paint;
        }

        // Match the level-filter buttons to the same height and vertical centering as the rest of the
        // toolbar (the designer gives them a taller, top-aligned box), so the whole row lines up.
        foreach (var chk in new[] { chkError, chkWarn, chkInfo, chkDebug })
        {
            chk.Height = navH;
            chk.Top = searchTop;
        }
        lblMatchCount.Top = searchTop + (txtSearch.Height - lblMatchCount.Height) / 2;

        // Position the × button inside the right edge of the search box.
        // Done here so the button tracks the auto-sized TextBox height and right-anchor position.
        // Scale the logical-pixel constants with DPI so the button stays proportional at 125%+.
        int clearButtonInset = LogicalToDeviceUnits(ClearButtonInset);
        int clearButtonMargin = LogicalToDeviceUnits(ClearButtonMargin);
        int cbSize = txtSearch.Height - clearButtonInset;
        btnClearSearch.Size = new Size(cbSize, cbSize);
        btnClearSearch.Location = new Point(txtSearch.Right - cbSize - clearButtonMargin, searchTop + clearButtonMargin);
        // Must be in front of the native TextBox HWND or it will be hidden behind it
        btnClearSearch.BringToFront();

        // Lock the minimum width so the right-anchored search block can never slide over the
        // left-side filter controls (same runtime-MinimumSize approach as MediaManagerForm, but
        // computed from the actual toolbar layout so the window still shrinks below its default
        // size). txtSearch is the leftmost right-anchored control; cboLogFile ends the left block.
        int toolbarGap = LogicalToDeviceUnits(8);
        int minClientWidth = cboLogFile.Right + toolbarGap + (ClientSize.Width - txtSearch.Left);
        MinimumSize = new Size(Width - ClientSize.Width + minClientWidth, MinimumSize.Height);

        PopulateLogFileDropdown();
        // Wire event after population to avoid triggering a load before the initial LoadInitialContentAsync below
        cboLogFile.SelectedIndexChanged += cboLogFile_SelectedIndexChanged;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Start the load only after the form's first paint so the "Loading..." overlay below is
        // actually drawn before the blocking read/parse begins (see LoadInitialContentAsync).
        _ = LoadInitialContentAsync(); // fire-and-forget; exceptions are handled inside LoadInitialContentAsync
    }

    // Escape closes the viewer, except while the search box has focus - there Escape clears the search
    // (handled in txtSearch_KeyDown). KeyPreview (set in OnLoad) lets the form see the key first.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !txtSearch.Focused)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Stop watcher events before the form is fully disposed to prevent callbacks on a dead form.
        // Actual disposal is handled in Dispose(bool) in the Designer file.
        if (_watcher is not null)
            _watcher.EnableRaisingEvents = false;

        // Release the line store now instead of waiting for heap pressure: on a large log it is
        // the process's dominant allocation, and without an explicit collection the freed memory
        // sits in the heap (and Task Manager) long after the viewer is gone. The store's backing
        // arrays live on the Large Object Heap, which only a compacting collection returns to the
        // OS; the working-set trim then pages out the remainder. Both are free here - nobody
        // interacts with the window after close (this is also why neither runs while it is open).
        _allLines.Clear();
        _allLines.TrimExcess();
        _visibleRows.Clear();
        _visibleRows.TrimExcess();
        _searchMatches.Clear();
        _searchMatches.TrimExcess();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Forced + blocking: Optimized lets the GC decline the collection (leaving the CompactOnce
        // flag armed to fire on an unpredictable later gen2), and background collections do not
        // compact the LOH.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true); // NOSONAR S1215 - blocking required for LOH compaction
        // Best-effort: return value ignored by design (a failed trim just leaves pages resident,
        // to be evicted later under memory pressure); pseudo-handle, so nothing to dispose.
        EmptyWorkingSet(GetCurrentProcess());

        base.OnFormClosed(e);
    }

    // Applies theme colors to the background, filter buttons, and search controls
    private void ApplyTheme()
    {
        // Native surface colors: SystemColors track the active dark/light mode under Application.SetColorMode.
        // Chrome and the read-only log use the dialog surface (Control) so the viewer matches the rest of the
        // app; editable fields use the input surface (Window) like every other form. Only the per-level
        // filter buttons use accent colors.
        Color surface = SystemColors.Control;
        Color input = SystemColors.Window;
        Color fg = SystemColors.WindowText;
        Color border = SystemColors.ControlDark;

        BackColor = surface;
        pnlToolbar.BackColor = surface;
        lvLog.BackColor = surface;
        lvLog.ForeColor = fg;

        ApplyFilterButtonStyle(chkError, _themeColors[LevelError]);
        ApplyFilterButtonStyle(chkWarn, _themeColors[LevelWarn]);
        ApplyFilterButtonStyle(chkInfo, _themeColors[LevelInfo]);
        ApplyFilterButtonStyle(chkDebug, _themeColors[LevelDebug]);

        cboSubsystem.BackColor = input;
        cboSubsystem.ForeColor = fg;

        cboLogFile.BackColor = input;
        cboLogFile.ForeColor = fg;

        txtSearch.BackColor = input;
        txtSearch.ForeColor = fg;

        foreach (var btn in new[] { btnPrev, btnNext, btnIssuePrev, btnIssueNext })
        {
            btn.BackColor = surface;
            btn.ForeColor = fg;
            btn.FlatAppearance.BorderColor = border;
        }

        // Issue-nav jumps between WARN/ERROR lines; tint it with the OS accent (severity-neutral,
        // mode-aware) so it reads as distinct from the neutral search-match arrows by the search box.
        btnIssuePrev.ForeColor = SystemColors.HotTrack;
        btnIssueNext.ForeColor = SystemColors.HotTrack;

        // Clear button sits inside the search box - blend it in rather than styling it like the nav buttons
        btnClearSearch.BackColor = txtSearch.BackColor;
        btnClearSearch.ForeColor = SystemColors.GrayText;
        btnClearSearch.FlatAppearance.BorderSize = 0;

        lblMatchCount.BackColor = surface;
        lblMatchCount.ForeColor = SystemColors.GrayText;
    }

    // Sets filter button foreground and border to the level colour when active, and the native
    // dimmed/disabled gray when inactive. The checked background is a subtle native tint; all three
    // track the color mode through SystemColors.
    private void ApplyFilterButtonStyle(CheckBox chk, Color levelColor)
    {
        chk.ForeColor = chk.Checked ? levelColor : SystemColors.GrayText;
        chk.FlatAppearance.BorderColor = chk.Checked ? levelColor : SystemColors.GrayText;
        chk.FlatAppearance.CheckedBackColor = SystemColors.ControlLight;
        chk.BackColor = pnlToolbar.BackColor;
    }

    // Owner-draws a crisp up/down chevron centered in a nav button, in the button's ForeColor (neutral
    // WindowText for the search-match arrows, the accent for the issue-nav arrows). Drawn instead of a
    // font glyph so it is always centered and its size/weight are exact. btnPrev/btnIssuePrev point up.
    private void NavButton_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Button btn) return;
        bool up = btn == btnPrev || btn == btnIssuePrev;

        float scale = btn.DeviceDpi / 96f;
        float halfW = 5f * scale;     // chevron half-width
        float halfH = 3.25f * scale;  // chevron half-height
        float cx = btn.ClientSize.Width / 2f;
        float cy = btn.ClientSize.Height / 2f;
        float armY  = up ? cy + halfH : cy - halfH; // the two ends
        float apexY = up ? cy - halfH : cy + halfH; // the point

        PointF[] chevron = [new(cx - halfW, armY), new(cx, apexY), new(cx + halfW, armY)];

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(btn.ForeColor, 1.8f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(pen, chevron);
    }

    // Called when any filter CheckBox changes - updates its style and rebuilds the visible rows
    private void filterButton_CheckedChanged(object? sender, EventArgs e)
    {
        if (sender is CheckBox chk)
            ApplyFilterButtonStyle(chk, GetButtonLevelColor(chk));
        RebuildDisplay();
    }

    // Called when the subsystem filter ComboBox changes - rebuilds the visible rows
    private void cboSubsystem_SelectedIndexChanged(object? sender, EventArgs e) => RebuildDisplay();

    // Returns the padded subsystem column token to match (e.g. "| MainApp       |"),
    // or null when "All" is selected (no filter). Built here, once per rebuild, so the
    // per-line check in IsLineVisible is a single allocation-free Contains.
    // Matching the full padded column (not a bare "| Name" prefix) prevents false hits
    // on message text that happens to contain the same characters.
    private string? GetSubsystemFilter()
    {
        var selected = cboSubsystem.SelectedItem?.ToString();
        return selected is null or "All"
            ? null
            : $"| {selected.PadRight(LoggingConstants.SubsystemMaxLength)} |";
    }

    private Color GetButtonLevelColor(CheckBox chk)
    {
        if (chk == chkError) return _themeColors[LevelError];
        if (chk == chkWarn) return _themeColors[LevelWarn];
        if (chk == chkInfo) return _themeColors[LevelInfo];
        return _themeColors[LevelDebug];
    }

    private void ctxLog_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ctxCopy.Enabled = lvLog.SelectedIndices.Count > 0;

    private void ctxCopy_Click(object? sender, EventArgs e) => CopySelectedRows();
    private void ctxCopyAll_Click(object? sender, EventArgs e) => CopyAllVisibleRows();
    private void ctxSelectAll_Click(object? sender, EventArgs e) => SelectAllRows();

    private void btnClearSearch_Click(object? sender, EventArgs e) => txtSearch.Clear();
    private void btnPrev_Click(object? sender, EventArgs e) => SearchPrev();
    private void btnNext_Click(object? sender, EventArgs e) => SearchNext();
    private void btnIssuePrev_Click(object? sender, EventArgs e) => IssuePrev();
    private void btnIssueNext_Click(object? sender, EventArgs e) => IssueNext();

    // Triggered when the search text changes - shows/hides the clear button and refreshes matches.
    // Highlights are painted per visible row in lvLog_DrawItem, so a repaint is all that is needed
    // to show or clear them - no document rebuild.
    private void txtSearch_TextChanged(object? sender, EventArgs e)
    {
        btnClearSearch.Visible = txtSearch.Text.Length > 0;
        RefreshSearch(navigateToFirst: true);
        lvLog.Invalidate();
    }

    // Handles Enter (next), Shift+Enter (prev), and Escape (clear) in the search box
    private void txtSearch_KeyDown(object? sender, KeyEventArgs e)
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

    // Keyboard shortcuts on the log list mirroring the context menu
    private void lvLog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control) return;
        switch (e.KeyCode)
        {
            case Keys.C:
                CopySelectedRows();
                e.Handled = true;
                break;
            case Keys.A:
                SelectAllRows();
                e.Handled = true;
                break;
        }
    }

    // Mouse drag-selection: a native ListView only range-selects via Shift+Click, so dragging
    // across rows (the way one selects in a text box) is implemented here. MouseDown anchors on
    // the pressed row; MouseMove extends the selection to the row under the cursor, scrolling
    // when the drag leaves the viewport (the control captures the mouse, so out-of-bounds
    // coordinates keep arriving).
    private int _dragAnchorRow = -1;
    private int _dragLastRow = -1;

    private void lvLog_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // Shift/Ctrl clicks are native range/toggle selection - do not start a drag anchor for them.
        if ((ModifierKeys & (Keys.Shift | Keys.Control)) != 0) return;
        _dragAnchorRow = lvLog.HitTest(e.Location).Item?.Index ?? -1;
        _dragLastRow = _dragAnchorRow;
    }

    private void lvLog_MouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragAnchorRow < 0) return;

        // Clamp to the client area for the hit test; the raw Y decides edge auto-scroll below.
        var probe = new Point(
            Math.Clamp(e.X, 0, lvLog.ClientSize.Width - 1),
            Math.Clamp(e.Y, 0, lvLog.ClientSize.Height - 1));
        int row = lvLog.HitTest(probe).Item?.Index ?? -1;
        if (row < 0) return;

        // Dragging past the top or bottom edge advances one row per move event.
        if (e.Y < 0 && row > 0) row--;
        else if (e.Y > lvLog.ClientSize.Height && row < lvLog.VirtualListSize - 1) row++;

        if (row == _dragLastRow) return;
        _dragLastRow = row;
        lvLog.EnsureVisible(row);
        SelectRowRange(_dragAnchorRow, row);
    }

    private void lvLog_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragAnchorRow = -1;
        _dragLastRow = -1;
    }

    // Replaces the selection with the inclusive row range [a, b] (either order).
    private void SelectRowRange(int a, int b)
    {
        SetAllRowsSelected(false);
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        for (int r = lo; r <= hi; r++)
            SetRowState(r, WinMsg.LVIS_SELECTED);
        lvLog.Invalidate();
    }

    // Rescans the visible rows for the current query and rebuilds the sorted match list.
    // scrollToMatch: when false, updates match list and count label but does not scroll.
    // Used by AppendNewLines to avoid yanking the user away from their scroll position.
    private void RefreshSearch(bool navigateToFirst = false, bool scrollToMatch = true)
    {
        _searchMatches.Clear();

        string query = txtSearch.Text;
        if (string.IsNullOrEmpty(query))
        {
            lblMatchCount.Text = string.Empty;
            return;
        }

        ScanRowsForMatches(0, query);

        if (_searchMatches.Count == 0)
        {
            _searchIndex = -1;
            lblMatchCount.Text = "0 / 0";
            return;
        }

        if (navigateToFirst || _searchIndex < 0 || _searchIndex >= _searchMatches.Count)
            _searchIndex = 0;

        if (scrollToMatch)
            NavigateToMatch(_searchIndex);
        else
            lblMatchCount.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
    }

    // Appends matches for visible rows from startRow onward (0 = full rescan). Matches stay
    // sorted by (Row, Offset) because rows are scanned in order.
    private void ScanRowsForMatches(int startRow, string query)
    {
        for (int r = startRow; r < _visibleRows.Count; r++)
        {
            string text = _allLines[_visibleRows[r]].Text;
            int start = 0;
            while (true)
            {
                int found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                _searchMatches.Add((r, found));
                start = found + 1;
            }
        }
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
        SelectRow(_searchMatches[index].Row);
        lblMatchCount.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
    }

    // Selects a single row, gives it the keyboard focus (so arrow/Shift+arrow navigation
    // continues from it), and scrolls it into view. Shared by search and issue navigation.
    private void SelectRow(int row)
    {
        if (row < 0 || row >= lvLog.VirtualListSize) return;
        SetAllRowsSelected(false);
        SetRowState(row, WinMsg.LVIS_SELECTED | WinMsg.LVIS_FOCUSED);
        lvLog.EnsureVisible(row);
        lvLog.Invalidate(); // repaint so the previous selection clears immediately
    }

    // Sets the given state bits (selected/focused) on one row via LVM_SETITEMSTATE.
    private void SetRowState(int row, uint state)
    {
        var item = new NativeListViewItem
        {
            Mask = WinMsg.LVIF_STATE,
            State = state,
            StateMask = state,
        };
        SendMessage(lvLog.Handle, WinMsg.LVM_SETITEMSTATE, row, ref item);
    }

    // Anchor row for issue navigation: the selected row, or -1 when nothing is selected.
    private int SelectedRow => lvLog.SelectedIndices.Count > 0 ? lvLog.SelectedIndices[0] : -1;

    // Scrolls to the previous (older) WARN or ERROR line relative to the current selection.
    private void IssuePrev()
    {
        int start = (SelectedRow < 0 ? _visibleRows.Count : SelectedRow) - 1;
        for (int r = start; r >= 0; r--)
        {
            if (_allLines[_visibleRows[r]].Level is LevelError or LevelWarn)
            {
                SelectRow(r);
                return;
            }
        }
    }

    // Scrolls to the next (newer) WARN or ERROR line relative to the current selection.
    private void IssueNext()
    {
        for (int r = SelectedRow + 1; r < _visibleRows.Count; r++)
        {
            if (_allLines[_visibleRows[r]].Level is LevelError or LevelWarn)
            {
                SelectRow(r);
                return;
            }
        }
    }

    /// <summary>Scrolls to the most recent (last) WARN or ERROR line in the log, falling back to
    /// the bottom of the log if none are present. Used when opening the log viewer with unviewed
    /// alerts (via tray balloon click or Show Logs).</summary>
    public void NavigateToLatestIssue()
    {
        for (int r = _visibleRows.Count - 1; r >= 0; r--)
        {
            if (_allLines[_visibleRows[r]].Level is LevelError or LevelWarn)
            {
                SelectRow(r);
                return;
            }
        }
        ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        if (lvLog.VirtualListSize > 0)
            lvLog.EnsureVisible(lvLog.VirtualListSize - 1);
    }

    // Returns true if the user is scrolled to the bottom of the log (last row visible).
    private bool IsAtBottom()
    {
        int count = lvLog.VirtualListSize;
        if (count == 0) return true;
        var top = lvLog.TopItem;
        if (top is null) return true;
        return top.Index + RowsPerPage(top) >= count;
    }

    // Rows that fit in the viewport, derived from the first visible row's height.
    private int RowsPerPage(ListViewItem topItem)
    {
        int rowHeight = Math.Max(1, topItem.Bounds.Height);
        return Math.Max(1, lvLog.ClientSize.Height / rowHeight);
    }

    // Rebuilds the visible-row index from the in-memory line store with the current filters,
    // then re-runs the search over the new rows. This is an index pass over cached levels - no
    // document is re-rendered, so it completes in milliseconds regardless of log size.
    // Preserves the viewport: stays at the bottom if the user was there, otherwise keeps the
    // previously top-most line in view.
    private void RebuildDisplay()
    {
        bool wasAtBottom = IsAtBottom();
        // Remember which source line was at the top so the viewport can be re-anchored after the
        // visible set changes (the same line may land on a different row index).
        int anchorLine = -1;
        var top = lvLog.TopItem;
        if (!wasAtBottom && top is not null && top.Index < _visibleRows.Count)
            anchorLine = _visibleRows[top.Index];

        RebuildVisibleRows();
        lvLog.VirtualListSize = _visibleRows.Count;
        UpdateColumnWidth();
        if (_visibleRows.Count > 0)
            HideMetaLabel();

        if (wasAtBottom)
            ScrollToBottom();
        else if (anchorLine >= 0)
            ScrollRowToTop(NearestRowForLine(anchorLine));

        RefreshSearch(navigateToFirst: true, scrollToMatch: false);
        lvLog.Invalidate();
    }

    // Recomputes _visibleRows (and the widest-line measurement) from _allLines and the current filters.
    private void RebuildVisibleRows()
    {
        bool[] filters = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];
        string? subsystemFilter = GetSubsystemFilter();

        _visibleRows.Clear();
        _maxLineLength = 0;
        for (int i = 0; i < _allLines.Count; i++)
        {
            if (!IsLineVisible(_allLines[i], filters, subsystemFilter)) continue;
            _visibleRows.Add(i);
            if (_allLines[i].Text.Length > _maxLineLength) _maxLineLength = _allLines[i].Text.Length;
        }
    }

    // First visible row whose source line is at or after the given line index (binary search -
    // _visibleRows is sorted). Clamped to the last row when the line is beyond the visible end.
    private int NearestRowForLine(int line)
    {
        int idx = _visibleRows.BinarySearch(line);
        if (idx < 0) idx = ~idx;
        return Math.Min(idx, _visibleRows.Count - 1);
    }

    // Scrolls so the given row is the top-most visible row: EnsureVisible only scrolls minimally,
    // so first bring a row one page further into view, then the target row.
    private void ScrollRowToTop(int row)
    {
        if (row < 0 || lvLog.VirtualListSize == 0) return;
        var top = lvLog.TopItem;
        int page = top is not null ? RowsPerPage(top) : 1;
        lvLog.EnsureVisible(Math.Min(lvLog.VirtualListSize - 1, row + page - 1));
        lvLog.EnsureVisible(row);
    }

    // Meta/unclassified lines (e.g. lines without a level column) are shown only when all level
    // filters are active and no subsystem filter is set; hiding them otherwise prevents stray
    // lines from appearing in a filtered view.
    private static bool IsLineVisible(LogLine line, bool[] filters, string? subsystemToken)
    {
        if (line.Level == LevelMeta) return subsystemToken is null && Array.TrueForAll(filters, f => f);
        if (!filters[line.Level]) return false;                     // level filtered out
        if (subsystemToken is not null && !line.Text.Contains(subsystemToken, StringComparison.Ordinal)) return false;
        return true;
    }

    // Convenience colour for meta/status messages (not log entries)
    private static Color MetaColor => SystemColors.GrayText; // mode-aware under SetColorMode

    // Supplies the virtual list view with an item on demand. Owner drawing reads the store
    // directly, but the item text is still provided for accessibility tooling.
    private void lvLog_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        e.Item = new ListViewItem(
            e.ItemIndex >= 0 && e.ItemIndex < _visibleRows.Count
                ? _allLines[_visibleRows[e.ItemIndex]].Text
                : string.Empty);
    }

    // Owner-draws one visible row: selection or surface background, search-match highlight runs,
    // then the line text in its level colour. Only on-screen rows are ever drawn, so highlight
    // count and log size have no effect on paint cost.
    private void lvLog_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visibleRows.Count) return;
        LogLine line = _allLines[_visibleRows[e.ItemIndex]];
        bool selected = IsRowSelected(e.ItemIndex);

        using (var back = new SolidBrush(selected ? SystemColors.Highlight : lvLog.BackColor))
            e.Graphics.FillRectangle(back, e.Bounds);

        // Search-match highlight runs (monospace font, so offset * _charWidth is exact).
        // Skipped on the selected row - the selection background already marks it.
        int queryLength = txtSearch.Text.Length;
        if (!selected && queryLength > 0 && _searchMatches.Count > 0)
        {
            using var highlight = new SolidBrush(AppConstants.SearchHighlight);
            foreach (int offset in MatchOffsetsForRow(e.ItemIndex))
            {
                var run = new RectangleF(
                    e.Bounds.Left + _textPadding + offset * _charWidth,
                    e.Bounds.Top,
                    queryLength * _charWidth,
                    e.Bounds.Height);
                e.Graphics.FillRectangle(highlight, run);
            }
        }

        var textBounds = new Rectangle(e.Bounds.Left + _textPadding, e.Bounds.Top, e.Bounds.Width - _textPadding, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, line.Text, lvLog.Font, textBounds,
            selected ? SystemColors.HighlightText : _themeColors[line.Level],
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    // Queries the native control for a row's real selection state. DrawListViewItemEventArgs.State
    // is documented as unreliable in Details view (it reported every row as selected here), so the
    // owner-draw path asks via LVM_GETITEMSTATE instead - an O(1) message per painted row.
    private bool IsRowSelected(int row) =>
        ((long)SendMessage(lvLog.Handle, WinMsg.LVM_GETITEMSTATE, row, (nint)WinMsg.LVIS_SELECTED) & WinMsg.LVIS_SELECTED) != 0;

    // Yields the match offsets for one row from the sorted match list (binary search to the
    // first entry of the row, then walk while the row matches).
    private IEnumerable<int> MatchOffsetsForRow(int row)
    {
        int lo = 0, hi = _searchMatches.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_searchMatches[mid].Row < row) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _searchMatches.Count && _searchMatches[i].Row == row; i++)
            yield return _searchMatches[i].Offset;
    }

    // Keeps the single column wide enough for the longest visible line (so the horizontal
    // scrollbar covers it) and never narrower than the viewport (so the selection bar spans
    // the full width on short lines).
    private void UpdateColumnWidth()
    {
        int contentWidth = _textPadding * 2 + (int)Math.Ceiling(_maxLineLength * _charWidth);
        colLog.Width = Math.Max(contentWidth, lvLog.ClientSize.Width);
    }

    private void CopySelectedRows()
    {
        if (lvLog.SelectedIndices.Count == 0) return;
        var sb = new StringBuilder();
        foreach (int row in lvLog.SelectedIndices)
            sb.AppendLine(_allLines[_visibleRows[row]].Text);
        AppConstants.TrySetClipboardText(sb.ToString());
    }

    private void CopyAllVisibleRows()
    {
        var sb = new StringBuilder();
        foreach (int line in _visibleRows)
            sb.AppendLine(_allLines[line].Text);
        AppConstants.TrySetClipboardText(sb.ToString());
    }

    // Selects every row via one native LVM_SETITEMSTATE broadcast (item index -1 = all items).
    // Adding rows to SelectedIndices one by one would send a message per row, which stalls for
    // seconds on very large logs.
    private void SelectAllRows()
    {
        if (lvLog.VirtualListSize == 0) return;
        SetAllRowsSelected(true);
        lvLog.Invalidate();
    }

    // Sets or clears the selected bit on all rows in one native broadcast (item index -1).
    private void SetAllRowsSelected(bool selected)
    {
        var item = new NativeListViewItem
        {
            Mask = WinMsg.LVIF_STATE,
            State = selected ? WinMsg.LVIS_SELECTED : 0,
            StateMask = WinMsg.LVIS_SELECTED,
        };
        SendMessage(lvLog.Handle, WinMsg.LVM_SETITEMSTATE, -1, ref item);
    }

    // Reads the full log file and parses it on a background thread, then swaps the store and
    // visible rows in on the UI thread. StartWatcher is called in the finally block so
    // _lastReadPosition is set before any live-update events can fire.
    private async Task LoadInitialContentAsync()
    {
        try
        {
            string loadPath = _activeLogFilePath;
            if (!File.Exists(loadPath))
            {
                SetMetaMessage("(No log entries yet)", MetaColor);
                return;
            }

            // Show a loading hint, but only for logs large enough that the read + parse is
            // perceptible - small logs (the common case) would otherwise flicker a "Loading" frame.
            if (new FileInfo(loadPath).Length > LoadingIndicatorMinBytes)
            {
                SetMetaMessage("Loading…", MetaColor);
                // Force the overlay to paint now, before the read/parse below completes and hides it.
                _metaLabel!.Refresh();
            }

            (LogLine[] lines, long position) = await Task.Run(() =>
            {
                lock (_readLock)
                {
                    using var fs = new FileStream(loadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    return (ParseLogLines(reader.ReadToEnd()), fs.Position);
                }
            });

            if (IsDisposed) return;

            _allLines.AddRange(lines);
            _lastReadPosition = position;

            RebuildVisibleRows();
            lvLog.VirtualListSize = _visibleRows.Count;
            UpdateColumnWidth();
            if (_visibleRows.Count > 0)
                HideMetaLabel();

            if (_navigateToLatestIssue) { NavigateToLatestIssue(); _navigateToLatestIssue = false; } else ScrollToBottom();
            if (!string.IsNullOrEmpty(txtSearch.Text))
                RefreshSearch(navigateToFirst: true);

            // Give the list keyboard focus so arrow/Shift+arrow selection works immediately -
            // unless the user has already started typing a search.
            if (!txtSearch.Focused)
                lvLog.Focus();
        }
        catch (Exception ex)
        {
            // _themeColors[LevelError] is the error color already resolved for the active theme; using a
            // fixed dark variant would clash with the foreground text colour in light mode.
            if (!IsDisposed)
                SetMetaMessage($"(Error reading log: {ex.Message})", _themeColors[LevelError]);
        }
        finally
        {
            StartWatcher();
        }
    }

    // Starts a FileSystemWatcher to detect new log entries and file rotation/clearing.
    // Only watches the current (non-rotated) log file; backup files are immutable.
    private void StartWatcher()
    {
        if (IsDisposed) return;
        if (_activeLogFilePath != _logFilePath) return;
        try
        {
            string? dir = Path.GetDirectoryName(_activeLogFilePath);
            string? file = Path.GetFileName(_activeLogFilePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                return;

            // Capture the current generation so events from this watcher can be distinguished
            // from events queued by a previously-disposed watcher (see _watcherGeneration).
            int generation = _watcherGeneration;
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            // Renamed (rotation moving the live file to .1) is deliberately not subscribed:
            // the subsequent Created/Changed events reset the read offset via the length check
            // in OnLogFileUpdated, so the new file's content is appended after the pre-rotation
            // lines already on screen. That preserves on-screen history across a rotation
            // instead of clearing the view mid-session.
            _watcher.Changed += (s, e) => OnLogFileUpdated(generation);
            _watcher.Created += (s, e) => OnLogFileUpdated(generation);
            _watcher.Deleted += (s, e) => OnLogFileDeleted(generation);
        }
        catch (Exception ex)
        {
            SetMetaMessage($"(Live updates unavailable: {ex.Message})", MetaColor);
        }
    }

    // Reads any new content appended since the last read and appends the parsed lines to the store.
    // Only scrolls to the bottom if the user was already there before the update.
    // The generation parameter holds the value of _watcherGeneration at watcher-subscription time.
    // Events with a stale generation are ignored to defend against the race between in-flight
    // FileSystemWatcher events and a file switch. See _watcherGeneration for details.
    private void OnLogFileUpdated(int generation)
    {
        try
        {
            LogLine[] newLines;
            lock (_readLock)
            {
                // Bail if a file switch happened between this event being queued and us
                // acquiring the lock - reading _activeLogFilePath here would read the
                // newly-active file at a reset offset and duplicate content.
                if (generation != _watcherGeneration) return;
                if (!File.Exists(_activeLogFilePath))
                    return;

                using var fs = new FileStream(_activeLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // File shorter than expected - it was rotated; read from the start
                if (fs.Length < _lastReadPosition)
                    _lastReadPosition = 0;

                if (fs.Length == _lastReadPosition)
                    return;

                fs.Seek(_lastReadPosition, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string raw = reader.ReadToEnd();

                // Only process content up to the last complete line. The FileSystemWatcher
                // fires as soon as the OS flushes a write, which can happen before the logger
                // has finished writing the full line. Reading past the last '\n' would capture
                // a partial line, store it in _allLines, and advance _lastReadPosition past it -
                // leaving an orphaned fragment in the display that can never be corrected.
                int lastNl = raw.LastIndexOf('\n');
                if (lastNl < 0) return; // no complete line yet; wait for the next cycle

                string complete = raw[..(lastNl + 1)];
                // Use fs.Position (actual file offset, includes any BOM bytes) minus the tail
                // byte count so the tail is re-read next cycle. += GetByteCount(complete) would
                // be 3 bytes short after a StreamReader-consumed UTF-8 BOM, producing a stray
                // 'r' line in the viewer (last bytes of the prior entry re-read as a new line).
                _lastReadPosition = fs.Position - Encoding.UTF8.GetByteCount(raw[(lastNl + 1)..]);

                newLines = ParseLogLines(complete);
            }

            if (newLines.Length == 0 || IsDisposed)
                return;

            try
            {
                Invoke(() =>
                {
                    // Re-check on the UI thread: the switch handler also runs here, so it may
                    // have completed between our lock release and this Invoke landing.
                    if (generation != _watcherGeneration) return;
                    AppendNewLines(newLines);
                });
            }
            catch (ObjectDisposedException) { /* form disposed between IsDisposed check and Invoke - expected on close */ }
            catch (InvalidOperationException) { /* handle destroyed by Close() before Dispose() - expected on close */ }
        }
        catch (Exception ex)
        {
            // Best-effort live update; transient errors during rotation or clear are expected
            LogManager.Instance.LogDebug($"LogViewerForm.OnLogFileUpdated: {ex.Message}");
        }
    }

    // Called when the log file is deleted (e.g. Clear Logs); resets state and clears the display.
    // See OnLogFileUpdated for the generation parameter's purpose.
    private void OnLogFileDeleted(int generation)
    {
        lock (_readLock)
        {
            if (generation != _watcherGeneration) return;
            _lastReadPosition = 0;
        }

        if (IsDisposed) return;
        try
        {
            Invoke(() =>
            {
                if (generation != _watcherGeneration) return;
                _allLines.Clear();
                _visibleRows.Clear();
                _searchMatches.Clear();
                _searchIndex = -1;
                _maxLineLength = 0;
                lvLog.VirtualListSize = 0;
                lvLog.Invalidate();
            });
        }
        catch (ObjectDisposedException) { /* form disposed between IsDisposed check and Invoke - expected on close */ }
        catch (InvalidOperationException) { /* handle destroyed by Close() before Dispose() - expected on close */ }
    }

    // Populates the log file dropdown with the current log and any existing rotated backups.
    // Called once on load; event is wired afterward to prevent a premature file switch.
    private void PopulateLogFileDropdown()
    {
        cboLogFile.Items.Clear();
        cboLogFile.Items.Add(new LogFileEntry("Current", _logFilePath));
        for (int i = 1; File.Exists($"{_logFilePath}.{i}"); i++)
            cboLogFile.Items.Add(new LogFileEntry($"Backup {i}", $"{_logFilePath}.{i}"));
        cboLogFile.SelectedIndex = 0;
    }

    private void cboLogFile_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cboLogFile.SelectedItem is not LogFileEntry entry) return;
        if (entry.FilePath == _activeLogFilePath) return;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _activeLogFilePath = entry.FilePath;

        lock (_readLock)
        {
            // Invalidate any in-flight events from the disposed watcher before clearing state.
            _watcherGeneration++;
            _allLines.Clear();
            _lastReadPosition = 0;
        }
        _visibleRows.Clear();
        _searchMatches.Clear();
        _searchIndex = -1;
        _maxLineLength = 0;
        lvLog.VirtualListSize = 0;
        lvLog.Invalidate();

        _ = LoadInitialContentAsync();
    }

    private readonly record struct LogFileEntry(string DisplayName, string FilePath)
    {
        public override string ToString() => DisplayName;
    }

    // Appends new lines to the store and extends the visible rows for those that pass the
    // current filters - no re-render of existing rows. Only scrolls if the user was at the
    // bottom, so the viewport stays stable during a live tail. Must be called on the UI thread.
    private void AppendNewLines(LogLine[] newLines)
    {
        bool wasAtBottom = IsAtBottom();
        bool[] filters = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];
        string? subsystemFilter = GetSubsystemFilter();

        int firstNewRow = _visibleRows.Count;
        int firstNewLine = _allLines.Count;
        _allLines.AddRange(newLines);

        for (int i = firstNewLine; i < _allLines.Count; i++)
        {
            if (!IsLineVisible(_allLines[i], filters, subsystemFilter)) continue;
            _visibleRows.Add(i);
            if (_allLines[i].Text.Length > _maxLineLength) _maxLineLength = _allLines[i].Text.Length;
        }

        if (_visibleRows.Count == firstNewRow) return; // nothing visible was added

        HideMetaLabel();
        lvLog.VirtualListSize = _visibleRows.Count;
        UpdateColumnWidth();

        // Extend the match list for the appended rows only (full rescan not needed - existing
        // rows are untouched by an append).
        if (txtSearch.Text.Length > 0)
        {
            ScanRowsForMatches(firstNewRow, txtSearch.Text);
            if (_searchMatches.Count > 0 && _searchIndex < 0) _searchIndex = 0;
            if (_searchMatches.Count > 0)
                lblMatchCount.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
        }

        if (wasAtBottom)
            ScrollToBottom();
    }

    // Splits raw log content on newlines, strips trailing \r so CRLF and LF files produce
    // identical lines, and classifies each line's level once (the level drives filtering and
    // row colouring for the rest of the line's lifetime). Blank lines are kept as meta rows:
    // LogManager writes one before each "Sync cycle started" as a deliberate visual separator
    // between cycles (IsLineVisible hides them in filtered views so no stray gaps appear).
    private static LogLine[] ParseLogLines(string raw)
    {
        string[] parts = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<LogLine>(parts.Length);
        foreach (string part in parts)
        {
            string text = part.TrimEnd('\r');
            lines.Add(new LogLine(text, ClassifyLine(text)));
        }
        return lines.ToArray();
    }

    // Returns the level index for a log line, matching the _themeColors palette.
    // Log format: "yyyy-MM-dd HH:mm:ss | LEVEL | Subsystem     | message" (level padded to 5 chars)
    private static byte ClassifyLine(string line)
    {
        if (line.Contains(ColError, StringComparison.Ordinal)) return LevelError;
        if (line.Contains(ColWarn, StringComparison.Ordinal)) return LevelWarn;
        if (line.Contains(ColInfo, StringComparison.Ordinal)) return LevelInfo;
        if (line.Contains(ColDebug, StringComparison.Ordinal)) return LevelDebug;
        return LevelMeta;
    }

    // Shows a centered status/placeholder message (loading, empty, error) over the log area.
    // The overlay Label fully covers lvLog with the same background, so it reads as the log's
    // own empty/error state; it is hidden again as soon as real content is displayed.
    private void SetMetaMessage(string text, Color color)
    {
        EnsureMetaLabel();
        _metaLabel!.BackColor = lvLog.BackColor;
        _metaLabel.ForeColor = color;
        _metaLabel.Text = text;
        SyncMetaLabelBounds();
        _metaLabel.Visible = true;
        _metaLabel.BringToFront();
    }

    // Creates the overlay Label as a sibling of lvLog (not a child - list views do not host
    // child controls reliably across repaints) and keeps it aligned to lvLog's bounds.
    private void EnsureMetaLabel()
    {
        if (_metaLabel is not null) return;
        _metaLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = lvLog.Font,
            Visible = false,
        };
        lvLog.Parent!.Controls.Add(_metaLabel);
        SyncMetaLabelBounds();
        lvLog.SizeChanged += (_, _) => SyncMetaLabelBounds();
        lvLog.LocationChanged += (_, _) => SyncMetaLabelBounds();
    }

    private void SyncMetaLabelBounds()
    {
        if (_metaLabel is not null) _metaLabel.Bounds = lvLog.Bounds;
    }

    private void HideMetaLabel()
    {
        if (_metaLabel is not null) _metaLabel.Visible = false;
    }

    // Double-buffered virtual list view: the base ListView flickers on scroll and repaint when
    // owner-drawn, and DoubleBuffered is protected, so a minimal subclass enables it.
    private sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
        }
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

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WinMsg.WM_PAINT && TextLength == 0 && !Focused && _placeholderText.Length > 0)
            {
                using var g = Graphics.FromHwnd(Handle);
                var rect = ClientRectangle;
                rect.Inflate(-2, 0);
                TextRenderer.DrawText(g, _placeholderText, Font, rect, SystemColors.GrayText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }
}

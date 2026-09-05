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

    // Palette indices into _themeColors, deliberately NOT the Shared.LogLevel enum values: that enum
    // is Info=0, Warn=1, Error=2, Debug=3, so Error and Info are swapped relative to these. Nothing
    // converts between the two - ClassifyLine derives the level from the line's text, never from a
    // LogLevel - so the disagreement is harmless today, and both orderings are load-bearing in their
    // own file (LogManager._levelLabels indexes by the enum and says so).
    //
    // Do not index _themeColors with a cast LogLevel: it would paint Info entries in the Error colour
    // and vice versa, in the one tool used to diagnose problems, without anything failing loudly. And
    // do not renumber these to match the enum without moving _themeColors and the four filter
    // checkboxes in step - LevelMeta has no enum counterpart, so the two can never fully align.
    private const byte LevelError = 0;
    private const byte LevelWarn = 1;
    private const byte LevelInfo = 2;
    private const byte LevelDebug = 3;
    private const byte LevelMeta = 4;

    private readonly string _logFilePath;
    private string _activeLogFilePath;
    private bool _navigateToLatestIssuePending;
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
    // scrollbar covers the widest line. chars * _charWidth is approximate for lines containing
    // font-fallback glyphs (a few px of scroll-range slack - invisible); highlight runs use the
    // exact MeasureMatchRun instead.
    private int _maxLineLength;
    private float _charWidth;   // measured monospace character width, device pixels
    private int _textPadding;   // left text inset inside a row, device pixels
    // Overlay shown for status/placeholder text (loading, empty, error). A ListView cannot
    // vertically centre a message, so these live on a Label that covers the log area.
    private Label? _metaLabel;
    // Mouse drag-selection state - see the comment block on lvLog_MouseDown for the design.
    private int _dragAnchorRow = -1;
    private int _dragLastRow = -1;
    private System.Windows.Forms.Timer? _dragScrollTimer;
    private int _dragScrollDirection; // -1 = scrolling up, +1 = scrolling down, 0 = idle
    private const int DragScrollIntervalMs = 60; // ~17 rows/s edge auto-scroll

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
        public const int LVM_SCROLL = 0x1014;
        public const int LVM_SETITEMSTATE = 0x102B;
        public const int LVM_GETITEMSTATE = 0x102C;
        public const uint LVIF_STATE = 0x0008;
        public const uint LVIS_FOCUSED = 0x0001;
        public const uint LVIS_SELECTED = 0x0002;
    }

    // Log column markers (format: "| LEVEL | ") used to classify lines once at parse time.
    // Built from the shared level labels so the viewer cannot drift from the two writers that
    // produce these entries - LogManager (main app) and HelperLogger (helper service) both take
    // their labels from LoggingConstants, and the labels carry their own padding to a fixed width.
    private static readonly string ColError = $"| {LoggingConstants.LevelErrorLabel} |";
    private static readonly string ColWarn = $"| {LoggingConstants.LevelWarnLabel} |";
    private static readonly string ColInfo = $"| {LoggingConstants.LevelInfoLabel} |";
    private static readonly string ColDebug = $"| {LoggingConstants.LevelDebugLabel} |";
    private const long LoadingIndicatorMinBytes = 1_000_000; // show "Loading…" only for logs large enough that the read + parse is perceptible

    public LogViewerForm() : this(string.Empty) { } // designer support only

    /// <summary>Opens the log viewer for the specified log file.</summary>
    /// <param name="logFilePath">Path to the log file to display.</param>
    /// <param name="navigateToLatestIssue">When <see langword="true"/>, scrolls to the most recent WARN or ERROR entry on open.</param>
    public LogViewerForm(string logFilePath, bool navigateToLatestIssue = false)
    {
        InitializeComponent();
        _logFilePath = logFilePath;
        _activeLogFilePath = logFilePath;
        _navigateToLatestIssuePending = navigateToLatestIssue;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _themeColors = [ThemeColors.LogError, ThemeColors.LogWarning, ThemeColors.LogInfo, ThemeColors.LogDebug, SystemColors.WindowText];
        Text = $"{AppIdentity.AppName} | Log Viewer";
        KeyPreview = true; // form sees keys before the focused control - see OnKeyDown (Escape to close, Ctrl+F)
        ApplyTheme();
        // Measure the monospace character width once for the active font/DPI. Averaged over a
        // block of characters so GDI padding rounding does not skew the per-char value; used for
        // the column width and to position search-highlight runs within a row.
        const int MeasureChars = 64;
        _charWidth = TextRenderer.MeasureText(new string('0', MeasureChars), lvLog.Font,
            Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width / (float)MeasureChars;
        _textPadding = LogicalToDeviceUnits(4);
        // Invalidate after a size change: rows newly exposed by growing the window (e.g.
        // maximize) are otherwise painted by the control's default renderer - plain white text
        // in dark mode - until the next append happens to trigger the owner-draw pass.
        lvLog.ClientSizeChanged += (_, _) => { UpdateColumnWidth(); lvLog.Invalidate(); };

        // Vertically center the two left-side combos - single-line controls auto-size their height
        // from the font, so the actual height is only known after layout.
        int searchTop = (pnlToolbar.Height - txtSearch.Height) / 2;
        cboSubsystem.Top = (pnlToolbar.Height - cboSubsystem.Height) / 2;
        cboTimeRange.Top = (pnlToolbar.Height - cboTimeRange.Height) / 2;
        cboLogFile.Top = (pnlToolbar.Height - cboLogFile.Height) / 2;

        // Lay out the right-aligned search group (search box, match counter, prev/next, clear button)
        // - shared with the help viewer. rightMargin 4: no form padding here, so the edge gap is baked
        // into the layout (the help viewer passes 0 and lets its form padding supply the gap).
        UiHelpers.LayoutSearchToolbar(pnlToolbar, txtSearch, lblMatchCount, btnPrev, btnNext, btnClearSearch, rightMargin: 4);

        // Owner-draw the chevrons on all four nav buttons; size/center the issue-nav buttons and the
        // level-filter checkboxes (LayoutSearchToolbar already sized and centered btnPrev/btnNext).
        int navH = txtSearch.Height;
        foreach (var btn in new[] { btnPrev, btnNext, btnIssuePrev, btnIssueNext })
            btn.Paint += NavButton_Paint;
        // Same treatment for the clear button's X - drawn, not typed, so its weight and centering
        // match the chevrons beside it and no font glyph is relied on.
        btnClearSearch.Paint += (_, e) => UiHelpers.DrawClearGlyph(btnClearSearch, e.Graphics);
        foreach (var btn in new[] { btnIssuePrev, btnIssueNext })
        {
            btn.Height = navH;
            btn.Top = searchTop;
        }
        foreach (var chk in new[] { chkError, chkWarn, chkInfo, chkDebug })
        {
            chk.Height = navH;
            chk.Top = searchTop;
        }

        PopulateLogFileDropdown();
        cboLogFile.SelectedIndex = 0; // "Current"
        // Wire events after population/selection to avoid triggering a load before the initial
        // LoadInitialContentAsync below. DropDown re-syncs the list with the on-disk files each
        // time the user opens it (rotation and Clear Logs change them while the viewer is open).
        cboLogFile.SelectedIndexChanged += cboLogFile_SelectedIndexChanged;
        cboLogFile.DropDown += cboLogFile_DropDown;

        // Same ordering rule as the pickers above: populate and select first, wire the event after,
        // so building the list cannot rebuild the display before the log has been read.
        foreach ((string label, double? _) in TimeRanges) cboTimeRange.Items.Add(label);
        cboTimeRange.Items.Add("Custom range…");   // must stay last: CustomRangeIndex is TimeRanges.Length
        cboTimeRange.SelectedIndex = 0; // "All time" - the viewer opens showing everything, as before
        _lastTimeRangeIndex = 0;
        cboTimeRange.SelectedIndexChanged += cboTimeRange_SelectedIndexChanged;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Start the load only after the form's first paint so the "Loading…" overlay below is
        // actually drawn before the blocking read/parse begins (see LoadInitialContentAsync).
        _ = LoadInitialContentAsync(); // fire-and-forget; exceptions are handled inside LoadInitialContentAsync
    }

    // Escape closes the viewer, except while the search box has focus - there Escape clears the search
    // (handled in txtSearch_KeyDown). Ctrl+F focuses the search box (same as the Help viewer).
    // KeyPreview (set in OnLoad) lets the form see the keys first.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !txtSearch.Focused)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.F)
        {
            txtSearch.Focus();
            txtSearch.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
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
        // lvLog uses the Control chrome (not the Window input surface) because it fills the whole
        // window and reads as the document itself. Embedded read-only data tables inside label-heavy
        // dialogs (StatusForm.lvHistory, MediaManagerForm.dgvResults) deliberately use Window instead,
        // so they read as a distinct data box - keep that distinction if revisiting theming.
        lvLog.BackColor = surface;
        lvLog.ForeColor = fg;

        ApplyFilterButtonStyle(chkError, _themeColors[LevelError]);
        ApplyFilterButtonStyle(chkWarn, _themeColors[LevelWarn]);
        ApplyFilterButtonStyle(chkInfo, _themeColors[LevelInfo]);
        ApplyFilterButtonStyle(chkDebug, _themeColors[LevelDebug]);

        cboSubsystem.BackColor = input;
        cboSubsystem.ForeColor = fg;

        cboTimeRange.BackColor = input;
        cboTimeRange.ForeColor = fg;

        cboLogFile.BackColor = input;
        cboLogFile.ForeColor = fg;

        txtSearch.BackColor = input;
        txtSearch.ForeColor = fg;

        // All four nav buttons share the OS accent (severity-neutral, mode-aware). The accent reads
        // as a finer, calmer stroke than plain WindowText, which blooms against a dark surface and
        // makes an identically-drawn chevron look heavier. Which pair is which is already clear from
        // position - issue-nav sits with the level filters, search-nav inside the search group - so
        // colour does not need to carry that distinction as well.
        foreach (var btn in new[] { btnPrev, btnNext, btnIssuePrev, btnIssueNext })
        {
            btn.BackColor = surface;
            btn.ForeColor = SystemColors.HotTrack;
            btn.FlatAppearance.BorderColor = border;
        }

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

    // Paints the nav chevrons via the shared drawer, in the button's ForeColor (neutral WindowText
    // for the search-match arrows, the accent for the issue-nav arrows). btnPrev/btnIssuePrev point up.
    private void NavButton_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Button btn) return;
        UiHelpers.DrawNavChevron(btn, e.Graphics, up: btn == btnPrev || btn == btnIssuePrev);
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

    // Time-range options, in the order they appear in the combo. Each is a window ending now, so a
    // preset can only ever answer "how far back from this moment"; anything already past needs the
    // custom range appended after these. Kept to one preset per distinct case - watching a problem
    // reproduce (15 min), reading back over something just seen (1 hour), and an overnight incident
    // (24 hours). Intermediate steps were dropped: they sit between those cases without adding one,
    // and a week is indistinguishable from "All time" on a log that rarely spans that long. Stored
    // as hours so the cutoff is a subtraction rather than a switch.
    private static readonly (string Label, double? Hours)[] TimeRanges =
    [
        ("All time",      null),
        ("Last 15 min",   0.25),
        ("Last hour",     1),
        ("Last 24 hours", 24),
    ];

    // Index of the "Custom range…" entry, which is appended after the presets.
    private static int CustomRangeIndex => TimeRanges.Length;

    // The chosen custom window, null until one is set. Kept so reselecting "Custom range…" reopens the
    // dialog on the last values, and so the window survives switching to a preset and back.
    private (DateTime From, DateTime To)? _customRange;

    // Remembers the last accepted selection so cancelling the custom dialog can put the combo back
    // rather than leaving it on an entry the user declined to configure.
    private int _lastTimeRangeIndex;

    private void cboTimeRange_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cboTimeRange.SelectedIndex == CustomRangeIndex && !PromptForCustomRange())
        {
            // Declined: restore the previous entry. Assigning SelectedIndex re-enters this handler,
            // which is harmless - the restored index is never the custom one, so it falls straight
            // through to the rebuild below.
            cboTimeRange.SelectedIndex = _lastTimeRangeIndex;
            return;
        }
        _lastTimeRangeIndex = cboTimeRange.SelectedIndex;
        UpdateTimeRangeTooltip();
        RebuildDisplay();
    }

    // Opens the range dialog pre-filled with the previous choice, or with the last hour on first use.
    // Returns false when the user cancels.
    private bool PromptForCustomRange()
    {
        (DateTime from, DateTime to) = _customRange ?? (DateTime.Now.AddHours(-1), DateTime.Now);
        using var dialog = new TimeRangeForm(from, to);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        _customRange = dialog.OrderedRange();
        return true;
    }

    // The combo is too narrow to show a custom range, so the tooltip carries it - otherwise the only
    // way to see what "Custom range…" currently means is to reopen the dialog. Every other selection
    // restores the static description: the custom range is the only one the combo cannot show in its
    // own width, and leaving the old range behind would have it describing the preset that replaced it.
    private void UpdateTimeRangeTooltip()
    {
        toolTip.SetToolTip(cboTimeRange,
            cboTimeRange.SelectedIndex == CustomRangeIndex && _customRange is { } range
                ? $"Showing {FormatTimestamp(range.From)} to {FormatTimestamp(range.To)}"
                : TimeRangeTooltip);
    }

    // Matches the initial value the designer sets on the combo, so switching off a custom range
    // returns the tooltip to exactly what it said before one was ever chosen.
    private const string TimeRangeTooltip = "Show only entries from a time range";

    // Formatted from LoggingConstants.DateFormat rather than a literal, so the tooltip is guaranteed
    // to echo the range in the same shape the dialog's fields accept.
    private static string FormatTimestamp(DateTime value) =>
        value.ToString(LoggingConstants.DateFormat, LoggingConstants.DateCulture);

    // The window the current selection admits: a preset ending now, an explicit custom span, or no
    // bounds at all for "All time". Computed per call rather than cached, so the cutoff a preset
    // applies to newly arriving lines follows the wall clock.
    //
    // A preset does NOT retroactively expire rows already on screen: AppendNewLines only ever tests
    // the lines it just read, so "Last 15 min" means "everything from 15 minutes before you chose it,
    // onward" rather than a window that slides out from under what is displayed. That is deliberate.
    // Expiring rows would mean deleting lines while the user is reading them, and the preset's job is
    // to get you to the recent entries - which it does. The live tail everyone watches is always
    // inside the window anyway. The custom range is unaffected either way: its bounds are fixed, so a
    // row that qualified still qualifies.
    private (DateTime? From, DateTime? To) GetTimeWindow()
    {
        int index = cboTimeRange.SelectedIndex;
        if (index == CustomRangeIndex)
            return _customRange is { } range ? (range.From, range.To) : (null, null);
        if (index < 0 || index >= TimeRanges.Length) return (null, null);
        return TimeRanges[index].Hours is double hours ? (DateTime.Now.AddHours(-hours), null) : (null, null);
    }



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
    private void txtSearch_KeyDown(object? sender, KeyEventArgs e) =>
        UiHelpers.HandleSearchKeyDown(e, txtSearch, SearchNext, SearchPrev);

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
    // the pressed row; MouseMove extends the selection to the row under the cursor. Holding the
    // cursor past the top or bottom edge keeps scrolling via _dragScrollTimer - MouseMove alone
    // cannot drive that, because it only fires while the pointer actually moves.
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

        // Outside the vertical bounds: hand off to the edge auto-scroll timer, which keeps
        // advancing while the cursor is held there (the control captures the mouse, so these
        // out-of-bounds coordinates are seen, but only as long as the pointer moves).
        int direction = 0;
        if (e.Y < 0) direction = -1;
        else if (e.Y >= lvLog.ClientSize.Height) direction = 1;
        SetDragScroll(direction);
        if (direction != 0) return;

        int row = lvLog.HitTest(new Point(
            Math.Clamp(e.X, 0, lvLog.ClientSize.Width - 1), e.Y)).Item?.Index ?? -1;
        if (row < 0 || row == _dragLastRow) return;
        lvLog.EnsureVisible(row);
        ExtendDragSelection(row);
        _dragLastRow = row;
    }

    private void lvLog_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        SetDragScroll(0);
        _dragAnchorRow = -1;
        _dragLastRow = -1;
    }

    // Arms or disarms the edge auto-scroll for the given direction. The timer is created lazily
    // on first use and lives for the form's lifetime (disposed in the Designer's Dispose).
    private void SetDragScroll(int direction)
    {
        if (direction == _dragScrollDirection) return;
        _dragScrollDirection = direction;
        if (direction == 0)
        {
            _dragScrollTimer?.Stop();
            return;
        }
        if (_dragScrollTimer is null)
        {
            _dragScrollTimer = new System.Windows.Forms.Timer { Interval = DragScrollIntervalMs };
            _dragScrollTimer.Tick += dragScrollTimer_Tick;
        }
        _dragScrollTimer.Start();
    }

    // Advances the drag selection one row per tick in the held direction, scrolling with it.
    private void dragScrollTimer_Tick(object? sender, EventArgs e)
    {
        // VirtualListSize == 0 also ends the drag: the list can empty mid-drag (log deleted or
        // cleared externally), and Math.Clamp below throws when max (count - 1) drops below min.
        if (_dragAnchorRow < 0 || _dragScrollDirection == 0 || lvLog.VirtualListSize == 0)
        {
            SetDragScroll(0);
            return;
        }
        int row = Math.Clamp(_dragLastRow + _dragScrollDirection, 0, lvLog.VirtualListSize - 1);
        if (row == _dragLastRow) return; // hit the first/last row - keep the timer armed in case rows are appended
        lvLog.EnsureVisible(row);
        ExtendDragSelection(row);
        _dragLastRow = row;
    }

    // Extends the drag selection from _dragLastRow to newRow, touching only the delta rows so
    // the per-move cost is proportional to the mouse movement, not the total range - a clear-all
    // plus full re-select per move would make a long edge-scroll drag quadratic and freeze the
    // UI. Rows entering the [anchor, newRow] span are selected; rows leaving it (dragging back
    // toward or past the anchor) are deselected. The anchor row itself always stays selected.
    private void ExtendDragSelection(int newRow)
    {
        int anchor = _dragAnchorRow;
        int last = _dragLastRow;

        // Deselect rows no longer in the span (present relative to 'last', absent relative to 'newRow').
        int oldLo = Math.Min(anchor, last), oldHi = Math.Max(anchor, last);
        int newLo = Math.Min(anchor, newRow), newHi = Math.Max(anchor, newRow);
        for (int r = oldLo; r <= oldHi; r++)
        {
            if (r < newLo || r > newHi)
                SetRowState(r, 0, WinMsg.LVIS_SELECTED);
        }
        // Select rows newly in the span.
        for (int r = newLo; r <= newHi; r++)
        {
            if (r < oldLo || r > oldHi)
                SetRowState(r, WinMsg.LVIS_SELECTED, WinMsg.LVIS_SELECTED);
        }
        lvLog.Invalidate();
    }

    // Rescans the visible rows for the current query and rebuilds the sorted match list.
    // scrollToMatch: when false, updates the match list and count label but does not scroll -
    // used by RebuildDisplay, which has already positioned the viewport (bottom or anchor line)
    // and must not have a filter toggle yank the user to the first match.
    private void RefreshSearch(bool navigateToFirst = false, bool scrollToMatch = true)
    {
        _searchMatches.Clear();

        string query = txtSearch.Text;
        if (string.IsNullOrEmpty(query))
        {
            _searchIndex = -1;
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
        var (row, offset) = _searchMatches[index];
        SelectRow(row);
        ScrollMatchIntoHorizontalView(row, offset);
        lblMatchCount.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
    }

    // Scrolls the list horizontally so the match run at (row, offset) is inside the viewport
    // (EnsureVisible in SelectRow handles only the vertical axis). The row's item rect X already
    // reflects the current horizontal scroll, so the run's on-screen position is rect.X plus the
    // character-offset pixels; LVM_SCROLL shifts by the delta needed to bring it into view with
    // a small margin. Left-edge correction is applied after the right-edge one so a run wider
    // than the viewport keeps its start visible.
    private void ScrollMatchIntoHorizontalView(int row, int offset)
    {
        Rectangle itemRect = lvLog.GetItemRect(row);
        int margin = LogicalToDeviceUnits(16);
        var (left, width) = MeasureMatchRun(_allLines[_visibleRows[row]].Text, offset, txtSearch.Text.Length);
        int runLeft = itemRect.X + _textPadding + left;
        int runRight = runLeft + width;

        int dx = 0;
        if (runRight > lvLog.ClientSize.Width)
            dx = runRight - lvLog.ClientSize.Width + margin;
        if (runLeft - dx < 0)
            dx = runLeft - margin;

        if (dx != 0)
            SendMessage(lvLog.Handle, WinMsg.LVM_SCROLL, dx, 0);
    }

    // Selects a single row, gives it the keyboard focus (so arrow/Shift+arrow navigation
    // continues from it), and scrolls it into view. Shared by search and issue navigation.
    private void SelectRow(int row)
    {
        if (row < 0 || row >= lvLog.VirtualListSize) return;
        SetAllRowsSelected(false);
        SetRowState(row, WinMsg.LVIS_SELECTED | WinMsg.LVIS_FOCUSED, WinMsg.LVIS_SELECTED | WinMsg.LVIS_FOCUSED);
        lvLog.EnsureVisible(row);
        lvLog.Invalidate(); // repaint so the previous selection clears immediately
    }

    // Applies the given state bits within stateMask on one row via LVM_SETITEMSTATE
    // (state = stateMask sets the bits; state = 0 clears them).
    private void SetRowState(int row, uint state, uint stateMask)
    {
        var item = new NativeListViewItem
        {
            Mask = WinMsg.LVIF_STATE,
            State = state,
            StateMask = stateMask,
        };
        SendMessage(lvLog.Handle, WinMsg.LVM_SETITEMSTATE, row, ref item);
    }

    // Anchor row for issue navigation: the selected row, or -1 when nothing is selected.
    private int SelectedRow => lvLog.SelectedIndices.Count > 0 ? lvLog.SelectedIndices[0] : -1;

    // Scrolls to the previous (older) WARN or ERROR line relative to the current selection.
    private void IssuePrev()
    {
        int start = (SelectedRow < 0 ? _visibleRows.Count : SelectedRow) - 1;
        int row = FindIssueRow(start, step: -1);
        if (row >= 0) SelectRow(row);
    }

    // Scrolls to the next (newer) WARN or ERROR line relative to the current selection.
    private void IssueNext()
    {
        int row = FindIssueRow(SelectedRow + 1, step: 1);
        if (row >= 0) SelectRow(row);
    }

    /// <summary>Scrolls to the most recent (last) WARN or ERROR line in the log, falling back to
    /// the bottom of the log if none are present. Used when opening the log viewer with unviewed
    /// alerts (via tray balloon click or Show Logs).</summary>
    public void NavigateToLatestIssue()
    {
        int row = FindIssueRow(_visibleRows.Count - 1, step: -1);
        if (row >= 0) SelectRow(row);
        else ScrollToBottom();
    }

    // Walks the visible rows from start in the given direction (+1 down, -1 up) and returns the
    // first WARN or ERROR row, or -1 when none is found. Single "what counts as an issue"
    // predicate shared by both issue-nav directions and the latest-issue jump.
    private int FindIssueRow(int start, int step)
    {
        for (int r = start; r >= 0 && r < _visibleRows.Count; r += step)
        {
            if (_allLines[_visibleRows[r]].Level is LevelError or LevelWarn)
                return r;
        }
        return -1;
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
        // >= count - 1 (not count): RowsPerPage floors, so a last row that is only partially
        // visible would otherwise read as "not at bottom" and silently stop the tail-follow.
        // Erring the other way is imperceptible (one extra scroll-to-bottom on append).
        return top.Index + RowsPerPage(top) >= count - 1;
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
        SetVirtualListSize(_visibleRows.Count);
        UpdateColumnWidth();
        UpdateMetaForRowCount();

        if (wasAtBottom)
            ScrollToBottom();
        else if (anchorLine >= 0)
            ScrollRowToTop(NearestRowForLine(anchorLine));

        RefreshSearch(navigateToFirst: true, scrollToMatch: false);
        lvLog.Invalidate();
    }

    // Applies a new row count to the virtual list. When the count shrinks, the native control
    // could otherwise still hold a selected/focused item at a now-out-of-range index - the
    // classic virtual-mode out-of-range fault, triggered unpredictably from paint, keyboard
    // focus restoration, or accessibility tooling - so selection and focus are cleared first.
    // Losing the selection on a shrink is acceptable: the row set changed anyway.
    private void SetVirtualListSize(int count)
    {
        if (count < lvLog.VirtualListSize)
        {
            // One native broadcast (index -1) clearing both bits; ListView.FocusedItem cannot be
            // set to null from managed code, so the focus bit must be cleared the same way.
            var item = new NativeListViewItem
            {
                Mask = WinMsg.LVIF_STATE,
                State = 0,
                StateMask = WinMsg.LVIS_SELECTED | WinMsg.LVIS_FOCUSED,
            };
            SendMessage(lvLog.Handle, WinMsg.LVM_SETITEMSTATE, -1, ref item);

            // Reset the scroll origin before shrinking. The native control clamps its scroll
            // range on a shrink but keeps painting items at the stale offset until the next
            // scroll or mouse interaction recomputes it, so a filter applied while scrolled
            // down in a large log shows the rows floating mid-viewport (or not at all) until
            // the user nudges the view. Scrolling to row 0 first zeroes the origin; the caller
            // (RebuildDisplay) re-positions the viewport to the bottom or anchor afterwards.
            if (lvLog.VirtualListSize > 0)
                lvLog.EnsureVisible(0);
        }
        lvLog.VirtualListSize = count;
    }

    // Recomputes _visibleRows (and the widest-line measurement) from _allLines and the current filters.
    private void RebuildVisibleRows()
    {
        _visibleRows.Clear();
        _maxLineLength = 0;
        AppendVisibleRows(fromLine: 0);
    }

    // Appends the lines from fromLine onward that pass the current filters to _visibleRows,
    // updating the widest-line measurement. Single filtering pass shared by the full rebuild
    // (fromLine 0 after a clear) and the live-tail append (fromLine = first new line), so both
    // paths always apply the identical visibility and width rules.
    // Meta rows are shown in filtered views too, but the two kinds are handled differently: see
    // ShouldAppend.
    private void AppendVisibleRows(int fromLine)
    {
        bool[] filters = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];
        string? subsystemFilter = GetSubsystemFilter();
        (DateTime? From, DateTime? To) window = GetTimeWindow();
        bool lastEntryVisible = WasLastEntryVisible(fromLine, filters, subsystemFilter, window);

        for (int i = fromLine; i < _allLines.Count; i++)
        {
            LogLine line = _allLines[i];
            if (!ShouldAppend(line, filters, subsystemFilter, window, ref lastEntryVisible)) continue;
            _visibleRows.Add(i);
            if (line.Text.Length > _maxLineLength) _maxLineLength = line.Text.Length;
        }
    }

    // Whether one line belongs in the filtered view. Split out of the append loop so the three cases
    // read as three cases, and so neither method carries the whole decision's complexity.
    //
    // LevelMeta covers two unrelated things, and conflating them was the bug this separates:
    // ClassifyLine assigns it to any line with no "| LEVEL |" column, which is both the blank cycle
    // separators LogManager writes and a continuation line wrapped from a multi-line message.
    //   - A classified line decides for itself, and its verdict becomes the parent verdict that any
    //     continuation lines following it inherit.
    //   - A blank separator is shown between groups, but never as the first visible row and never
    //     twice running, so a view whose filter drops entire cycles (e.g. ERROR only) shows one
    //     separator between groups rather than a wall of blank lines.
    //   - A continuation follows its parent. Without this it bypassed every filter, so hiding an
    //     entry left its continuation lines behind as orphans under an unrelated entry.
    private bool ShouldAppend(LogLine line, bool[] filters, string? subsystemToken,
        (DateTime? From, DateTime? To) window, ref bool lastEntryVisible)
    {
        if (line.Level != LevelMeta)
        {
            lastEntryVisible = IsLineVisible(line, filters, subsystemToken, window);
            return lastEntryVisible;
        }

        // The dedup test asks whether the previous visible row is a separator, not whether it is
        // LevelMeta. Those were the same thing until continuations were separated out; testing the
        // level would now drop the second of two consecutive continuation lines, and swallow a
        // separator that happens to follow one.
        if (IsSeparatorLine(line))
            return _visibleRows.Count > 0 && !IsSeparatorLine(_allLines[_visibleRows[^1]]);

        return lastEntryVisible;
    }

    // A meta line with text is a continuation of the entry above it; a blank one is a cycle separator.
    //
    // Known limitation, deliberately not guessed at: a blank line *inside* a multi-line entry is
    // indistinguishable from a cycle separator, because both are whitespace-only lines with no level
    // column. Such a line therefore takes the separator branch in ShouldAppend and does not inherit
    // its parent's visibility, so a filtered-out entry containing one can still leave that blank row
    // behind. Nothing in the app writes a multi-line entry today (every call passes ex.Message, not
    // ex.ToString()), so this is unreachable in practice, and telling the two apart would mean
    // inferring structure the log format does not record - a guess that could just as easily swallow
    // a real cycle separator. If entries ever do span lines, the fix belongs in the writer: have
    // LogManager mark continuations explicitly rather than have the reader infer them.
    private static bool IsSeparatorLine(LogLine line) =>
        line.Level == LevelMeta && string.IsNullOrWhiteSpace(line.Text);

    // Visibility of the entry preceding fromLine, which any continuation lines at the start of an
    // incremental append belong to. Recomputed by walking back rather than carried in a field: the
    // append has several call sites and the places that clear _visibleRows are a different set again
    // (only RebuildVisibleRows does both), so a cached field would need resetting at each and would
    // silently go stale the next time either set grew. Deliberately not stated as a count - the
    // previous wording named one and both numbers had drifted. Continuation lines are rare, so this
    // normally stops on the first line it looks at.
    //
    // True when no earlier classified line exists - a buffer that begins mid-entry (the log rotated
    // partway through one) should show those lines rather than hide content that has no parent to
    // inherit from.
    private bool WasLastEntryVisible(int fromLine, bool[] filters, string? subsystemToken,
        (DateTime? From, DateTime? To) window)
    {
        for (int i = fromLine - 1; i >= 0; i--)
        {
            if (_allLines[i].Level == LevelMeta) continue;
            return IsLineVisible(_allLines[i], filters, subsystemToken, window);
        }
        return true;
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

    // Resolves the meta overlay against the current row count: hides it when rows are showing,
    // and otherwise names the reason the view is empty - filters excluding everything, or a log
    // with no entries at all. Called after every load, rebuild, and visible append so a
    // "Loading…" overlay can never be left stranded over an empty view.
    private void UpdateMetaForRowCount()
    {
        if (_visibleRows.Count > 0)
            HideMetaLabel();
        else if (_allLines.Count > 0)
            SetMetaMessage("(No entries match the current filters)", MetaColor);
        else
            SetMetaMessage("(No log entries yet)", MetaColor);
    }

    // Level, subsystem and time-range visibility for one classified line. Meta rows never reach this
    // method: ShouldAppend decides those, a blank separator by the dedup rule and a continuation line
    // by inheriting its parent entry's verdict from here.
    //
    // Nothing writes a multi-line entry today - every logging call passes ex.Message rather than
    // ex.ToString() - so the continuation path is not currently exercised. It is handled anyway
    // because that is a convention nobody enforces: the first ex.ToString() to land would otherwise
    // split an entry across a filter boundary, silently and in the one tool used to diagnose it.
    private static bool IsLineVisible(LogLine line, bool[] filters, string? subsystemToken, (DateTime? From, DateTime? To) window)
    {
        if (!filters[line.Level]) return false;                     // level filtered out
        if (subsystemToken is not null && !line.Text.Contains(subsystemToken, StringComparison.Ordinal)) return false;
        // The window test is guarded so an unfiltered view never pays for the timestamp parse, and
        // short-circuits before it. The null branch of TryReadTimestamp is a safety net rather than a
        // live path: every line that gets this far carries a level column, and only a timestamped
        // entry has one. See the note above for the untimestamped lines, which never arrive here.
        if ((window.From is not null || window.To is not null) && TryReadTimestamp(line.Text) is DateTime stamp)
        {
            if (window.From is DateTime from && stamp < from) return false;
            if (window.To is DateTime to && stamp > to) return false;
        }
        return true;
    }

    // Parses the fixed-width timestamp every entry starts with, or null for a line that has none
    // (continuation lines wrapped from a multi-line message, and the blank cycle separators).
    // Both of those classify as LevelMeta and are diverted before IsLineVisible runs, so the time
    // filter never actually asks about them; the null result only matters if an entry ever carries a
    // level column without a parseable stamp.
    //
    // Parsed on demand rather than stored on LogLine: the store is the process's dominant allocation
    // on a large log, and adding 8 bytes per line to save a parse that only runs on a filter rebuild
    // is the wrong trade.
    private static DateTime? TryReadTimestamp(string text) =>
        text.Length >= LoggingConstants.DateFormat.Length &&
        DateTime.TryParseExact(text.AsSpan(0, LoggingConstants.DateFormat.Length), LoggingConstants.DateFormat,
            LoggingConstants.DateCulture, System.Globalization.DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;

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

    // Owner-draws one visible row: surface background (plus a translucent selection tint when
    // the row is selected), search-match highlight runs, then the line text in its level colour.
    // Only on-screen rows are ever drawn, so highlight count and log size have no effect on
    // paint cost.
    private void lvLog_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visibleRows.Count) return;
        LogLine line = _allLines[_visibleRows[e.ItemIndex]];
        bool selected = IsRowSelected(e.ItemIndex);

        // Selection is a translucent tint over the normal background rather than the solid
        // system bar with HighlightText: the per-level line colors ARE the viewer's information
        // (a navigated-to WARN must still read as gold), and the solid bar + HighlightText combo
        // crushed them into low-contrast text in dark mode.
        using (var back = new SolidBrush(lvLog.BackColor))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var selection = new SolidBrush(Color.FromArgb(90, SystemColors.Highlight));
            e.Graphics.FillRectangle(selection, e.Bounds);
        }

        // Search-match highlight runs, positioned by measuring the actual rendered text so they
        // stay glyph-exact even when a line contains non-ASCII characters (font-fallback glyphs
        // have different advances than the averaged _charWidth; accented media file names are the
        // realistic case). Cost is bounded: only match runs on rows actually painted are measured.
        // Drawn on selected rows too - navigation selects the current match's row, and without
        // the runs the active row would be the one place the matches are invisible. The CURRENT
        // match paints in the full highlight color while the others use a translucent version,
        // so Next/Prev visibly steps between occurrences even when several share one line.
        int queryLength = txtSearch.Text.Length;
        if (queryLength > 0 && _searchMatches.Count > 0)
        {
            (int Row, int Offset) current = _searchIndex >= 0 && _searchIndex < _searchMatches.Count
                ? _searchMatches[_searchIndex]
                : (-1, -1);
            using var currentHighlight = new SolidBrush(ThemeColors.SearchHighlight);
            using var otherHighlight = new SolidBrush(Color.FromArgb(110, ThemeColors.SearchHighlight));
            foreach (int offset in MatchOffsetsForRow(e.ItemIndex))
            {
                bool isCurrent = current.Row == e.ItemIndex && current.Offset == offset;
                var (runLeft, runWidth) = MeasureMatchRun(line.Text, offset, queryLength);
                var run = new Rectangle(
                    e.Bounds.Left + _textPadding + runLeft,
                    e.Bounds.Top,
                    runWidth,
                    e.Bounds.Height);
                e.Graphics.FillRectangle(isCurrent ? currentHighlight : otherHighlight, run);
            }
        }

        var textBounds = new Rectangle(e.Bounds.Left + _textPadding, e.Bounds.Top, e.Bounds.Width - _textPadding, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, line.Text, lvLog.Font, textBounds,
            _themeColors[line.Level],
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    // Queries the native control for a row's real selection state. DrawListViewItemEventArgs.State
    // is documented as unreliable in Details view (it reported every row as selected here), so the
    // owner-draw path asks via LVM_GETITEMSTATE instead - an O(1) message per painted row.
    private bool IsRowSelected(int row) =>
        ((long)SendMessage(lvLog.Handle, WinMsg.LVM_GETITEMSTATE, row, (nint)WinMsg.LVIS_SELECTED) & WinMsg.LVIS_SELECTED) != 0;

    // Measures a match run's pixel position within a line by measuring the rendered prefix and
    // the matched substring - exact for any content or font, unlike offset * _charWidth which
    // drifts on font-fallback glyphs. Shared by the draw path and horizontal scroll-to-match.
    private (int Left, int Width) MeasureMatchRun(string text, int offset, int length)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int left = offset == 0 ? 0 : TextRenderer.MeasureText(text.AsSpan(0, offset), lvLog.Font, Size.Empty, flags).Width;
        int width = TextRenderer.MeasureText(text.AsSpan(offset, length), lvLog.Font, Size.Empty, flags).Width;
        return (left, width);
    }

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
        UiHelpers.SetClipboardTextSafely(sb.ToString());
    }

    private void CopyAllVisibleRows()
    {
        var sb = new StringBuilder();
        foreach (int line in _visibleRows)
            sb.AppendLine(_allLines[line].Text);
        UiHelpers.SetClipboardTextSafely(sb.ToString());
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
        // Capture the generation at entry (UI thread, same thread that increments it) so a load
        // whose file was switched away from mid-read can detect it is stale after the await and
        // bail. Without this, switching Current -> Backup -> Current while a large file loads
        // lets the stale load append the wrong file's lines into the new view, and its finally
        // would start a second watcher on the same generation - overwriting _watcher without
        // dispose, after which every tail append is processed twice (duplicated lines).
        int generation = _watcherGeneration;
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
                    // FileShare.Delete for the same reason AppFiles.OpenShared grants it: LogManager rotates by
                    // renaming this file, and a read in flight without DELETE access makes that rename
                    // fail in the writer. Rotation retries on its next check so a lost race only defers
                    // it, but there is no reason to lose the race.
                    using var fs = new FileStream(loadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    return (ParseLogLines(reader.ReadToEnd()), fs.Position);
                }
            });

            if (IsDisposed || generation != _watcherGeneration) return;

            _allLines.AddRange(lines);
            _lastReadPosition = position;

            RebuildVisibleRows();
            SetVirtualListSize(_visibleRows.Count);
            UpdateColumnWidth();
            UpdateMetaForRowCount();

            if (_navigateToLatestIssuePending) { NavigateToLatestIssue(); _navigateToLatestIssuePending = false; } else ScrollToBottom();
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
            if (!IsDisposed && generation == _watcherGeneration)
                SetMetaMessage($"(Error reading log: {ex.Message})", _themeColors[LevelError]);
        }
        finally
        {
            // A stale load must not start a watcher: the switch handler already disposed the old
            // one, and the load for the new file will start its own (see the generation comment
            // at the top of this method).
            if (generation == _watcherGeneration)
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

                // FileShare.Delete: see the load path above - this tail read races LogManager's rotation rename.
                using var fs = new FileStream(_activeLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

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
                ClearDisplayState();
            });
        }
        catch (ObjectDisposedException) { /* form disposed between IsDisposed check and Invoke - expected on close */ }
        catch (InvalidOperationException) { /* handle destroyed by Close() before Dispose() - expected on close */ }
    }

    // Fills the log file dropdown from the on-disk state: the current log plus the contiguous
    // rotated backups. Leaves nothing selected - callers set the selection (initial load picks
    // "Current"; the drop-down refresh restores the active entry by path).
    private void PopulateLogFileDropdown()
    {
        cboLogFile.Items.Clear();
        cboLogFile.Items.Add(new LogFileEntry("Current", _logFilePath));
        for (int i = 1; File.Exists($"{_logFilePath}.{i}"); i++)
            cboLogFile.Items.Add(new LogFileEntry($"Backup {i}", $"{_logFilePath}.{i}"));
    }

    // Refreshes the log file list at the moment the user opens the dropdown, so it reflects the
    // current on-disk state: rotation shifts backups and Clear Logs deletes them while the
    // viewer is open. Refreshing here (not from the watcher) covers every staleness source -
    // the watcher only runs while viewing "Current", never sees backup deletions, and a closed
    // combo shows no stale content anyway. The selection is restored by file path, or kept as
    // an extra entry when the active file no longer exists on disk, so opening the list never
    // switches the view (the path guard in cboLogFile_SelectedIndexChanged makes re-selecting
    // the active entry a no-op).
    private void cboLogFile_DropDown(object? sender, EventArgs e)
    {
        if (cboLogFile.SelectedItem is not LogFileEntry active) return;

        cboLogFile.BeginUpdate();
        PopulateLogFileDropdown();

        int index = 0; // "Current" is always item 0
        if (active.FilePath != _logFilePath)
        {
            index = -1;
            for (int i = 1; i < cboLogFile.Items.Count; i++)
            {
                if (cboLogFile.Items[i] is LogFileEntry entry && entry.FilePath == active.FilePath)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                cboLogFile.Items.Add(active);
                index = cboLogFile.Items.Count - 1;
            }
        }
        cboLogFile.SelectedIndex = index;
        cboLogFile.EndUpdate();
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
            _lastReadPosition = 0;
        }
        ClearDisplayState();

        _ = LoadInitialContentAsync();
    }

    // Resets the line store and everything derived from it (visible rows, search state, column
    // width, row count, meta overlay). Single reset path shared by the log-file switch and the
    // file-deleted handler so no derived state can be forgotten at one of the sites.
    // Must be called on the UI thread.
    private void ClearDisplayState()
    {
        _allLines.Clear();
        _visibleRows.Clear();
        _searchMatches.Clear();
        _searchIndex = -1;
        _maxLineLength = 0;
        SetVirtualListSize(0);
        UpdateMetaForRowCount();
        lvLog.Invalidate();
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

        int firstNewRow = _visibleRows.Count;
        int firstNewLine = _allLines.Count;
        _allLines.AddRange(newLines);
        AppendVisibleRows(firstNewLine);

        if (_visibleRows.Count == firstNewRow) return; // nothing visible was added

        UpdateMetaForRowCount();
        lvLog.VirtualListSize = _visibleRows.Count; // append only grows the count - no shrink guard needed
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

    // Splits raw log content on newlines and classifies each line's level once (the level drives
    // filtering and row colouring for the rest of the line's lifetime). LogManager writes a blank
    // line before each "Sync cycle started" as a deliberate visual separator between cycles, and
    // those are kept as meta rows (AppendVisibleRows dedups them in filtered views so they never
    // stack up).
    //
    // RemoveEmptyEntries and "blank lines are kept" only coexist because the separator is CRLF:
    // LogBlankLine writes Environment.NewLine, so on disk it is "\r\n\r\n", and splitting on '\n'
    // leaves a segment holding exactly "\r" - not empty, so it survives the split - which the
    // TrimEnd below flattens to "". A bare-LF separator would be dropped silently and every cycle
    // boundary in the viewer would disappear. Measured on a 12.8 MB live log: 3,536 separators, all
    // CRLF, all rendered. So do NOT normalise "\r\n" to "\n" up front (as HelpForm.RenderMarkdown
    // does) and do NOT drop RemoveEmptyEntries as redundant - either one alone deletes every
    // separator, with nothing failing to say so. Windows-only app, so the dependency is safe; it is
    // the "simplification" that is not.
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

}

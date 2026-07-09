using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace qbPortWeaver;

/// <summary>Modeless log viewer with live tail updates and log-level colour coding. Opened via the tray menu or tray icon double-click; only one instance is allowed at a time (enforced by MainForm.ShowOrActivate).</summary>
public partial class LogViewerForm : Form
{
    private readonly string _logFilePath;
    private string _activeLogFilePath;
    private bool _navigateToLatestIssue;
    private readonly object _readLock = new();
    private readonly List<string> _allLines = new(); // all raw lines from file; display is rebuilt from these on filter change
    private readonly List<int> _searchMatches = new(); // character indices of current search hits in rtbLog
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
    private System.Windows.Forms.Timer? _searchDebounceTimer;
    // Reclaims memory once the viewer goes idle after a burst of content. Live appends and the
    // initial load build transient RTF strings that land on the Large Object Heap; a normal
    // pressure-driven GC frees them but never compacts the LOH, so committed-but-empty segments
    // keep the working set inflated to hundreds of MB on an otherwise small log (it only returns
    // to baseline on close, which forces LOH compaction). This timer runs that same compaction
    // ~ReclaimIdleSeconds after the last content change, gated on idle so the blocking GC never
    // fires while logging is actively streaming in. Content stays visible - only garbage is freed.
    private System.Windows.Forms.Timer? _reclaimTimer;
    private long _lastActivityTicks; // Environment.TickCount64 (monotonic ms) at the last content change
    private bool _reclaimPending;
    private const int ReclaimTimerIntervalMs = 1000;
    private const int ReclaimIdleSeconds = 3;
    // Overlay shown for status/placeholder text (loading, empty, error). A RichTextBox cannot
    // vertically centre its content, so these messages live on a Label that covers the log area.
    private Label? _metaLabel;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint lParam);

    // Asks Windows to trim the calling process's working set. Pages page back in on next access.
    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    // Returns the current-process pseudo-handle (a constant, not a real handle - no open/close
    // needed), used for self-targeted calls like EmptyWorkingSet.
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private static class WinMsg
    {
        public const int WM_PAINT = 0x000F;
        public const int WM_SETREDRAW = 0x000B;
        public const int WM_VSCROLL = 0x0115;
        public const int SB_BOTTOM = 7;
        public const int EM_GETSCROLLPOS = 0x04DD;
        public const int EM_SETSCROLLPOS = 0x04DE;
    }

    // Log column markers (format: "| LEVEL | ") and corresponding search terms
    private const string ColError = "| ERROR |";
    private const string ColWarn = "| WARN  |";
    private const string ColInfo = "| INFO  |";
    private const string ColDebug = "| DEBUG |";
    private const int MaxHighlights = 500;
    private const long LoadingIndicatorMinBytes = 1_000_000; // show "Loading..." only for logs large enough that the read + RTF parse is perceptible
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
        PopulateLogFileDropdown();
        // Wire event after population to avoid triggering a load before the initial LoadInitialContentAsync below
        cboLogFile.SelectedIndexChanged += cboLogFile_SelectedIndexChanged;
        CreateReclaimTimer();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Start the load only after the form's first paint. Kicking it off in OnLoad raced the
        // form's initial WM_PAINT: the "Loading..." overlay was made visible, but for a fast read
        // the background task's continuation ran HideMetaLabel before any paint occurred, so the
        // label was never actually drawn. By OnShown the form is painted, so the overlay below
        // renders before the blocking read/RTF parse begins.
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
        // Stop events/ticks before the form is fully disposed to prevent callbacks on a dead form.
        // Actual disposal is handled in Dispose(bool) in the Designer file.
        if (_watcher is not null)
            _watcher.EnableRaisingEvents = false;

        _searchDebounceTimer?.Stop();
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = null;

        _reclaimTimer?.Stop();
        _reclaimTimer?.Dispose();
        _reclaimTimer = null;
        _reclaimPending = false;

        // Release the large log content before Dispose runs. The native RichEdit control caches
        // its document buffer and is slow to release after handle destruction, so clearing while
        // the handle is still alive forces the buffer free now. _allLines is the matching managed
        // backing store; clearing + TrimExcess hands its array straight to GC instead of waiting
        // for a Gen 2 collection.
        try { rtbLog?.Clear(); }
        catch (ObjectDisposedException) { /* control already disposed - nothing to clear */ }
        _allLines.Clear();
        _allLines.TrimExcess();

        // Content already cleared above; compact the LOH and trim the working set now rather than
        // waiting for heap pressure. Shared with the periodic idle reclaim - see ReclaimUnusedMemory.
        ReclaimUnusedMemory();

        base.OnFormClosed(e);
    }

    // Creates the idle-reclaim timer in a stopped state. It is started on content activity and
    // stopped again once the reclaim has run, so an idle or empty viewer has no periodic wakeups.
    private void CreateReclaimTimer()
    {
        _reclaimTimer = new System.Windows.Forms.Timer { Interval = ReclaimTimerIntervalMs };
        _reclaimTimer.Tick += reclaimTimer_Tick;
    }

    // Records that content was just added (initial load or live append), arms a reclaim, and starts
    // the timer if it is not already running. Called on the UI thread; resets the idle clock so the
    // reclaim only fires after activity has settled.
    private void MarkContentActivity()
    {
        _lastActivityTicks = Environment.TickCount64;
        _reclaimPending = true;
        _reclaimTimer?.Start(); // idempotent if already running
    }

    // Fires on the UI thread. Once the viewer has been idle for ReclaimIdleSeconds after the last
    // content change, runs one LOH-compacting reclaim and disarms until the next content arrives.
    // Gating on idle keeps the blocking GC out of the active logging path: a continuous scan keeps
    // pushing _lastActivityUtc forward, so the reclaim waits until the stream stops.
    //
    // Do NOT convert this to a fixed/max-interval reclaim: ReclaimUnusedMemory is process-wide (the
    // blocking gen2 GC suspends all managed threads and EmptyWorkingSet trims the whole process), so
    // firing it on an interval would perturb an actively-running media scan that has merely paused
    // its logging. Idle-gating confines those costs to genuinely quiet moments.
    private void reclaimTimer_Tick(object? sender, EventArgs e)
    {
        if (!_reclaimPending) return;
        if (Environment.TickCount64 - _lastActivityTicks < ReclaimIdleSeconds * 1000L) return;
        _reclaimPending = false;
        _reclaimTimer?.Stop(); // nothing more to do until the next content activity
        ReclaimUnusedMemory();
    }

    // Forces a blocking Gen2 collection with one-time LOH compaction, then trims the working set.
    // The transient RTF strings built on each append/load sit mostly on the Large Object Heap, which
    // a normal pressure-driven GC frees but never compacts - the committed-but-empty segments are
    // what keep the viewer's footprint inflated to hundreds of MB on an otherwise small log.
    // CompactOnce returns those segments; EmptyWorkingSet then asks Windows to page out the freed
    // pages so Task Manager reflects live memory. Callers invoke this only when idle (after a burst
    // settles) or on close - never mid-append - so the blocking pause is not user-visible.
    private static void ReclaimUnusedMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Forced (not Optimized): Optimized lets the GC decline the collection, which would leave
        // the CompactOnce flag armed to fire on an unpredictable later gen2 (possibly mid-scan).
        // blocking: true is required - background/concurrent collections do not compact the LOH.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true); // NOSONAR S1215 - blocking required for LOH compaction
        // No WaitForPendingFinalizers: the reclaimed garbage (RTF strings, char[]/string[] buffers)
        // is non-finalizable, so the forced collection above reaps it directly.
        // best-effort: return value ignored by design (a failed trim just leaves pages resident,
        // to be evicted later under memory pressure); pseudo-handle, so nothing to dispose.
        EmptyWorkingSet(GetCurrentProcess());
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
        rtbLog.BackColor = surface;

        ApplyFilterButtonStyle(chkError, _themeColors[0]);
        ApplyFilterButtonStyle(chkWarn, _themeColors[1]);
        ApplyFilterButtonStyle(chkInfo, _themeColors[2]);
        ApplyFilterButtonStyle(chkDebug, _themeColors[3]);

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

    // Called when any filter CheckBox changes - updates its style and rebuilds the display
    private void filterButton_CheckedChanged(object? sender, EventArgs e)
    {
        if (sender is CheckBox chk)
            ApplyFilterButtonStyle(chk, GetButtonLevelColor(chk));
        RebuildDisplay();
    }

    // Called when the subsystem filter ComboBox changes - rebuilds the display
    private void cboSubsystem_SelectedIndexChanged(object? sender, EventArgs e) => RebuildDisplay();

    // Returns the padded subsystem column token to match (e.g. "| MainApp       |"),
    // or null when "All" is selected (no filter). Built here, once per rebuild, so the
    // per-line check in IsLineVisibleWithFilters is a single allocation-free Contains.
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
        if (chk == chkError) return _themeColors[0];
        if (chk == chkWarn) return _themeColors[1];
        if (chk == chkInfo) return _themeColors[2];
        return _themeColors[3];
    }

    private void ctxLog_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ctxCopy.Enabled = rtbLog.SelectionLength > 0;

    private void ctxCopy_Click(object? sender, EventArgs e) => rtbLog.Copy();
    private void ctxCopyAll_Click(object? sender, EventArgs e) => AppConstants.TrySetClipboardText(rtbLog.Text);
    private void ctxSelectAll_Click(object? sender, EventArgs e) => rtbLog.SelectAll();

    private void btnClearSearch_Click(object? sender, EventArgs e) => txtSearch.Clear();
    private void btnPrev_Click(object? sender, EventArgs e) => SearchPrev();
    private void btnNext_Click(object? sender, EventArgs e) => SearchNext();
    private void btnIssuePrev_Click(object? sender, EventArgs e) => IssuePrev();
    private void btnIssueNext_Click(object? sender, EventArgs e) => IssueNext();

    // Triggered when the search text changes - shows/hides the clear button, updates match
    // count and navigation immediately (fast text scan), then debounces the RTF rebuild
    // so that highlights appear without blocking typing.
    private void txtSearch_TextChanged(object? sender, EventArgs e)
    {
        btnClearSearch.Visible = txtSearch.Text.Length > 0;

        if (_searchDebounceTimer is null)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (_, _) => { timer.Stop(); RebuildDisplay(); };
            _searchDebounceTimer = timer;
        }

        if (txtSearch.Text.Length == 0)
        {
            // Search cleared - rebuild immediately to remove highlights (clean RTF = clean slate).
            // RebuildDisplay calls RefreshSearch internally, so no separate call needed here.
            _searchDebounceTimer.Stop();
            RebuildDisplay();
        }
        else
        {
            // Immediate match-count feedback while the user types (fast IndexOf scan).
            // The debounced RebuildDisplay applies the actual highlights afterward.
            RefreshSearch(navigateToFirst: true);
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
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

    // scrollToMatch: when false, updates match list and count label but does not scroll.
    // Used by AppendNewLines to avoid yanking the user away from their scroll position.
    private void RefreshSearch(bool navigateToFirst = false, bool scrollToMatch = true)
    {
        _searchMatches.Clear();

        string query = txtSearch.Text;
        if (string.IsNullOrEmpty(query))
        {
            lblMatchCount.Text = string.Empty;
            rtbLog.SelectionLength = 0;
            return;
        }

        // Scan plain text with IndexOf - avoids calling rtbLog.Find() which changes the
        // RichTextBox selection on every call and causes visible flashing.
        string text = rtbLog.Text;
        int start = 0;
        while (true)
        {
            int found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            _searchMatches.Add(found);
            start = found + 1;
        }

        if (_searchMatches.Count == 0)
        {
            _searchIndex = -1;
            lblMatchCount.Text = "0 / 0";
            rtbLog.SelectionLength = 0;
            return;
        }

        if (navigateToFirst || _searchIndex < 0 || _searchIndex >= _searchMatches.Count)
            _searchIndex = 0;

        if (scrollToMatch)
            NavigateToMatch(_searchIndex);
        else
            lblMatchCount.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
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

    // Paints yellow background on search matches using SelectionBackColor.
    // RTF-based highlighting (\cb, \highlight) does not render in WinForms RichTextBox.
    // Called after RebuildDisplay (clean slate) and AppendNewLines (re-paints all matches
    // including previously highlighted ones, which is harmless since the colour is the same).
    // Capped at 500 highlights to avoid freezing on very large result sets.
    // Callers must bracket this method with WinMsg.WM_SETREDRAW(false)/WinMsg.WM_SETREDRAW(true)
    // and call Invalidate() afterward to avoid flicker during the Select loop.
    private void ApplySearchHighlights()
    {
        if (_searchMatches.Count == 0 || txtSearch.Text.Length == 0 || rtbLog.TextLength == 0)
            return;

        int savedStart = rtbLog.SelectionStart;
        int savedLen = rtbLog.SelectionLength;
        Color bg = AppConstants.SearchHighlight;
        int len = txtSearch.Text.Length;
        int count = Math.Min(_searchMatches.Count, MaxHighlights);

        for (int i = 0; i < count; i++)
        {
            rtbLog.Select(_searchMatches[i], len);
            rtbLog.SelectionBackColor = bg;
        }

        rtbLog.Select(savedStart, savedLen);
    }

    // Rebuilds the RTF display from the in-memory line store with the current filter,
    // then re-applies search highlights. Preserves scroll: only scrolls to bottom if the user was there.
    private void RebuildDisplay()
    {
        bool wasAtBottom = IsAtBottom();
        bool[] filters = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];
        string? subsystemFilter = GetSubsystemFilter();
        string[] filtered = _allLines.Where(l => IsLineVisibleWithFilters(l, filters, subsystemFilter)).ToArray();

        // Rebuild under redraw suppression so the swap + scroll + highlight paint a single settled
        // frame. Setting Rtf resets the scroll to the top, so doing it inside the suppression avoids
        // a brief flash where the new content shows at the top before scrolling.
        WithRedrawSuppressed(() =>
        {
            HideMetaLabel();
            rtbLog.Rtf = BuildRtf(filtered, _themeColors);
            if (wasAtBottom) ScrollToBottom();
            RefreshSearch(navigateToFirst: true);
            ApplySearchHighlights();
        });
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

    // Scrolls to the previous (older) WARN or ERROR line relative to the current selection.
    // Matches the level-column markers (e.g. "| WARN  |") so settings names like
    // warnOnInterfaceMismatch=True are not falsely matched.
    private void IssuePrev()
    {
        int end = rtbLog.SelectionStart;
        if (end <= 0 || rtbLog.TextLength == 0) return;
        int prevError = rtbLog.Find(ColError, 0, end, RichTextBoxFinds.Reverse);
        int prevWarn = rtbLog.Find(ColWarn, 0, end, RichTextBoxFinds.Reverse);
        int prev = (prevError, prevWarn) switch
        {
            ( < 0, _) => prevWarn,
            (_, < 0) => prevError,
            _ => Math.Max(prevError, prevWarn)
        };
        if (prev < 0) return;
        rtbLog.Select(prev, 0);
        rtbLog.ScrollToCaret();
    }

    // Scrolls to the next (newer) WARN or ERROR line relative to the current selection.
    // Matches the level-column markers (e.g. "| WARN  |") so settings names like
    // warnOnInterfaceMismatch=True are not falsely matched.
    private void IssueNext()
    {
        int start = rtbLog.SelectionStart + 1;
        if (start >= rtbLog.TextLength) return;
        int nextError = rtbLog.Find(ColError, start, RichTextBoxFinds.None);
        int nextWarn = rtbLog.Find(ColWarn, start, RichTextBoxFinds.None);
        int next = (nextError, nextWarn) switch
        {
            ( < 0, _) => nextWarn,
            (_, < 0) => nextError,
            _ => Math.Min(nextError, nextWarn)
        };
        if (next < 0) return;
        rtbLog.Select(next, 0);
        rtbLog.ScrollToCaret();
    }

    /// <summary>Scrolls to the most recent (last) WARN or ERROR line in the log, falling back to
    /// the bottom of the log if none are present. Used when opening the log viewer with unviewed
    /// alerts (via tray balloon click or Show Logs).</summary>
    public void NavigateToLatestIssue()
    {
        if (rtbLog.TextLength == 0) { ScrollToBottom(); return; }
        int lastError = rtbLog.Find(ColError, 0, rtbLog.TextLength, RichTextBoxFinds.Reverse);
        int lastWarn = rtbLog.Find(ColWarn, 0, rtbLog.TextLength, RichTextBoxFinds.Reverse);
        int lastIdx = Math.Max(lastError, lastWarn);
        if (lastIdx < 0) { ScrollToBottom(); return; }
        rtbLog.Select(lastIdx, 0);
        rtbLog.ScrollToCaret();
    }

    private void ScrollToBottom()
    {
        SendMessage(rtbLog.Handle, WinMsg.WM_VSCROLL, (IntPtr)WinMsg.SB_BOTTOM, IntPtr.Zero);
    }

    // Batches a set of RichTextBox mutations (Rtf/SelectedRtf swap, scroll, highlight) into a single
    // visible frame: suppresses painting, runs the update, then re-enables redraw and repaints once.
    // Without this the intermediate states (content at top, caret at end, highlight loop) flicker.
    private void WithRedrawSuppressed(Action update)
    {
        SendMessage(rtbLog.Handle, WinMsg.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            update();
        }
        finally
        {
            SendMessage(rtbLog.Handle, WinMsg.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            rtbLog.Invalidate();
        }
    }

    // Ensures the existing text ends with \n so the next SelectedRtf insert starts on
    // its own line. The last \par in an RTF document does not always produce a trailing
    // newline in WinForms RichTextBox, which causes line merging on append.
    private void EnsureTrailingNewline()
    {
        int len = rtbLog.TextLength;
        if (len == 0) return;
        rtbLog.Select(len - 1, 1);
        if (rtbLog.SelectedText != "\n")
        {
            rtbLog.Select(len, 0);
            rtbLog.SelectedText = "\n";
        }
    }

    // Reads the full log file and builds its RTF representation on a background thread,
    // then sets rtbLog.Rtf in a single UI-thread operation for near-instant rendering.
    // StartWatcher is called in the finally block so _lastReadPosition is set before
    // any live-update events can fire.
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

            // Show a loading hint, but only for logs large enough that the read + RTF parse is
            // perceptible - small logs (the common case) would otherwise flicker a "Loading" frame.
            if (new FileInfo(loadPath).Length > LoadingIndicatorMinBytes)
            {
                SetMetaMessage("Loading\u2026", MetaColor);
                // Force the overlay to paint now, before the blocking read/RTF parse below. Without
                // this, the read can finish and its continuation can hide the label before the next
                // paint, leaving the label invisible despite being momentarily set visible.
                _metaLabel!.Refresh();
            }

            // Capture UI-thread state before entering the background task
            Color[] colors = _themeColors;
            bool[] filters = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];
            string? subsystemFilter = GetSubsystemFilter();

            (string rtf, long position, string[] allLines) = await Task.Run(() =>
            {
                lock (_readLock)
                {
                    using var fs = new FileStream(loadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    string[] lines = ParseLogLines(reader.ReadToEnd());
                    string[] filtered = lines.Where(l => IsLineVisibleWithFilters(l, filters, subsystemFilter)).ToArray();
                    return (BuildRtf(filtered, colors), fs.Position, lines);
                }
            });

            if (IsDisposed) return;

            _allLines.AddRange(allLines);
            _lastReadPosition = position;

            // Populate under redraw suppression so the user sees one final frame (scrolled to the
            // bottom / latest issue) instead of content painting at the top and then visibly jumping.
            WithRedrawSuppressed(() =>
            {
                rtbLog.Rtf = rtf;
                // Native RichEdit records every Rtf/SelectedRtf assignment in its undo buffer.
                // The viewer is read-only so undo is unreachable - clear it so the buffer does
                // not hold a redundant copy of the document content.
                rtbLog.ClearUndo();
                if (_navigateToLatestIssue) { NavigateToLatestIssue(); _navigateToLatestIssue = false; } else ScrollToBottom();
                if (!string.IsNullOrEmpty(txtSearch.Text))
                {
                    RefreshSearch(navigateToFirst: true);
                    ApplySearchHighlights();
                }
                // Hide the "Loading..." overlay only after the (slow) Rtf parse and scroll have
                // completed. rtbLog's painting is suppressed during this block, so keeping the
                // sibling overlay visible here means the user sees "Loading..." for the whole parse
                // instead of a blank frozen window, then a single settled frame of real content.
                HideMetaLabel();
            });

            // Arm a reclaim so the transient RTF built for this load is compacted once idle.
            MarkContentActivity();
        }
        catch (Exception ex)
        {
            // _themeColors[0] is the error color already resolved for the active theme; using a
            // fixed dark variant would clash with the foreground text colour in light mode.
            if (!IsDisposed)
                SetMetaMessage($"(Error reading log: {ex.Message})", _themeColors[0]);
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

    // Reads any new content appended since the last read and appends visible lines to the display.
    // Only scrolls to the bottom if the user was already there before the update.
    // The generation parameter holds the value of _watcherGeneration at watcher-subscription time.
    // Events with a stale generation are ignored to defend against the race between in-flight
    // FileSystemWatcher events and a file switch. See _watcherGeneration for details.
    private void OnLogFileUpdated(int generation)
    {
        try
        {
            string[] newLines;
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
                rtbLog.Clear();
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
        rtbLog.Clear();

        _ = LoadInitialContentAsync();
    }

    private readonly record struct LogFileEntry(string DisplayName, string FilePath)
    {
        public override string ToString() => DisplayName;
    }

    // Appends new lines to the in-memory store and inserts visible lines into the display
    // via SelectedRtf. Unlike a full RTF rebuild, SelectedRtf does not reset the scroll
    // position, so the user's viewport stays stable and there is no flicker.
    // Must be called on the UI thread.
    private void AppendNewLines(string[] newLines)
    {
        // Any append (even one filtered to nothing visible) parses lines and grows _allLines,
        // producing transient garbage; arm a reclaim so it is compacted once logging settles.
        MarkContentActivity();

        bool wasAtBottom = IsAtBottom();
        bool[] filters = [chkError.Checked, chkWarn.Checked, chkInfo.Checked, chkDebug.Checked];
        string? subsystemFilter = GetSubsystemFilter();

        _allLines.AddRange(newLines);

        string[] visibleNew = newLines.Where(l => IsLineVisibleWithFilters(l, filters, subsystemFilter)).ToArray();
        if (visibleNew.Length == 0)
            return;

        // Save caret, selection, and scroll so the append is invisible to the user.
        // Text is appended at the end, so existing character positions remain valid.
        int savedSelStart = rtbLog.SelectionStart;
        int savedSelLen = rtbLog.SelectionLength;
        NativePoint scrollPos = default;
        if (!wasAtBottom)
            SendMessage(rtbLog.Handle, WinMsg.EM_GETSCROLLPOS, IntPtr.Zero, ref scrollPos);

        // Suppress painting so intermediate states (caret at end, highlight loop) don't flicker.
        // The scroll restore is the last step of the batch (it depends on whether the user was at the
        // bottom before the append); the helper handles re-enabling redraw and the single repaint.
        WithRedrawSuppressed(() =>
        {
            HideMetaLabel();
            EnsureTrailingNewline();
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectedRtf = BuildRtf(visibleNew, _themeColors);
            // Native RichEdit records every SelectedRtf insertion in its undo buffer; left
            // unchecked the buffer accumulates a copy of every appended batch and grows the
            // working set linearly with append count. The viewer is read-only so undo is
            // unreachable - clear it after each insertion to bound the buffer.
            rtbLog.ClearUndo();

            if (!string.IsNullOrEmpty(txtSearch.Text))
            {
                RefreshSearch(navigateToFirst: false, scrollToMatch: false);
                ApplySearchHighlights();
            }

            rtbLog.Select(savedSelStart, savedSelLen);

            if (wasAtBottom)
                ScrollToBottom();
            else
                SendMessage(rtbLog.Handle, WinMsg.EM_SETSCROLLPOS, IntPtr.Zero, ref scrollPos);
        });
    }

    // Splits raw log content on newlines and strips trailing \r so CRLF and LF files produce identical lines.
    private static string[] ParseLogLines(string raw) =>
        raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
           .Select(l => l.TrimEnd('\r'))
           .ToArray();

    // Returns the 0-based colour index for a log line, used by BuildRtf and IsLineVisibleWithFilters.
    // Log format: "yyyy-MM-dd HH:mm:ss | LEVEL | Subsystem     | message" (level padded to 5 chars)
    private static int GetLineColorIndex(string line)
    {
        if (line.Contains(ColError, StringComparison.Ordinal)) return 0;
        if (line.Contains(ColWarn, StringComparison.Ordinal)) return 1;
        if (line.Contains(ColInfo, StringComparison.Ordinal)) return 2;
        if (line.Contains(ColDebug, StringComparison.Ordinal)) return 3;
        return 4;
    }

    // Static - safe to call from background threads (no UI state access).
    // Meta/unclassified lines (index >= 4, e.g. blank cycle separators) are shown only when
    // all level filters are active and no subsystem filter is set; hiding them otherwise
    // prevents blank lines from appearing in a filtered view.
    private static bool IsLineVisibleWithFilters(string line, bool[] filters, string? subsystemToken)
    {
        int idx = GetLineColorIndex(line);
        if (idx >= filters.Length) return subsystemToken is null && Array.TrueForAll(filters, f => f);
        if (!filters[idx]) return false;                            // level filtered out
        if (subsystemToken is not null && !line.Contains(subsystemToken, StringComparison.Ordinal)) return false;
        return true;
    }

    // Convenience colour for meta/status messages (not log entries)
    private static Color MetaColor => SystemColors.GrayText; // mode-aware under SetColorMode

    // Writes the RTF document header for BuildRtf:
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
    // Thread-safe (no UI state access); called from both background and UI threads.
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
                case '{': sb.Append("\\{"); break;
                case '}': sb.Append("\\}"); break;
                default:
                    if (c == '\0') break; // NUL terminates RTF in RichEdit; skip to avoid corrupting the document
                    if (c > 127) sb.Append($"\\u{(int)(short)c} ");
                    else sb.Append(c);
                    break;
            }
        }
    }

    // Displays a one-off meta/error message (e.g. "No log entries yet", watcher errors).
    // Shows a centred status/placeholder message (loading, empty, error) over the log area.
    // The overlay Label fully covers rtbLog with the same background, so it reads as the log's
    // own empty/error state; it is hidden again as soon as real content is displayed.
    private void SetMetaMessage(string text, Color color)
    {
        EnsureMetaLabel();
        _metaLabel!.BackColor = rtbLog.BackColor;
        _metaLabel.ForeColor = color;
        _metaLabel.Text = text;
        SyncMetaLabelBounds();
        _metaLabel.Visible = true;
        _metaLabel.BringToFront();
    }

    // Creates the overlay Label as a sibling of rtbLog (not a child - a RichTextBox does not host
    // child controls reliably across repaints) and keeps it aligned to rtbLog's bounds.
    private void EnsureMetaLabel()
    {
        if (_metaLabel is not null) return;
        _metaLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = rtbLog.Font,
            Visible = false,
        };
        rtbLog.Parent!.Controls.Add(_metaLabel);
        SyncMetaLabelBounds();
        rtbLog.SizeChanged += (_, _) => SyncMetaLabelBounds();
        rtbLog.LocationChanged += (_, _) => SyncMetaLabelBounds();
    }

    private void SyncMetaLabelBounds()
    {
        if (_metaLabel is not null) _metaLabel.Bounds = rtbLog.Bounds;
    }

    private void HideMetaLabel()
    {
        if (_metaLabel is not null) _metaLabel.Visible = false;
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

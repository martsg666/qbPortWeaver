namespace qbPortWeaver;

partial class LogViewerForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher?.Dispose();
            _dragScrollTimer?.Dispose();

            // Dispose explicitly created fonts (WinForms controls do not own their Font)
            lvLog?.Font?.Dispose();
            chkError?.Font?.Dispose();
            chkWarn?.Font?.Dispose();
            chkInfo?.Font?.Dispose();
            chkDebug?.Font?.Dispose();
            lblMatchCount?.Font?.Dispose();

            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components     = new System.ComponentModel.Container();
        lvLog          = new BufferedListView();
        colLog         = new ColumnHeader();
        pnlToolbar     = new Panel();
        chkError       = new CheckBox();
        chkWarn        = new CheckBox();
        chkInfo        = new CheckBox();
        chkDebug       = new CheckBox();
        cboSubsystem   = new ComboBox();
        cboTimeRange   = new ComboBox();
        cboLogFile     = new ComboBox();
        btnIssuePrev   = new Button();
        btnIssueNext   = new Button();
        txtSearch      = new PlaceholderTextBox();
        btnClearSearch = new Button();
        btnPrev        = new Button();
        btnNext        = new Button();
        lblMatchCount  = new Label();
        ctxLog         = new ContextMenuStrip(components);
        ctxCopy        = new ToolStripMenuItem();
        ctxCopyAll     = new ToolStripMenuItem();
        ctxSelectAll   = new ToolStripMenuItem();
        toolTip        = new ToolTip(components);
        pnlToolbar.SuspendLayout();
        SuspendLayout();

        // Filter CheckBoxes - colors applied in OnLoad after theme is determined
        AnchorStyles leftAnchor = AnchorStyles.Left | AnchorStyles.Top;

        chkError.Anchor                  = leftAnchor;
        chkError.Appearance              = Appearance.Button;
        chkError.AutoSize                = false;
        chkError.Checked                 = true;
        chkError.FlatStyle               = FlatStyle.Flat;
        chkError.Font                    = new Font("Segoe UI", 8.25F, FontStyle.Bold);
        chkError.Location                = new Point(4, 4);
        chkError.Size                    = new Size(68, 26);
        chkError.TabIndex                = 0;
        chkError.Text                    = "ERROR";
        chkError.TextAlign               = ContentAlignment.MiddleCenter;
        chkError.UseVisualStyleBackColor = false;

        chkWarn.Anchor                  = leftAnchor;
        chkWarn.Appearance              = Appearance.Button;
        chkWarn.AutoSize                = false;
        chkWarn.Checked                 = true;
        chkWarn.FlatStyle               = FlatStyle.Flat;
        chkWarn.Font                    = new Font("Segoe UI", 8.25F, FontStyle.Bold);
        chkWarn.Location                = new Point(76, 4);
        chkWarn.Size                    = new Size(68, 26);
        chkWarn.TabIndex                = 1;
        chkWarn.Text                    = "WARN";
        chkWarn.TextAlign               = ContentAlignment.MiddleCenter;
        chkWarn.UseVisualStyleBackColor = false;

        chkInfo.Anchor                  = leftAnchor;
        chkInfo.Appearance              = Appearance.Button;
        chkInfo.AutoSize                = false;
        chkInfo.Checked                 = true;
        chkInfo.FlatStyle               = FlatStyle.Flat;
        chkInfo.Font                    = new Font("Segoe UI", 8.25F, FontStyle.Bold);
        chkInfo.Location                = new Point(148, 4);
        chkInfo.Size                    = new Size(68, 26);
        chkInfo.TabIndex                = 2;
        chkInfo.Text                    = "INFO";
        chkInfo.TextAlign               = ContentAlignment.MiddleCenter;
        chkInfo.UseVisualStyleBackColor = false;

        chkDebug.Anchor                  = leftAnchor;
        chkDebug.Appearance              = Appearance.Button;
        chkDebug.AutoSize                = false;
        chkDebug.Checked                 = true;
        chkDebug.FlatStyle               = FlatStyle.Flat;
        chkDebug.Font                    = new Font("Segoe UI", 8.25F, FontStyle.Bold);
        chkDebug.Location                = new Point(220, 4);
        chkDebug.Size                    = new Size(68, 26);
        chkDebug.TabIndex                = 3;
        chkDebug.Text                    = "DEBUG";
        chkDebug.TextAlign               = ContentAlignment.MiddleCenter;
        chkDebug.UseVisualStyleBackColor = false;

        // Wire filter events after setting Checked = true to avoid premature filterButton_CheckedChanged
        chkError.CheckedChanged += filterButton_CheckedChanged;
        chkWarn.CheckedChanged  += filterButton_CheckedChanged;
        chkInfo.CheckedChanged  += filterButton_CheckedChanged;
        chkDebug.CheckedChanged += filterButton_CheckedChanged;
        toolTip.SetToolTip(chkError, "Show or hide ERROR entries");
        toolTip.SetToolTip(chkWarn,  "Show or hide WARN entries");
        toolTip.SetToolTip(chkInfo,  "Show or hide INFO entries");
        toolTip.SetToolTip(chkDebug, "Show or hide DEBUG entries");

        // Issue navigation buttons - grouped with the level filter buttons, navigate between WARN/ERROR lines.
        // Positioned immediately after chkDebug; y/h kept at filter-button values (no OnLoad adjustment).
        btnIssuePrev.Anchor    = leftAnchor;
        btnIssuePrev.FlatStyle = FlatStyle.Flat;
        btnIssuePrev.Location  = new Point(296, 4);
        btnIssuePrev.Size      = new Size(26, 26);
        btnIssuePrev.TabIndex  = 4;
        btnIssuePrev.Text      = ""; // up chevron owner-drawn in NavButton_Paint
        btnIssuePrev.Click    += btnIssuePrev_Click;
        toolTip.SetToolTip(btnIssuePrev, "Previous warning or error");

        btnIssueNext.Anchor    = leftAnchor;
        btnIssueNext.FlatStyle = FlatStyle.Flat;
        btnIssueNext.Location  = new Point(322, 4);
        btnIssueNext.Size      = new Size(26, 26);
        btnIssueNext.TabIndex  = 5;
        btnIssueNext.Text      = ""; // down chevron owner-drawn in NavButton_Paint
        btnIssueNext.Click    += btnIssueNext_Click;
        toolTip.SetToolTip(btnIssueNext, "Next warning or error");

        // Subsystem filter - positioned after the level buttons and issue nav buttons
        // FlatStyle.Flat on every combo (app-wide): the themed DropDownList face ignores dark
        // mode and renders light; the flat face is drawn with the mode-aware BackColor instead.
        cboSubsystem.Anchor        = AnchorStyles.Left | AnchorStyles.Top;
        cboSubsystem.DropDownStyle = ComboBoxStyle.DropDownList;
        cboSubsystem.FlatStyle     = FlatStyle.Flat;
        cboSubsystem.Items.AddRange(new object[] { "All", Subsystem.MainApp, Subsystem.MediaManager, Subsystem.HelperService });
        cboSubsystem.Location      = new Point(380, 6);
        cboSubsystem.Size          = new Size(130, 23);
        cboSubsystem.TabIndex      = 6;
        cboSubsystem.SelectedIndex = 0;
        // Wire event after setting SelectedIndex to avoid premature RebuildDisplay
        cboSubsystem.SelectedIndexChanged += cboSubsystem_SelectedIndexChanged;
        toolTip.SetToolTip(cboSubsystem, "Filter entries by subsystem");

        // Time range - narrows the view to a window ending now, or to a custom span. Sits between the
        // subsystem and log-file pickers because all three answer "which entries", unlike search.
        cboTimeRange.Anchor        = AnchorStyles.Left | AnchorStyles.Top;
        cboTimeRange.DropDownStyle = ComboBoxStyle.DropDownList;
        cboTimeRange.FlatStyle     = FlatStyle.Flat;
        cboTimeRange.Location      = new Point(628, 6);
        cboTimeRange.Size          = new Size(118, 23);
        cboTimeRange.TabIndex      = 8;
        // Items and SelectedIndex are set in OnLoad, with the event wired after, so populating the
        // list cannot trigger a rebuild before the log has been read.
        toolTip.SetToolTip(cboTimeRange, "Show only entries from a time range");

        // Log file picker - populated in OnLoad; event wired there after population to avoid premature load
        cboLogFile.Anchor        = AnchorStyles.Left | AnchorStyles.Top;
        cboLogFile.DropDownStyle = ComboBoxStyle.DropDownList;
        cboLogFile.FlatStyle     = FlatStyle.Flat;
        cboLogFile.Location      = new Point(514, 6);
        cboLogFile.Size          = new Size(110, 23);
        cboLogFile.TabIndex      = 7;
        toolTip.SetToolTip(cboLogFile, "Select log file");

        // Search controls - anchored Right so they stay visible when the form is resized
        // Layout from right: [4] [btnNext:26] [btnPrev:26] [4] [lblMatchCount:64] [4] [txtSearch:220] [8]
        // btnClearSearch floats inside the right edge of txtSearch (z-order above it); positioned in OnLoad.
        AnchorStyles rightAnchor = AnchorStyles.Right | AnchorStyles.Top;

        // txtSearch - height is auto-sized by its font; vertically centered in the 36px toolbar in OnLoad
        txtSearch.Anchor          = rightAnchor;
        txtSearch.BorderStyle     = BorderStyle.FixedSingle;
        txtSearch.Location        = new Point(752, 8);
        txtSearch.PlaceholderText = "Search…";
        txtSearch.Width           = 220; // height is auto-sized by font; vertically centered in OnLoad
        txtSearch.TabIndex        = 9;
        txtSearch.TextChanged    += txtSearch_TextChanged;
        txtSearch.KeyDown        += txtSearch_KeyDown;
        toolTip.SetToolTip(txtSearch, "Search the log (Ctrl+F)");

        // btnClearSearch - overlays the right interior of txtSearch; sized and positioned in OnLoad.
        // Right-margin set to txtSearch.RightMargin - 2 so the button always stays 2px inside the box on resize.
        btnClearSearch.Anchor                    = rightAnchor;
        btnClearSearch.FlatStyle                 = FlatStyle.Flat;
        btnClearSearch.FlatAppearance.BorderSize = 0;
        btnClearSearch.Location                  = new Point(950, 5); // fine-tuned in OnLoad
        btnClearSearch.Padding                   = new Padding(0);
        btnClearSearch.Size                      = new Size(16, 16);  // fine-tuned in OnLoad
        btnClearSearch.Text                      = ""; // clear glyph owner-drawn in ClearButton_Paint
        btnClearSearch.TextAlign                 = ContentAlignment.MiddleCenter;
        btnClearSearch.TabIndex                  = 10;
        btnClearSearch.TabStop                   = false;
        btnClearSearch.Visible                   = false;
        btnClearSearch.Click                    += btnClearSearch_Click;
        toolTip.SetToolTip(btnClearSearch, "Clear search");

        // btnPrev
        btnPrev.Anchor    = rightAnchor;
        btnPrev.FlatStyle = FlatStyle.Flat;
        btnPrev.Location  = new Point(1044, 5);
        btnPrev.Size      = new Size(26, 26);
        btnPrev.TabIndex  = 11;
        btnPrev.Text      = ""; // up chevron owner-drawn in NavButton_Paint
        btnPrev.Click    += btnPrev_Click;
        toolTip.SetToolTip(btnPrev, "Previous match");

        // btnNext
        btnNext.Anchor    = rightAnchor;
        btnNext.FlatStyle = FlatStyle.Flat;
        btnNext.Location  = new Point(1070, 5);
        btnNext.Size      = new Size(26, 26);
        btnNext.TabIndex  = 12;
        btnNext.Text      = ""; // down chevron owner-drawn in NavButton_Paint
        btnNext.Click    += btnNext_Click;
        toolTip.SetToolTip(btnNext, "Next match");

        // lblMatchCount
        lblMatchCount.Anchor    = rightAnchor;
        lblMatchCount.AutoSize  = false;
        lblMatchCount.Font      = new Font("Segoe UI", 8F);
        lblMatchCount.Location  = new Point(976, 11);
        lblMatchCount.Size      = new Size(64, 14);
        lblMatchCount.TextAlign = ContentAlignment.MiddleLeft;

        // pnlToolbar - Width must be set explicitly before right-anchored children are added,
        // otherwise the default panel width (~200px) produces a negative right-margin and
        // causes right-anchored controls to fly off-screen when the panel expands to form width.
        pnlToolbar.Controls.AddRange(new Control[] {
            chkError, chkWarn, chkInfo, chkDebug, cboSubsystem, cboTimeRange, cboLogFile, btnIssuePrev, btnIssueNext,
            txtSearch, btnClearSearch, btnPrev, btnNext, lblMatchCount });
        pnlToolbar.Dock     = DockStyle.Top;
        pnlToolbar.Size     = new Size(1100, 36);
        pnlToolbar.TabIndex = 0;

        // ctxLog - right-click context menu for the log viewer
        ctxCopy.Text      = "Copy";
        ctxCopyAll.Text   = "Copy All";
        ctxSelectAll.Text = "Select All";
        ctxCopy.Click      += ctxCopy_Click;
        ctxCopyAll.Click   += ctxCopyAll_Click;
        ctxSelectAll.Click += ctxSelectAll_Click;
        ctxLog.Items.AddRange(new ToolStripItem[] { ctxCopy, ctxCopyAll, ctxSelectAll });
        ctxLog.Opening += ctxLog_Opening;
        lvLog.ContextMenuStrip = ctxLog;

        // lvLog - owner-drawn virtual list over the in-memory line store. VirtualMode renders
        // rows on demand, so filter changes and appends never re-render the whole document.
        // One header-less column provides the horizontal scrollbar; its width tracks the
        // widest visible line (see UpdateColumnWidth).
        colLog.Text  = "";
        colLog.Width = 1100;
        lvLog.BackColor       = SystemColors.Control;
        lvLog.BorderStyle     = BorderStyle.None;
        lvLog.Columns.Add(colLog);
        lvLog.Dock            = DockStyle.Fill;
        lvLog.Font            = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        lvLog.FullRowSelect   = true;
        lvLog.HeaderStyle     = ColumnHeaderStyle.None;
        lvLog.MultiSelect     = true;
        lvLog.OwnerDraw       = true;
        lvLog.ShowGroups      = false;
        lvLog.Size            = new Size(1100, 524);
        lvLog.TabIndex        = 1;
        lvLog.UseCompatibleStateImageBehavior = false;
        lvLog.View            = View.Details;
        lvLog.VirtualMode     = true;
        lvLog.RetrieveVirtualItem += lvLog_RetrieveVirtualItem;
        lvLog.DrawItem            += lvLog_DrawItem;
        lvLog.KeyDown             += lvLog_KeyDown;
        lvLog.MouseDown           += lvLog_MouseDown;
        lvLog.MouseMove           += lvLog_MouseMove;
        lvLog.MouseUp             += lvLog_MouseUp;

        // LogViewerForm
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;
        ClientSize          = new Size(1100, 680);
        Controls.Add(lvLog);
        Controls.Add(pnlToolbar);
        MaximizeBox         = true;
        MinimizeBox         = true;
        // Static designer minimum (DPI-scaled by AutoScaleMode.Font), the pattern shared by all
        // sizable forms. Width is the point where the right-anchored search block would meet the left
        // filter/combo block. LayoutSearchToolbar places txtSearch.Left at clientWidth minus its own
        // fixed span: margin 4 + next 26 + prev 26 + gap 4 + count 64 + gap 4 + search 220 = 348. The
        // rightmost left-hand control is now cboTimeRange, ending at 746, so the narrowest client that
        // still clears it is 746 + gap 4 + 348 = 1098, plus ~16 window border.
        //
        // This leaves little room to narrow the window, which is deliberate: the toolbar is at
        // capacity, and the alternatives were shrinking the search field or the time-range combo for
        // every user to serve a rare window size. A log viewer is a wide-content window anyway.
        // Anything added to the toolbar from here needs this recomputed - nothing enforces it, and the
        // failure mode is silent: the search box simply covers whatever it lands on.
        MinimumSize         = new Size(1114, 300);
        Name                = "LogViewerForm";
        Icon                = Properties.Resources.qbPortWeaver;
        ShowIcon            = true;
        ShowInTaskbar       = true;
        StartPosition       = FormStartPosition.CenterScreen;
        Text                = "qbPortWeaver | Log Viewer"; // overridden in OnLoad with AppIdentity.AppName

        pnlToolbar.ResumeLayout(false);
        ResumeLayout(false);
    }

    private BufferedListView   lvLog;
    private ColumnHeader       colLog;
    private Panel              pnlToolbar;
    private CheckBox           chkError;
    private CheckBox           chkWarn;
    private CheckBox           chkInfo;
    private CheckBox           chkDebug;
    private ComboBox           cboSubsystem;
    private ComboBox           cboTimeRange;
    private ComboBox           cboLogFile;
    private Button             btnIssuePrev;
    private Button             btnIssueNext;
    private PlaceholderTextBox txtSearch;
    private Button             btnClearSearch;
    private Button             btnPrev;
    private Button             btnNext;
    private Label              lblMatchCount;
    private ContextMenuStrip   ctxLog;
    private ToolStripMenuItem  ctxCopy;
    private ToolStripMenuItem  ctxCopyAll;
    private ToolStripMenuItem  ctxSelectAll;
    private ToolTip            toolTip;
}

namespace qbPortWeaver;

partial class LogViewerForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher?.Dispose();
            _searchDebounceTimer?.Dispose();

            // Dispose explicitly created fonts (WinForms controls do not own their Font)
            rtbLog?.Font?.Dispose();
            chkError?.Font?.Dispose();
            chkWarn?.Font?.Dispose();
            chkInfo?.Font?.Dispose();
            chkDebug?.Font?.Dispose();
            btnClearSearch?.Font?.Dispose();
            lblMatchCount?.Font?.Dispose();

            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        rtbLog        = new System.Windows.Forms.RichTextBox();
        pnlToolbar    = new System.Windows.Forms.Panel();
        chkError      = new System.Windows.Forms.CheckBox();
        chkWarn       = new System.Windows.Forms.CheckBox();
        chkInfo       = new System.Windows.Forms.CheckBox();
        chkDebug      = new System.Windows.Forms.CheckBox();
        cboSubsystem  = new System.Windows.Forms.ComboBox();
        cboLogFile    = new System.Windows.Forms.ComboBox();
        btnIssuePrev   = new System.Windows.Forms.Button();
        btnIssueNext   = new System.Windows.Forms.Button();
        txtSearch      = new PlaceholderTextBox();
        btnClearSearch = new System.Windows.Forms.Button();
        btnPrev        = new System.Windows.Forms.Button();
        btnNext        = new System.Windows.Forms.Button();
        lblMatchCount  = new System.Windows.Forms.Label();
        ctxLog         = new System.Windows.Forms.ContextMenuStrip();
        ctxCopy        = new System.Windows.Forms.ToolStripMenuItem();
        ctxCopyAll     = new System.Windows.Forms.ToolStripMenuItem();
        ctxSelectAll   = new System.Windows.Forms.ToolStripMenuItem();
        toolTip        = new System.Windows.Forms.ToolTip();
        components     = new System.ComponentModel.Container();
        components.Add(ctxLog);
        components.Add(toolTip);
        pnlToolbar.SuspendLayout();
        SuspendLayout();

        // Filter CheckBoxes - colors applied in OnLoad after theme is determined
        System.Windows.Forms.AnchorStyles leftAnchor = System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Top;

        chkError.Anchor                  = leftAnchor;
        chkError.Appearance              = System.Windows.Forms.Appearance.Button;
        chkError.AutoSize                = false;
        chkError.Checked                 = true;
        chkError.FlatStyle               = System.Windows.Forms.FlatStyle.Flat;
        chkError.Font                    = new System.Drawing.Font("Segoe UI", 8.25F, System.Drawing.FontStyle.Bold);
        chkError.Location                = new System.Drawing.Point(4, 4);
        chkError.Size                    = new System.Drawing.Size(68, 26);
        chkError.TabIndex                = 0;
        chkError.Text                    = "ERROR";
        chkError.TextAlign               = System.Drawing.ContentAlignment.MiddleCenter;
        chkError.UseVisualStyleBackColor = false;

        chkWarn.Anchor                  = leftAnchor;
        chkWarn.Appearance              = System.Windows.Forms.Appearance.Button;
        chkWarn.AutoSize                = false;
        chkWarn.Checked                 = true;
        chkWarn.FlatStyle               = System.Windows.Forms.FlatStyle.Flat;
        chkWarn.Font                    = new System.Drawing.Font("Segoe UI", 8.25F, System.Drawing.FontStyle.Bold);
        chkWarn.Location                = new System.Drawing.Point(76, 4);
        chkWarn.Size                    = new System.Drawing.Size(68, 26);
        chkWarn.TabIndex                = 1;
        chkWarn.Text                    = "WARN";
        chkWarn.TextAlign               = System.Drawing.ContentAlignment.MiddleCenter;
        chkWarn.UseVisualStyleBackColor = false;

        chkInfo.Anchor                  = leftAnchor;
        chkInfo.Appearance              = System.Windows.Forms.Appearance.Button;
        chkInfo.AutoSize                = false;
        chkInfo.Checked                 = true;
        chkInfo.FlatStyle               = System.Windows.Forms.FlatStyle.Flat;
        chkInfo.Font                    = new System.Drawing.Font("Segoe UI", 8.25F, System.Drawing.FontStyle.Bold);
        chkInfo.Location                = new System.Drawing.Point(148, 4);
        chkInfo.Size                    = new System.Drawing.Size(68, 26);
        chkInfo.TabIndex                = 2;
        chkInfo.Text                    = "INFO";
        chkInfo.TextAlign               = System.Drawing.ContentAlignment.MiddleCenter;
        chkInfo.UseVisualStyleBackColor = false;

        chkDebug.Anchor                  = leftAnchor;
        chkDebug.Appearance              = System.Windows.Forms.Appearance.Button;
        chkDebug.AutoSize                = false;
        chkDebug.Checked                 = true;
        chkDebug.FlatStyle               = System.Windows.Forms.FlatStyle.Flat;
        chkDebug.Font                    = new System.Drawing.Font("Segoe UI", 8.25F, System.Drawing.FontStyle.Bold);
        chkDebug.Location                = new System.Drawing.Point(220, 4);
        chkDebug.Size                    = new System.Drawing.Size(68, 26);
        chkDebug.TabIndex                = 3;
        chkDebug.Text                    = "DEBUG";
        chkDebug.TextAlign               = System.Drawing.ContentAlignment.MiddleCenter;
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
        btnIssuePrev.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
        btnIssuePrev.Location  = new System.Drawing.Point(296, 4);
        btnIssuePrev.Size      = new System.Drawing.Size(26, 26);
        btnIssuePrev.TabIndex  = 4;
        btnIssuePrev.Text      = "▲";
        btnIssuePrev.Click    += btnIssuePrev_Click;
        toolTip.SetToolTip(btnIssuePrev, "Previous warning or error");

        btnIssueNext.Anchor    = leftAnchor;
        btnIssueNext.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
        btnIssueNext.Location  = new System.Drawing.Point(322, 4);
        btnIssueNext.Size      = new System.Drawing.Size(26, 26);
        btnIssueNext.TabIndex  = 5;
        btnIssueNext.Text      = "▼";
        btnIssueNext.Click    += btnIssueNext_Click;
        toolTip.SetToolTip(btnIssueNext, "Next warning or error");

        // Subsystem filter - positioned after the level buttons and issue nav buttons
        cboSubsystem.Anchor        = System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Top;
        cboSubsystem.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
        cboSubsystem.Items.AddRange(new object[] { "All", Subsystem.MainApp, Subsystem.MediaManager, Subsystem.HelperService });
        cboSubsystem.Location      = new System.Drawing.Point(380, 6);
        cboSubsystem.Size          = new System.Drawing.Size(130, 23);
        cboSubsystem.TabIndex      = 6;
        cboSubsystem.SelectedIndex = 0;
        // Wire event after setting SelectedIndex to avoid premature RebuildDisplay
        cboSubsystem.SelectedIndexChanged += cboSubsystem_SelectedIndexChanged;
        toolTip.SetToolTip(cboSubsystem, "Filter entries by subsystem");

        // Log file picker - populated in OnLoad; event wired there after population to avoid premature load
        cboLogFile.Anchor        = System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Top;
        cboLogFile.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
        cboLogFile.Location      = new System.Drawing.Point(514, 6);
        cboLogFile.Size          = new System.Drawing.Size(110, 23);
        cboLogFile.TabIndex      = 7;
        toolTip.SetToolTip(cboLogFile, "Select log file");

        // Search controls - anchored Right so they stay visible when the form is resized
        // Layout from right: [4] [btnNext:26] [btnPrev:26] [4] [lblMatchCount:64] [4] [txtSearch:220] [8]
        // btnClearSearch floats inside the right edge of txtSearch (z-order above it); positioned in OnLoad.
        System.Windows.Forms.AnchorStyles rightAnchor = System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Top;

        // txtSearch - font set explicitly so height is predictable; vertically centered in the 36px toolbar
        txtSearch.Anchor          = rightAnchor;
        txtSearch.BorderStyle     = System.Windows.Forms.BorderStyle.FixedSingle;
        txtSearch.Location        = new System.Drawing.Point(752, 8);
        txtSearch.PlaceholderText = "Search…";
        txtSearch.Width           = 220; // height is auto-sized by font; vertically centered in OnLoad
        txtSearch.TabIndex        = 8;
        txtSearch.TextChanged    += txtSearch_TextChanged;
        txtSearch.KeyDown        += txtSearch_KeyDown;
        toolTip.SetToolTip(txtSearch, "Search the log (highlights matches)");

        // btnClearSearch - overlays the right interior of txtSearch; sized and positioned in OnLoad.
        // Right-margin set to txtSearch.RightMargin - 2 so the button always stays 2px inside the box on resize.
        btnClearSearch.Anchor                    = rightAnchor;
        btnClearSearch.FlatStyle                 = System.Windows.Forms.FlatStyle.Flat;
        btnClearSearch.FlatAppearance.BorderSize = 0;
        btnClearSearch.Font                      = new System.Drawing.Font("Segoe UI", 7F, System.Drawing.FontStyle.Bold);
        btnClearSearch.Location                  = new System.Drawing.Point(950, 5); // fine-tuned in OnLoad
        btnClearSearch.Padding                   = new System.Windows.Forms.Padding(0);
        btnClearSearch.Size                      = new System.Drawing.Size(16, 16);  // fine-tuned in OnLoad
        btnClearSearch.Text                      = "X";
        btnClearSearch.TextAlign                 = System.Drawing.ContentAlignment.MiddleCenter;
        btnClearSearch.TabIndex                  = 9;
        btnClearSearch.TabStop                   = false;
        btnClearSearch.Visible                   = false;
        btnClearSearch.Click                    += btnClearSearch_Click;
        toolTip.SetToolTip(btnClearSearch, "Clear search");

        // btnPrev
        btnPrev.Anchor    = rightAnchor;
        btnPrev.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
        btnPrev.Location  = new System.Drawing.Point(1044, 5);
        btnPrev.Size      = new System.Drawing.Size(26, 26);
        btnPrev.TabIndex  = 10;
        btnPrev.Text      = "▲";
        btnPrev.Click    += btnPrev_Click;
        toolTip.SetToolTip(btnPrev, "Previous match");

        // btnNext
        btnNext.Anchor    = rightAnchor;
        btnNext.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
        btnNext.Location  = new System.Drawing.Point(1070, 5);
        btnNext.Size      = new System.Drawing.Size(26, 26);
        btnNext.TabIndex  = 11;
        btnNext.Text      = "▼";
        btnNext.Click    += btnNext_Click;
        toolTip.SetToolTip(btnNext, "Next match");

        // lblMatchCount
        lblMatchCount.Anchor    = rightAnchor;
        lblMatchCount.AutoSize  = false;
        lblMatchCount.Font      = new System.Drawing.Font("Segoe UI", 8F);
        lblMatchCount.Location  = new System.Drawing.Point(976, 11);
        lblMatchCount.Size      = new System.Drawing.Size(64, 14);
        lblMatchCount.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

        // pnlToolbar - Width must be set explicitly before right-anchored children are added,
        // otherwise the default panel width (~200px) produces a negative right-margin and
        // causes right-anchored controls to fly off-screen when the panel expands to form width.
        pnlToolbar.Controls.AddRange(new System.Windows.Forms.Control[] {
            chkError, chkWarn, chkInfo, chkDebug, cboSubsystem, cboLogFile, btnIssuePrev, btnIssueNext,
            txtSearch, btnClearSearch, btnPrev, btnNext, lblMatchCount });
        pnlToolbar.Dock     = System.Windows.Forms.DockStyle.Top;
        pnlToolbar.Size     = new System.Drawing.Size(1100, 36);
        pnlToolbar.TabIndex = 0;

        // ctxLog - right-click context menu for the log viewer
        ctxCopy.Text      = "Copy";
        ctxCopyAll.Text   = "Copy All";
        ctxSelectAll.Text = "Select All";
        ctxCopy.Click      += ctxCopy_Click;
        ctxCopyAll.Click   += ctxCopyAll_Click;
        ctxSelectAll.Click += ctxSelectAll_Click;
        ctxLog.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { ctxCopy, ctxCopyAll, ctxSelectAll });
        ctxLog.Opening += ctxLog_Opening;
        rtbLog.ContextMenuStrip = ctxLog;

        // rtbLog
        rtbLog.BackColor        = System.Drawing.SystemColors.Control;
        rtbLog.BorderStyle      = System.Windows.Forms.BorderStyle.None;
        rtbLog.Dock             = System.Windows.Forms.DockStyle.Fill;
        rtbLog.Font             = new System.Drawing.Font("Consolas", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
        rtbLog.ReadOnly         = true;
        rtbLog.DetectUrls       = false;
        rtbLog.ScrollBars       = System.Windows.Forms.RichTextBoxScrollBars.Both;
        rtbLog.ShortcutsEnabled = true;
        rtbLog.Size             = new System.Drawing.Size(1100, 524);
        rtbLog.TabIndex         = 1;
        rtbLog.WordWrap         = false;

        // LogViewerForm
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode       = System.Windows.Forms.AutoScaleMode.Font;
        ClientSize          = new System.Drawing.Size(1100, 680);
        Controls.Add(rtbLog);
        Controls.Add(pnlToolbar);
        MaximizeBox         = true;
        MinimizeBox         = true;
        MinimumSize         = new System.Drawing.Size(600, 300);
        Name                = "LogViewerForm";
        Icon                = Properties.Resources.qbPortWeaver;
        ShowIcon            = true;
        ShowInTaskbar       = true;
        StartPosition       = System.Windows.Forms.FormStartPosition.CenterScreen;
        Text                = "qbPortWeaver | Log Viewer"; // overridden in OnLoad with AppIdentity.AppName

        pnlToolbar.ResumeLayout(false);
        ResumeLayout(false);
    }

    private System.Windows.Forms.RichTextBox       rtbLog;
    private System.Windows.Forms.Panel             pnlToolbar;
    private System.Windows.Forms.CheckBox          chkError;
    private System.Windows.Forms.CheckBox          chkWarn;
    private System.Windows.Forms.CheckBox          chkInfo;
    private System.Windows.Forms.CheckBox          chkDebug;
    private System.Windows.Forms.ComboBox          cboSubsystem;
    private System.Windows.Forms.ComboBox          cboLogFile;
    private System.Windows.Forms.Button            btnIssuePrev;
    private System.Windows.Forms.Button            btnIssueNext;
    private PlaceholderTextBox                     txtSearch;
    private System.Windows.Forms.Button            btnClearSearch;
    private System.Windows.Forms.Button            btnPrev;
    private System.Windows.Forms.Button            btnNext;
    private System.Windows.Forms.Label             lblMatchCount;
    private System.Windows.Forms.ContextMenuStrip  ctxLog;
    private System.Windows.Forms.ToolStripMenuItem ctxCopy;
    private System.Windows.Forms.ToolStripMenuItem ctxCopyAll;
    private System.Windows.Forms.ToolStripMenuItem ctxSelectAll;
    private System.Windows.Forms.ToolTip           toolTip;
}

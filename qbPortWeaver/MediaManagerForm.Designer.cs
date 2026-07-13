namespace qbPortWeaver;

partial class MediaManagerForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // CTS cancel/dispose lives in OnFormClosed (like the other forms' in-flight
            // operation sources) - FormClosed always runs before disposal in this app.
            lblTmdbTitle?.Font?.Dispose();
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components       = new System.ComponentModel.Container();
        toolTip          = new ToolTip(components);
        tabMedia         = new TabControl();
        tabPageGeneral   = new TabPage();
        tabPageFolders   = new TabPage();
        grpGeneral       = new GroupBox();
        chkEnabled       = new CheckBox();
        lblTmdbApiKey    = new Label();
        txtTmdbApiKey    = new TextBox();
        chkDryRun        = new CheckBox();
        chkCreateFolders      = new CheckBox();
        chkDeleteEmptyFolders = new CheckBox();
        lblImportMode         = new Label();
        cboImportMode         = new ComboBox();
        grpLibrary                 = new GroupBox();
        lblMoviesLibraryPath       = new Label();
        txtMoviesLibraryPath       = new TextBox();
        btnBrowseMoviesLibrary     = new Button();
        lblTvShowsLibraryPath      = new Label();
        txtTvShowsLibraryPath      = new TextBox();
        btnBrowseTvShowsLibrary    = new Button();
        grpSourceFolders      = new GroupBox();
        lstSourceFolders      = new ListBox();
        btnAddSourceFolder    = new Button();
        btnRemoveSourceFolder = new Button();
        btnScanNow        = new Button();
        btnImportNow      = new Button();
        btnClearCache     = new Button();
        btnRematch        = new Button();
        lblScanStatus       = new Label();
        chkShowOnlyReview   = new CheckBox();
        lblLegendUncertain  = new Label();
        lblLegendUnmatched  = new Label();
        prgScan        = new ProgressBar();
        dgvResults     = new DataGridView();
        colInclude     = new DataGridViewCheckBoxColumn();
        colType        = new DataGridViewTextBoxColumn();
        colCurrent     = new DataGridViewTextBoxColumn();
        colProposed    = new DataGridViewTextBoxColumn();
        pnlTmdbDetail     = new Panel();
        picTmdbPoster     = new PictureBox();
        lblTmdbTitle      = new Label();
        lblTmdbMeta       = new Label();
        lblTmdbConfidence = new Label();
        rtbTmdbOverview   = new RichTextBox();
        btnOK          = new Button();
        btnCancel      = new Button();
        tabMedia.SuspendLayout();
        tabPageGeneral.SuspendLayout();
        tabPageFolders.SuspendLayout();
        grpGeneral.SuspendLayout();
        grpLibrary.SuspendLayout();
        grpSourceFolders.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)dgvResults).BeginInit();
        pnlTmdbDetail.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)picTmdbPoster).BeginInit();
        SuspendLayout();
        // ── grpGeneral ────────────────────────────────────────────────
        grpGeneral.Controls.Add(chkEnabled);
        grpGeneral.Controls.Add(lblTmdbApiKey);
        grpGeneral.Controls.Add(txtTmdbApiKey);
        grpGeneral.Controls.Add(chkDryRun);
        grpGeneral.Controls.Add(chkCreateFolders);
        grpGeneral.Controls.Add(chkDeleteEmptyFolders);
        grpGeneral.Controls.Add(lblImportMode);
        grpGeneral.Controls.Add(cboImportMode);
        // Fixed size (not docked or anchored) - matches SettingsForm's tab groups. A fixed-size group
        // never resizes, so its anchored children (the stretchy path/key fields, right-pinned browse
        // buttons) render at their designer size instead of re-capturing margins against a transient
        // tab-page size, which is what made them overflow / clip when the group was docked or anchored.
        grpGeneral.Location = new Point(4, 4);
        grpGeneral.Name     = "grpGeneral";
        grpGeneral.Size     = new Size(668, 204);
        grpGeneral.TabIndex = 0;
        grpGeneral.TabStop  = false;
        grpGeneral.Text     = "General";
        chkEnabled.AutoSize = true;
        chkEnabled.Location = new Point(15, 24);
        chkEnabled.Name     = "chkEnabled";
        chkEnabled.TabIndex = 0;
        chkEnabled.Text     = "Enable Media Manager";
        lblTmdbApiKey.Location  = new Point(12, 53);
        lblTmdbApiKey.Name      = "lblTmdbApiKey";
        lblTmdbApiKey.Size      = new Size(130, 23);
        lblTmdbApiKey.TabIndex  = 1;
        lblTmdbApiKey.Text      = "TMDB API Key:";
        lblTmdbApiKey.TextAlign = ContentAlignment.MiddleLeft;
        txtTmdbApiKey.Location     = new Point(148, 53);
        txtTmdbApiKey.Name         = "txtTmdbApiKey";
        txtTmdbApiKey.PasswordChar = '*';
        txtTmdbApiKey.Size         = new Size(508, 23);
        txtTmdbApiKey.TabIndex     = 2;
        chkDryRun.AutoSize = true;
        chkDryRun.Location = new Point(15, 82);
        chkDryRun.Name     = "chkDryRun";
        chkDryRun.TabIndex = 3;
        chkDryRun.Text     = "Dry run (preview only - no files will be imported)";
        chkCreateFolders.AutoSize = true;
        chkCreateFolders.Location = new Point(15, 111);
        chkCreateFolders.Name     = "chkCreateFolders";
        chkCreateFolders.TabIndex = 4;
        chkCreateFolders.Text     = "Create Plex folder structure when importing";
        chkDeleteEmptyFolders.AutoSize = true;
        chkDeleteEmptyFolders.Location = new Point(15, 140);
        chkDeleteEmptyFolders.Name     = "chkDeleteEmptyFolders";
        chkDeleteEmptyFolders.TabIndex = 5;
        chkDeleteEmptyFolders.Text     = "Delete empty source folders after importing (folders with only .nfo files are also removed)";
        lblImportMode.Location  = new Point(12, 169);
        lblImportMode.Name      = "lblImportMode";
        lblImportMode.Size      = new Size(130, 23);
        lblImportMode.TabIndex  = 6;
        lblImportMode.Text      = "Import mode:";
        lblImportMode.TextAlign = ContentAlignment.MiddleLeft;
        cboImportMode.DropDownStyle = ComboBoxStyle.DropDownList;
        cboImportMode.Items.AddRange(new object[] { "Hardlink", "Copy", "Move" });
        cboImportMode.Location = new Point(148, 169);
        cboImportMode.Name     = "cboImportMode";
        cboImportMode.Size     = new Size(120, 23);
        cboImportMode.TabIndex = 7;
        // ── grpLibrary ────────────────────────────────────────────────
        grpLibrary.Controls.Add(lblMoviesLibraryPath);
        grpLibrary.Controls.Add(txtMoviesLibraryPath);
        grpLibrary.Controls.Add(btnBrowseMoviesLibrary);
        grpLibrary.Controls.Add(lblTvShowsLibraryPath);
        grpLibrary.Controls.Add(txtTvShowsLibraryPath);
        grpLibrary.Controls.Add(btnBrowseTvShowsLibrary);
        // Fixed size (see grpGeneral) - stacked above grpSourceFolders on the Folders tab.
        grpLibrary.Location = new Point(4, 4);
        grpLibrary.Name     = "grpLibrary";
        grpLibrary.Size     = new Size(668, 88);
        grpLibrary.TabIndex = 1;
        grpLibrary.TabStop  = false;
        grpLibrary.Text     = "Library Folders";
        lblMoviesLibraryPath.Location  = new Point(12, 24);
        lblMoviesLibraryPath.Name      = "lblMoviesLibraryPath";
        lblMoviesLibraryPath.Size      = new Size(130, 23);
        lblMoviesLibraryPath.TabIndex  = 0;
        lblMoviesLibraryPath.Text      = "Movies library:";
        lblMoviesLibraryPath.TextAlign = ContentAlignment.MiddleLeft;
        txtMoviesLibraryPath.Location = new Point(148, 24);
        txtMoviesLibraryPath.Name     = "txtMoviesLibraryPath";
        txtMoviesLibraryPath.Size     = new Size(464, 23);
        txtMoviesLibraryPath.TabIndex = 1;
        btnBrowseMoviesLibrary.Location = new Point(618, 24);
        btnBrowseMoviesLibrary.Name     = "btnBrowseMoviesLibrary";
        btnBrowseMoviesLibrary.Size     = new Size(40, 23);
        btnBrowseMoviesLibrary.TabIndex = 2;
        btnBrowseMoviesLibrary.Text     = "...";
        btnBrowseMoviesLibrary.Click   += btnBrowseMoviesLibrary_Click;
        lblTvShowsLibraryPath.Location  = new Point(12, 53);
        lblTvShowsLibraryPath.Name      = "lblTvShowsLibraryPath";
        lblTvShowsLibraryPath.Size      = new Size(130, 23);
        lblTvShowsLibraryPath.TabIndex  = 3;
        lblTvShowsLibraryPath.Text      = "TV shows library:";
        lblTvShowsLibraryPath.TextAlign = ContentAlignment.MiddleLeft;
        txtTvShowsLibraryPath.Location = new Point(148, 53);
        txtTvShowsLibraryPath.Name     = "txtTvShowsLibraryPath";
        txtTvShowsLibraryPath.Size     = new Size(464, 23);
        txtTvShowsLibraryPath.TabIndex = 4;
        btnBrowseTvShowsLibrary.Location = new Point(618, 53);
        btnBrowseTvShowsLibrary.Name     = "btnBrowseTvShowsLibrary";
        btnBrowseTvShowsLibrary.Size     = new Size(40, 23);
        btnBrowseTvShowsLibrary.TabIndex = 5;
        btnBrowseTvShowsLibrary.Text     = "...";
        btnBrowseTvShowsLibrary.Click   += btnBrowseTvShowsLibrary_Click;
        // ── grpSourceFolders ──────────────────────────────────────────
        grpSourceFolders.Controls.Add(lstSourceFolders);
        grpSourceFolders.Controls.Add(btnAddSourceFolder);
        grpSourceFolders.Controls.Add(btnRemoveSourceFolder);
        // Fixed size (see grpGeneral) - below grpLibrary on the Folders tab.
        grpSourceFolders.Location = new Point(4, 100);
        grpSourceFolders.Name     = "grpSourceFolders";
        grpSourceFolders.Size     = new Size(668, 119);
        grpSourceFolders.TabIndex = 2;
        grpSourceFolders.TabStop  = false;
        grpSourceFolders.Text     = "Source Folders";
        lstSourceFolders.Location = new Point(12, 24);
        lstSourceFolders.Name     = "lstSourceFolders";
        lstSourceFolders.Size     = new Size(644, 56);
        lstSourceFolders.TabIndex = 0;
        btnAddSourceFolder.Location = new Point(12, 84);
        btnAddSourceFolder.Name     = "btnAddSourceFolder";
        btnAddSourceFolder.Size     = new Size(75, 23);
        btnAddSourceFolder.TabIndex = 1;
        btnAddSourceFolder.Text     = "Add...";
        btnAddSourceFolder.Click   += btnAddSourceFolder_Click;
        btnRemoveSourceFolder.Location = new Point(95, 84); // 8px gap after btnAddSourceFolder (ends at x=87)
        btnRemoveSourceFolder.Name     = "btnRemoveSourceFolder";
        btnRemoveSourceFolder.Size     = new Size(75, 23);
        btnRemoveSourceFolder.TabIndex = 2;
        btnRemoveSourceFolder.Text     = "Remove";
        btnRemoveSourceFolder.Click   += btnRemoveSourceFolder_Click;
        // ── tabMedia ──────────────────────────────────────────────────
        // Tabs keep the dialog short enough for small screens and high-DPI scaling (the stacked
        // single-column layout outgrew a 1080p display at 125% scaling) - same pattern as
        // SettingsForm. General holds the feature settings; Folders holds library and source paths.
        tabMedia.Controls.Add(tabPageGeneral);
        tabMedia.Controls.Add(tabPageFolders);
        // Fixed width (Top|Left, not Right) so the fixed-size group boxes inside are never stretched;
        // the form stays Sizable for the results grid/detail panel below, which do stretch. The tab
        // ends flush with the grid at the design width; widening the form grows the grid past it.
        tabMedia.Anchor        = AnchorStyles.Top | AnchorStyles.Left;
        tabMedia.Padding       = new Point(16, 5); // larger native tabs - auto-sized to text, centered and theme-correct in dark mode
        tabMedia.Location      = new Point(8, 8);
        tabMedia.Name          = "tabMedia";
        tabMedia.SelectedIndex = 0;
        tabMedia.Size          = new Size(684, 262);
        tabMedia.TabIndex      = 0;
        // UseVisualStyleBackColor is deliberately left false on both pages: the visual-style
        // page background renders light even in dark mode (see SettingsForm's tabs).
        tabPageGeneral.Controls.Add(grpGeneral);
        tabPageGeneral.Name = "tabPageGeneral";
        tabPageGeneral.Text = "General";
        tabPageFolders.Controls.Add(grpLibrary);
        tabPageFolders.Controls.Add(grpSourceFolders);
        tabPageFolders.Name = "tabPageFolders";
        tabPageFolders.Text = "Folders";
        // ── Action buttons ────────────────────────────────────────────
        btnScanNow.Location = new Point(8, 278);
        btnScanNow.Name     = "btnScanNow";
        btnScanNow.Size     = new Size(90, 28);
        btnScanNow.TabIndex = 3;
        btnScanNow.Text     = "Scan Now";
        btnScanNow.Click   += btnScanNow_Click;
        btnImportNow.Enabled  = false;
        btnImportNow.Location = new Point(106, 278);
        btnImportNow.Name     = "btnImportNow";
        btnImportNow.Size     = new Size(100, 28);
        btnImportNow.TabIndex = 4;
        btnImportNow.Text     = "Import Now";
        btnImportNow.Click   += btnImportNow_Click;
        btnClearCache.Location = new Point(214, 278);
        btnClearCache.Name     = "btnClearCache";
        btnClearCache.Size     = new Size(100, 28);
        btnClearCache.TabIndex = 5;
        btnClearCache.Text     = "Clear Cache";
        btnClearCache.Click   += btnClearCache_Click;
        btnRematch.Location = new Point(322, 278);
        btnRematch.Name     = "btnRematch";
        btnRematch.Size     = new Size(90, 28);
        btnRematch.TabIndex = 6;
        btnRematch.Text     = "Re-match";
        btnRematch.Click   += btnRematch_Click;
        lblScanStatus.Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lblScanStatus.Location  = new Point(420, 278);
        lblScanStatus.Name      = "lblScanStatus";
        lblScanStatus.Size      = new Size(272, 28);
        lblScanStatus.TabIndex  = 7;
        lblScanStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblScanStatus.ForeColor = SystemColors.GrayText;
        // ── Results grid ──────────────────────────────────────────────
        chkShowOnlyReview.Anchor   = AnchorStyles.Bottom | AnchorStyles.Left;
        chkShowOnlyReview.Location = new Point(8, 527);
        chkShowOnlyReview.Name     = "chkShowOnlyReview";
        chkShowOnlyReview.AutoSize = true;
        chkShowOnlyReview.TabIndex = 10;
        chkShowOnlyReview.Text     = "Show only rows with uncertain or no TMDB match";
        chkShowOnlyReview.CheckedChanged += chkShowOnlyReview_CheckedChanged;
        lblLegendUncertain.Anchor    = AnchorStyles.Bottom | AnchorStyles.Right;
        lblLegendUncertain.Location  = new Point(438, 527);
        lblLegendUncertain.Name      = "lblLegendUncertain";
        lblLegendUncertain.Size      = new Size(130, 20);
        lblLegendUncertain.TextAlign = ContentAlignment.MiddleRight;
        lblLegendUncertain.TabIndex  = 11;
        lblLegendUncertain.Text      = "\u25cf Uncertain TMDB";
        lblLegendUnmatched.Anchor    = AnchorStyles.Bottom | AnchorStyles.Right;
        lblLegendUnmatched.Location  = new Point(572, 527);
        lblLegendUnmatched.Name      = "lblLegendUnmatched";
        lblLegendUnmatched.Size      = new Size(120, 20);
        lblLegendUnmatched.TextAlign = ContentAlignment.MiddleRight;
        lblLegendUnmatched.TabIndex  = 12;
        lblLegendUnmatched.Text      = "\u25cf No TMDB match";
        prgScan.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        prgScan.Location = new Point(8, 310);
        prgScan.Name     = "prgScan";
        prgScan.Size     = new Size(684, 16);
        prgScan.TabIndex = 8;
        prgScan.Visible  = false;
        dgvResults.AllowUserToAddRows    = false;
        dgvResults.AllowUserToDeleteRows = false;
        dgvResults.AllowUserToResizeRows = false;
        dgvResults.AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.Fill;
        // Window (input surface), not the Control chrome: an embedded data grid reads as a distinct
        // data box (like StatusForm.lvHistory). Contrast with LogViewerForm.lvLog, which fills the
        // whole window and so uses Control to blend with the app chrome.
        dgvResults.BackgroundColor       = SystemColors.Window;
        dgvResults.BorderStyle           = BorderStyle.Fixed3D;
        dgvResults.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        dgvResults.Columns.AddRange(colInclude, colType, colCurrent, colProposed);
        dgvResults.Anchor       = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        dgvResults.Location     = new Point(8, 328);
        dgvResults.Name         = "dgvResults";
        dgvResults.RowHeadersVisible = false;
        dgvResults.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgvResults.Size         = new Size(684, 196);
        dgvResults.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        dgvResults.TabIndex          = 9;
        dgvResults.TabStop           = false;
        dgvResults.CellFormatting          += dgvResults_CellFormatting;
        dgvResults.CellContentClick        += dgvResults_CellContentClick;
        dgvResults.CellEndEdit             += dgvResults_CellEndEdit;
        dgvResults.ColumnHeaderMouseClick  += dgvResults_ColumnHeaderMouseClick;
        dgvResults.KeyDown                 += dgvResults_KeyDown;
        dgvResults.SelectionChanged        += dgvResults_SelectionChanged;
        colInclude.FillWeight   = 5;
        colInclude.HeaderText   = "";
        colInclude.MinimumWidth = 30;
        colInclude.Name         = "colInclude";
        colInclude.SortMode     = DataGridViewColumnSortMode.NotSortable;
        colType.FillWeight    = 12;
        colType.HeaderText    = "Type";
        colType.MinimumWidth  = 50;
        colType.Name          = "colType";
        colType.ReadOnly      = true;
        colType.SortMode      = DataGridViewColumnSortMode.Automatic;
        colCurrent.FillWeight   = 44;
        colCurrent.HeaderText   = "Current";
        colCurrent.MinimumWidth = 100;
        colCurrent.Name         = "colCurrent";
        colCurrent.ReadOnly     = true;
        colCurrent.SortMode     = DataGridViewColumnSortMode.Automatic;
        colProposed.FillWeight   = 44;
        colProposed.HeaderText   = "Proposed";
        colProposed.MinimumWidth = 100;
        colProposed.Name         = "colProposed";
        colProposed.SortMode     = DataGridViewColumnSortMode.Automatic;
        // ── TMDB detail panel ─────────────────────────────────────────
        pnlTmdbDetail.Controls.Add(picTmdbPoster);
        pnlTmdbDetail.Controls.Add(lblTmdbTitle);
        pnlTmdbDetail.Controls.Add(lblTmdbMeta);
        pnlTmdbDetail.Controls.Add(lblTmdbConfidence);
        pnlTmdbDetail.Controls.Add(rtbTmdbOverview);
        pnlTmdbDetail.Anchor      = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        pnlTmdbDetail.BorderStyle = BorderStyle.FixedSingle;
        pnlTmdbDetail.Location    = new Point(8, 554);
        pnlTmdbDetail.Name        = "pnlTmdbDetail";
        pnlTmdbDetail.Size        = new Size(684, 136);
        pnlTmdbDetail.TabIndex    = 13;
        picTmdbPoster.Location    = new Point(8, 8);
        picTmdbPoster.Name        = "picTmdbPoster";
        picTmdbPoster.Size        = new Size(67, 94);
        picTmdbPoster.SizeMode    = PictureBoxSizeMode.Zoom;
        picTmdbPoster.TabIndex    = 0;
        picTmdbPoster.TabStop     = false;
        picTmdbPoster.Visible     = false;
        lblTmdbTitle.Anchor       = AnchorStyles.Top | AnchorStyles.Left;
        lblTmdbTitle.Font         = new System.Drawing.Font(Font.FontFamily, Font.Size, System.Drawing.FontStyle.Bold);
        lblTmdbTitle.Location     = new Point(84, 10);
        lblTmdbTitle.Name         = "lblTmdbTitle";
        lblTmdbTitle.Size         = new Size(390, 20);
        lblTmdbTitle.TabIndex     = 1;
        lblTmdbMeta.Anchor        = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lblTmdbMeta.Location      = new Point(84, 36);
        lblTmdbMeta.Name          = "lblTmdbMeta";
        lblTmdbMeta.Size          = new Size(590, 18);
        lblTmdbMeta.TabIndex      = 2;
        lblTmdbMeta.ForeColor     = SystemColors.GrayText;
        lblTmdbConfidence.Anchor  = AnchorStyles.Top | AnchorStyles.Right;
        lblTmdbConfidence.Location = new Point(480, 10);
        lblTmdbConfidence.Name    = "lblTmdbConfidence";
        lblTmdbConfidence.Size    = new Size(190, 20);
        lblTmdbConfidence.TabIndex = 3;
        lblTmdbConfidence.TextAlign = ContentAlignment.MiddleRight;
        rtbTmdbOverview.Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        rtbTmdbOverview.BackColor = SystemColors.Control; // blend with the detail panel (read-only display)
        rtbTmdbOverview.BorderStyle = BorderStyle.None;
        rtbTmdbOverview.Location  = new Point(84, 58);
        rtbTmdbOverview.Name      = "rtbTmdbOverview";
        rtbTmdbOverview.ReadOnly  = true;
        rtbTmdbOverview.ScrollBars = RichTextBoxScrollBars.Vertical;
        rtbTmdbOverview.Size      = new Size(590, 70);
        rtbTmdbOverview.TabIndex  = 4;
        rtbTmdbOverview.TabStop   = false;
        // ── Buttons ───────────────────────────────────────────────────
        btnOK.Anchor   = AnchorStyles.Bottom | AnchorStyles.Right;
        btnOK.Location = new Point(520, 697);
        btnOK.Name     = "btnOK";
        btnOK.Size     = new Size(82, 28);
        btnOK.TabIndex = 14;
        btnOK.Text     = "OK";
        btnOK.Click   += btnOK_Click;
        btnCancel.DialogResult = DialogResult.Cancel;
        btnCancel.Anchor       = AnchorStyles.Bottom | AnchorStyles.Right;
        btnCancel.Location     = new Point(610, 697);
        btnCancel.Name         = "btnCancel";
        btnCancel.Size         = new Size(82, 28);
        btnCancel.TabIndex     = 15;
        btnCancel.Text         = "Cancel";
        btnCancel.Click       += btnCancel_Click;
        // ── MediaManagerForm ──────────────────────────────────────────
        AcceptButton        = btnOK;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;
        CancelButton        = btnCancel;
        ClientSize          = new Size(700, 733);
        Controls.Add(tabMedia);
        Controls.Add(btnScanNow);
        Controls.Add(btnImportNow);
        Controls.Add(btnClearCache);
        Controls.Add(btnRematch);
        Controls.Add(lblScanStatus);
        Controls.Add(chkShowOnlyReview);
        Controls.Add(lblLegendUncertain);
        Controls.Add(lblLegendUnmatched);
        Controls.Add(prgScan);
        Controls.Add(dgvResults);
        Controls.Add(pnlTmdbDetail);
        Controls.Add(btnOK);
        Controls.Add(btnCancel);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        MinimizeBox     = true;
        // MinimumSize is set at runtime in OnLoad (locks width, allows a bounded height shrink) so
        // the dialog fits shorter work areas; a static designer value here would be overridden anyway.
        Name            = "MediaManagerForm";
        Icon            = Properties.Resources.qbPortWeaver;
        ShowIcon        = true;
        ShowInTaskbar   = true;
        StartPosition   = FormStartPosition.CenterScreen;
        Text            = "qbPortWeaver | Media Manager";
        grpGeneral.ResumeLayout(false);
        grpGeneral.PerformLayout();
        grpLibrary.ResumeLayout(false);
        grpLibrary.PerformLayout();
        grpSourceFolders.ResumeLayout(false);
        tabPageGeneral.ResumeLayout(false);
        tabPageFolders.ResumeLayout(false);
        tabMedia.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)dgvResults).EndInit();
        pnlTmdbDetail.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)picTmdbPoster).EndInit();
        ResumeLayout(false);
    }

    private TabControl tabMedia;
    private TabPage    tabPageGeneral;
    private TabPage    tabPageFolders;

    private GroupBox grpGeneral;
    private CheckBox chkEnabled;
    private Label    lblTmdbApiKey;
    private TextBox  txtTmdbApiKey;
    private CheckBox chkDryRun;
    private CheckBox chkCreateFolders;
    private CheckBox chkDeleteEmptyFolders;
    private Label    lblImportMode;
    private ComboBox cboImportMode;

    private GroupBox grpLibrary;
    private Label    lblMoviesLibraryPath;
    private TextBox  txtMoviesLibraryPath;
    private Button   btnBrowseMoviesLibrary;
    private Label    lblTvShowsLibraryPath;
    private TextBox  txtTvShowsLibraryPath;
    private Button   btnBrowseTvShowsLibrary;

    private GroupBox grpSourceFolders;
    private ListBox  lstSourceFolders;
    private Button   btnAddSourceFolder;
    private Button   btnRemoveSourceFolder;

    private ProgressBar      prgScan;
    private Button           btnScanNow;
    private Button           btnImportNow;
    private Button           btnClearCache;
    private Button           btnRematch;
    private Label            lblScanStatus;
    private CheckBox         chkShowOnlyReview;
    private Label            lblLegendUncertain;
    private Label            lblLegendUnmatched;
    private DataGridView     dgvResults;
    private DataGridViewCheckBoxColumn colInclude;
    private DataGridViewTextBoxColumn colType;
    private DataGridViewTextBoxColumn colCurrent;
    private DataGridViewTextBoxColumn colProposed;

    private Panel       pnlTmdbDetail;
    private PictureBox  picTmdbPoster;
    private Label       lblTmdbTitle;
    private Label       lblTmdbMeta;
    private Label       lblTmdbConfidence;
    private RichTextBox rtbTmdbOverview;

    private Button btnOK;
    private Button btnCancel;

    private ToolTip toolTip;
}

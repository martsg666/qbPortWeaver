namespace qbPortWeaver
{
    partial class MediaManagerForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _scanCts?.Cancel();
                _scanCts?.Dispose();
                components?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components       = new System.ComponentModel.Container();
            toolTip          = new ToolTip(components);
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
            btnScanNow     = new Button();
            btnImportNow   = new Button();
            btnClearCache  = new Button();
            lblScanStatus  = new Label();
            prgScan        = new ProgressBar();
            dgvResults     = new DataGridView();
            colInclude     = new DataGridViewCheckBoxColumn();
            colType        = new DataGridViewTextBoxColumn();
            colCurrent     = new DataGridViewTextBoxColumn();
            colProposed    = new DataGridViewTextBoxColumn();
            btnOK          = new Button();
            btnCancel      = new Button();
            grpGeneral.SuspendLayout();
            grpLibrary.SuspendLayout();
            grpSourceFolders.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)dgvResults).BeginInit();
            SuspendLayout();
            //
            // grpGeneral
            //
            grpGeneral.Controls.Add(chkEnabled);
            grpGeneral.Controls.Add(lblTmdbApiKey);
            grpGeneral.Controls.Add(txtTmdbApiKey);
            grpGeneral.Controls.Add(chkDryRun);
            grpGeneral.Controls.Add(chkCreateFolders);
            grpGeneral.Controls.Add(chkDeleteEmptyFolders);
            grpGeneral.Controls.Add(lblImportMode);
            grpGeneral.Controls.Add(cboImportMode);
            grpGeneral.Location = new Point(8, 8);
            grpGeneral.Name     = "grpGeneral";
            grpGeneral.Size     = new Size(684, 190);
            grpGeneral.TabIndex = 0;
            grpGeneral.TabStop  = false;
            grpGeneral.Text     = "General";
            //
            // chkEnabled
            //
            chkEnabled.AutoSize = true;
            chkEnabled.Location = new Point(12, 24);
            chkEnabled.Name     = "chkEnabled";
            chkEnabled.TabIndex = 0;
            chkEnabled.Text     = "Enable Media Manager";
            //
            // lblTmdbApiKey
            //
            lblTmdbApiKey.Location  = new Point(12, 53);
            lblTmdbApiKey.Name      = "lblTmdbApiKey";
            lblTmdbApiKey.Size      = new Size(130, 23);
            lblTmdbApiKey.TabIndex  = 1;
            lblTmdbApiKey.Text      = "TMDB API Key:";
            lblTmdbApiKey.TextAlign = ContentAlignment.MiddleLeft;
            //
            // txtTmdbApiKey
            //
            txtTmdbApiKey.Location     = new Point(148, 53);
            txtTmdbApiKey.Name         = "txtTmdbApiKey";
            txtTmdbApiKey.PasswordChar = '*';
            txtTmdbApiKey.Size         = new Size(524, 23);
            txtTmdbApiKey.TabIndex     = 2;
            //
            // chkDryRun
            //
            chkDryRun.AutoSize = true;
            chkDryRun.Location = new Point(12, 85);
            chkDryRun.Name     = "chkDryRun";
            chkDryRun.TabIndex = 3;
            chkDryRun.Text     = "Dry run (preview only - no files will be imported)";
            //
            // chkCreateFolders
            //
            chkCreateFolders.AutoSize = true;
            chkCreateFolders.Location = new Point(12, 110);
            chkCreateFolders.Name     = "chkCreateFolders";
            chkCreateFolders.TabIndex = 4;
            chkCreateFolders.Text     = "Create Plex folder structure when importing";
            //
            // chkDeleteEmptyFolders
            //
            chkDeleteEmptyFolders.AutoSize = true;
            chkDeleteEmptyFolders.Location = new Point(12, 135);
            chkDeleteEmptyFolders.Name     = "chkDeleteEmptyFolders";
            chkDeleteEmptyFolders.TabIndex = 5;
            chkDeleteEmptyFolders.Text     = "Delete empty source folders after importing (folders with only .nfo files are also removed)";
            //
            // lblImportMode
            //
            lblImportMode.Location  = new Point(12, 162);
            lblImportMode.Name      = "lblImportMode";
            lblImportMode.Size      = new Size(130, 23);
            lblImportMode.TabIndex  = 6;
            lblImportMode.Text      = "Import mode:";
            lblImportMode.TextAlign = ContentAlignment.MiddleLeft;
            //
            // cboImportMode
            //
            cboImportMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cboImportMode.Items.AddRange(new object[] { "Hardlink", "Copy", "Move" });
            cboImportMode.Location = new Point(148, 162);
            cboImportMode.Name     = "cboImportMode";
            cboImportMode.Size     = new Size(120, 23);
            cboImportMode.TabIndex = 7;
            //
            // grpLibrary
            //
            grpLibrary.Controls.Add(lblMoviesLibraryPath);
            grpLibrary.Controls.Add(txtMoviesLibraryPath);
            grpLibrary.Controls.Add(btnBrowseMoviesLibrary);
            grpLibrary.Controls.Add(lblTvShowsLibraryPath);
            grpLibrary.Controls.Add(txtTvShowsLibraryPath);
            grpLibrary.Controls.Add(btnBrowseTvShowsLibrary);
            grpLibrary.Location = new Point(8, 206);
            grpLibrary.Name     = "grpLibrary";
            grpLibrary.Size     = new Size(684, 82);
            grpLibrary.TabIndex = 1;
            grpLibrary.TabStop  = false;
            grpLibrary.Text     = "Library Folders";
            //
            // lblMoviesLibraryPath
            //
            lblMoviesLibraryPath.Location  = new Point(12, 24);
            lblMoviesLibraryPath.Name      = "lblMoviesLibraryPath";
            lblMoviesLibraryPath.Size      = new Size(130, 23);
            lblMoviesLibraryPath.TabIndex  = 0;
            lblMoviesLibraryPath.Text      = "Movies library:";
            lblMoviesLibraryPath.TextAlign = ContentAlignment.MiddleLeft;
            //
            // txtMoviesLibraryPath
            //
            txtMoviesLibraryPath.Location = new Point(148, 24);
            txtMoviesLibraryPath.Name     = "txtMoviesLibraryPath";
            txtMoviesLibraryPath.Size     = new Size(480, 23);
            txtMoviesLibraryPath.TabIndex = 1;
            //
            // btnBrowseMoviesLibrary
            //
            btnBrowseMoviesLibrary.Location = new Point(634, 24);
            btnBrowseMoviesLibrary.Name     = "btnBrowseMoviesLibrary";
            btnBrowseMoviesLibrary.Size     = new Size(40, 23);
            btnBrowseMoviesLibrary.TabIndex = 2;
            btnBrowseMoviesLibrary.Text     = "...";
            btnBrowseMoviesLibrary.Click   += btnBrowseMoviesLibrary_Click;
            //
            // lblTvShowsLibraryPath
            //
            lblTvShowsLibraryPath.Location  = new Point(12, 53);
            lblTvShowsLibraryPath.Name      = "lblTvShowsLibraryPath";
            lblTvShowsLibraryPath.Size      = new Size(130, 23);
            lblTvShowsLibraryPath.TabIndex  = 3;
            lblTvShowsLibraryPath.Text      = "TV shows library:";
            lblTvShowsLibraryPath.TextAlign = ContentAlignment.MiddleLeft;
            //
            // txtTvShowsLibraryPath
            //
            txtTvShowsLibraryPath.Location = new Point(148, 53);
            txtTvShowsLibraryPath.Name     = "txtTvShowsLibraryPath";
            txtTvShowsLibraryPath.Size     = new Size(480, 23);
            txtTvShowsLibraryPath.TabIndex = 4;
            //
            // btnBrowseTvShowsLibrary
            //
            btnBrowseTvShowsLibrary.Location = new Point(634, 53);
            btnBrowseTvShowsLibrary.Name     = "btnBrowseTvShowsLibrary";
            btnBrowseTvShowsLibrary.Size     = new Size(40, 23);
            btnBrowseTvShowsLibrary.TabIndex = 5;
            btnBrowseTvShowsLibrary.Text     = "...";
            btnBrowseTvShowsLibrary.Click   += btnBrowseTvShowsLibrary_Click;
            //
            // grpSourceFolders
            //
            grpSourceFolders.Controls.Add(lstSourceFolders);
            grpSourceFolders.Controls.Add(btnAddSourceFolder);
            grpSourceFolders.Controls.Add(btnRemoveSourceFolder);
            grpSourceFolders.Location = new Point(8, 296);
            grpSourceFolders.Name     = "grpSourceFolders";
            grpSourceFolders.Size     = new Size(684, 110);
            grpSourceFolders.TabIndex = 2;
            grpSourceFolders.TabStop  = false;
            grpSourceFolders.Text     = "Source Folders";
            //
            // lstSourceFolders
            //
            lstSourceFolders.Location = new Point(12, 24);
            lstSourceFolders.Name     = "lstSourceFolders";
            lstSourceFolders.Size     = new Size(660, 60);
            lstSourceFolders.TabIndex = 0;
            //
            // btnAddSourceFolder
            //
            btnAddSourceFolder.Location = new Point(12, 80);
            btnAddSourceFolder.Name     = "btnAddSourceFolder";
            btnAddSourceFolder.Size     = new Size(75, 23);
            btnAddSourceFolder.TabIndex = 1;
            btnAddSourceFolder.Text     = "Add...";
            btnAddSourceFolder.Click   += btnAddSourceFolder_Click;
            //
            // btnRemoveSourceFolder
            //
            btnRemoveSourceFolder.Location = new Point(92, 80);
            btnRemoveSourceFolder.Name     = "btnRemoveSourceFolder";
            btnRemoveSourceFolder.Size     = new Size(75, 23);
            btnRemoveSourceFolder.TabIndex = 2;
            btnRemoveSourceFolder.Text     = "Remove";
            btnRemoveSourceFolder.Click   += btnRemoveSourceFolder_Click;
            //
            // btnScanNow
            //
            btnScanNow.Location = new Point(8, 414);
            btnScanNow.Name     = "btnScanNow";
            btnScanNow.Size     = new Size(90, 28);
            btnScanNow.TabIndex = 3;
            btnScanNow.Text     = "Scan Now";
            btnScanNow.Click   += btnScanNow_Click;
            //
            // btnImportNow
            //
            btnImportNow.Enabled  = false;
            btnImportNow.Location = new Point(106, 414);
            btnImportNow.Name     = "btnImportNow";
            btnImportNow.Size     = new Size(100, 28);
            btnImportNow.TabIndex = 4;
            btnImportNow.Text     = "Import Now";
            btnImportNow.Click   += btnImportNow_Click;
            //
            // btnClearCache
            //
            btnClearCache.Location = new Point(214, 414);
            btnClearCache.Name     = "btnClearCache";
            btnClearCache.Size     = new Size(100, 28);
            btnClearCache.TabIndex = 5;
            btnClearCache.Text     = "Clear Cache";
            btnClearCache.Click   += btnClearCache_Click;
            //
            // lblScanStatus
            //
            lblScanStatus.Location  = new Point(322, 414);
            lblScanStatus.Name      = "lblScanStatus";
            lblScanStatus.Size      = new Size(370, 28);
            lblScanStatus.TabIndex  = 6;
            lblScanStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblScanStatus.ForeColor = SystemColors.GrayText;
            //
            // prgScan
            //
            prgScan.Location = new Point(8, 442);
            prgScan.Name     = "prgScan";
            prgScan.Size     = new Size(684, 16);
            prgScan.TabIndex = 7;
            prgScan.Visible  = false;
            //
            // dgvResults
            //
            dgvResults.AllowUserToAddRows    = false;
            dgvResults.AllowUserToDeleteRows = false;
            dgvResults.AllowUserToResizeRows = false;
            dgvResults.AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.Fill;
            dgvResults.BackgroundColor       = SystemColors.Window;
            dgvResults.BorderStyle           = BorderStyle.Fixed3D;
            dgvResults.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dgvResults.Columns.AddRange(colInclude, colType, colCurrent, colProposed);
            dgvResults.Location     = new Point(8, 460);
            dgvResults.Name         = "dgvResults";
            dgvResults.RowHeadersVisible = false;
            dgvResults.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvResults.Size         = new Size(684, 170);
            dgvResults.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            dgvResults.TabIndex          = 8;
            dgvResults.TabStop           = false;
            dgvResults.CellFormatting          += dgvResults_CellFormatting;
            dgvResults.CellContentClick        += dgvResults_CellContentClick;
            dgvResults.ColumnHeaderMouseClick  += dgvResults_ColumnHeaderMouseClick;
            dgvResults.KeyDown                 += dgvResults_KeyDown;
            //
            // colInclude
            //
            colInclude.FillWeight   = 5;
            colInclude.HeaderText   = "";
            colInclude.MinimumWidth = 30;
            colInclude.Name         = "colInclude";
            colInclude.SortMode     = DataGridViewColumnSortMode.NotSortable;
            //
            // colType
            //
            colType.FillWeight    = 12;
            colType.HeaderText    = "Type";
            colType.MinimumWidth  = 50;
            colType.Name          = "colType";
            colType.ReadOnly      = true;
            colType.SortMode      = DataGridViewColumnSortMode.Automatic;
            //
            // colCurrent
            //
            colCurrent.FillWeight   = 44;
            colCurrent.HeaderText   = "Current";
            colCurrent.MinimumWidth = 100;
            colCurrent.Name         = "colCurrent";
            colCurrent.ReadOnly     = true;
            colCurrent.SortMode     = DataGridViewColumnSortMode.Automatic;
            //
            // colProposed
            //
            colProposed.FillWeight   = 44;
            colProposed.HeaderText   = "Proposed";
            colProposed.MinimumWidth = 100;
            colProposed.Name         = "colProposed";
            colProposed.SortMode     = DataGridViewColumnSortMode.Automatic;
            //
            // btnOK
            //
            btnOK.Location = new Point(520, 638);
            btnOK.Name     = "btnOK";
            btnOK.Size     = new Size(82, 28);
            btnOK.TabIndex = 9;
            btnOK.Text     = "OK";
            btnOK.Click   += btnOK_Click;
            //
            // btnCancel
            //
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location     = new Point(610, 638);
            btnCancel.Name         = "btnCancel";
            btnCancel.Size         = new Size(82, 28);
            btnCancel.TabIndex     = 10;
            btnCancel.Text         = "Cancel";
            btnCancel.Click       += btnCancel_Click;
            //
            // MediaManagerForm
            //
            AcceptButton        = btnOK;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode       = AutoScaleMode.Font;
            CancelButton        = btnCancel;
            ClientSize          = new Size(700, 680);
            Controls.Add(grpGeneral);
            Controls.Add(grpLibrary);
            Controls.Add(grpSourceFolders);
            Controls.Add(btnScanNow);
            Controls.Add(btnImportNow);
            Controls.Add(btnClearCache);
            Controls.Add(lblScanStatus);
            Controls.Add(prgScan);
            Controls.Add(dgvResults);
            Controls.Add(btnOK);
            Controls.Add(btnCancel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            Name            = "MediaManagerForm";
            ShowIcon        = false;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Text            = "qbPortWeaver | Media Manager";
            grpGeneral.ResumeLayout(false);
            grpGeneral.PerformLayout();
            grpLibrary.ResumeLayout(false);
            grpLibrary.PerformLayout();
            grpSourceFolders.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)dgvResults).EndInit();
            ResumeLayout(false);
        }

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
        private Label            lblScanStatus;
        private DataGridView     dgvResults;
        private DataGridViewCheckBoxColumn colInclude;
        private DataGridViewTextBoxColumn colType;
        private DataGridViewTextBoxColumn colCurrent;
        private DataGridViewTextBoxColumn colProposed;

        private Button btnOK;
        private Button btnCancel;

        private ToolTip toolTip;
    }
}

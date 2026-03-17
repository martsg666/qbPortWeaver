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

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            components       = new System.ComponentModel.Container();
            toolTip          = new ToolTip(components);
            grpGeneral       = new GroupBox();
            chkEnabled       = new CheckBox();
            lblTmdbApiKey    = new Label();
            txtTmdbApiKey    = new TextBox();
            chkDryRun        = new CheckBox();
            chkCreateFolders = new CheckBox();
            tabFolders       = new TabControl();
            tabMovies        = new TabPage();
            lstMovieFolders      = new ListBox();
            btnAddMovieFolder    = new Button();
            btnRemoveMovieFolder = new Button();
            tabTvShows       = new TabPage();
            lstTvShowFolders      = new ListBox();
            btnAddTvShowFolder    = new Button();
            btnRemoveTvShowFolder = new Button();
            btnScanNow     = new Button();
            btnRenameNow   = new Button();
            lblScanStatus  = new Label();
            dgvResults     = new DataGridView();
            colType        = new DataGridViewTextBoxColumn();
            colCurrent     = new DataGridViewTextBoxColumn();
            colProposed    = new DataGridViewTextBoxColumn();
            btnOK          = new Button();
            btnCancel      = new Button();
            grpGeneral.SuspendLayout();
            tabFolders.SuspendLayout();
            tabMovies.SuspendLayout();
            tabTvShows.SuspendLayout();
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
            grpGeneral.Location = new Point(8, 8);
            grpGeneral.Name     = "grpGeneral";
            grpGeneral.Size     = new Size(684, 136);
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
            txtTmdbApiKey.Location = new Point(148, 53);
            txtTmdbApiKey.Name     = "txtTmdbApiKey";
            txtTmdbApiKey.Size     = new Size(524, 23);
            txtTmdbApiKey.TabIndex = 2;
            //
            // chkDryRun
            //
            chkDryRun.AutoSize = true;
            chkDryRun.Location = new Point(12, 85);
            chkDryRun.Name     = "chkDryRun";
            chkDryRun.TabIndex = 3;
            chkDryRun.Text     = "Dry run (preview only - no files will be renamed)";
            //
            // chkCreateFolders
            //
            chkCreateFolders.AutoSize = true;
            chkCreateFolders.Location = new Point(12, 110);
            chkCreateFolders.Name     = "chkCreateFolders";
            chkCreateFolders.TabIndex = 4;
            chkCreateFolders.Text     = "Create Plex folder structure when renaming";
            //
            // tabFolders
            //
            tabFolders.Controls.Add(tabMovies);
            tabFolders.Controls.Add(tabTvShows);
            tabFolders.Location      = new Point(8, 152);
            tabFolders.Name          = "tabFolders";
            tabFolders.SelectedIndex = 0;
            tabFolders.Size          = new Size(684, 152);
            tabFolders.TabIndex      = 1;
            //
            // tabMovies
            //
            tabMovies.Controls.Add(lstMovieFolders);
            tabMovies.Controls.Add(btnAddMovieFolder);
            tabMovies.Controls.Add(btnRemoveMovieFolder);
            tabMovies.Location            = new Point(4, 24);
            tabMovies.Name                = "tabMovies";
            tabMovies.Padding             = new Padding(3);
            tabMovies.Size                = new Size(676, 124);
            tabMovies.TabIndex            = 0;
            tabMovies.Text                = "Movies";
            tabMovies.UseVisualStyleBackColor = true;
            //
            // lstMovieFolders
            //
            lstMovieFolders.Location = new Point(8, 8);
            lstMovieFolders.Name     = "lstMovieFolders";
            lstMovieFolders.Size     = new Size(660, 82);
            lstMovieFolders.TabIndex = 0;
            //
            // btnAddMovieFolder
            //
            btnAddMovieFolder.Location = new Point(8, 96);
            btnAddMovieFolder.Name     = "btnAddMovieFolder";
            btnAddMovieFolder.Size     = new Size(75, 23);
            btnAddMovieFolder.TabIndex = 1;
            btnAddMovieFolder.Text     = "Add...";
            btnAddMovieFolder.Click   += btnAddMovieFolder_Click;
            //
            // btnRemoveMovieFolder
            //
            btnRemoveMovieFolder.Location = new Point(88, 96);
            btnRemoveMovieFolder.Name     = "btnRemoveMovieFolder";
            btnRemoveMovieFolder.Size     = new Size(75, 23);
            btnRemoveMovieFolder.TabIndex = 2;
            btnRemoveMovieFolder.Text     = "Remove";
            btnRemoveMovieFolder.Click   += btnRemoveMovieFolder_Click;
            //
            // tabTvShows
            //
            tabTvShows.Controls.Add(lstTvShowFolders);
            tabTvShows.Controls.Add(btnAddTvShowFolder);
            tabTvShows.Controls.Add(btnRemoveTvShowFolder);
            tabTvShows.Location            = new Point(4, 24);
            tabTvShows.Name                = "tabTvShows";
            tabTvShows.Padding             = new Padding(3);
            tabTvShows.Size                = new Size(676, 124);
            tabTvShows.TabIndex            = 1;
            tabTvShows.Text                = "TV Shows";
            tabTvShows.UseVisualStyleBackColor = true;
            //
            // lstTvShowFolders
            //
            lstTvShowFolders.Location = new Point(8, 8);
            lstTvShowFolders.Name     = "lstTvShowFolders";
            lstTvShowFolders.Size     = new Size(660, 82);
            lstTvShowFolders.TabIndex = 0;
            //
            // btnAddTvShowFolder
            //
            btnAddTvShowFolder.Location = new Point(8, 96);
            btnAddTvShowFolder.Name     = "btnAddTvShowFolder";
            btnAddTvShowFolder.Size     = new Size(75, 23);
            btnAddTvShowFolder.TabIndex = 1;
            btnAddTvShowFolder.Text     = "Add...";
            btnAddTvShowFolder.Click   += btnAddTvShowFolder_Click;
            //
            // btnRemoveTvShowFolder
            //
            btnRemoveTvShowFolder.Location = new Point(88, 96);
            btnRemoveTvShowFolder.Name     = "btnRemoveTvShowFolder";
            btnRemoveTvShowFolder.Size     = new Size(75, 23);
            btnRemoveTvShowFolder.TabIndex = 2;
            btnRemoveTvShowFolder.Text     = "Remove";
            btnRemoveTvShowFolder.Click   += btnRemoveTvShowFolder_Click;
            //
            // btnScanNow
            //
            btnScanNow.Location = new Point(8, 312);
            btnScanNow.Name     = "btnScanNow";
            btnScanNow.Size     = new Size(90, 28);
            btnScanNow.TabIndex = 2;
            btnScanNow.Text     = "Scan Now";
            btnScanNow.Click   += btnScanNow_Click;
            //
            // btnRenameNow
            //
            btnRenameNow.Enabled  = false;
            btnRenameNow.Location = new Point(106, 312);
            btnRenameNow.Name     = "btnRenameNow";
            btnRenameNow.Size     = new Size(100, 28);
            btnRenameNow.TabIndex = 3;
            btnRenameNow.Text     = "Rename Now";
            btnRenameNow.Click   += btnRenameNow_Click;
            //
            // lblScanStatus
            //
            lblScanStatus.Location  = new Point(214, 312);
            lblScanStatus.Name      = "lblScanStatus";
            lblScanStatus.Size      = new Size(478, 28);
            lblScanStatus.TabIndex  = 4;
            lblScanStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblScanStatus.ForeColor = SystemColors.GrayText;
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
            dgvResults.Columns.AddRange(colType, colCurrent, colProposed);
            dgvResults.Location     = new Point(8, 348);
            dgvResults.Name         = "dgvResults";
            dgvResults.RowHeadersVisible = false;
            dgvResults.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvResults.Size         = new Size(684, 420);
            dgvResults.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            dgvResults.TabIndex          = 5;
            dgvResults.TabStop           = false;
            dgvResults.CellFormatting   += dgvResults_CellFormatting;
            dgvResults.KeyDown          += dgvResults_KeyDown;
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
            btnOK.Location = new Point(510, 780);
            btnOK.Name     = "btnOK";
            btnOK.Size     = new Size(82, 28);
            btnOK.TabIndex = 6;
            btnOK.Text     = "OK";
            btnOK.Click   += btnOK_Click;
            //
            // btnCancel
            //
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location     = new Point(602, 780);
            btnCancel.Name         = "btnCancel";
            btnCancel.Size         = new Size(82, 28);
            btnCancel.TabIndex     = 7;
            btnCancel.Text         = "Cancel";
            btnCancel.Click       += btnCancel_Click;
            //
            // MediaManagerForm
            //
            AcceptButton        = btnOK;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode       = AutoScaleMode.Font;
            CancelButton        = btnCancel;
            ClientSize          = new Size(700, 820);
            Controls.Add(grpGeneral);
            Controls.Add(tabFolders);
            Controls.Add(btnScanNow);
            Controls.Add(btnRenameNow);
            Controls.Add(lblScanStatus);
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
            tabFolders.ResumeLayout(false);
            tabMovies.ResumeLayout(false);
            tabTvShows.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)dgvResults).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private GroupBox grpGeneral;
        private CheckBox chkEnabled;
        private Label    lblTmdbApiKey;
        private TextBox  txtTmdbApiKey;
        private CheckBox chkDryRun;
        private CheckBox chkCreateFolders;

        private TabControl tabFolders;

        private TabPage  tabMovies;
        private ListBox  lstMovieFolders;
        private Button   btnAddMovieFolder;
        private Button   btnRemoveMovieFolder;

        private TabPage  tabTvShows;
        private ListBox  lstTvShowFolders;
        private Button   btnAddTvShowFolder;
        private Button   btnRemoveTvShowFolder;

        private Button           btnScanNow;
        private Button           btnRenameNow;
        private Label            lblScanStatus;
        private DataGridView     dgvResults;
        private DataGridViewTextBoxColumn colType;
        private DataGridViewTextBoxColumn colCurrent;
        private DataGridViewTextBoxColumn colProposed;

        private Button btnOK;
        private Button btnCancel;

        private ToolTip toolTip;
    }
}

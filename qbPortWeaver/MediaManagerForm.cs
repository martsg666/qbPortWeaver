namespace qbPortWeaver
{
    /// <summary>Dialog for configuring the Media Manager feature, previewing proposed renames (Scan Now), and applying them (Rename Now).</summary>
    public partial class MediaManagerForm : Form
    {
        private enum RowConfidence { Confident, Uncertain, Unmatched }
        private sealed record RowData(RowConfidence Confidence, RenameProposal Proposal);

        private CancellationTokenSource? _scanCts;
        private ToolStripMenuItem? _mnuPaste;

        public MediaManagerForm()
        {
            InitializeComponent();
            Text = $"{AppConstants.AppName} | Media Manager";
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SetupTooltips();
            SetupGridContextMenu();
            LoadSettings();
        }

        private void SetupTooltips()
        {
            toolTip.SetToolTip(chkEnabled,            "Enable or disable Media Manager - when enabled, files are processed on each sync cycle");
            toolTip.SetToolTip(txtTmdbApiKey,         "Your TMDB API key - get one free at themoviedb.org/settings/api");
            toolTip.SetToolTip(lblTmdbApiKey,         "Your TMDB API key - get one free at themoviedb.org/settings/api");
            toolTip.SetToolTip(chkDryRun,             "When checked, the automatic sync cycle will only log what it would rename without touching any files");
            toolTip.SetToolTip(chkCreateFolders,      "Move each title into its own Plex-recommended folder: Movies/Title (Year)/Title (Year).ext");
            toolTip.SetToolTip(chkDeleteEmptyFolders, "Delete folders left empty after renaming - folders containing only .nfo files are also removed");
            toolTip.SetToolTip(lstMovieFolders,       "Folders scanned for movie files on each cycle");
            toolTip.SetToolTip(btnAddMovieFolder,     "Add a folder to scan for movies");
            toolTip.SetToolTip(btnRemoveMovieFolder,  "Remove the selected folder from the list");
            toolTip.SetToolTip(lstTvShowFolders,      "Folders scanned for TV episode files on each cycle");
            toolTip.SetToolTip(btnAddTvShowFolder,    "Add a folder to scan for TV shows");
            toolTip.SetToolTip(btnRemoveTvShowFolder, "Remove the selected folder from the list");
            toolTip.SetToolTip(btnScanNow,            "Preview which files would be renamed - no files are touched");
            toolTip.SetToolTip(btnRenameNow,          "Apply the renames shown in the grid - files will be moved or renamed immediately");
            toolTip.SetToolTip(dgvResults,            "Files that would be renamed. Uncheck a row to exclude it from renaming. Rows in red are uncertain TMDB matches - double-click the Proposed cell to correct the name before renaming.");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = null; // prevent double-dispose in Dispose(bool)
            base.OnFormClosed(e);
        }

        private void LoadSettings()
        {
            chkEnabled.Checked       = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled);
            txtTmdbApiKey.Text       = RegistrySettingsManager.GetEncryptedValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyTmdbApiKey);
            chkDryRun.Checked        = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
            chkCreateFolders.Checked      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);
            chkDeleteEmptyFolders.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDeleteEmptyFolders);

            LoadFolderList(lstMovieFolders,  RegistrySettingsManager.KeyMediaMovieFolders);
            LoadFolderList(lstTvShowFolders, RegistrySettingsManager.KeyMediaTvShowFolders);
        }

        private void btnOK_Click(object? sender, EventArgs e)
        {
            SaveSettings();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void btnCancel_Click(object? sender, EventArgs e) => Close();

        private void SaveSettings()
        {
            RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia,  RegistrySettingsManager.KeyMediaEnabled,      chkEnabled.Checked);
            RegistrySettingsManager.SetEncryptedValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyTmdbApiKey, txtTmdbApiKey.Text.Trim());
            RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia,  RegistrySettingsManager.KeyMediaDryRun,        chkDryRun.Checked);
            RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia,  RegistrySettingsManager.KeyMediaCreateFolders,      chkCreateFolders.Checked);
            RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia,  RegistrySettingsManager.KeyMediaDeleteEmptyFolders, chkDeleteEmptyFolders.Checked);

            SaveFolderList(lstMovieFolders,  RegistrySettingsManager.KeyMediaMovieFolders);
            SaveFolderList(lstTvShowFolders, RegistrySettingsManager.KeyMediaTvShowFolders);
        }

        private void btnAddMovieFolder_Click(object? sender, EventArgs e)     => AddFolder(lstMovieFolders);
        private void btnRemoveMovieFolder_Click(object? sender, EventArgs e)  => RemoveSelectedFolder(lstMovieFolders);
        private void btnAddTvShowFolder_Click(object? sender, EventArgs e)    => AddFolder(lstTvShowFolders);
        private void btnRemoveTvShowFolder_Click(object? sender, EventArgs e) => RemoveSelectedFolder(lstTvShowFolders);

        // Scan Now - previews renames using current (unsaved) form values, never touches files
        private async void btnScanNow_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
        {
            var apiKey = txtTmdbApiKey.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                lblScanStatus.Text = "TMDB API key required.";
                return;
            }

            var movieFolders  = lstMovieFolders.Items.Cast<string>().ToArray();
            var tvShowFolders = lstTvShowFolders.Items.Cast<string>().ToArray();

            if (movieFolders.Length == 0 && tvShowFolders.Length == 0)
            {
                lblScanStatus.Text = "No folders configured.";
                return;
            }

            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            SetBusy(true);
            lblScanStatus.Text = "Scanning\u2026";
            dgvResults.Rows.Clear();

            try
            {
                bool createFolders = chkCreateFolders.Checked;
                var proposals = await MediaManagerService.ScanAsync(apiKey, createFolders, movieFolders, tvShowFolders, ct);

                PopulateGrid(proposals);

                int unmatched     = proposals.Count(p => !p.IsMatched);
                int toRename      = proposals.Count - unmatched;
                string renameStr  = $"{toRename} file{(toRename == 1 ? "" : "s")}";
                string unmatchedStr = $"{unmatched} file{(unmatched == 1 ? "" : "s")}";
                lblScanStatus.Text = (toRename, unmatched) switch
                {
                    (0, 0) => "All files already correctly named.",
                    (0, _) => $"{unmatchedStr} had no TMDB match - enter proposed names manually.",
                    (_, 0) => $"{renameStr} would be renamed.",
                    _      => $"{renameStr} would be renamed, {unmatchedStr} had no TMDB match."
                };

                btnRenameNow.Enabled = proposals.Count > 0;
            }
            catch (OperationCanceledException)
            {
                lblScanStatus.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        // Rename Now - applies proposals from the grid, respecting any user edits to the Proposed column
        private async void btnRenameNow_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
        {
            // Count only checked rows with a proposed name (unchecked rows are excluded from renaming)
            int proposalCount = dgvResults.Rows.Cast<DataGridViewRow>()
                .Count(r => r.Cells[colInclude.Index].Value is true
                            && !string.IsNullOrWhiteSpace(r.Cells[colProposed.Index].Value?.ToString()));

            var confirm = MessageBox.Show(
                $"{proposalCount} file{(proposalCount == 1 ? "" : "s")} will be renamed. This cannot be undone.\n\nContinue?",
                "qbPortWeaver | Media Manager",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            SetBusy(true);
            btnRenameNow.Enabled = false;
            lblScanStatus.Text   = "Renaming\u2026";

            try
            {
                var toApply = BuildProposalsFromGrid();

                await MediaManagerService.ApplyProposalsAsync(toApply, ct);

                // Re-scan to reflect the new state
                var apiKey         = txtTmdbApiKey.Text.Trim();
                bool createFolders = chkCreateFolders.Checked;
                var movieFolders   = lstMovieFolders.Items.Cast<string>().ToArray();
                var tvShowFolders  = lstTvShowFolders.Items.Cast<string>().ToArray();

                if (chkDeleteEmptyFolders.Checked)
                {
                    foreach (var folder in movieFolders)
                        MediaManagerService.CleanupEmptyFolders(folder, dryRun: false);
                    foreach (var folder in tvShowFolders)
                        MediaManagerService.CleanupEmptyFolders(folder, dryRun: false);
                }

                var remaining = await MediaManagerService.ScanAsync(apiKey, createFolders, movieFolders, tvShowFolders, ct);
                PopulateGrid(remaining);

                string remainingLabel = $"{remaining.Count} file{(remaining.Count == 1 ? "" : "s")}";
                lblScanStatus.Text   = remaining.Count == 0
                    ? "Done - all files renamed successfully."
                    : $"Done - {remainingLabel} could not be renamed.";
                btnRenameNow.Enabled = remaining.Count > 0;
            }
            catch (OperationCanceledException)
            {
                lblScanStatus.Text = "Rename cancelled.";
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        // Builds updated proposals from the grid, honouring any edits the user made to the Proposed column.
        // Each row carries its own RenameProposal in Tag so row order is irrelevant.
        private List<RenameProposal> BuildProposalsFromGrid()
        {
            var toApply = new List<RenameProposal>();
            foreach (DataGridViewRow row in dgvResults.Rows)
            {
                if (row.Tag is not RowData { Proposal: var original }) continue;
                if (row.Cells[colInclude.Index].Value is not true) continue; // user excluded this row

                var editedName = row.Cells[colProposed.Index].Value?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(editedName)) continue; // unmatched row with no user-supplied name

                // Always use the original file's directory unless createFolders is still checked -
                // the user may have toggled the checkbox between Scan and Rename Now.
                var proposedDir  = chkCreateFolders.Checked && !string.IsNullOrEmpty(original.ProposedPath)
                                   ? Path.GetDirectoryName(original.ProposedPath) ?? string.Empty
                                   : Path.GetDirectoryName(original.OriginalPath) ?? string.Empty;
                var proposedPath = Path.Combine(proposedDir, editedName);

                if (!string.Equals(original.OriginalPath, proposedPath, StringComparison.OrdinalIgnoreCase))
                    toApply.Add(original with { ProposedPath = proposedPath, IsMatched = true });
            }
            return toApply;
        }

        private void SetupGridContextMenu()
        {
            var mnuCopy      = new ToolStripMenuItem("Copy");
            _mnuPaste        = new ToolStripMenuItem("Paste");
            var mnuSelectAll = new ToolStripMenuItem("Select All");

            mnuCopy.ShortcutKeyDisplayString       = "Ctrl+C";
            _mnuPaste.ShortcutKeyDisplayString     = "Ctrl+V";
            mnuSelectAll.ShortcutKeyDisplayString  = "Ctrl+A";

            mnuCopy.Click      += gridContextCopy_Click;
            _mnuPaste.Click    += gridContextPaste_Click;
            mnuSelectAll.Click += gridContextSelectAll_Click;

            var menu = new ContextMenuStrip(components);
            menu.Items.AddRange([mnuCopy, _mnuPaste, new ToolStripSeparator(), mnuSelectAll]);

            dgvResults.MouseDown    += gridResults_MouseDown;
            menu.Opening            += gridContextMenu_Opening;
            dgvResults.ContextMenuStrip = menu;
        }

        private void gridContextCopy_Click(object? sender, EventArgs e)
        {
            if (dgvResults.CurrentCell?.Value is string v && v.Length > 0)
                Clipboard.SetText(v);
        }

        private void gridContextPaste_Click(object? sender, EventArgs e) => PasteToCurrentCell();

        private void gridContextSelectAll_Click(object? sender, EventArgs e) => dgvResults.SelectAll();

        // Right-click first moves focus to the cell under the cursor, then the menu opens
        private void gridResults_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = dgvResults.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0 && hit.ColumnIndex >= 0)
                dgvResults.CurrentCell = dgvResults[hit.ColumnIndex, hit.RowIndex];
        }

        private void gridContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            bool canPaste = dgvResults.CurrentCell?.ColumnIndex == colProposed.Index
                            && !dgvResults.CurrentCell.ReadOnly
                            && Clipboard.ContainsText();
            _mnuPaste!.Enabled = canPaste;
        }

        private void PasteToCurrentCell()
        {
            if (dgvResults.CurrentCell?.ColumnIndex == colProposed.Index
                && !dgvResults.CurrentCell.ReadOnly
                && Clipboard.ContainsText())
                dgvResults.CurrentCell.Value = Clipboard.GetText().Trim();
        }

        // Keyboard shortcuts mirroring the context menu
        private void dgvResults_KeyDown(object? sender, KeyEventArgs e)
        {
            if (!e.Control) return;
            switch (e.KeyCode)
            {
                case Keys.V:
                    PasteToCurrentCell();
                    e.Handled = true;
                    break;
                case Keys.A:
                    dgvResults.SelectAll();
                    e.Handled = true;
                    break;
            }
        }

        // Commits checkbox edits immediately so the value is available without leaving the row
        private void dgvResults_CellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == colInclude.Index)
                dgvResults.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        // Colors rows by match confidence: orange = no TMDB match, red = uncertain match
        private void dgvResults_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= dgvResults.Rows.Count) return;
            Color color = (dgvResults.Rows[e.RowIndex].Tag as RowData)?.Confidence switch
            {
                RowConfidence.Unmatched => Color.DarkOrange,
                RowConfidence.Uncertain => Color.Firebrick,
                _                       => dgvResults.DefaultCellStyle.ForeColor
            };
            e.CellStyle.ForeColor          = color;
            e.CellStyle.SelectionForeColor = color;
        }

        // Disables Scan Now and the folder tabs while an async operation is running.
        // btnRenameNow is managed separately by each handler to preserve its proposals-count state.
        private void SetBusy(bool busy)
        {
            btnScanNow.Enabled  = !busy;
            tabFolders.Enabled  = !busy;
        }

        private void PopulateGrid(List<RenameProposal> proposals)
        {
            dgvResults.Rows.Clear();
            foreach (var p in proposals)
            {
                RowConfidence confidence;
                if (p.IsMatched)
                    confidence = p.IsConfident ? RowConfidence.Confident : RowConfidence.Uncertain;
                else
                    confidence = RowConfidence.Unmatched;

                int idx = dgvResults.Rows.Add(
                    true,
                    p.MediaType,
                    Path.GetFileName(p.OriginalPath),
                    p.IsMatched ? Path.GetFileName(p.ProposedPath) : string.Empty);
                dgvResults.Rows[idx].Tag = new RowData(confidence, p);
            }
        }

        private static void AddFolder(ListBox listBox)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description            = "Select a media folder",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() == DialogResult.OK && !listBox.Items.Contains(dlg.SelectedPath))
                listBox.Items.Add(dlg.SelectedPath);
        }

        private static void RemoveSelectedFolder(ListBox listBox)
        {
            if (listBox.SelectedIndex >= 0)
                listBox.Items.RemoveAt(listBox.SelectedIndex);
        }

        private static void LoadFolderList(ListBox listBox, string key)
        {
            listBox.Items.Clear();
            var value = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, key);
            foreach (var folder in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                listBox.Items.Add(folder);
        }

        private static void SaveFolderList(ListBox listBox, string key)
        {
            var folders = listBox.Items.Cast<string>();
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionMedia, key, string.Join(';', folders));
        }
    }
}

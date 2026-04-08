namespace qbPortWeaver
{
    /// <summary>Dialog for configuring the Media Manager feature, previewing proposed imports (Scan Now), and applying them (Import Now).</summary>
    public partial class MediaManagerForm : Form
    {
        private const int MaxStatusFileNameLength = 40;

        private enum RowConfidence { Confident, Uncertain, Unmatched }
        private sealed record RowData(RowConfidence Confidence, MediaProposal Proposal);

        private CancellationTokenSource? _scanCts;
        private ToolStripMenuItem? _mnuPaste;
        private bool _allIncluded = true;
        private bool _isBusy;

        public MediaManagerForm()
        {
            InitializeComponent();
            Text = $"{AppConstants.AppName} | Media Manager";
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size; // lock minimum to initial window size so controls are never clipped
            SetupTooltips();
            SetupGridContextMenu();
            LoadSettings();
        }

        private void SetupTooltips()
        {
            toolTip.SetToolTip(chkEnabled,            "Enable or disable Media Manager - when enabled, files are imported on each sync cycle");
            toolTip.SetToolTip(txtTmdbApiKey,         "Your TMDB API key - get one free at themoviedb.org/settings/api");
            toolTip.SetToolTip(lblTmdbApiKey,         "Your TMDB API key - get one free at themoviedb.org/settings/api");
            toolTip.SetToolTip(chkDryRun,             "When checked, the automatic sync cycle will only log what it would import without touching any files");
            toolTip.SetToolTip(chkCreateFolders,      "Import each title into its own Plex-recommended folder: Movies/Title (Year)/Title (Year).ext");
            toolTip.SetToolTip(chkDeleteEmptyFolders, "Delete source folders left empty after importing - folders containing only .nfo files are also removed");
            toolTip.SetToolTip(cboImportMode,         "Hardlink: links without copying (same volume required). Copy: duplicates the file. Move: relocates the file.");
            toolTip.SetToolTip(txtMoviesLibraryPath,  "Target library folder for imported movies");
            toolTip.SetToolTip(txtTvShowsLibraryPath, "Target library folder for imported TV shows");
            toolTip.SetToolTip(lstSourceFolders,      "Source folders scanned for movies and TV shows on each cycle");
            toolTip.SetToolTip(btnAddSourceFolder,    "Add a folder to scan");
            toolTip.SetToolTip(btnRemoveSourceFolder, "Remove the selected folder from the list");
            toolTip.SetToolTip(btnScanNow,            "Preview which files would be imported - no files are touched");
            toolTip.SetToolTip(btnImportNow,           "Import the files shown in the grid into the library");
            toolTip.SetToolTip(btnClearCache,          "Delete cached fingerprints and TMDB lookups so the next scan starts fresh");
            toolTip.SetToolTip(dgvResults,            "Files that would be imported. Uncheck a row to exclude it. Rows in red are uncertain TMDB matches - double-click the Proposed cell to correct the name before importing.");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isBusy)
            {
                var result = MessageBox.Show(
                    "A scan or import is in progress.\n\nClosing will cancel the operation. Any files already imported will remain in the library.\n\nClose anyway?",
                    $"{AppConstants.AppName} | Media Manager",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            base.OnFormClosing(e);
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

            var importMode = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaImportMode);
            cboImportMode.SelectedItem = cboImportMode.Items.Contains(importMode) ? importMode : RegistrySettingsManager.ImportModeHardlink;

            txtMoviesLibraryPath.Text  = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaMoviesLibraryPath);
            txtTvShowsLibraryPath.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaTvShowsLibraryPath);

            LoadFolderList(lstSourceFolders, RegistrySettingsManager.KeyMediaSourceFolders);
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
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionMedia,  RegistrySettingsManager.KeyMediaImportMode,         cboImportMode.SelectedItem?.ToString() ?? RegistrySettingsManager.ImportModeHardlink);
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionMedia,  RegistrySettingsManager.KeyMediaMoviesLibraryPath,  txtMoviesLibraryPath.Text.Trim());
            RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionMedia,  RegistrySettingsManager.KeyMediaTvShowsLibraryPath, txtTvShowsLibraryPath.Text.Trim());

            SaveFolderList(lstSourceFolders, RegistrySettingsManager.KeyMediaSourceFolders);
        }

        private void btnAddSourceFolder_Click(object? sender, EventArgs e)    => AddFolder(lstSourceFolders);
        private void btnRemoveSourceFolder_Click(object? sender, EventArgs e) => RemoveSelectedFolder(lstSourceFolders);
        private void btnClearCache_Click(object? sender, EventArgs e)
        {
            MediaManagerService.ClearAllCaches();
            dgvResults.Rows.Clear();
            btnImportNow.Enabled = false;
            prgScan.Visible      = false;
            lblScanStatus.Text   = "Cache cleared - run Scan Now to re-index.";
        }

        private void btnBrowseMoviesLibrary_Click(object? sender, EventArgs e)  => BrowseForFolder(txtMoviesLibraryPath);
        private void btnBrowseTvShowsLibrary_Click(object? sender, EventArgs e) => BrowseForFolder(txtTvShowsLibraryPath);

        // Scan Now - previews imports using current (unsaved) form values, never touches files
        private async void btnScanNow_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
        {
            var apiKey = txtTmdbApiKey.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                lblScanStatus.Text = "TMDB API key required.";
                return;
            }

            var moviesLibraryPath  = txtMoviesLibraryPath.Text.Trim();
            var tvShowsLibraryPath = txtTvShowsLibraryPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(moviesLibraryPath) && string.IsNullOrWhiteSpace(tvShowsLibraryPath))
            {
                lblScanStatus.Text = "At least one library path required.";
                return;
            }

            var sourceFolders = lstSourceFolders.Items.Cast<string>().ToArray();
            if (sourceFolders.Length == 0)
            {
                lblScanStatus.Text = "No source folders configured.";
                return;
            }

            var ct = await ResetCancellationTokenAsync();

            SetBusy(true);
            BeginProgress();
            lblScanStatus.Text = "Scanning\u2026";
            dgvResults.Rows.Clear();

            try
            {
                bool createFolders = chkCreateFolders.Checked;
                var proposals = await MediaManagerService.ScanAsync(apiKey, createFolders, sourceFolders, moviesLibraryPath, tvShowsLibraryPath, CreateScanProgress("Scanning\u2026"), ct);

                PopulateGrid(proposals);
                UpdateScanStatus();
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) lblScanStatus.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                if (!IsDisposed) lblScanStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (!IsDisposed)
                {
                    FinishProgress();
                    SetBusy(false);
                }
            }
        }

        // Import Now - applies proposals from the grid, respecting any user edits to the Proposed column
        private async void btnImportNow_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
        {
            // Count only checked rows with a proposed name (unchecked rows are excluded)
            int proposalCount = dgvResults.Rows.Cast<DataGridViewRow>()
                .Count(r => r.Cells[colInclude.Index].Value is true
                            && !string.IsNullOrWhiteSpace(r.Cells[colProposed.Index].Value?.ToString()));

            var confirm = MessageBox.Show(
                $"{proposalCount} file{(proposalCount == 1 ? "" : "s")} will be imported. This cannot be undone.\n\nContinue?",
                $"{AppConstants.AppName} | Media Manager",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            var ct = await ResetCancellationTokenAsync();

            SetBusy(true);
            BeginProgress();
            btnImportNow.Enabled = false;
            lblScanStatus.Text   = "Importing\u2026";
            lblScanStatus.Refresh();

            try
            {
                await RunImportAndRescanAsync(ct);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) lblScanStatus.Text = "Import cancelled.";
            }
            catch (Exception ex)
            {
                if (!IsDisposed) lblScanStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (!IsDisposed)
                {
                    FinishProgress();
                    SetBusy(false);
                    UpdateScanStatus();
                }
            }
        }

        // Applies proposals, optionally cleans up empty folders, then re-scans to refresh the grid.
        private async Task RunImportAndRescanAsync(CancellationToken ct)
        {
            var toApply    = BuildProposalsFromGrid();
            var importMode = MediaManagerService.ParseImportMode(cboImportMode.SelectedItem?.ToString() ?? RegistrySettingsManager.ImportModeHardlink);

            var progress = new Progress<(int Current, int Total, string FileName)>(p =>
            {
                if (IsDisposed) return;
                prgScan.Style   = ProgressBarStyle.Blocks;
                prgScan.Maximum = p.Total > 0 ? p.Total : 1;
                prgScan.Value   = Math.Min(p.Current, prgScan.Maximum);
                string name = p.FileName.Length > MaxStatusFileNameLength
                    ? string.Concat(p.FileName.AsSpan(0, MaxStatusFileNameLength - 3), "...")
                    : p.FileName;
                lblScanStatus.Text = $"Importing {p.Current}/{p.Total} - {name}";
            });
            await MediaManagerService.ApplyProposalsAsync(toApply, importMode, progress, ct);

            if (IsDisposed) return;

            var sourceFolders = lstSourceFolders.Items.Cast<string>().ToArray();
            if (chkDeleteEmptyFolders.Checked)
            {
                lblScanStatus.Text = "Cleaning up empty folders\u2026";
                await Task.Run(() =>
                {
                    foreach (var folder in sourceFolders)
                    {
                        ct.ThrowIfCancellationRequested();
                        MediaManagerService.CleanupEmptyFolders(folder, dryRun: false);
                    }
                }, ct);
            }

            if (IsDisposed) return;

            lblScanStatus.Text = "Re-scanning\u2026";
            BeginProgress();
            var remaining = await MediaManagerService.ScanAsync(
                txtTmdbApiKey.Text.Trim(), chkCreateFolders.Checked, sourceFolders,
                txtMoviesLibraryPath.Text.Trim(), txtTvShowsLibraryPath.Text.Trim(), CreateScanProgress("Re-scanning\u2026"), ct);

            if (IsDisposed) return;
            PopulateGrid(remaining);

            string remainingLabel = $"{remaining.Count} file{(remaining.Count == 1 ? "" : "s")}";
            lblScanStatus.Text   = remaining.Count == 0
                ? "Done - all files imported successfully."
                : $"Done - {remainingLabel} could not be imported.";
            btnImportNow.Enabled = remaining.Count > 0;
        }

        // Builds updated proposals from the grid, honouring any edits the user made to the Proposed column.
        // Each row carries its own MediaProposal in Tag so row order is irrelevant.
        private List<MediaProposal> BuildProposalsFromGrid()
        {
            var toApply = new List<MediaProposal>();
            foreach (DataGridViewRow row in dgvResults.Rows)
            {
                if (row.Tag is not RowData { Proposal: var original }) continue;
                if (row.Cells[colInclude.Index].Value is not true) continue; // user excluded this row

                var editedName = row.Cells[colProposed.Index].Value?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(editedName)) continue; // unmatched row with no user-supplied name

                // Use the proposed directory from the scan (which targets the library).
                // Skip rows where no library directory is known - this covers unmatched rows where
                // the user typed a filename but no library path can be determined.
                var proposedDir = !string.IsNullOrEmpty(original.ProposedPath)
                                  ? Path.GetDirectoryName(original.ProposedPath) ?? string.Empty
                                  : string.Empty;
                if (string.IsNullOrEmpty(proposedDir)) continue;

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
            menu.Items.Add(mnuCopy);
            menu.Items.Add(_mnuPaste);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(mnuSelectAll);

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
            {
                dgvResults.CommitEdit(DataGridViewDataErrorContexts.Commit);
                UpdateScanStatus();
            }
        }

        // Clicking the Include column header toggles all checkboxes on/off
        private void dgvResults_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex != colInclude.Index) return;

            _allIncluded = !_allIncluded;
            foreach (DataGridViewRow row in dgvResults.Rows)
                row.Cells[colInclude.Index].Value = _allIncluded;
            UpdateScanStatus();
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

        private async Task<CancellationToken> ResetCancellationTokenAsync()
        {
            if (_scanCts is not null) { await _scanCts.CancelAsync(); _scanCts.Dispose(); }
            _scanCts = new CancellationTokenSource();
            return _scanCts.Token;
        }

        private IProgress<(int Current, int Total)> CreateScanProgress(string verb)
            => new Progress<(int Current, int Total)>(p =>
            {
                if (IsDisposed) return;
                prgScan.Style      = ProgressBarStyle.Blocks;
                prgScan.Maximum    = p.Total > 0 ? p.Total : 1;
                prgScan.Value      = Math.Min(p.Current, prgScan.Maximum);
                lblScanStatus.Text = $"{verb} {p.Current}/{p.Total}";
            });

        private void BeginProgress()
        {
            prgScan.Style   = ProgressBarStyle.Marquee;
            prgScan.Value   = 0;
            prgScan.Visible = true;
        }

        private void FinishProgress()
        {
            prgScan.Style = ProgressBarStyle.Blocks;
            prgScan.Value = prgScan.Maximum;
        }

        // Disables input controls while an async operation is running.
        // btnImportNow is managed separately by each handler to preserve its proposals-count state.
        private void SetBusy(bool busy)
        {
            _isBusy                         = busy;
            btnScanNow.Enabled              = !busy;
            btnClearCache.Enabled           = !busy;
            btnAddSourceFolder.Enabled      = !busy;
            btnRemoveSourceFolder.Enabled   = !busy;
            txtMoviesLibraryPath.Enabled    = !busy;
            txtTvShowsLibraryPath.Enabled   = !busy;
            btnBrowseMoviesLibrary.Enabled  = !busy;
            btnBrowseTvShowsLibrary.Enabled = !busy;
            chkCreateFolders.Enabled        = !busy;
            cboImportMode.Enabled           = !busy;
            chkDryRun.Enabled               = !busy;
            chkDeleteEmptyFolders.Enabled   = !busy;
            dgvResults.Enabled              = !busy;
        }

        // Updates the status label and Import Now button based on checked rows
        private void UpdateScanStatus()
        {
            int included = 0;
            int unmatched = 0;

            foreach (DataGridViewRow row in dgvResults.Rows)
            {
                if (row.Tag is not RowData { Proposal: var p }) continue;
                bool isChecked = row.Cells[colInclude.Index].Value is true;

                if (isChecked && !p.IsMatched)
                    unmatched++;
                else if (isChecked)
                    included++;
            }

            string includeStr   = $"{included} file{(included == 1 ? "" : "s")}";
            string unmatchedStr = $"{unmatched} file{(unmatched == 1 ? "" : "s")}";

            lblScanStatus.Text = (included, unmatched) switch
            {
                (0, 0) => "All files already imported.",
                (0, _) => $"{unmatchedStr} had no TMDB match - enter proposed names manually.",
                (_, 0) => $"{includeStr} would be imported.",
                _      => $"{includeStr} would be imported, {unmatchedStr} had no TMDB match."
            };

            btnImportNow.Enabled = included > 0 || unmatched > 0;
        }

        private void PopulateGrid(List<MediaProposal> proposals)
        {
            dgvResults.Rows.Clear();
            _allIncluded = true;
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

        private static void BrowseForFolder(TextBox target)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description            = "Select a library folder",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                target.Text = dlg.SelectedPath;
        }

        private static void AddFolder(ListBox listBox)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description            = "Select a source folder",
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

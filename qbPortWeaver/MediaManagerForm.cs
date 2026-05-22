using qbPortWeaver.Shared;

namespace qbPortWeaver;

/// <summary>Dialog for configuring the Media Manager feature, previewing proposed imports (Scan Now), and applying them (Import Now).</summary>
public partial class MediaManagerForm : Form
{
    private const int MaxStatusFileNameLength = 40;
    [System.Text.RegularExpressions.GeneratedRegex(@"\(\d{4}\)$")]
    private static partial System.Text.RegularExpressions.Regex TitleYearFolderRegex();

    private enum RowConfidence { Confident, Uncertain, Unmatched }
    private sealed record RowData(RowConfidence Confidence, MediaProposal Proposal);
    private readonly record struct TmdbMatch(string? PosterPath, int? TmdbId, int VoteCount, string? Overview);

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _thumbnailCts;
    private readonly Dictionary<string, Image> _posterCache = new(StringComparer.OrdinalIgnoreCase);
    private ToolStripMenuItem? _mnuPaste;
    private bool _allIncluded = true;
    private bool _isBusy;
    private bool _showOnlyReviewNeeded;
    // Controls disabled while an async operation is running. After the operation, SetBusy(false)
    // re-enables _busyControls and each handler's finally calls UpdateScanStatus() which sets
    // btnImportNow.Enabled appropriately based on the current grid state.
    private Control[] _busyControls;

    // Row confidence colors - set once in OnLoad based on active theme
    private bool _isDarkMode;
    private Color _colorUncertain;
    private Color _colorUnmatched;

    public MediaManagerForm()
    {
        InitializeComponent();
        Text = $"{AppIdentity.AppName} | Media Manager";
        rtbTmdbOverview.Enter += (s, e) => dgvResults.Focus();
        _busyControls =
        [
            btnScanNow, btnRematch, btnImportNow, btnClearCache,
            btnAddSourceFolder, btnRemoveSourceFolder,
            txtMoviesLibraryPath, txtTvShowsLibraryPath,
            btnBrowseMoviesLibrary, btnBrowseTvShowsLibrary,
            chkCreateFolders, cboImportMode, chkDryRun,
            chkDeleteEmptyFolders, chkShowOnlyReview,
            dgvResults,
        ];
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = Size; // lock minimum to initial window size so controls are never clipped
        _isDarkMode = AppConstants.IsDarkModeEnabled();
        _colorUncertain = _isDarkMode ? AppConstants.DarkModeWarning : AppConstants.LightModeWarning;
        _colorUnmatched = _isDarkMode ? AppConstants.DarkModeError : AppConstants.LightModeError;
        lblLegendUncertain.ForeColor = _colorUncertain;
        lblLegendUnmatched.ForeColor = _colorUnmatched;
        rtbTmdbOverview.Font = Font;
        rtbTmdbOverview.ForeColor = ForeColor;
        if (_isDarkMode)
            rtbTmdbOverview.ForeColor = AppConstants.DarkModeText;
        SetupTooltips();
        SetupGridContextMenu();
        LoadSettings();
    }

    private void SetupTooltips()
    {
        toolTip.SetToolTip(chkEnabled, "Enable or disable Media Manager - when enabled, files are imported on each sync cycle");
        toolTip.SetToolTip(txtTmdbApiKey, "Your TMDB credential - accepts both the API Key (v3, 32-char hex) and the API Read Access Token (v4, JWT). Get one free at themoviedb.org/settings/api");
        toolTip.SetToolTip(lblTmdbApiKey, "Your TMDB credential - accepts both the API Key (v3, 32-char hex) and the API Read Access Token (v4, JWT). Get one free at themoviedb.org/settings/api");
        toolTip.SetToolTip(chkDryRun, "When checked, the automatic sync cycle will only log what it would import without touching any files");
        toolTip.SetToolTip(chkCreateFolders, "Import each title into its own Plex-recommended folder: Movies/Title (Year)/Title (Year).ext");
        toolTip.SetToolTip(chkDeleteEmptyFolders, "Delete source folders left empty after importing - folders containing only .nfo files are also removed");
        toolTip.SetToolTip(cboImportMode, "Hardlink: links without copying (same volume required). Copy: duplicates the file. Move: relocates the file.");
        toolTip.SetToolTip(txtMoviesLibraryPath, "Target library folder for imported movies");
        toolTip.SetToolTip(btnBrowseMoviesLibrary, "Browse for the movies library folder");
        toolTip.SetToolTip(txtTvShowsLibraryPath, "Target library folder for imported TV shows");
        toolTip.SetToolTip(btnBrowseTvShowsLibrary, "Browse for the TV shows library folder");
        toolTip.SetToolTip(lstSourceFolders, "Source folders scanned for movies and TV shows on each cycle");
        toolTip.SetToolTip(btnAddSourceFolder, "Add a folder to scan");
        toolTip.SetToolTip(btnRemoveSourceFolder, "Remove the selected folder from the list");
        toolTip.SetToolTip(btnScanNow, "Preview which files would be imported - no files are touched");
        toolTip.SetToolTip(btnImportNow, "Import the files shown in the grid into the library");
        toolTip.SetToolTip(btnClearCache, "Delete cached fingerprints and TMDB lookups so the next scan starts fresh");
        toolTip.SetToolTip(btnRematch, "Re-run TMDB matching for uncertain and unmatched rows using the current Proposed filenames");
        toolTip.SetToolTip(dgvResults, "Files that would be imported. Uncheck a row to exclude it. Gold rows are uncertain TMDB matches, red rows have no match - double-click the Proposed cell to correct the name before importing.");
        toolTip.SetToolTip(chkShowOnlyReview, "Show only uncertain TMDB matches (gold) and rows with no TMDB match found (red) - hides confident matches.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isBusy)
        {
            var result = MessageBox.Show(
                "A scan or import is in progress.\n\nClosing will cancel the operation. Any files already imported will remain in the library.\n\nClose anyway?",
                $"{AppIdentity.AppName} | Media Manager",
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
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        foreach (var img in _posterCache.Values) img.Dispose();
        _posterCache.Clear();
        base.OnFormClosed(e);
    }

    private void LoadSettings()
    {
        chkEnabled.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled);
        txtTmdbApiKey.Text = RegistrySettingsManager.GetTmdbApiKey();
        chkDryRun.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
        chkCreateFolders.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);
        chkDeleteEmptyFolders.Checked = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDeleteEmptyFolders);

        var importMode = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaImportMode);
        cboImportMode.SelectedItem = cboImportMode.Items.Contains(importMode) ? importMode : RegistrySettingsManager.ImportModeHardlink;

        txtMoviesLibraryPath.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaMoviesLibraryPath);
        txtTvShowsLibraryPath.Text = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaTvShowsLibraryPath);

        LoadFolderList(lstSourceFolders, RegistrySettingsManager.KeyMediaSourceFolders);
    }

    private void btnOK_Click(object? sender, EventArgs e)
    {
        SaveSettings();
        Close();
    }

    private void btnCancel_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static

    private void SaveSettings()
    {
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled, chkEnabled.Checked);
        RegistrySettingsManager.SetTmdbApiKey(txtTmdbApiKey.Text.Trim());
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun, chkDryRun.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders, chkCreateFolders.Checked);
        RegistrySettingsManager.SetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDeleteEmptyFolders, chkDeleteEmptyFolders.Checked);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaImportMode, cboImportMode.SelectedItem?.ToString() ?? RegistrySettingsManager.ImportModeHardlink);
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaMoviesLibraryPath, txtMoviesLibraryPath.Text.Trim());
        RegistrySettingsManager.SetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaTvShowsLibraryPath, txtTvShowsLibraryPath.Text.Trim());

        SaveFolderList(lstSourceFolders, RegistrySettingsManager.KeyMediaSourceFolders);
    }

    private void btnAddSourceFolder_Click(object? sender, EventArgs e) => AddFolder(lstSourceFolders);
    private void btnRemoveSourceFolder_Click(object? sender, EventArgs e) => RemoveSelectedFolder(lstSourceFolders);
    private void btnClearCache_Click(object? sender, EventArgs e)
    {
        MediaManagerService.ClearAllCaches();
        dgvResults.Rows.Clear();
        ClearDetailPanel();
        foreach (var img in _posterCache.Values) img.Dispose();
        _posterCache.Clear();
        btnImportNow.Enabled = false;
        prgScan.Visible = false;
        lblScanStatus.Text = "Cache cleared - run Scan Now to re-index.";
    }

    private async void btnRematch_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        var apiKey = txtTmdbApiKey.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            lblScanStatus.Text = "TMDB API key required.";
            return;
        }

        var cancellationToken = await RenewScanCancellationTokenAsync();
        SetBusy(true);
        BeginProgress();
        lblScanStatus.Text = "Re-matching\u2026";

        string? completionStatus = null;
        try
        {
            completionStatus = await RematchRowsAsync(apiKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) completionStatus = "Re-match cancelled.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) completionStatus = $"Error: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                FinishProgress();
                SetBusy(false);
                UpdateScanStatus();
                if (completionStatus is not null) lblScanStatus.Text = completionStatus;
            }
        }
    }

    // Re-verifies uncertain and unmatched rows using the current Proposed filenames.
    // TV show rows are grouped by show name - one TMDB call per show, not per episode.
    // Rows where TMDB still cannot find a match are left unchanged.
    private async Task<string?> RematchRowsAsync(string apiKey, CancellationToken cancellationToken)
    {
        var tmdb = new TmdbClient(apiKey);
        bool createFolders = chkCreateFolders.Checked;
        var moviesLib = txtMoviesLibraryPath.Text.Trim();
        var tvShowLib = txtTvShowsLibraryPath.Text.Trim();

        // Split by media type upfront so the progress total is exact regardless of future types
        var tvShowRows = GetRematchRows(MediaProposal.TypeTvShow);
        var movieRows = GetRematchRows(MediaProposal.TypeMovie);
        int total = tvShowRows.Count + movieRows.Count;

        if (total == 0)
            return "No uncertain rows to re-match.";

        prgScan.Style = ProgressBarStyle.Blocks;
        prgScan.Maximum = total;
        prgScan.Value = 0;

        int done = await RematchTvShowRowsAsync(tmdb, tvShowLib, tvShowRows, createFolders, total, 0, cancellationToken);
        if (IsDisposed) return null;
        await RematchMovieRowsAsync(tmdb, moviesLib, movieRows, createFolders, total, done, cancellationToken);
        if (IsDisposed) return null;

        dgvResults.Refresh(); // force full repaint so CellFormatting fires for all visible cells
        dgvResults_SelectionChanged(dgvResults, EventArgs.Empty); // refresh detail panel for the selected row
        int verified = tvShowRows.Count(r => ((RowData)r.Tag!).Confidence == RowConfidence.Confident)
                     + movieRows.Count(r => ((RowData)r.Tag!).Confidence == RowConfidence.Confident);
        return verified == total
            ? "Re-match complete - all rows verified."
            : $"Re-match complete - {verified}/{total} rows verified.";
    }

    // Re-matches TV show rows grouped by show name. Returns updated done count.
    private async Task<int> RematchTvShowRowsAsync(TmdbClient tmdb, string tvShowLib,
        List<DataGridViewRow> tvShowRows, bool createFolders, int total, int done, CancellationToken cancellationToken)
    {
        foreach (var (showKey, showRows) in GroupTvShowRowsByShow(tvShowRows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (showInfo, showConfident) = string.IsNullOrWhiteSpace(tvShowLib)
                ? (null, false)
                : await SearchTmdbByPlexNameAsync<TvShowInfo>(showKey, tmdb.SearchTvShowCandidatesAsync, i => i.Title, i => i.Year, i => i.VoteCount, "TV show", cancellationToken);
            foreach (var row in showRows)
            {
                if (IsDisposed) return done;
                if (showInfo is not null)
                    ApplyTvRematchResult(row, showInfo, tvShowLib, createFolders, showConfident);
                prgScan.Value = Math.Min(++done, prgScan.Maximum);
                lblScanStatus.Text = $"Re-matching\u2026 {done}/{total}";
            }
        }
        return done;
    }

    // Re-matches movie rows individually. Returns updated done count.
    private async Task<int> RematchMovieRowsAsync(TmdbClient tmdb, string moviesLib,
        List<DataGridViewRow> movieRows, bool createFolders, int total, int done, CancellationToken cancellationToken)
    {
        foreach (var row in movieRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return done;
            if (!string.IsNullOrWhiteSpace(moviesLib))
            {
                var editedName = GetProposedName(row);
                var (movieInfo, movieConfident) = await SearchTmdbByPlexNameAsync<MovieInfo>(editedName, tmdb.SearchMovieCandidatesAsync, i => i.Title, i => i.Year, i => i.VoteCount, "movie", cancellationToken);
                if (movieInfo is not null)
                    ApplyMovieRematchResult(row, movieInfo, moviesLib, createFolders, editedName, movieConfident);
            }
            prgScan.Value = Math.Min(++done, prgScan.Maximum);
            lblScanStatus.Text = $"Re-matching\u2026 {done}/{total}";
        }
        return done;
    }

    // Returns uncertain/unmatched rows of the given media type.
    private List<DataGridViewRow> GetRematchRows(string mediaType) =>
        dgvResults.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Tag is RowData { Confidence: not RowConfidence.Confident } rd && rd.Proposal.MediaType == mediaType)
            .ToList();

    // Groups TV show rows by their extracted show-folder name for batch TMDB lookup.
    private Dictionary<string, List<DataGridViewRow>> GroupTvShowRowsByShow(List<DataGridViewRow> tvShowRows)
    {
        var groups = new Dictionary<string, List<DataGridViewRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in tvShowRows)
        {
            var editedName = GetProposedName(row);
            var key = ExtractTvShowFolderKey(editedName) ?? editedName;
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = [];
            group.Add(row);
        }
        return groups;
    }

    // Parses a Plex-formatted name into title+year and searches TMDB with confidence tracking.
    // Returns (null, false) if the name is blank, unparseable, or has no TMDB match.
    private static async Task<(T? Info, bool IsConfident)> SearchTmdbByPlexNameAsync<T>(
        string name, Func<string, int?, CancellationToken, Task<IReadOnlyList<T>?>> search,
        Func<T, string> getTitle, Func<T, int?> getYear, Func<T, int> getVoteCount,
        string mediaLabel, CancellationToken cancellationToken = default) where T : class
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, false);
        var (title, year) = FileNameParser.ParseTitleYear(name);
        if (string.IsNullOrWhiteSpace(title)) return (null, false);
        try
        {
            var (info, isConfident) = await TmdbClient.SearchWithConfidenceAsync(
                title, year, search, i => getYear(i) is not null, getTitle, getVoteCount, cancellationToken).ConfigureAwait(false);

            if (info is null)
            {
                LogManager.Instance.LogDebug($"MediaManagerForm.SearchTmdbByPlexNameAsync: No TMDB match found for {mediaLabel} '{title}'", Subsystem.MediaManager);
                return (null, false);
            }
            LogManager.Instance.LogDebug($"MediaManagerForm.SearchTmdbByPlexNameAsync: Matched {mediaLabel} '{getTitle(info)}' ({getYear(info)})", Subsystem.MediaManager);
            return (info, isConfident);
        }
        catch (HttpRequestException ex)
        {
            // TaskCanceledException and JsonException propagate to the caller's handlers.
            LogManager.Instance.LogMessage($"Re-match {mediaLabel} lookup failed for '{title}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            return (null, false);
        }
    }

    private void ApplyTvRematchResult(DataGridViewRow row, TvShowInfo info, string tvShowLib, bool createFolders, bool isConfident)
    {
        var editedName = GetProposedName(row);
        var episodeInfo = FileNameParser.ParseTvShowEpisode(editedName);
        if (episodeInfo is null)
        {
            LogManager.Instance.LogDebug($"MediaManagerForm.ApplyTvRematchResult: could not parse episode info from '{editedName}' - row skipped", Subsystem.MediaManager);
            return;
        }

        var proposedPath = TvShowProcessor.BuildEpisodePath(editedName, info, episodeInfo, tvShowLib, createFolders);
        UpdateRow(row, proposedPath, Path.GetFileName(proposedPath), isConfident, new TmdbMatch(info.PosterPath, info.TmdbId, info.VoteCount, info.Overview));
    }

    private void ApplyMovieRematchResult(DataGridViewRow row, MovieInfo info, string moviesLib, bool createFolders, string editedName, bool isConfident)
    {
        var proposedPath = MovieProcessor.BuildStandaloneMoviePath(editedName, info, moviesLib, createFolders);
        UpdateRow(row, proposedPath, Path.GetFileName(proposedPath), isConfident, new TmdbMatch(info.PosterPath, info.TmdbId, info.VoteCount, info.Overview));
    }

    private void UpdateRow(DataGridViewRow row, string proposedPath, string displayName, bool isConfident, TmdbMatch match = default)
    {
        var original = ((RowData)row.Tag!).Proposal;
        var confidence = isConfident ? RowConfidence.Confident : RowConfidence.Uncertain;
        row.Tag = new RowData(confidence, original with { ProposedPath = proposedPath, IsMatched = true, IsConfident = isConfident, PosterPath = match.PosterPath, TmdbId = match.TmdbId, VoteCount = match.VoteCount, Overview = match.Overview });
        row.Cells[colProposed.Index].Value = displayName;
    }

    private void btnBrowseMoviesLibrary_Click(object? sender, EventArgs e) => BrowseForFolder(txtMoviesLibraryPath);
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

        var moviesLibraryPath = txtMoviesLibraryPath.Text.Trim();
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

        var cancellationToken = await RenewScanCancellationTokenAsync();

        SetBusy(true);
        BeginProgress();
        lblScanStatus.Text = "Scanning\u2026";
        // Detach the poster image before disposing cached images: Rows.Clear() fires
        // SelectionChanged which awaits CancelAsync and yields, leaving picTmdbPoster.Image
        // pointing at a freshly-disposed image until the handler resumes - a paint in that
        // window would hit ObjectDisposedException.
        picTmdbPoster.Image = null;
        dgvResults.Rows.Clear();
        foreach (var img in _posterCache.Values) img.Dispose();
        _posterCache.Clear();

        string? completionStatus = null;
        try
        {
            bool createFolders = chkCreateFolders.Checked;
            var proposals = await MediaManagerService.ScanAsync(apiKey, createFolders, sourceFolders, moviesLibraryPath, tvShowsLibraryPath, CreateScanProgress("Scanning\u2026"), cancellationToken);

            PopulateGrid(proposals);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) completionStatus = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) completionStatus = $"Error: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                FinishProgress();
                SetBusy(false);
                UpdateScanStatus();
                if (completionStatus is not null) lblScanStatus.Text = completionStatus;
            }
        }
    }

    // Import Now - applies proposals from the grid, respecting any user edits to the Proposed column
    private async void btnImportNow_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        // Count only checked rows with a proposed name (unchecked rows are excluded)
        int proposalCount = dgvResults.Rows.Cast<DataGridViewRow>()
            .Count(r => r.Cells[colInclude.Index].Value is true // NOSONAR S1125 - 'is true' pattern requires the literal to match the boxed bool value
                        && !string.IsNullOrWhiteSpace(r.Cells[colProposed.Index].Value?.ToString()));

        var confirm = MessageBox.Show(
            $"{proposalCount} file{(proposalCount == 1 ? "" : "s")} will be imported. This cannot be undone.\n\nContinue?",
            $"{AppIdentity.AppName} | Media Manager",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        var cancellationToken = await RenewScanCancellationTokenAsync();

        SetBusy(true);
        BeginProgress();
        lblScanStatus.Text = "Importing\u2026";
        lblScanStatus.Refresh();

        string? completionStatus = null;
        try
        {
            await RunImportAndRescanAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) completionStatus = "Import cancelled.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) completionStatus = $"Error: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                FinishProgress();
                SetBusy(false);
                UpdateScanStatus();
                if (completionStatus is not null) lblScanStatus.Text = completionStatus;
            }
        }
    }

    // Applies proposals, optionally cleans up empty folders, then re-scans to refresh the grid.
    private async Task RunImportAndRescanAsync(CancellationToken cancellationToken)
    {
        var toApply = BuildProposalsFromGrid();
        var importMode = MediaManagerService.ParseImportMode(cboImportMode.SelectedItem?.ToString() ?? RegistrySettingsManager.ImportModeHardlink);

        var progress = new Progress<(int Current, int Total, string FileName)>(p =>
        {
            if (IsDisposed) return;
            prgScan.Style = ProgressBarStyle.Blocks;
            prgScan.Maximum = p.Total > 0 ? p.Total : 1;
            prgScan.Value = Math.Min(p.Current, prgScan.Maximum);
            string name = p.FileName.Length > MaxStatusFileNameLength
                ? string.Concat(p.FileName.AsSpan(0, MaxStatusFileNameLength - 3), "...")
                : p.FileName;
            lblScanStatus.Text = $"Importing {p.Current}/{p.Total} - {name}";
        });
        await MediaManagerService.ApplyProposalsAsync(toApply, importMode, progress, cancellationToken);

        if (IsDisposed) return;

        var sourceFolders = lstSourceFolders.Items.Cast<string>().ToArray();
        if (chkDeleteEmptyFolders.Checked)
        {
            lblScanStatus.Text = "Cleaning up empty folders\u2026";
            await Task.Run(() =>
            {
                foreach (var folder in sourceFolders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MediaManagerService.CleanupEmptyFolders(folder, dryRun: false);
                }
            }, cancellationToken);
        }

        if (IsDisposed) return;

        lblScanStatus.Text = "Re-scanning\u2026";
        BeginProgress();
        var remaining = await MediaManagerService.ScanAsync(
            txtTmdbApiKey.Text.Trim(), chkCreateFolders.Checked, sourceFolders,
            txtMoviesLibraryPath.Text.Trim(), txtTvShowsLibraryPath.Text.Trim(), CreateScanProgress("Re-scanning\u2026"), cancellationToken);

        if (IsDisposed) return;
        PopulateGrid(remaining);

        string remainingLabel = $"{remaining.Count} file{(remaining.Count == 1 ? "" : "s")}";
        lblScanStatus.Text = remaining.Count == 0
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
            if (row.Cells[colInclude.Index].Value is not true) continue; // NOSONAR S1125 - 'is not true' pattern requires the literal; user excluded this row

            var editedName = GetProposedName(row);
            if (string.IsNullOrWhiteSpace(editedName)) continue; // unmatched row with no user-supplied name

            // Use the proposed directory from the scan (which targets the library).
            // Skip rows where no library directory is known - this covers unmatched rows where
            // the user typed a filename but no library path can be determined.
            var proposedDir = !string.IsNullOrEmpty(original.ProposedPath)
                              ? Path.GetDirectoryName(original.ProposedPath) ?? string.Empty
                              : string.Empty;
            if (string.IsNullOrEmpty(proposedDir)) continue;

            // Rebuild the containing folder(s) from the edited filename so that the show/movie
            // folder stays consistent with the new name - not the stale name from the original scan.
            var resolvedDir = RebuildProposedDir(proposedDir, editedName);
            var proposedPath = Path.Combine(resolvedDir, editedName);

            if (!string.Equals(original.OriginalPath, proposedPath, StringComparison.OrdinalIgnoreCase))
                toApply.Add(original with { ProposedPath = proposedPath, IsMatched = true });
        }
        return toApply;
    }

    // Rebuilds the destination directory from the edited filename so the show/movie folder
    // matches the new name rather than the stale TMDB result from the original scan.
    //
    // TV show with season subfolder:  library\OldShow\Season XX\ → library\NewShow\Season XX\
    // Movie with title subfolder:     library\OldMovie (Year)\   → library\NewTitle (Year)\
    // Flat layout (no subfolder):     library\                   → library\ (unchanged)
    private static string RebuildProposedDir(string originalDir, string editedFileName)
    {
        string lastSegment = Path.GetFileName(originalDir);

        // TV show with season subfolder: immediate parent is "Season XX"
        if (lastSegment.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
        {
            var newShowFolder = ExtractTvShowFolderKey(editedFileName);
            if (newShowFolder is not null)
            {
                var libraryPath = Path.GetDirectoryName(Path.GetDirectoryName(originalDir)) ?? string.Empty;
                return Path.Combine(libraryPath, newShowFolder, lastSegment);
            }
        }

        // Movie with title subfolder: folder name ends with "(YYYY)" - confirms it was named after the title,
        // not a flat library root. Regex avoids matching generic folder names like "Movies" or "TV Shows".
        if (TitleYearFolderRegex().IsMatch(lastSegment))
        {
            var newTitle = Path.GetFileNameWithoutExtension(editedFileName);
            var libraryPath = Path.GetDirectoryName(originalDir) ?? string.Empty;
            return Path.Combine(libraryPath, newTitle);
        }

        // Flat layout or unrecognised structure: keep the original directory
        return originalDir;
    }

    // Extracts the show folder name from a Plex-formatted episode filename: "Show Name (Year) - SxxExx.ext"
    // Returns null if the pattern is not recognised.
    private static string? ExtractTvShowFolderKey(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var sepIdx = nameWithoutExt.LastIndexOf(" - S", StringComparison.Ordinal);
        if (sepIdx <= 0) return null;

        // Verify the suffix matches SxxExx (digits, 'E', digits) to avoid false matches
        var afterSep = nameWithoutExt[(sepIdx + 4)..]; // skip " - S"
        int i = 0;
        while (i < afterSep.Length && char.IsDigit(afterSep[i])) i++;
        if (i == 0 || i >= afterSep.Length || char.ToUpperInvariant(afterSep[i]) != 'E') return null;
        i++;
        if (i >= afterSep.Length || !char.IsDigit(afterSep[i])) return null;

        return nameWithoutExt[..sepIdx].Trim();
    }

    private async void dgvResults_SelectionChanged(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        // Always create new CTS first so _thumbnailCts is never null and LoadThumbnailAsync
        // always reads its cancellation state from a live (not disposed) token.
        var newCts = new CancellationTokenSource();
        using var oldCts = Interlocked.Exchange(ref _thumbnailCts, newCts);
        if (oldCts is not null) await oldCts.CancelAsync().ConfigureAwait(true);

        if (dgvResults.SelectedRows.Count == 0 || dgvResults.SelectedRows[0].Tag is not RowData rd)
        {
            ClearDetailPanel();
            return;
        }

        UpdateDetailPanel(rd);

        var posterPath = rd.Proposal.PosterPath;
        if (string.IsNullOrEmpty(posterPath))
        {
            picTmdbPoster.Visible = false;
            return;
        }

        if (_posterCache.TryGetValue(posterPath, out var cached))
        {
            picTmdbPoster.Image = cached;
            picTmdbPoster.Visible = true;
            return;
        }

        picTmdbPoster.Visible = false;
        await LoadThumbnailAsync(posterPath, newCts.Token);
    }

    private async Task LoadThumbnailAsync(string posterPath, CancellationToken cancellationToken)
    {
        // No ConfigureAwait(false): continuation must resume on the UI thread to touch controls
        var image = await TmdbClient.FetchPosterAsync(posterPath, cancellationToken);
        if (cancellationToken.IsCancellationRequested || IsDisposed) { image?.Dispose(); return; }
        if (image is null) return;

        _posterCache[posterPath] = image;
        picTmdbPoster.Image = image;
        picTmdbPoster.Visible = true;
    }

    private void UpdateDetailPanel(RowData rd)
    {
        var p = rd.Proposal;
        if (!p.IsMatched)
        {
            lblTmdbTitle.Text = "No TMDB match found";
            lblTmdbMeta.Text = p.MediaType;
            lblTmdbConfidence.Text = string.Empty;
            lblTmdbTitle.ForeColor = _colorUnmatched;
            lblTmdbMeta.ForeColor = SystemColors.ControlText;
            picTmdbPoster.Image = null;
            picTmdbPoster.Visible = false;
            rtbTmdbOverview.Text = string.Empty;
            return;
        }

        var displayName = !string.IsNullOrEmpty(p.ProposedPath)
            ? Path.GetFileNameWithoutExtension(p.ProposedPath)
            : Path.GetFileNameWithoutExtension(p.OriginalPath);

        lblTmdbTitle.Text = displayName;
        lblTmdbTitle.ForeColor = SystemColors.ControlText;
        var voteStr = p.VoteCount > 0 ? $"  |  {p.VoteCount:N0} votes" : string.Empty;
        lblTmdbMeta.Text = p.MediaType + (p.TmdbId.HasValue ? $"  |  TMDB ID: {p.TmdbId}" : string.Empty) + voteStr;
        lblTmdbMeta.ForeColor = SystemColors.GrayText;

        if (!p.IsConfident)
        {
            lblTmdbConfidence.Text = "Uncertain match - review recommended";
            lblTmdbConfidence.ForeColor = _colorUncertain;
        }
        else
        {
            lblTmdbConfidence.Text = string.Empty;
            lblTmdbConfidence.ForeColor = SystemColors.ControlText;
        }

        rtbTmdbOverview.Text = p.Overview ?? string.Empty;
    }

    private void ClearDetailPanel()
    {
        lblTmdbTitle.Text = string.Empty;
        lblTmdbMeta.Text = string.Empty;
        lblTmdbConfidence.Text = string.Empty;
        rtbTmdbOverview.Text = string.Empty;
        picTmdbPoster.Image = null;
        picTmdbPoster.Visible = false;
    }

    private void SetupGridContextMenu()
    {
        var mnuCopy = new ToolStripMenuItem("Copy");
        _mnuPaste = new ToolStripMenuItem("Paste");
        var mnuSelectAll = new ToolStripMenuItem("Select All");

        mnuCopy.ShortcutKeyDisplayString = "Ctrl+C";
        _mnuPaste.ShortcutKeyDisplayString = "Ctrl+V";
        mnuSelectAll.ShortcutKeyDisplayString = "Ctrl+A";

        mnuCopy.Click += gridContextCopy_Click;
        _mnuPaste.Click += gridContextPaste_Click;
        mnuSelectAll.Click += gridContextSelectAll_Click;

        var menu = new ContextMenuStrip(components);
        menu.Items.Add(mnuCopy);
        menu.Items.Add(_mnuPaste);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(mnuSelectAll);

        dgvResults.MouseDown += gridResults_MouseDown;
        menu.Opening += gridContextMenu_Opening;
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
            case Keys.C:
                gridContextCopy_Click(sender, e);
                e.Handled = true;
                break;
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

    // When the user edits a Proposed cell:
    //   - Demotes a previously confirmed row back to Uncertain so Re-match will pick it up again.
    //   - For TV show rows, propagates the corrected show folder to all sibling episodes so that
    //     Re-match groups them together and looks them up under the new name.
    private void dgvResults_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != colProposed.Index) return;
        var row = dgvResults.Rows[e.RowIndex];
        if (row.Tag is not RowData rd) return;

        if (rd.Confidence == RowConfidence.Confident)
        {
            rd = rd with { Confidence = RowConfidence.Uncertain };
            row.Tag = rd;
            dgvResults.InvalidateRow(e.RowIndex);
        }

        if (rd.Proposal.MediaType == MediaProposal.TypeTvShow)
            PropagateTvShowNameToSiblings(e.RowIndex);
    }

    // Propagates the show folder from an edited TV Show row to all sibling rows that share the
    // same original source show name, so Re-match groups them together under the corrected name.
    // Siblings are identified by the show name parsed from their original source filename.
    private void PropagateTvShowNameToSiblings(int editedRowIndex)
    {
        var editedRow = dgvResults.Rows[editedRowIndex];
        var newProposed = GetProposedName(editedRow);
        var newShowFolder = ExtractTvShowFolderKey(newProposed);
        if (newShowFolder is null) return;

        var editedShowName = FileNameParser.ParseTvShowEpisode(
            Path.GetFileName(((RowData)editedRow.Tag!).Proposal.OriginalPath))?.ShowName;
        if (editedShowName is null) return;

        foreach (DataGridViewRow row in dgvResults.Rows)
        {
            if (row.Index != editedRowIndex)
                UpdateSiblingShowFolder(row, editedShowName, newShowFolder);
        }
    }

    // Updates a single sibling row's show folder if it belongs to the same TV show.
    private void UpdateSiblingShowFolder(DataGridViewRow row, string editedShowName, string newShowFolder)
    {
        if (row.Tag is not RowData rd) return;
        if (rd.Proposal.MediaType != MediaProposal.TypeTvShow) return;

        var siblingShowName = FileNameParser.ParseTvShowEpisode(
            Path.GetFileName(rd.Proposal.OriginalPath))?.ShowName;
        if (!string.Equals(siblingShowName, editedShowName, StringComparison.OrdinalIgnoreCase)) return;

        var currentProposed = GetProposedName(row);
        var currentShowFolder = ExtractTvShowFolderKey(currentProposed);
        if (currentShowFolder is null) return;
        if (string.Equals(currentShowFolder, newShowFolder, StringComparison.OrdinalIgnoreCase)) return;

        // Replace the show folder portion; keep the episode code and extension unchanged
        row.Cells[colProposed.Index].Value = newShowFolder + currentProposed[currentShowFolder.Length..];

        if (rd.Confidence == RowConfidence.Confident)
        {
            row.Tag = rd with { Confidence = RowConfidence.Uncertain };
            dgvResults.InvalidateRow(row.Index);
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

    // Colors rows by match confidence: gold = uncertain TMDB match, orange-red = no TMDB match
    private void dgvResults_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= dgvResults.Rows.Count) return;
        Color color = (dgvResults.Rows[e.RowIndex].Tag as RowData)?.Confidence switch
        {
            RowConfidence.Unmatched => _colorUnmatched,
            RowConfidence.Uncertain => _colorUncertain,
            _ => dgvResults.DefaultCellStyle.ForeColor
        };
        e.CellStyle.ForeColor = color;
        e.CellStyle.SelectionForeColor = SystemColors.HighlightText;
    }

    private async Task<CancellationToken> RenewScanCancellationTokenAsync()
    {
        var newCts = new CancellationTokenSource();
        using var oldCts = Interlocked.Exchange(ref _scanCts, newCts);
        if (oldCts is not null) await oldCts.CancelAsync().ConfigureAwait(true);
        return newCts.Token;
    }

    private IProgress<(int Current, int Total)> CreateScanProgress(string verb)
        => new Progress<(int Current, int Total)>(p =>
        {
            if (IsDisposed) return;
            prgScan.Style = ProgressBarStyle.Blocks;
            prgScan.Maximum = p.Total > 0 ? p.Total : 1;
            prgScan.Value = Math.Min(p.Current, prgScan.Maximum);
            lblScanStatus.Text = $"{verb} {p.Current}/{p.Total}";
        });

    private void BeginProgress()
    {
        prgScan.Style = ProgressBarStyle.Marquee;
        prgScan.Value = 0;
        prgScan.Visible = true;
    }

    private void FinishProgress()
    {
        prgScan.Style = ProgressBarStyle.Blocks;
        prgScan.Value = prgScan.Maximum;
        prgScan.Visible = false;
    }

    private string GetProposedName(DataGridViewRow row) => row.Cells[colProposed.Index].Value?.ToString() ?? string.Empty;

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        foreach (var control in _busyControls)
            control.Enabled = !busy;
    }

    // Updates the status label and Import Now button based on checked rows
    private void UpdateScanStatus()
    {
        int included = 0;
        int unmatched = 0;

        foreach (DataGridViewRow row in dgvResults.Rows)
        {
            if (row.Tag is not RowData { Proposal: var p }) continue;
            bool isChecked = row.Cells[colInclude.Index].Value is true; // NOSONAR S1125 - 'is true' pattern requires the literal to match the boxed bool value

            if (isChecked && !p.IsMatched)
                unmatched++;
            else if (isChecked)
                included++;
        }

        string includeStr = $"{included} file{(included == 1 ? "" : "s")}";
        string unmatchedStr = $"{unmatched} file{(unmatched == 1 ? "" : "s")}";

        lblScanStatus.Text = (included, unmatched) switch
        {
            (0, 0) => "All files already imported.",
            (0, _) => $"{unmatchedStr} had no TMDB match - enter proposed names manually.",
            (_, 0) => $"{includeStr} would be imported.",
            _ => $"{includeStr} would be imported, {unmatchedStr} had no TMDB match."
        };

        btnImportNow.Enabled = included > 0;
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

            var row = new DataGridViewRow();
            row.CreateCells(dgvResults,
                true,
                p.MediaType,
                Path.GetFileName(p.OriginalPath),
                p.IsMatched ? Path.GetFileName(p.ProposedPath) : string.Empty);
            row.Tag = new RowData(confidence, p);
            dgvResults.Rows.Add(row);
        }
        ApplyGridFilter();
    }

    private void chkShowOnlyReview_CheckedChanged(object? sender, EventArgs e)
    {
        _showOnlyReviewNeeded = chkShowOnlyReview.Checked;
        ApplyGridFilter();
    }

    // Hides confident rows when the "Review needed only" filter is active.
    private void ApplyGridFilter()
    {
        foreach (DataGridViewRow row in dgvResults.Rows)
        {
            bool isConfident = row.Tag is RowData { Confidence: RowConfidence.Confident };
            row.Visible = !_showOnlyReviewNeeded || !isConfident;
        }
    }

    private static void BrowseForFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a library folder",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            target.Text = dlg.SelectedPath;
    }

    private static void AddFolder(ListBox listBox)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a source folder",
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

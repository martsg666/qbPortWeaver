namespace qbPortWeaver;

/// <summary>About dialog showing version info, update availability, and contributor credits.</summary>
public partial class AboutForm : Form
{
    // The newer release when an update is available; null when up-to-date or not yet checked.
    private LatestReleaseInfo? _availableUpdate;
    private bool _isDarkMode;

    /// <summary>Raised when the user clicks Update for an available release. MainForm handles it by
    /// opening the shared update dialog (in-app download and install), the same as the tray entry point.</summary>
    public event Action<LatestReleaseInfo>? UpdateRequested;
    // Cancels in-flight GitHub requests when the form closes so they do not run to completion
    // in the background after the user has dismissed the dialog.
    private readonly CancellationTokenSource _githubCts = new();

    public AboutForm()
    {
        InitializeComponent();
        lblAppName.Text = AppIdentity.AppName;
        lblAppVersion.Text = $"Version {AppConstants.AppVersion}";
        lblCurrentVersionValue.Text = AppConstants.AppVersion;
        lnkGitHub.Text = $"{AppConstants.GitHubRepoOwner}/{AppIdentity.AppName}";
        Text = $"{AppIdentity.AppName} | About";
    }

    // Kick off the GitHub data fetch as fire-and-forget; the IsDisposed guard in the async method handles early close
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _isDarkMode = AppConstants.IsDarkModeEnabled();
        if (_isDarkMode)
        {
            lnkAuthor.LinkColor = AppConstants.DarkModeLinkColor;
            lnkGitHub.LinkColor = AppConstants.DarkModeLinkColor;
        }
        _ = LoadGitHubDataAsync(); // fire-and-forget; exceptions are handled inside LoadGitHubDataAsync
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _githubCts.Cancel();
        _githubCts.Dispose();
        base.OnFormClosed(e);
    }

    private void btnClose_Click(object? sender, EventArgs e) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static

    private void btnWhatsNew_Click(object? sender, EventArgs e)
    {
        using var form = new WhatsNewForm();
        form.ShowDialog(this);
    }

    // Opens the in-app update dialog if an update is available; otherwise re-runs the update check.
    private void btnCheckForUpdates_Click(object? sender, EventArgs e)
    {
        if (_availableUpdate is not null)
            UpdateRequested?.Invoke(_availableUpdate);
        else
        {
            btnCheckForUpdates.Enabled = false;
            _ = LoadGitHubDataAsync(); // fire-and-forget; exceptions are handled inside LoadGitHubDataAsync
        }
    }

    // Each link region carries its contributor profile URL as LinkData
    private static void lnkAuthor_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        if (e.Link?.LinkData is string url && !string.IsNullOrEmpty(url))
            AppConstants.OpenUrl(url);
    }

    private static void lnkGitHub_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        AppConstants.OpenUrl(AppConstants.GitHubRepoUrl);
    }

    // Fetches the latest release info and contributor list in parallel, then populates all UI fields
    private async Task LoadGitHubDataAsync()
    {
        try
        {
            btnCheckForUpdates.Enabled = false;
            btnCheckForUpdates.Text = "Checking\u2026";
            lblLatestVersionValue.Text = "Checking\u2026";
            lblLatestVersionValue.ForeColor = SystemColors.GrayText;
            lblStatusValue.Text = string.Empty;
            _availableUpdate = null;

            // Fetch release info and contributor list in parallel
            var releaseTask = UpdateChecker.GetLatestReleaseInfoAsync(_githubCts.Token);
            var contributorsTask = UpdateChecker.GetReleaseContributorsAsync(_githubCts.Token);
            await Task.WhenAll(releaseTask, contributorsTask);

            // Guard against the form being closed while the GitHub requests were in flight
            if (IsDisposed) return;

            // Await already-completed tasks to unwrap exceptions directly rather than
            // through AggregateException (which .Result throws after WhenAll).
            var contributors = await contributorsTask;
            if (contributors.Count > 0)
                SetContributorLinks(contributors);
            else
                lnkAuthor.Text = AppConstants.GitHubRepoOwner;

            ApplyReleaseInfo(await releaseTask);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"AboutForm.LoadGitHubDataAsync: {ex.Message}");
            // Surface the failure in the labels and reset the button text. ApplyReleaseInfo(null)
            // owns the "check failed" text so the success path can keep its "Update" label intact.
            if (!IsDisposed) ApplyReleaseInfo(null);
        }
        finally
        {
            if (!IsDisposed)
                btnCheckForUpdates.Enabled = true;
        }
    }

    // Populates the release info labels and status based on the latest release data
    private void ApplyReleaseInfo(LatestReleaseInfo? info)
    {
        lblLatestVersionValue.ForeColor = SystemColors.ControlText;
        if (info is null)
        {
            lblLatestVersionValue.Text = "Unable to check";
            lblStatusValue.Text = "Check failed";
            lblStatusValue.ForeColor = SystemColors.ControlText;
            btnCheckForUpdates.Text = "Check for Updates";
            return;
        }
        lblLatestVersionValue.Text = info.Version;
        if (info.IsNewer)
        {
            lblStatusValue.Text = "Update available";
            lblStatusValue.ForeColor = _isDarkMode ? AppConstants.StatusWarning : AppConstants.StatusWarningLight;
            btnCheckForUpdates.Text = "Update";
            _availableUpdate = info;
        }
        else
        {
            lblStatusValue.Text = "Up to date";
            lblStatusValue.ForeColor = _isDarkMode ? AppConstants.StatusOk : AppConstants.StatusOkLight;
            btnCheckForUpdates.Text = "Check for Updates";
        }
    }

    // Populates lnkAuthor with one clickable link region per contributor
    private void SetContributorLinks(IReadOnlyList<ContributorInfo> contributors)
    {
        lnkAuthor.Text = string.Join(", ", contributors.Select(c => c.Login));
        lnkAuthor.Links.Clear();

        int offset = 0;
        foreach (var c in contributors)
        {
            lnkAuthor.Links.Add(offset, c.Login.Length, c.ProfileUrl);
            offset += c.Login.Length + ", ".Length;
        }
    }
}

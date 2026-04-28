namespace qbPortWeaver
{
    /// <summary>About dialog showing version info, update availability, and contributor credits.</summary>
    public partial class AboutForm : Form
    {
        // Set to the release URL when an update is available; null when up-to-date or not yet checked
        private string? _releaseUrl;
        private bool    _isDarkMode;

        public AboutForm()
        {
            InitializeComponent();
            lblAppName.Text             = AppConstants.AppName;
            lblAppVersion.Text          = $"Version {AppConstants.AppVersion}";
            lblCurrentVersionValue.Text = AppConstants.AppVersion;
            lnkGitHub.Text              = $"{AppConstants.GitHubRepoOwner}/{AppConstants.AppName}";
            Text                        = $"{AppConstants.AppName} | About";
        }

        // Kick off the GitHub data fetch as fire-and-forget; the IsDisposed guard in the async method handles early close
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _isDarkMode = AppConstants.IsDarkModeEnabled();
            if (_isDarkMode)
            {
                lnkAuthor.LinkColor = Color.CornflowerBlue;
                lnkGitHub.LinkColor = Color.CornflowerBlue;
            }
            _ = LoadGitHubDataAsync(); // fire-and-forget; exceptions are handled inside LoadGitHubDataAsync
        }

        private void btnClose_Click(object? sender, EventArgs e) => Close();

        private void btnWhatsNew_Click(object? sender, EventArgs e)
        {
            using var form = new WhatsNewForm();
            form.ShowDialog(this);
        }

        // Opens the release page if an update is available; otherwise re-runs the update check
        private void btnCheckForUpdates_Click(object? sender, EventArgs e)
        {
            if (_releaseUrl is not null)
                AppConstants.OpenUrl(_releaseUrl);
            else
            {
                btnCheckForUpdates.Enabled = false;
                _ = LoadGitHubDataAsync(); // fire-and-forget; exceptions are handled inside LoadGitHubDataAsync
            }
        }

        // Each link region carries its contributor profile URL as LinkData
        private void lnkAuthor_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            if (e.Link?.LinkData is string url && !string.IsNullOrEmpty(url))
                AppConstants.OpenUrl(url);
        }

        private void lnkGitHub_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            AppConstants.OpenUrl(AppConstants.GitHubRepoUrl);
        }

        // Fetches the latest release info and contributor list in parallel, then populates all UI fields
        private async Task LoadGitHubDataAsync()
        {
            try
            {
                btnCheckForUpdates.Enabled      = false;
                btnCheckForUpdates.Text         = "Checking\u2026";
                lblLatestVersionValue.Text      = "Checking\u2026";
                lblLatestVersionValue.ForeColor = SystemColors.GrayText;
                lblStatusValue.Text             = "";
                _releaseUrl                     = null;

                // Fetch release info and contributor list in parallel
                var releaseTask      = UpdateChecker.GetLatestReleaseInfoAsync();
                var contributorsTask = UpdateChecker.GetReleaseContributorsAsync();
                await Task.WhenAll(releaseTask, contributorsTask);

                // Guard against the form being closed while the GitHub requests were in flight
                if (IsDisposed) return;

                var contributors = contributorsTask.Result;
                if (contributors.Count > 0)
                    SetContributorLinks(contributors);
                else
                    lnkAuthor.Text = AppConstants.GitHubRepoOwner;

                var info = releaseTask.Result;
                lblLatestVersionValue.ForeColor = SystemColors.ControlText;
                if (info is null)
                {
                    lblLatestVersionValue.Text = "Unable to check";
                    lblStatusValue.Text        = "Check failed";
                    lblStatusValue.ForeColor   = SystemColors.ControlText;
                    btnCheckForUpdates.Text    = "Check for Updates";
                }
                else
                {
                    lblLatestVersionValue.Text = info.Version;

                    if (info.IsNewer)
                    {
                        lblStatusValue.Text      = "Update available";
                        lblStatusValue.ForeColor = _isDarkMode ? Color.Orange : Color.DarkOrange;
                        btnCheckForUpdates.Text  = "Update";
                        _releaseUrl              = info.ReleaseUrl;
                    }
                    else
                    {
                        lblStatusValue.Text      = "Up to date";
                        lblStatusValue.ForeColor = _isDarkMode ? Color.LimeGreen : Color.Green;
                        btnCheckForUpdates.Text  = "Check for Updates";
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"AboutForm.LoadGitHubDataAsync: {ex.Message}");
            }
            finally
            {
                if (!IsDisposed)
                {
                    btnCheckForUpdates.Enabled = true;
                    btnCheckForUpdates.Text    = "Check for Updates";
                }
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
}

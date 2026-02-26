using System.Diagnostics;

namespace qbPortWeaver
{
    public partial class frmAbout : Form
    {
        // Set to the release URL when an update is available; null when up-to-date or not yet checked
        private string? _releaseUrl;

        public frmAbout()
        {
            InitializeComponent();
            lblAppVersion.Text         = $"Version {AppConstants.APP_VERSION}";
            lblCurrentVersionValue.Text = AppConstants.APP_VERSION;
            lnkGitHub.Text             = $"{AppConstants.GITHUB_REPO_OWNER}/{AppConstants.APP_NAME}";
            Text                       = $"{AppConstants.APP_NAME} | About";
        }

        // Kick off the GitHub data fetch as fire-and-forget; the IsDisposed guard in the async method handles early close
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _ = LoadGitHubDataAsync().ContinueWith(
                t => LogManager.Instance.LogDebug($"frmAbout.LoadGitHubDataAsync: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // Fetches the latest release info and contributor list in parallel, then populates all UI fields
        private async Task LoadGitHubDataAsync()
        {
            btnCheckForUpdates.Enabled      = false;
            btnCheckForUpdates.Text         = "Checking\u2026";
            lblLatestVersionValue.Text      = "Checking\u2026";
            lblLatestVersionValue.ForeColor = SystemColors.GrayText;
            lblStatusValue.Text             = "";
            _releaseUrl                     = null;

            // Fetch release info and release commit authors in parallel
            var releaseTask      = UpdateChecker.GetLatestReleaseInfoAsync();
            var contributorsTask = UpdateChecker.GetReleaseContributorsAsync();
            await Task.WhenAll(releaseTask, contributorsTask);

            // Guard against the form being closed while the GitHub requests were in flight
            if (IsDisposed) return;

            // ── Contributors ─────────────────────────────────────────────
            var contributors = contributorsTask.Result;
            if (contributors.Count > 0)
                SetContributorLinks(contributors);
            else
                lnkAuthor.Text = AppConstants.GITHUB_REPO_OWNER;

            // ── Version / update status ───────────────────────────────────
            var info = releaseTask.Result;
            if (info == null)
            {
                lblLatestVersionValue.Text      = "Unable to check";
                lblLatestVersionValue.ForeColor = SystemColors.ControlText;
                lblStatusValue.Text             = "Check failed";
                lblStatusValue.ForeColor        = SystemColors.ControlText;
                btnCheckForUpdates.Text         = "Check for Updates";
            }
            else
            {
                lblLatestVersionValue.Text      = info.TagName;
                lblLatestVersionValue.ForeColor = SystemColors.ControlText;

                if (info.IsNewer)
                {
                    lblStatusValue.Text      = "Update available";
                    lblStatusValue.ForeColor = Color.DarkOrange;
                    btnCheckForUpdates.Text  = "View Release";
                    _releaseUrl              = info.ReleaseUrl;
                }
                else
                {
                    lblStatusValue.Text      = "Up to date";
                    lblStatusValue.ForeColor = Color.Green;
                    btnCheckForUpdates.Text  = "Check for Updates";
                }
            }

            btnCheckForUpdates.Enabled = true;
        }

        // Populates lnkAuthor with one clickable link region per contributor
        private void SetContributorLinks(IReadOnlyList<UpdateChecker.ContributorInfo> contributors)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < contributors.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(contributors[i].Login);
            }

            lnkAuthor.Text = sb.ToString();
            lnkAuthor.Links.Clear();

            int offset = 0;
            foreach (var c in contributors)
            {
                lnkAuthor.Links.Add(offset, c.Login.Length, c.ProfileUrl);
                offset += c.Login.Length + 2; // +2 for ", "
            }
        }

        // Opens the release page if an update is available; otherwise re-runs the update check
        private void btnCheckForUpdates_Click(object? sender, EventArgs e)
        {
            if (_releaseUrl != null)
                OpenUrl(_releaseUrl);
            else
                _ = LoadGitHubDataAsync();
        }

        // Each link region carries its contributor profile URL as LinkData
        private void lnkAuthor_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            if (e.Link?.LinkData is string url && !string.IsNullOrEmpty(url))
                OpenUrl(url);
        }

        // Opens the project repository in the default browser
        private void lnkGitHub_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            OpenUrl(AppConstants.GITHUB_REPO_URL);
        }

        // Opens a URL in the default browser; UseShellExecute is required for shell-handled URLs
        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"frmAbout.OpenUrl: {ex.Message}");
            }
        }
    }
}

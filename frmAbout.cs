using System.Diagnostics;

namespace qbPortWeaver
{
    public partial class frmAbout : Form
    {
        private string? _releaseUrl;

        public frmAbout()
        {
            InitializeComponent();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _ = LoadGitHubDataAsync();
        }

        private async Task LoadGitHubDataAsync()
        {
            btnCheckForUpdates.Enabled      = false;
            btnCheckForUpdates.Text         = "Checking\u2026";
            lblLatestVersionValue.Text      = "Checking\u2026";
            lblLatestVersionValue.ForeColor = SystemColors.GrayText;
            lblStatusValue.Text             = "";
            _releaseUrl                     = null;

            // Fetch release info and release commit authors in parallel
            var releaseTask      = UpdateChecker.GetLatestReleaseInfoAsync(AppConstants.APP_VERSION);
            var contributorsTask = UpdateChecker.GetReleaseContributorsAsync();
            await Task.WhenAll(releaseTask, contributorsTask);

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
        private void SetContributorLinks(IReadOnlyList<ContributorInfo> contributors)
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

        private void btnCheckForUpdates_Click(object? sender, EventArgs e)
        {
            if (_releaseUrl != null)
                OpenUrl(_releaseUrl);
            else
                _ = LoadGitHubDataAsync();
        }

        private void lnkAuthor_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            if (e.Link.LinkData is string url && !string.IsNullOrEmpty(url))
                OpenUrl(url);
        }

        private void lnkGitHub_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            OpenUrl(AppConstants.GITHUB_REPO_URL);
        }

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

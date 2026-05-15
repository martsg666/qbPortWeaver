using System.Net.Http.Headers;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Latest release metadata from GitHub, including whether it is newer than the running version.</summary>
    /// <param name="TagName">Git tag name (e.g. "v2.1.0").</param>
    /// <param name="ReleaseUrl">URL of the GitHub release page.</param>
    /// <param name="IsNewer">True when the release version is greater than <see cref="AppConstants.AppVersion"/>.</param>
    public sealed record LatestReleaseInfo(string TagName, string ReleaseUrl, bool IsNewer)
    {
        /// <summary>Tag name with the leading 'v'/'V' stripped (e.g. "v2.1.0" becomes "2.1.0").</summary>
        public string Version => TagName.TrimStart('v', 'V');
    }

    /// <summary>A GitHub contributor's login and profile URL.</summary>
    /// <param name="Login">GitHub username.</param>
    /// <param name="ProfileUrl">URL of the contributor's GitHub profile.</param>
    public sealed record ContributorInfo(string Login, string ProfileUrl);

    /// <summary>Queries the GitHub API for release and contributor information.</summary>
    public static class UpdateChecker
    {
        private const string JsonPropTagName = "tag_name";
        private const string JsonPropHtmlUrl = "html_url";

        private static readonly string _gitHubBaseApiUrl = $"https://api.github.com/repos/{AppConstants.GitHubRepoOwner}/{AppConstants.AppName}";
        private static readonly string _gitHubApiUrl     = _gitHubBaseApiUrl + "/releases/latest";

        private static readonly HttpClient _httpClient = CreateHttpClient(); // Not disposed - static lifetime matches process lifetime (recommended pattern for HttpClient)

        /// <summary>Returns the latest release version and URL if a newer version exists; null if up-to-date or on any error.</summary>
        public static async Task<(string Version, string Url)?> GetAvailableUpdateAsync(CancellationToken cancellationToken = default)
        {
            var info = await GetLatestReleaseInfoAsync(cancellationToken).ConfigureAwait(false);
            return info?.IsNewer == true ? (info.Version, info.ReleaseUrl) : null;
        }

        /// <summary>Returns full release info from GitHub including whether a newer version exists; null on any error.</summary>
        public static async Task<LatestReleaseInfo?> GetLatestReleaseInfoAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync(_gitHubApiUrl, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = doc.RootElement;

                if (!root.TryGetProperty(JsonPropTagName, out var tagElement) ||
                    !root.TryGetProperty(JsonPropHtmlUrl, out var urlElement))
                    return null;

                string tagName    = tagElement.GetString() ?? "";
                string releaseUrl = urlElement.GetString() ?? "";
                string versionStr = tagName.TrimStart('v', 'V');

                bool isNewer = Version.TryParse(versionStr, out var latest) &&
                               Version.TryParse(AppConstants.AppVersion, out var current) &&
                               latest > current;

                return new LatestReleaseInfo(tagName, releaseUrl, isNewer);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null; // shutdown - not a real failure, suppress log noise
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetLatestReleaseInfoAsync: {ex.Message}");
                return null;
            }
        }

        /// <summary>Returns all unique human contributors to the repo, with the owner listed first. Bots are excluded. Returns an empty list on any error.</summary>
        public static async Task<IReadOnlyList<ContributorInfo>> GetReleaseContributorsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync(_gitHubBaseApiUrl + "/contributors?per_page=100", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                var seen         = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var contributors = new List<ContributorInfo>();

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string login = item.TryGetProperty("login",          out var loginEl) ? loginEl.GetString() ?? "" : "";
                    string url   = item.TryGetProperty(JsonPropHtmlUrl,  out var urlEl)   ? urlEl.GetString()   ?? "" : "";
                    string type  = item.TryGetProperty("type",           out var typeEl)  ? typeEl.GetString()  ?? "" : "";

                    if (string.IsNullOrEmpty(login)) continue;
                    if (IsBot(login, type)) continue;
                    if (!seen.Add(login)) continue;

                    contributors.Add(new ContributorInfo(login, url));
                }

                // Always list the repo owner first
                int ownerIndex = contributors.FindIndex(c => c.Login.Equals(AppConstants.GitHubRepoOwner, StringComparison.OrdinalIgnoreCase));
                if (ownerIndex > 0)
                {
                    var owner = contributors[ownerIndex];
                    contributors.RemoveAt(ownerIndex);
                    contributors.Insert(0, owner);
                }

                return contributors;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return []; // shutdown - not a real failure, suppress log noise
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetReleaseContributorsAsync: {ex.Message}");
                return [];
            }
        }

        // Creates the shared HttpClient pre-configured with the required User-Agent, timeout,
        // and GitHub API headers (Accept media type and pinned API version) so future GitHub
        // default changes do not silently alter the response shape.
        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppConstants.AppName, AppConstants.AppVersion));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        private static bool IsBot(string login, string type) =>
            type.Equals("Bot", StringComparison.OrdinalIgnoreCase) ||
            login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);
    }
}

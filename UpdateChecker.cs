using System.Net.Http.Headers;
using System.Text.Json;

namespace qbPortWeaver
{
    public record LatestReleaseInfo(string TagName, string ReleaseUrl, string AuthorLogin, string AuthorUrl, bool IsNewer);
    public record ContributorInfo(string Login, string ProfileUrl);

    public static class UpdateChecker
    {
        private static readonly string GITHUB_BASE_API_URL = $"https://api.github.com/repos/{AppConstants.GITHUB_REPO_OWNER}/{AppConstants.APP_NAME}";
        private static readonly string GITHUB_API_URL      = GITHUB_BASE_API_URL + "/releases/latest";
        private static readonly string JSON_HTML_URL_ELEMENT = "html_url";
        private const int HTTP_TIMEOUT_SECONDS = 10;

        // Returns unique human commit authors for the latest release by comparing its tag against the
        // previous release tag (falls back to the last 100 commits on the tag if only one release exists).
        // Bots are excluded. Returns an empty list on any error.
        public static async Task<IReadOnlyList<ContributorInfo>> GetReleaseContributorsAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppConstants.APP_NAME, AppConstants.APP_VERSION));
                client.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);

                // Fetch the two most recent releases to determine the comparison range
                using var relResponse = await client.GetAsync(GITHUB_BASE_API_URL + "/releases?per_page=2");
                relResponse.EnsureSuccessStatusCode();

                using var relStream = await relResponse.Content.ReadAsStreamAsync();
                using var relDoc    = await JsonDocument.ParseAsync(relStream);
                var releases = relDoc.RootElement.EnumerateArray().ToList();

                if (releases.Count == 0) return Array.Empty<ContributorInfo>();

                string latestTag = releases[0].TryGetProperty("tag_name", out var t0) ? t0.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(latestTag)) return Array.Empty<ContributorInfo>();

                // Compare current tag against previous release tag; fall back to raw commit list
                string commitsUrl;
                if (releases.Count >= 2)
                {
                    string prevTag = releases[1].TryGetProperty("tag_name", out var t1) ? t1.GetString() ?? "" : "";
                    commitsUrl = !string.IsNullOrEmpty(prevTag)
                        ? $"{GITHUB_BASE_API_URL}/compare/{prevTag}...{latestTag}"
                        : $"{GITHUB_BASE_API_URL}/commits?sha={latestTag}&per_page=100";
                }
                else
                {
                    commitsUrl = $"{GITHUB_BASE_API_URL}/commits?sha={latestTag}&per_page=100";
                }

                using var cmpResponse = await client.GetAsync(commitsUrl);
                cmpResponse.EnsureSuccessStatusCode();

                using var cmpStream = await cmpResponse.Content.ReadAsStreamAsync();
                using var cmpDoc    = await JsonDocument.ParseAsync(cmpStream);

                // Compare API returns { commits: [...] }; commits list API returns [...]
                IEnumerable<JsonElement> commitsArray;
                if (cmpDoc.RootElement.ValueKind == JsonValueKind.Array)
                    commitsArray = cmpDoc.RootElement.EnumerateArray();
                else if (cmpDoc.RootElement.TryGetProperty("commits", out var commitsEl))
                    commitsArray = commitsEl.EnumerateArray();
                else
                    commitsArray = [];

                var seen         = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var contributors = new List<ContributorInfo>();

                foreach (var commit in commitsArray)
                {
                    // Top-level "author" is the GitHub user object (has login); skip unlinked git authors
                    if (!commit.TryGetProperty("author", out var authorEl) || authorEl.ValueKind == JsonValueKind.Null)
                        continue;

                    string login = authorEl.TryGetProperty("login",    out var l) ? l.GetString() ?? "" : "";
                    string url   = authorEl.TryGetProperty(JSON_HTML_URL_ELEMENT, out var u) ? u.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(login)) continue;
                    if (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(login)) continue;

                    contributors.Add(new ContributorInfo(login, url));
                }

                return contributors;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetReleaseContributorsAsync: {ex.Message}");
                return Array.Empty<ContributorInfo>();
            }
        }

        // Returns full release info from GitHub including author and whether a newer version exists; null on any error
        public static async Task<LatestReleaseInfo?> GetLatestReleaseInfoAsync(string currentVersion)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppConstants.APP_NAME, currentVersion));
                client.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);

                using var response = await client.GetAsync(GITHUB_API_URL);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagElement) ||
                    !root.TryGetProperty(JSON_HTML_URL_ELEMENT, out var urlElement))
                    return null;

                string tagName = tagElement.GetString() ?? "";
                string htmlUrl = urlElement.GetString() ?? "";

                string authorLogin = "";
                string authorUrl   = "";
                if (root.TryGetProperty("author", out var authorEl))
                {
                    authorLogin = authorEl.TryGetProperty("login",    out var loginEl) ? loginEl.GetString() ?? "" : "";
                    authorUrl   = authorEl.TryGetProperty(JSON_HTML_URL_ELEMENT, out var aUrlEl)  ? aUrlEl.GetString()  ?? "" : "";
                }

                string versionString = tagName.TrimStart('v', 'V');
                bool isNewer = Version.TryParse(versionString, out var latest) &&
                               Version.TryParse(currentVersion, out var current) &&
                               latest > current;

                return new LatestReleaseInfo(tagName, htmlUrl, authorLogin, authorUrl, isNewer);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetLatestReleaseInfoAsync: {ex.Message}");
                return null;
            }
        }

        // Returns the latest release tag and URL if a newer version exists on GitHub; null if up-to-date or on any error
        public static async Task<(string Version, string Url)?> CheckForUpdateAsync(string currentVersion)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppConstants.APP_NAME, currentVersion));
                client.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);

                using var response = await client.GetAsync(GITHUB_API_URL);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagElement) ||
                    !root.TryGetProperty(JSON_HTML_URL_ELEMENT, out var urlElement))
                    return null;

                string tagName = tagElement.GetString() ?? "";
                string htmlUrl = urlElement.GetString() ?? "";

                // Strip leading 'v' or 'V' if present
                string versionString = tagName.TrimStart('v', 'V');

                if (Version.TryParse(versionString, out var latestVersion) &&
                    Version.TryParse(currentVersion, out var current) &&
                    latestVersion > current)
                {
                    return (tagName, htmlUrl);
                }

                return null;
            }
            catch (Exception ex)
            {
                // Update check is best-effort — log but do not propagate (no network, API down, etc.)
                LogManager.Instance.LogDebug($"UpdateChecker.CheckForUpdateAsync: {ex.Message}");
                return null;
            }
        }
    }
}

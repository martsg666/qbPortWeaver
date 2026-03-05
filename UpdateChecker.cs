using System.Net.Http.Headers;
using System.Text.Json;

namespace qbPortWeaver
{
    public sealed record LatestReleaseInfo(string TagName, string ReleaseUrl, bool IsNewer)
    {
        // TagName with leading 'v'/'V' stripped (e.g. "v2.1.0" → "2.1.0")
        public string VersionString => TagName.TrimStart('v', 'V');
    }

    public sealed record ContributorInfo(string Login, string ProfileUrl);

    public static class UpdateChecker
    {
        private const string JsonPropTagName = "tag_name";
        private const string JsonPropHtmlUrl = "html_url";

        private static readonly string GitHubBaseApiUrl = $"https://api.github.com/repos/{AppConstants.GitHubRepoOwner}/{AppConstants.AppName}";
        private static readonly string GitHubApiUrl     = GitHubBaseApiUrl + "/releases/latest";

        private static readonly HttpClient _httpClient = CreateHttpClient();

        // Returns the latest release version string and URL if a newer version exists; null if up-to-date or on any error
        public static async Task<(string Version, string Url)?> GetAvailableUpdateAsync()
        {
            var info = await GetLatestReleaseInfoAsync();
            return info?.IsNewer == true ? (info.VersionString, info.ReleaseUrl) : null;
        }

        // Returns full release info from GitHub including whether a newer version exists; null on any error
        public static async Task<LatestReleaseInfo?> GetLatestReleaseInfoAsync()
        {
            try
            {
                using var response = await _httpClient.GetAsync(GitHubApiUrl).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc    = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                var root = doc.RootElement;

                if (!root.TryGetProperty(JsonPropTagName, out var tagElement) ||
                    !root.TryGetProperty(JsonPropHtmlUrl, out var urlElement))
                    return null;

                string tagName = tagElement.GetString() ?? "";
                string htmlUrl = urlElement.GetString() ?? "";

                // Strip leading 'v' or 'V' from the tag (e.g. "v2.1.0" → "2.1.0") before parsing
                string versionString = tagName.TrimStart('v', 'V');
                bool isNewer = Version.TryParse(versionString, out var latest) &&
                               Version.TryParse(AppConstants.AppVersion, out var current) &&
                               latest > current;

                return new LatestReleaseInfo(tagName, htmlUrl, isNewer);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetLatestReleaseInfoAsync: {ex.Message}");
                return null;
            }
        }

        // Returns all unique human contributors to the repo. Bots are excluded.
        // Returns an empty list on any error.
        public static async Task<IReadOnlyList<ContributorInfo>> GetReleaseContributorsAsync()
        {
            try
            {
                using var response = await _httpClient.GetAsync(GitHubBaseApiUrl + "/contributors?per_page=100").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc    = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

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
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetReleaseContributorsAsync: {ex.Message}");
                return [];
            }
        }

        private static bool IsBot(string login, string type) =>
            type.Equals("Bot", StringComparison.OrdinalIgnoreCase) ||
            login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

        // Creates the shared HttpClient pre-configured with the required User-Agent and timeout
        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppConstants.AppName, AppConstants.AppVersion));
            return client;
        }
    }
}

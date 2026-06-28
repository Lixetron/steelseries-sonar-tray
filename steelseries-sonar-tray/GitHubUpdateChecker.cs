using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonarQuickMixer;

public sealed class GitHubUpdateChecker : IDisposable
{
    private const string Owner = "lixetron";
    private const string Repo = "steelseries-sonar-tray";

    private static readonly Uri LatestReleaseApiUri =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public GitHubUpdateChecker()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SonarQuickMixer-UpdateChecker");
    }

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(LatestReleaseApiUri, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var release = await JsonSerializer
                .DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (release?.TagName is null)
            {
                return null;
            }

            var currentVersion = AppVersion.Current;
            var latestVersion = ParseVersionTag(release.TagName);
            if (latestVersion is null || latestVersion <= currentVersion)
            {
                return null;
            }

            return new UpdateCheckResult(
                IsUpdateAvailable: true,
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                ReleaseUrl: release.HtmlUrl ?? DefaultReleaseUrl);
        }
        catch
        {
            return null;
        }
    }

    private static Version? ParseVersionTag(string tag)
    {
        var trimmed = tag.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    private static string DefaultReleaseUrl =>
        $"https://github.com/{Owner}/{Repo}/releases/latest";

    public void Dispose() => _httpClient.Dispose();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version LatestVersion,
    string ReleaseUrl);

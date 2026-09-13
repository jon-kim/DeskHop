using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DeskHop;

internal sealed class UpdateChecker
{
    private static readonly HttpClient HttpClient = new();

    private readonly string _owner;
    private readonly string _repository;

    public UpdateChecker(string owner, string repository)
    {
        _owner = owner;
        _repository = repository;
    }

    public async Task<UpdateInfo?> GetAvailableUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        var requestUri = $"https://api.github.com/repos/{_owner}/{_repository}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("DeskHop-Updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName) || string.IsNullOrWhiteSpace(release.HtmlUrl))
        {
            return null;
        }

        if (!TryParseVersion(release.TagName, out var latestVersion))
        {
            return null;
        }

        if (latestVersion <= currentVersion)
        {
            return null;
        }

        return new UpdateInfo(latestVersion, release.TagName, release.HtmlUrl);
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var normalized = tag.Trim();

        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var prereleaseSeparator = normalized.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            normalized = normalized[..prereleaseSeparator];
        }

        return Version.TryParse(normalized, out version!);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

internal sealed record UpdateInfo(Version Version, string TagName, string ReleaseUrl);

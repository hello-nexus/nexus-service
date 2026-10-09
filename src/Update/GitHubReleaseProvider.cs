using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Update;

/// <summary>OS a release installer asset targets. Drives <see cref="GitHubReleaseProvider.SelectInstallerAsset"/>.</summary>
public enum RuntimePlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// <see cref="IUpdateSource"/> backed by GitHub Releases. The only file in the
/// codebase that is aware of GitHub's REST shapes or snake_case DTOs.
///
/// Production channel: GET /repos/{owner}/{repo}/releases/latest (excludes
/// prereleases by design). Beta channel: GET /repos/{owner}/{repo}/releases
/// and picks the highest-semver non-draft prerelease that carries this
/// platform's installer asset; stables are never offered to a beta install.
///
/// SHA-256: prefers the "SHA256SUMS" release asset; falls back to the
/// asset-level "digest" field ("sha256:{hex}") if SHA256SUMS is absent.
/// </summary>
public sealed class GitHubReleaseProvider : IUpdateSource
{
    // Releases are published to the public hello-nexus/nexus repo. Configurable
    // so a test repo or provider swap is a one-line change.
    public const string DefaultOwnerRepo = "hello-nexus/nexus";

    private readonly IHttpClientFactory _http;
    private readonly string _apiBase;

    /// <param name="apiBase">Releases API root without a trailing slash. Null uses the
    /// build-time <c>NexusUpdateApiBase</c> when set, else the public GitHub repo.</param>
    public GitHubReleaseProvider(IHttpClientFactory http, string ownerRepo = DefaultOwnerRepo, string? apiBase = null)
    {
        _http = http;
        _apiBase = (string.IsNullOrEmpty(apiBase) ? DefaultApiBase(ownerRepo) : apiBase).TrimEnd('/');
    }

    private static string DefaultApiBase(string ownerRepo) =>
        BuildInfo.UpdateApiBase.Length > 0 ? BuildInfo.UpdateApiBase : $"https://api.github.com/repos/{ownerRepo}";

    public async Task<UpdateManifest?> GetLatestAsync(string channel, CancellationToken ct)
    {
        using var client = BuildClient();

        GitHubRelease? release = channel == "beta"
            ? await GetNewestBetaAsync(client, ct)
            : await GetLatestProductionAsync(client, ct);

        if (release is null) return null;

        var asset = SelectInstallerAsset(release.Assets, CurrentRuntimePlatform());
        if (asset is null) return null;

        var (sha256, fromSumsFile) = await ResolveHashAsync(client, release, asset, ct);

        return new UpdateManifest
        {
            Version = release.TagName ?? "",
            Notes = release.Body ?? "",
            AssetUrl = asset.BrowserDownloadUrl ?? "",
            Sha256 = sha256,
            Sha256IsFromSumsFile = fromSumsFile,
            AssetSize = asset.Size,
            PublishedAt = release.PublishedAt,
        };
    }

    private async Task<GitHubRelease?> GetLatestProductionAsync(HttpClient client, CancellationToken ct)
    {
        var url = $"{_apiBase}/releases/latest";
        using var resp = await client.GetAsync(url, ct);
        // 404: the repo has no stable release yet.
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        // Any other refusal (a rate limit, an outage) is a failed check, never "nothing newer".
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.GitHubRelease, ct);
    }

    private async Task<GitHubRelease?> GetNewestBetaAsync(HttpClient client, CancellationToken ct)
    {
        var url = $"{_apiBase}/releases?per_page=100";
        using var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var releases = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.ListGitHubRelease, ct);
        return SelectNewestBeta(releases, CurrentRuntimePlatform());
    }

    /// <summary>
    /// Highest-semver published prerelease with an installer for <paramref name="platform"/>.
    /// Publish date is ignored so a re-published older tag cannot win; drafts,
    /// stables, unparseable tags and releases without this platform's asset are skipped.
    /// </summary>
    internal static GitHubRelease? SelectNewestBeta(IEnumerable<GitHubRelease>? releases, RuntimePlatform platform)
    {
        GitHubRelease? best = null;
        foreach (var r in releases ?? Enumerable.Empty<GitHubRelease>())
        {
            if (!r.Prerelease || r.Draft || string.IsNullOrEmpty(r.TagName))
            {
                continue;
            }

            if (SelectInstallerAsset(r.Assets, platform) is null)
            {
                continue;
            }

            if (!VersionCompare.TryParseSemver(r.TagName, out _))
            {
                continue;
            }

            if (best is null || VersionCompare.IsNewer(r.TagName, best.TagName!))
            {
                best = r;
            }
        }
        return best;
    }

    private static async Task<(string? Hash, bool FromSumsFile)> ResolveHashAsync(
        HttpClient client,
        GitHubRelease release,
        GitHubReleaseAsset asset,
        CancellationToken ct)
    {
        var sumsAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase));

        if (sumsAsset is not null && !string.IsNullOrEmpty(sumsAsset.BrowserDownloadUrl))
        {
            try
            {
                var text = await client.GetStringAsync(sumsAsset.BrowserDownloadUrl, ct);
                var hash = ParseSha256Sums(text, asset.Name ?? "");
                if (!string.IsNullOrEmpty(hash))
                {
                    return (hash, true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update] SHA256SUMS fetch failed: {ex.Message}");
            }
        }

        // Fallback: asset digest. Usable for manual installs, not for auto-stage.
        if (!string.IsNullOrEmpty(asset.Digest) &&
            asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return (asset.Digest["sha256:".Length..].ToLowerInvariant(), false);
        }

        return (null, false);
    }

    /// <summary>
    /// Selects the installer asset for the given platform: Windows via a
    /// Nexus-Setup prefix match (resolves both the versioned and legacy bare
    /// name), macOS via .dmg, Linux via a name containing "linux" ending .tar.gz.
    /// </summary>
    public static GitHubReleaseAsset? SelectInstallerAsset(
        IEnumerable<GitHubReleaseAsset> assets,
        RuntimePlatform platform) =>
        platform switch
        {
            RuntimePlatform.Windows => assets.FirstOrDefault(a =>
                a.Name is not null
                && a.Name.StartsWith("Nexus-Setup", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
            RuntimePlatform.MacOS => assets.FirstOrDefault(a =>
                a.Name is not null
                && a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)),
            RuntimePlatform.Linux => assets.FirstOrDefault(a =>
                a.Name is not null
                && a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };

    private static RuntimePlatform CurrentRuntimePlatform() =>
        OperatingSystem.IsWindows() ? RuntimePlatform.Windows :
        OperatingSystem.IsMacOS() ? RuntimePlatform.MacOS :
        RuntimePlatform.Linux;

    /// <summary>
    /// Parses a SHA256SUMS file of the form "{hash}  {filename}" per line.
    /// Returns the hash for the given filename, or null if not found.
    /// </summary>
    public static string? ParseSha256Sums(string content, string filename)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: "<hash>  <filename>" or "<hash> <filename>" (one or two spaces).
            var trimmed = line.Trim();
            if (trimmed.Length < 66) continue;
            var hash = trimmed[..64];
            var rest = trimmed[64..].TrimStart();
            // sha256sum output marks a binary-mode entry with a leading '*' and
            // may list the file with a './' path prefix (Ollama's sums do both
            // forms); neither is part of the asset name.
            if (rest.StartsWith('*')) rest = rest[1..];
            if (rest.StartsWith("./", StringComparison.Ordinal)) rest = rest[2..];
            if (string.Equals(rest, filename, StringComparison.OrdinalIgnoreCase))
            {
                return hash.ToLowerInvariant();
            }
        }
        return null;
    }

    private HttpClient BuildClient()
    {
        var client = _http.CreateClient("GitHubOta");
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("Nexus-Service/" + BuildInfo.Version);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}

// GitHub REST DTOs. snake_case properties must carry explicit [JsonPropertyName]
// attributes because the global AOT context policy is camelCase.

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

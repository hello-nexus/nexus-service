using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class ChannelSwitchTests
{
    private const string Hash64 = "abc123def456abc123def456abc123def456abc123def456abc123def4560000";
    private const string ApiBase = "http://127.0.0.1:8790/repos/test/nexus";

    // Release selection

    private static GitHubRelease Release(string tag, bool prerelease = true, bool draft = false, bool installer = true, DateTimeOffset? published = null)
    {
        var r = new GitHubRelease
        {
            TagName = tag,
            Prerelease = prerelease,
            Draft = draft,
            PublishedAt = published ?? DateTimeOffset.UnixEpoch,
        };
        if (installer)
        {
            r.Assets.Add(new GitHubReleaseAsset { Name = $"Nexus-Setup-{tag}.exe" });
            r.Assets.Add(new GitHubReleaseAsset { Name = $"Nexus-{tag}.dmg" });
            r.Assets.Add(new GitHubReleaseAsset { Name = $"Nexus-Linux-x64-{tag}.tar.gz" });
        }
        r.Assets.Add(new GitHubReleaseAsset { Name = "SHA256SUMS" });
        return r;
    }

    [Theory]
    [InlineData(RuntimePlatform.Windows)]
    [InlineData(RuntimePlatform.MacOS)]
    [InlineData(RuntimePlatform.Linux)]
    public void SelectNewestBeta_picks_highest_semver_not_latest_published(RuntimePlatform platform)
    {
        var releases = new List<GitHubRelease>
        {
            Release("v3.1.0-beta.2", published: DateTimeOffset.UnixEpoch.AddDays(1)),
            Release("v3.1.0-beta.10", published: DateTimeOffset.UnixEpoch.AddDays(2)),
            Release("v3.1.0-beta.9", published: DateTimeOffset.UnixEpoch.AddDays(30)),
        };
        Assert.Equal("v3.1.0-beta.10", GitHubReleaseProvider.SelectNewestBeta(releases, platform)?.TagName);
    }

    [Fact]
    public void SelectNewestBeta_ignores_stables_even_when_newer()
    {
        var releases = new List<GitHubRelease>
        {
            Release("v3.1.0", prerelease: false, published: DateTimeOffset.UnixEpoch.AddDays(9)),
            Release("v3.1.0-beta.1"),
        };
        Assert.Equal("v3.1.0-beta.1", GitHubReleaseProvider.SelectNewestBeta(releases, RuntimePlatform.Windows)?.TagName);
    }

    [Fact]
    public void SelectNewestBeta_ignores_drafts()
    {
        var releases = new List<GitHubRelease> { Release("v3.2.0-beta.1", draft: true), Release("v3.1.0-beta.1") };
        Assert.Equal("v3.1.0-beta.1", GitHubReleaseProvider.SelectNewestBeta(releases, RuntimePlatform.Windows)?.TagName);
    }

    [Fact]
    public void SelectNewestBeta_skips_newer_release_without_this_platforms_installer()
    {
        var releases = new List<GitHubRelease> { Release("v3.2.0-beta.1", installer: false), Release("v3.1.0-beta.1") };
        Assert.Equal("v3.1.0-beta.1", GitHubReleaseProvider.SelectNewestBeta(releases, RuntimePlatform.MacOS)?.TagName);
    }

    [Fact]
    public void SelectNewestBeta_skips_unparseable_tags()
    {
        var releases = new List<GitHubRelease> { Release("nightly-prerelease"), Release("v63"), Release("v3.1.0-beta.1") };
        Assert.Equal("v3.1.0-beta.1", GitHubReleaseProvider.SelectNewestBeta(releases, RuntimePlatform.Windows)?.TagName);
    }

    [Fact]
    public void SelectNewestBeta_none_returns_null()
    {
        Assert.Null(GitHubReleaseProvider.SelectNewestBeta(new List<GitHubRelease> { Release("v3.1.0", prerelease: false) }, RuntimePlatform.Windows));
        Assert.Null(GitHubReleaseProvider.SelectNewestBeta(null, RuntimePlatform.Windows));
    }

    // Provider against a stubbed API base

    [Fact]
    public async Task GetLatestAsync_beta_reads_the_overridden_base_and_picks_highest_prerelease()
    {
        var handler = new RoutingHandler();
        handler.Json("/releases?per_page=100", """
            [
              {"tag_name":"v3.1.0","prerelease":false,"draft":false,"published_at":"2026-10-09T00:00:00Z","assets":[]},
              {"tag_name":"v3.1.0-beta.2","prerelease":true,"draft":false,"published_at":"2026-10-01T00:00:00Z","body":"notes","assets":[
                {"name":"Nexus-Setup-v3.1.0-beta.2.exe","browser_download_url":"http://127.0.0.1:8790/dl/a.exe","size":10},
                {"name":"Nexus-v3.1.0-beta.2.dmg","browser_download_url":"http://127.0.0.1:8790/dl/a.dmg","size":10},
                {"name":"Nexus-Linux-x64-v3.1.0-beta.2.tar.gz","browser_download_url":"http://127.0.0.1:8790/dl/a.tar.gz","size":10},
                {"name":"SHA256SUMS","browser_download_url":"http://127.0.0.1:8790/dl/SHA256SUMS","size":1}]},
              {"tag_name":"v3.1.0-beta.3","prerelease":true,"draft":true,"published_at":"2026-10-08T00:00:00Z","assets":[]}
            ]
            """);
        handler.Text("/dl/SHA256SUMS",
            $"{Hash64}  Nexus-Setup-v3.1.0-beta.2.exe\n{Hash64}  Nexus-v3.1.0-beta.2.dmg\n{Hash64}  Nexus-Linux-x64-v3.1.0-beta.2.tar.gz\n");
        var provider = new GitHubReleaseProvider(new HandlerFactory(handler), apiBase: ApiBase + "/");

        var manifest = await provider.GetLatestAsync("beta", CancellationToken.None);

        Assert.Equal("v3.1.0-beta.2", manifest?.Version);
        Assert.Equal(Hash64, manifest?.Sha256);
        Assert.True(manifest?.Sha256IsFromSumsFile);
        Assert.Contains(handler.Requested, u => u == ApiBase + "/releases?per_page=100");
    }

    [Fact]
    public async Task GetLatestAsync_production_uses_latest_endpoint_and_404_is_null()
    {
        var handler = new RoutingHandler();
        var provider = new GitHubReleaseProvider(new HandlerFactory(handler), apiBase: ApiBase);

        Assert.Null(await provider.GetLatestAsync("production", CancellationToken.None));
        Assert.Equal(ApiBase + "/releases/latest", Assert.Single(handler.Requested));
    }

    // Direction

    [Theory]
    [InlineData("v3.1.0-beta.1", "v3.1.0", "downgrade")]
    [InlineData("v3.1.0", "v3.1.0-beta.1", "upgrade")]
    [InlineData("v3.1.0", "v3.0.9", "upgrade")]
    [InlineData("v3.0.9", "v3.1.0-beta.4", "downgrade")]
    [InlineData("v3.1.0", "v3.1.0", "none")]
    [InlineData("V3.1.0", "v3.1.0", "none")]
    [InlineData("", "v3.1.0", "none")]
    public void ComputeDirection_orders_by_semver(string target, string running, string expected)
    {
        Assert.Equal(expected, UpdateService.ComputeDirection(target, running));
    }

    // Exact-version marker

    [Theory]
    [InlineData("v3.1.0-beta.1", "v3.1.0-beta.1", true)]
    [InlineData("v3.1.0-beta.1", "V3.1.0-BETA.1", true)]
    [InlineData("v3.1.0-beta.1", "v3.1.0", false)]
    [InlineData("v3.1.0", "v3.1.0-beta.1", false)]
    [InlineData("v3.1.0", "v3.2.0", false)]
    [InlineData("3.1.0-beta.1", "v3.1.0-beta.1", true)]
    [InlineData("garbage", "v3.1.0", false)]
    public void InstallSucceeded_exact_marker_requires_the_same_version(string markerVersion, string running, bool expected)
    {
        var marker = new StagedInstallMarker { Version = markerVersion, ExactVersion = true };
        Assert.Equal(expected, UpdateService.InstallSucceeded(marker, running));
    }

    [Theory]
    [InlineData("v3.1.0", "v3.1.0", true)]
    [InlineData("v3.1.0", "v3.2.0", true)]
    [InlineData("v3.1.0", "v3.0.0", false)]
    public void InstallSucceeded_plain_marker_keeps_the_at_or_beyond_rule(string markerVersion, string running, bool expected)
    {
        var marker = new StagedInstallMarker { Version = markerVersion };
        Assert.Equal(expected, UpdateService.InstallSucceeded(marker, running));
    }

    [Fact]
    public void Marker_without_exactVersion_json_defaults_to_false_and_flag_round_trips()
    {
        var old = JsonSerializer.Deserialize("""{"version":"v3.1.0","state":"attempted"}""", AppJsonContext.Default.StagedInstallMarker);
        Assert.False(old!.ExactVersion);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new StagedInstallMarker { Version = "v3.1.0", ExactVersion = true }, AppJsonContext.Default.StagedInstallMarker);
        Assert.True(JsonSerializer.Deserialize(bytes, AppJsonContext.Default.StagedInstallMarker)!.ExactVersion);
        Assert.Contains("\"exact_version\":true", Encoding.UTF8.GetString(bytes));
    }

    // Switch refusals

    private static UpdateManifest Manifest(string version = "v3.1.0-beta.1", string? sha = Hash64) =>
        new() { Version = version, Sha256 = sha, AssetUrl = "http://127.0.0.1/x" };

    [Theory]
    [InlineData("stable", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("beta", false)]
    public void CheckSwitchTarget_refuses_invalid_channel_or_unapplicable_install(string? channel, bool canApply)
    {
        Assert.NotNull(UpdateService.CheckSwitchTarget(channel, "v3.1.0-beta.1", Manifest(), "v3.1.0", canApply));
    }

    [Fact]
    public void CheckSwitchTarget_refuses_missing_release_version_mismatch_same_version_and_missing_hash()
    {
        Assert.Contains("No release", UpdateService.CheckSwitchTarget("beta", "v3.1.0-beta.1", null, "v3.1.0", true));
        Assert.Contains("mismatch", UpdateService.CheckSwitchTarget("beta", "v3.1.0-beta.0", Manifest(), "v3.1.0", true));
        Assert.Contains("already running", UpdateService.CheckSwitchTarget("beta", "v3.1.0-beta.1", Manifest(), "v3.1.0-beta.1", true));
        Assert.Contains("SHA-256", UpdateService.CheckSwitchTarget("beta", "v3.1.0-beta.1", Manifest(sha: ""), "v3.1.0", true));
    }

    [Fact]
    public void CheckSwitchTarget_accepts_a_downgrade()
    {
        Assert.Null(UpdateService.CheckSwitchTarget("beta", "v3.1.0-beta.1", Manifest(), "v3.1.0", true));
    }

    [Theory]
    [InlineData("beta", true)]
    [InlineData("production", true)]
    [InlineData("nightly", false)]
    [InlineData(null, false)]
    public void IsValidChannel_accepts_only_the_two_tracks(string? channel, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsValidChannel(channel));
    }

    [Fact]
    public async Task Unofficial_build_previews_nothing_and_refuses_the_switch()
    {
        if (Nexus.Service.Common.ClientCredential.IsOfficial)
        {
            return;
        }

        var svc = new UpdateService(
            new FakeSource(Manifest()),
            new UpdateDownloader(new HandlerFactory(new RoutingHandler())),
            new MemoryStore(),
            TestFirmwareFlasher.Create(),
            new MultiplexHub());

        var target = await svc.GetChannelTargetAsync("beta", CancellationToken.None);
        Assert.Equal("Updates are managed outside this build.", target.Error);
        Assert.Equal("", target.Version);

        var (started, reason) = await svc.SwitchChannelAsync("beta", "v3.1.0-beta.1", CancellationToken.None);
        Assert.False(started);
        Assert.Equal("Updates are managed outside this build.", reason);
    }

    private sealed class FakeSource(UpdateManifest? manifest) : IUpdateSource
    {
        public Task<UpdateManifest?> GetLatestAsync(string channel, CancellationToken ct) => Task.FromResult(manifest);
    }

    private sealed class MemoryStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, string Type)> _routes = new();
        public List<string> Requested { get; } = new();

        public void Json(string pathAndQuery, string body) => _routes[pathAndQuery] = (body, "application/json");
        public void Text(string path, string body) => _routes[path] = (body, "text/plain");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requested.Add(uri.ToString());
            foreach (var (suffix, route) in _routes)
            {
                if (uri.PathAndQuery.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(route.Body, Encoding.UTF8, route.Type),
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

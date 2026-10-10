using System;
using System.Collections.Generic;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class BetaFeedTests
{
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
}

using System.Net;
using System.Net.Http;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class GitHubReleaseProviderTests
{
    // A valid 64-character lowercase hex SHA-256 used across all parsing tests.
    private const string Hash64 = "abc123def456abc123def456abc123def456abc123def456abc123def4560000";

    // SHA256SUMS parsing

    [Fact]
    public void ParseSha256Sums_two_space_separator_returns_hash()
    {
        var content = $"{Hash64}  Nexus-Setup.exe\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "Nexus-Setup.exe");
        Assert.Equal(Hash64, hash);
    }

    [Fact]
    public void ParseSha256Sums_one_space_separator_returns_hash()
    {
        var content = $"{Hash64} Nexus-Setup.exe\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "Nexus-Setup.exe");
        Assert.Equal(Hash64, hash);
    }

    [Fact]
    public void ParseSha256Sums_case_insensitive_filename_match()
    {
        var content = $"{Hash64}  nexus-setup.exe\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "Nexus-Setup.exe");
        Assert.Equal(Hash64, hash);
    }

    [Fact]
    public void ParseSha256Sums_multiple_lines_returns_correct_hash()
    {
        const string other = "0000000000000000000000000000000000000000000000000000000000000001";
        var content =
            $"{other}  other-file.exe\n" +
            $"{Hash64}  Nexus-Setup.exe\n" +
            $"{other}  another.zip\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "Nexus-Setup.exe");
        Assert.Equal(Hash64, hash);
    }

    [Fact]
    public void ParseSha256Sums_missing_file_returns_null()
    {
        var content = $"{Hash64}  other-file.exe\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "Nexus-Setup.exe");
        Assert.Null(hash);
    }

    [Fact]
    public void ParseSha256Sums_strips_dot_slash_path_prefix()
    {
        // Ollama's sha256sum.txt lists every asset with a './' prefix.
        var content = $"{Hash64}  ./ollama-windows-arm64.zip\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "ollama-windows-arm64.zip");
        Assert.Equal(Hash64, hash);
    }

    [Fact]
    public void ParseSha256Sums_strips_binary_mode_asterisk()
    {
        var content = $"{Hash64} *ollama-darwin.tgz\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "ollama-darwin.tgz");
        Assert.Equal(Hash64, hash);
    }

    [Fact]
    public void ParseSha256Sums_empty_content_returns_null()
    {
        var hash = GitHubReleaseProvider.ParseSha256Sums("", "Nexus-Setup.exe");
        Assert.Null(hash);
    }

    [Fact]
    public void ParseSha256Sums_normalizes_hash_to_lowercase()
    {
        // Uppercase hash in file should be normalized to lowercase.
        var uppercase = Hash64.ToUpperInvariant();
        var content = $"{uppercase}  Nexus-Setup.exe\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "Nexus-Setup.exe");
        Assert.Equal(Hash64, hash);
    }

    [Fact]
    public void ParseSha256Sums_versioned_filename_returns_hash()
    {
        var content = $"{Hash64}  Nexus-Setup-3.0.0-beta.2.exe\n";
        var hash = GitHubReleaseProvider.ParseSha256Sums(content, "Nexus-Setup-3.0.0-beta.2.exe");
        Assert.Equal(Hash64, hash);
    }

    // Installer asset selection

    private static GitHubReleaseAsset Asset(string name) => new() { Name = name };

    [Fact]
    public void SelectInstallerAsset_windows_matches_versioned_name()
    {
        var assets = new[] { Asset("SHA256SUMS"), Asset("Nexus-Setup-3.0.0-beta.2.exe") };
        var picked = GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.Windows);
        Assert.Equal("Nexus-Setup-3.0.0-beta.2.exe", picked?.Name);
    }

    [Fact]
    public void SelectInstallerAsset_windows_matches_legacy_bare_name()
    {
        var assets = new[] { Asset("Nexus-Setup.exe"), Asset("Nexus.dmg") };
        var picked = GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.Windows);
        Assert.Equal("Nexus-Setup.exe", picked?.Name);
    }

    [Fact]
    public void SelectInstallerAsset_windows_ignores_dmg_tarball_and_sums()
    {
        var assets = new[] { Asset("Nexus-3.0.0.dmg"), Asset("Nexus-Linux-x64-3.0.0.tar.gz"), Asset("SHA256SUMS") };
        Assert.Null(GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.Windows));
    }

    [Fact]
    public void SelectInstallerAsset_windows_null_when_no_installer()
    {
        Assert.Null(GitHubReleaseProvider.SelectInstallerAsset(System.Array.Empty<GitHubReleaseAsset>(), RuntimePlatform.Windows));
    }

    [Fact]
    public void SelectInstallerAsset_macos_matches_dmg()
    {
        var assets = new[] { Asset("SHA256SUMS"), Asset("Nexus-3.0.0.dmg") };
        var picked = GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.MacOS);
        Assert.Equal("Nexus-3.0.0.dmg", picked?.Name);
    }

    [Fact]
    public void SelectInstallerAsset_macos_matches_legacy_bare_name()
    {
        var assets = new[] { Asset("Nexus-Setup.exe"), Asset("Nexus.dmg") };
        var picked = GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.MacOS);
        Assert.Equal("Nexus.dmg", picked?.Name);
    }

    [Fact]
    public void SelectInstallerAsset_macos_ignores_exe_tarball_and_sums()
    {
        var assets = new[] { Asset("Nexus-Setup-3.0.0.exe"), Asset("Nexus-Linux-x64-3.0.0.tar.gz"), Asset("SHA256SUMS") };
        Assert.Null(GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.MacOS));
    }

    [Fact]
    public void SelectInstallerAsset_macos_null_when_no_installer()
    {
        Assert.Null(GitHubReleaseProvider.SelectInstallerAsset(System.Array.Empty<GitHubReleaseAsset>(), RuntimePlatform.MacOS));
    }

    [Fact]
    public void SelectInstallerAsset_linux_matches_tarball()
    {
        var assets = new[] { Asset("SHA256SUMS"), Asset("Nexus-Linux-x64-3.0.0.tar.gz") };
        var picked = GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.Linux);
        Assert.Equal("Nexus-Linux-x64-3.0.0.tar.gz", picked?.Name);
    }

    [Fact]
    public void SelectInstallerAsset_linux_ignores_exe_dmg_and_sums()
    {
        var assets = new[] { Asset("Nexus-Setup-3.0.0.exe"), Asset("Nexus-3.0.0.dmg"), Asset("SHA256SUMS") };
        Assert.Null(GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.Linux));
    }

    [Fact]
    public void SelectInstallerAsset_linux_requires_linux_in_name()
    {
        var assets = new[] { Asset("Nexus-x64-3.0.0.tar.gz") };
        Assert.Null(GitHubReleaseProvider.SelectInstallerAsset(assets, RuntimePlatform.Linux));
    }

    [Fact]
    public void SelectInstallerAsset_linux_null_when_no_installer()
    {
        Assert.Null(GitHubReleaseProvider.SelectInstallerAsset(System.Array.Empty<GitHubReleaseAsset>(), RuntimePlatform.Linux));
    }

    // Release fetch status handling

    [Theory]
    [InlineData("beta", HttpStatusCode.Forbidden)]
    [InlineData("beta", HttpStatusCode.ServiceUnavailable)]
    [InlineData("production", HttpStatusCode.Forbidden)]
    [InlineData("production", HttpStatusCode.InternalServerError)]
    public async Task GetLatestAsync_refused_request_throws_instead_of_reporting_no_release(string channel, HttpStatusCode status)
    {
        var provider = new GitHubReleaseProvider(new StubHttpClientFactory(status));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetLatestAsync(channel, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestAsync_production_without_a_stable_release_returns_null()
    {
        var provider = new GitHubReleaseProvider(new StubHttpClientFactory(HttpStatusCode.NotFound));
        Assert.Null(await provider.GetLatestAsync("production", CancellationToken.None));
    }

    private sealed class StubHttpClientFactory(HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(status));
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }
}

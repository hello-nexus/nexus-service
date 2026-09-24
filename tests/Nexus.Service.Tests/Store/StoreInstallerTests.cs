using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Store;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Store;

public class StoreInstallerTests : IDisposable
{
    private readonly string _root;

    public StoreInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-store-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A .nexus-app: a zip whose ROOT is the app dir, which is what the panel
    // install path expects to find at apps/<id>.
    private static byte[] Artifact(string id, string version, string extraFile = "widget.mjs")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(manifest.Open()))
            {
                w.Write($"{{\"schema\":\"nexus.app/1\",\"id\":\"{id}\",\"version\":\"{version}\",\"name\":\"Test\"}}");
            }
            var bundle = zip.CreateEntry(extraFile);
            using (var w = new StreamWriter(bundle.Open())) w.Write("export default 1;");
        }
        return ms.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ByteHandler : HttpMessageHandler
    {
        private readonly byte[]? _body;
        public string? LastUrl { get; private set; }
        public ByteHandler(byte[]? body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();
            if (_body is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
        }
    }

    private StoreInstaller New(byte[]? body, out ByteHandler handler)
    {
        handler = new ByteHandler(body);
        return new StoreInstaller(new HttpClient(handler), new AppRegistry(), () => _root);
    }

    private static StoreInstallRequest Req(string id, string version, string sha, long size = 0) =>
        new() { AppId = id, Version = version, Sha256 = sha, Size = size };

    [Fact]
    public async Task installs_the_artifact_into_the_app_dir()
    {
        var bytes = Artifact("com.hellonexus.aquarium", "1.0.0");
        var installer = New(bytes, out var handler);

        var res = await installer.InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", Sha256(bytes), bytes.Length), default);

        Assert.True(res.Ok);
        Assert.Null(res.Reason);
        var dir = Path.Combine(_root, "com.hellonexus.aquarium");
        Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(dir, "widget.mjs")));
        Assert.EndsWith("/apps/com.hellonexus.aquarium/1.0.0.nexus-app", handler.LastUrl);
    }

    [Fact]
    public async Task an_update_replaces_the_installed_version_and_leaves_nothing_aside()
    {
        var v1 = Artifact("com.hellonexus.aquarium", "1.0.0");
        Assert.True((await New(v1, out _).InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", Sha256(v1)), default)).Ok);
        var v2 = Artifact("com.hellonexus.aquarium", "1.1.0");

        var res = await New(v2, out _).UpdateAsync(Req("com.hellonexus.aquarium", "1.1.0", Sha256(v2)), default);

        Assert.True(res.Ok);
        Assert.Contains("\"1.1.0\"", File.ReadAllText(Path.Combine(_root, "com.hellonexus.aquarium", "manifest.json")));
        Assert.Equal(new[] { "com.hellonexus.aquarium" }, Directory.GetDirectories(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task an_update_of_an_app_that_is_no_longer_installed_is_refused()
    {
        var bytes = Artifact("com.hellonexus.aquarium", "1.1.0");

        var res = await New(bytes, out _).UpdateAsync(Req("com.hellonexus.aquarium", "1.1.0", Sha256(bytes)), default);

        Assert.False(res.Ok);
        Assert.Equal("not_installed", res.Reason);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task a_tampered_artifact_is_refused_and_nothing_is_installed()
    {
        var bytes = Artifact("com.hellonexus.aquarium", "1.0.0");
        var installer = New(bytes, out _);

        var res = await installer.InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", Sha256(Encoding.UTF8.GetBytes("other"))), default);

        Assert.False(res.Ok);
        Assert.Equal("hash_mismatch", res.Reason);
        Assert.False(Directory.Exists(Path.Combine(_root, "com.hellonexus.aquarium")));
    }

    [Fact]
    public async Task an_artifact_describing_another_app_is_refused()
    {
        var bytes = Artifact("com.someone.else", "1.0.0");
        var installer = New(bytes, out _);

        var res = await installer.InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", Sha256(bytes)), default);

        Assert.False(res.Ok);
        Assert.Equal("manifest_mismatch", res.Reason);
        Assert.False(Directory.Exists(Path.Combine(_root, "com.hellonexus.aquarium")));
    }

    [Fact]
    public async Task an_artifact_at_another_version_is_refused()
    {
        var bytes = Artifact("com.hellonexus.aquarium", "2.0.0");
        var installer = New(bytes, out _);

        var res = await installer.InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", Sha256(bytes)), default);

        Assert.False(res.Ok);
        Assert.Equal("manifest_mismatch", res.Reason);
    }

    [Fact]
    public async Task a_missing_artifact_reports_unavailable()
    {
        var installer = New(null, out _);

        var res = await installer.InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", new string('a', 64)), default);

        Assert.False(res.Ok);
        Assert.Equal("artifact_unavailable", res.Reason);
    }

    [Fact]
    public async Task the_hash_is_required()
    {
        var installer = New(Artifact("com.hellonexus.aquarium", "1.0.0"), out _);

        var res = await installer.InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", ""), default);

        Assert.False(res.Ok);
        Assert.Equal("missing_hash", res.Reason);
    }

    [Fact]
    public async Task reinstalling_replaces_the_previous_contents()
    {
        var dir = Path.Combine(_root, "com.hellonexus.aquarium");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stale.mjs"), "old");

        var bytes = Artifact("com.hellonexus.aquarium", "1.0.0");
        var installer = New(bytes, out _);
        var res = await installer.InstallAsync(Req("com.hellonexus.aquarium", "1.0.0", Sha256(bytes)), default);

        Assert.True(res.Ok);
        Assert.False(File.Exists(Path.Combine(dir, "stale.mjs")));
        Assert.True(File.Exists(Path.Combine(dir, "widget.mjs")));
    }

    [Theory]
    [InlineData("", "1.0.0", "invalid_app_id")]
    [InlineData("no-dots", "1.0.0", "invalid_app_id")]
    [InlineData("com.hellonexus.aquarium", "", "invalid_version")]
    [InlineData("com.hellonexus.aquarium", "1.0", "invalid_version")]
    [InlineData("com.hellonexus.aquarium", "../../etc", "invalid_version")]
    [InlineData("com.hellonexus.aquarium", "1.0.0/../..", "invalid_version")]
    public async Task rejects_ids_and_versions_that_could_escape_the_apps_dir(string id, string version, string reason)
    {
        var installer = New(Artifact("com.hellonexus.aquarium", "1.0.0"), out _);

        var res = await installer.InstallAsync(Req(id, version, new string('a', 64)), default);

        Assert.False(res.Ok);
        Assert.Equal(reason, res.Reason);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("10.20.30", true)]
    [InlineData("1.0.0-beta.1", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0.0.0", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("1.0.0/x", false)]
    [InlineData("../1.0.0", false)]
    public void version_validation_matches_semver(string version, bool valid)
    {
        Assert.Equal(valid, StoreInstaller.IsValidVersion(version));
    }
}

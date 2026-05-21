using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Devices.Firmware;
using Xunit;

namespace Qos.Service.Tests;

public class FirmwareStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public FirmwareStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "qos-fwstore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private FirmwareStore NewStore(HttpClient? http = null) => new(_tempRoot, http ?? new HttpClient(new FakeHandler()));

    // ── GetDeviceDir / GetBinaryPath ──

    [Fact]
    public void GetDeviceDir_creates_the_directory_on_demand()
    {
        var store = NewStore();
        var dir = store.GetDeviceDir("np50");
        Assert.True(Directory.Exists(dir));
        Assert.EndsWith(Path.Combine("np50"), dir);
    }

    [Fact]
    public void GetBinaryPath_combines_version_and_extension()
    {
        var store = NewStore();
        var path = store.GetBinaryPath("np50", "2.0.5.1", "hex");
        Assert.EndsWith(Path.Combine("np50", "2.0.5.1.hex"), path);
    }

    [Fact]
    public void GetBinaryPath_accepts_extension_with_or_without_dot()
    {
        var store = NewStore();
        Assert.Equal(
            store.GetBinaryPath("np50", "1.0.0.0", ".hex"),
            store.GetBinaryPath("np50", "1.0.0.0", "hex"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../escape")]
    [InlineData("np50/sub")]
    public void GetDeviceDir_rejects_unsafe_device_types(string deviceType)
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.GetDeviceDir(deviceType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0.0/../etc")]
    [InlineData("1 0 0")]
    public void GetBinaryPath_rejects_unsafe_versions(string version)
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.GetBinaryPath("np50", version, "hex"));
    }

    // ── Manifest load / save ──

    [Fact]
    public async Task LoadLocalManifestAsync_returns_null_when_missing()
    {
        var store = NewStore();
        Assert.Null(await store.LoadLocalManifestAsync("np50", CancellationToken.None));
    }

    [Fact]
    public async Task SaveLocalManifestAsync_round_trips_through_LoadLocalManifestAsync()
    {
        var store = NewStore();
        var saved = new FirmwareManifest
        {
            Latest = "2.0.5.1",
            Files =
            {
                ["2.0.5.1"] = new FirmwareFile
                {
                    Sha256 = "abc123",
                    Size = 12345,
                    Url = "https://example.com/np50-2.0.5.1.hex",
                    Changelog = "Fix flicker",
                },
            },
        };

        await store.SaveLocalManifestAsync("np50", saved, CancellationToken.None);
        var loaded = await store.LoadLocalManifestAsync("np50", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("2.0.5.1", loaded!.Latest);
        Assert.True(loaded.Files.ContainsKey("2.0.5.1"));
        Assert.Equal("abc123", loaded.Files["2.0.5.1"].Sha256);
        Assert.Equal(12345, loaded.Files["2.0.5.1"].Size);
        Assert.Equal("https://example.com/np50-2.0.5.1.hex", loaded.Files["2.0.5.1"].Url);
        Assert.Equal("Fix flicker", loaded.Files["2.0.5.1"].Changelog);
    }

    [Fact]
    public async Task LoadLocalManifestAsync_returns_null_when_file_is_corrupt()
    {
        var store = NewStore();
        var manifestPath = Path.Combine(store.GetDeviceDir("np50"), "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{not-json", CancellationToken.None);

        Assert.Null(await store.LoadLocalManifestAsync("np50", CancellationToken.None));
    }

    // ── Remote fetch ──

    [Fact]
    public async Task FetchRemoteManifestAsync_parses_the_response()
    {
        var json = """{"latest":"2.0.5.1","files":{"2.0.5.1":{"sha256":"deadbeef","size":7,"url":"https://x/y.hex"}}}""";
        var http = new HttpClient(new FakeHandler { Response = Ok(json) });
        var store = NewStore(http);

        var manifest = await store.FetchRemoteManifestAsync("https://api.example.com/np50/latest.json", CancellationToken.None);

        Assert.Equal("2.0.5.1", manifest.Latest);
        Assert.Equal("deadbeef", manifest.Files["2.0.5.1"].Sha256);
    }

    [Fact]
    public async Task FetchRemoteManifestAsync_rejects_manifest_without_files()
    {
        var http = new HttpClient(new FakeHandler { Response = Ok("""{"latest":"1.0.0","files":{}}""") });
        var store = NewStore(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.FetchRemoteManifestAsync("https://api.example.com/np50/latest.json", CancellationToken.None));
    }

    [Fact]
    public async Task FetchRemoteManifestAsync_throws_on_http_error()
    {
        var http = new HttpClient(new FakeHandler { Response = new HttpResponseMessage(HttpStatusCode.NotFound) });
        var store = NewStore(http);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            store.FetchRemoteManifestAsync("https://api.example.com/np50/latest.json", CancellationToken.None));
    }

    // ── Download ──

    [Fact]
    public async Task DownloadAsync_writes_binary_and_returns_path_on_success()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var sha = Sha256Hex(payload);
        var http = new HttpClient(new FakeHandler { Response = OkBytes(payload) });
        var store = NewStore(http);

        var path = await store.DownloadAsync(
            "np50", "1.0.0.0", "hex",
            "https://example.com/np50-1.0.0.0.hex",
            sha, payload.Length, progress: null, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DownloadAsync_skips_network_when_local_copy_is_valid()
    {
        var payload = new byte[] { 0xAA, 0xBB };
        var sha = Sha256Hex(payload);
        var store = NewStore();
        // Seed the cache manually so the handler is unused.
        var path = store.GetBinaryPath("np50", "1.0.0.0", "hex");
        await File.WriteAllBytesAsync(path, payload);

        var handler = new ExplodingHandler();
        var http = new HttpClient(handler);
        var skipStore = new FirmwareStore(_tempRoot, http);

        var returned = await skipStore.DownloadAsync(
            "np50", "1.0.0.0", "hex",
            "https://example.com/np50-1.0.0.0.hex",
            sha, payload.Length, progress: null, CancellationToken.None);

        Assert.Equal(path, returned);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_discards_partial_file_on_sha_mismatch()
    {
        var payload = new byte[] { 0xDE, 0xAD };
        var http = new HttpClient(new FakeHandler { Response = OkBytes(payload) });
        var store = NewStore(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(
            "np50", "1.0.0.0", "hex",
            "https://example.com/wrong.hex",
            expectedSha256: "0000000000000000000000000000000000000000000000000000000000000000",
            expectedSize: payload.Length, progress: null, CancellationToken.None));

        var finalPath = store.GetBinaryPath("np50", "1.0.0.0", "hex");
        var tmpPath = finalPath + ".tmp";
        Assert.False(File.Exists(finalPath));
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public async Task DownloadAsync_discards_when_size_mismatches_manifest()
    {
        var payload = new byte[] { 0x01, 0x02 };
        var http = new HttpClient(new FakeHandler { Response = OkBytes(payload) });
        var store = NewStore(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(
            "np50", "1.0.0.0", "hex",
            "https://example.com/wrong-size.hex",
            Sha256Hex(payload), expectedSize: 999, progress: null, CancellationToken.None));

        Assert.False(File.Exists(store.GetBinaryPath("np50", "1.0.0.0", "hex")));
    }

    [Fact]
    public async Task DownloadAsync_reports_progress()
    {
        var payload = new byte[1024 * 256];
        new Random(1).NextBytes(payload);
        var http = new HttpClient(new FakeHandler { Response = OkBytes(payload) });
        var store = NewStore(http);

        long lastReported = 0;
        var progress = new Progress<long>(b => lastReported = b);

        await store.DownloadAsync(
            "np50", "1.0.0.0", "hex",
            "https://example.com/big.hex",
            Sha256Hex(payload), payload.Length, progress, CancellationToken.None);

        // Progress is async via Progress<T>; spin briefly for the final report.
        for (var i = 0; i < 50 && lastReported < payload.Length; i++) await Task.Delay(10);
        Assert.Equal(payload.Length, lastReported);
    }

    // ── HasValidBinaryAsync ──

    [Fact]
    public async Task HasValidBinaryAsync_returns_false_when_file_missing()
    {
        var store = NewStore();
        Assert.False(await store.HasValidBinaryAsync("np50", "1.0.0.0", "hex", "abc", CancellationToken.None));
    }

    [Fact]
    public async Task HasValidBinaryAsync_returns_true_only_when_sha_matches()
    {
        var payload = new byte[] { 0x11, 0x22, 0x33 };
        var store = NewStore();
        var path = store.GetBinaryPath("np50", "1.0.0.0", "hex");
        await File.WriteAllBytesAsync(path, payload);

        Assert.True(await store.HasValidBinaryAsync("np50", "1.0.0.0", "hex", Sha256Hex(payload), CancellationToken.None));
        Assert.False(await store.HasValidBinaryAsync("np50", "1.0.0.0", "hex", "deadbeef", CancellationToken.None));
    }

    // ── Helpers ──

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage OkBytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Response);
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("FirmwareStore should not have hit the network for a cached binary.");
        }
    }
}

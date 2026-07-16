using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

public class ExternalToolManagerTests : IDisposable
{
    private readonly string _root;     // cache root
    private readonly string _preload;  // app-bundle preload dir

    public ExternalToolManagerTests()
    {
        var b = Path.Combine(Path.GetTempPath(), "nexus-toolmgr-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(b, "cache");
        _preload = Path.Combine(b, "preload");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_preload);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    private ExternalToolSpec Spec(string? preload = null, ToolSession session = ToolSession.System) => new(
        ToolId: "test-tool",
        Variant: "v1",
        ManifestUrl: "https://assets.hellonexus.com/test-tool/v1/latest.json",
        DownloadUrlBase: "https://assets.hellonexus.com/test-tool/v1",
        FilePattern: "*.bin",
        Launch: new ToolLaunchOptions(Hidden: true, Session: session),
        PreloadDir: preload);

    [Fact]
    public async Task ResolveAsync_downloads_hash_pinned_binary_from_manifest()
    {
        var payload = RandomBytes(2048);
        var manifest = ManifestJson("tool.bin", Sha256Hex(payload), payload.Length);
        var http = new HttpClient(new RouteHandler(manifest, payload));
        var mgr = new ExternalToolManager(http, _root);

        var path = await mgr.ResolveAsync(Spec());

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path!));
        Assert.StartsWith(_root, path!);
    }

    [Fact]
    public async Task ResolveAsync_rejects_binary_when_manifest_hash_is_wrong()
    {
        var payload = RandomBytes(512);
        var manifest = ManifestJson("tool.bin", new string('0', 64), payload.Length); // wrong hash
        var http = new HttpClient(new RouteHandler(manifest, payload));
        var mgr = new ExternalToolManager(http, _root);

        // Hash mismatch is swallowed inside resolve → returns null, nothing cached.
        var path = await mgr.ResolveAsync(Spec());

        Assert.Null(path);
        Assert.False(File.Exists(Path.Combine(_root, "test-tool", "v1", "tool.bin")));
    }

    [Fact]
    public async Task ResolveAsync_uses_bundled_pin_without_touching_network()
    {
        var payload = RandomBytes(1024);
        var pinned = Path.Combine(_preload, "tool-1.0.0.bin");
        await File.WriteAllBytesAsync(pinned, payload);
        await File.WriteAllTextAsync(Path.Combine(_preload, "bundled.json"),
            BundledPinJson("tool-1.0.0.bin", Sha256Hex(payload), payload.Length));

        var handler = new ExplodingHandler();
        var mgr = new ExternalToolManager(new HttpClient(handler), _root);

        var path = await mgr.ResolveAsync(Spec(preload: _preload));

        Assert.Equal(pinned, path);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_returns_null_when_offline_and_unpinned()
    {
        var http = new HttpClient(new RouteHandler(manifestJson: null, payload: null,
            status: HttpStatusCode.NotFound));
        var mgr = new ExternalToolManager(http, _root);

        Assert.Null(await mgr.ResolveAsync(Spec(preload: _preload)));
    }

    [Fact]
    public async Task ResolveAsync_reuses_downloaded_binary_when_manifest_is_unreachable()
    {
        // The AW5 regression: a driver downloaded while online must still launch on
        // a later offline boot, not go dark until the manifest is reachable again.
        var payload = RandomBytes(2048);
        var manifest = ManifestJson("tool.bin", Sha256Hex(payload), payload.Length);
        var online = await new ExternalToolManager(new HttpClient(new RouteHandler(manifest, payload)), _root)
            .ResolveAsync(Spec());
        Assert.NotNull(online);

        var offline = new ExternalToolManager(new HttpClient(new OfflineHandler()), _root);

        Assert.Equal(online, await offline.ResolveAsync(Spec()));
    }

    [Fact]
    public async Task ResolveAsync_prefers_updated_manifest_over_cached_binary()
    {
        // The cache must not shadow an upgrade: a reachable manifest still wins.
        var v1 = RandomBytes(2048, seed: 1);
        await new ExternalToolManager(
                new HttpClient(new RouteHandler(ManifestJson("tool-1.bin", Sha256Hex(v1), v1.Length), v1)), _root)
            .ResolveAsync(Spec());

        var v2 = RandomBytes(2048, seed: 2);
        var path = await new ExternalToolManager(
                new HttpClient(new RouteHandler(ManifestJson("tool-2.bin", Sha256Hex(v2), v2.Length), v2)), _root)
            .ResolveAsync(Spec());

        Assert.Equal("tool-2.bin", Path.GetFileName(path));
        Assert.Equal(v2, await File.ReadAllBytesAsync(path!));
    }

    [Fact]
    public async Task ResolveAsync_ignores_cached_binary_that_fails_its_pin()
    {
        // A pinned file swapped on disk must not run: the hash, not the pin's
        // presence, is what the cache is trusted on.
        var payload = RandomBytes(2048);
        var manifest = ManifestJson("tool.bin", Sha256Hex(payload), payload.Length);
        var cached = await new ExternalToolManager(new HttpClient(new RouteHandler(manifest, payload)), _root)
            .ResolveAsync(Spec());
        // Same size, different content: the hash is what rejects it, not the size pre-check.
        await File.WriteAllBytesAsync(cached!, new byte[payload.Length]);

        var offline = new ExternalToolManager(new HttpClient(new OfflineHandler()), _root);

        Assert.Null(await offline.ResolveAsync(Spec()));
    }

    [Fact]
    public async Task ResolveAsync_does_not_use_cache_when_manifest_withdraws_the_version()
    {
        // The manifest is how a driver is pulled. A reachable manifest that names no
        // usable version must stop the tool, not fall through to the cached copy.
        var payload = RandomBytes(2048);
        await new ExternalToolManager(
                new HttpClient(new RouteHandler(ManifestJson("tool.bin", Sha256Hex(payload), payload.Length), payload)), _root)
            .ResolveAsync(Spec());

        var withdrawn = new ExternalToolManager(
            new HttpClient(new RouteHandler("{\"latestVersion\":\"1.0.0\",\"versions\":{}}", null)), _root);

        Assert.Null(await withdrawn.ResolveAsync(Spec()));
    }

    [Fact]
    public async Task ResolveAsync_does_not_use_cache_when_manifest_answers_with_an_error_status()
    {
        // A status is an answer, unlike no network: don't paper over it with the cache.
        var payload = RandomBytes(2048);
        await new ExternalToolManager(
                new HttpClient(new RouteHandler(ManifestJson("tool.bin", Sha256Hex(payload), payload.Length), payload)), _root)
            .ResolveAsync(Spec());

        var gone = new ExternalToolManager(
            new HttpClient(new RouteHandler(null, null, HttpStatusCode.NotFound)), _root);

        Assert.Null(await gone.ResolveAsync(Spec()));
    }

    [Fact]
    public async Task ResolveAsync_does_not_use_cache_when_download_fails_its_manifest_hash()
    {
        // A tampered payload must fail closed even with a good copy already cached.
        var payload = RandomBytes(2048, seed: 1);
        await new ExternalToolManager(
                new HttpClient(new RouteHandler(ManifestJson("tool.bin", Sha256Hex(payload), payload.Length), payload)), _root)
            .ResolveAsync(Spec());

        var tampered = RandomBytes(2048, seed: 2);
        var mgr = new ExternalToolManager(
            new HttpClient(new RouteHandler(ManifestJson("tool-2.bin", Sha256Hex(payload), payload.Length), tampered)), _root);

        Assert.Null(await mgr.ResolveAsync(Spec()));
    }

    [Fact]
    public async Task ResolveAsync_does_not_use_cache_from_a_dir_it_could_not_lock()
    {
        // An unlocked cache dir is one any local user can write a pin into, so the
        // service must not trust its own record there - dark beats running theirs.
        var payload = RandomBytes(2048);
        var manifest = ManifestJson("tool.bin", Sha256Hex(payload), payload.Length);
        await new ExternalToolManager(new HttpClient(new RouteHandler(manifest, payload)), _root)
            .ResolveAsync(Spec());

        var unlocked = new ExternalToolManager(
            new IToolInstallStrategy[] { new HostExeInstallStrategy() },
            new HttpClient(new OfflineHandler()), _root, _ => false);

        Assert.Null(await unlocked.ResolveAsync(Spec()));
    }

    [Fact]
    public async Task ResolveAsync_uses_cache_from_a_locked_dir()
    {
        // The pair to the above: same offline resolve, lock reported good.
        var payload = RandomBytes(2048);
        var manifest = ManifestJson("tool.bin", Sha256Hex(payload), payload.Length);
        var online = await new ExternalToolManager(new HttpClient(new RouteHandler(manifest, payload)), _root)
            .ResolveAsync(Spec());

        var locked = new ExternalToolManager(
            new IToolInstallStrategy[] { new HostExeInstallStrategy() },
            new HttpClient(new OfflineHandler()), _root, _ => true);

        Assert.Equal(online, await locked.ResolveAsync(Spec()));
    }

    [Fact]
    public async Task ResolveAsync_rejects_pin_naming_a_path_outside_its_dir()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.bin");
        var payload = RandomBytes(256);
        await File.WriteAllBytesAsync(outside, payload);
        await File.WriteAllTextAsync(Path.Combine(_preload, "bundled.json"),
            BundledPinJson("../outside.bin", Sha256Hex(payload), payload.Length));

        var mgr = new ExternalToolManager(new HttpClient(new OfflineHandler()), _root);

        Assert.Null(await mgr.ResolveAsync(Spec(preload: _preload)));
    }

    [Fact]
    public void GetStatus_reports_NoDevice_when_absent_and_not_running()
    {
        var mgr = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _root);
        Assert.Equal(ToolStatus.NoDevice, mgr.GetStatus("test-tool", devicePresent: false));
        Assert.Equal(ToolStatus.NotRunning, mgr.GetStatus("test-tool", devicePresent: true));
    }

    [Fact]
    public async Task LaunchAsync_routes_to_strategy_matching_target()
    {
        var fake = new FakeStrategy(ToolTarget.AndroidAdb);
        var mgr = new ExternalToolManager(
            new IToolInstallStrategy[] { new HostExeInstallStrategy(), fake },
            new HttpClient(new ExplodingHandler()), _root);

        await mgr.LaunchAsync(Spec() with { Target = ToolTarget.AndroidAdb });

        Assert.Equal(1, fake.LaunchCount);
    }

    [Fact]
    public async Task LaunchAsync_with_unrouted_target_is_noop()
    {
        // Only the host-exe strategy is registered; an adb-targeted spec must not throw.
        var mgr = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _root);

        await mgr.LaunchAsync(Spec() with { Target = ToolTarget.AndroidAdb });

        Assert.Equal(ToolStatus.NotRunning, mgr.GetStatus("test-tool"));
    }

    [Fact]
    public void GetStatus_surfaces_owning_strategy_status()
    {
        var fake = new FakeStrategy(ToolTarget.AndroidAdb, ToolStatus.Running);
        var mgr = new ExternalToolManager(
            new IToolInstallStrategy[] { new HostExeInstallStrategy(), fake },
            new HttpClient(new ExplodingHandler()), _root);

        Assert.Equal(ToolStatus.Running, mgr.GetStatus("test-tool"));
    }

    [Fact]
    public void Terminate_fans_out_to_every_strategy()
    {
        var fake = new FakeStrategy(ToolTarget.AndroidAdb);
        var mgr = new ExternalToolManager(
            new IToolInstallStrategy[] { new HostExeInstallStrategy(), fake },
            new HttpClient(new ExplodingHandler()), _root);

        mgr.Terminate("test-tool");

        Assert.Equal(1, fake.TerminateCount);
    }

    [Fact]
    public async Task LaunchAsync_runs_then_single_instances_then_terminates()
    {
        // The launch path uses a real long-running child; gate to Unix where we can
        // mint an executable sleeper script. The Windows launch path (same .NET API)
        // is exercised in the PC e2e phase.
        if (OperatingSystem.IsWindows()) return;

        var sleeper = MakeSleeperScript();
        var payload = await File.ReadAllBytesAsync(sleeper);
        await File.WriteAllTextAsync(Path.Combine(_preload, "bundled.json"),
            BundledPinJson("sleeper.sh", Sha256Hex(payload), payload.Length));

        var mgr = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _root);
        var spec = Spec(preload: _preload);

        await mgr.LaunchAsync(spec);
        Assert.Equal(ToolStatus.Running, mgr.GetStatus("test-tool"));

        // Second launch while running is a no-op (single instance).
        await mgr.LaunchAsync(spec);
        Assert.Equal(ToolStatus.Running, mgr.GetStatus("test-tool"));

        mgr.Terminate("test-tool");
        Assert.Equal(ToolStatus.NotRunning, mgr.GetStatus("test-tool"));

        // TerminateAll is idempotent after everything is already down.
        mgr.TerminateAll();
    }

    [Fact]
    public async Task TerminateAll_kills_running_tool_on_shutdown()
    {
        if (OperatingSystem.IsWindows()) return;

        var sleeper = MakeSleeperScript();
        var payload = await File.ReadAllBytesAsync(sleeper);
        await File.WriteAllTextAsync(Path.Combine(_preload, "bundled.json"),
            BundledPinJson("sleeper.sh", Sha256Hex(payload), payload.Length));

        var mgr = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _root);
        await mgr.LaunchAsync(Spec(preload: _preload));
        Assert.Equal(ToolStatus.Running, mgr.GetStatus("test-tool"));

        await mgr.StopAsync(CancellationToken.None); // hosted-service shutdown → TerminateAll
        Assert.Equal(ToolStatus.NotRunning, mgr.GetStatus("test-tool"));
    }

    // ── Helpers ──

    private string MakeSleeperScript()
    {
        var path = Path.Combine(_preload, "sleeper.sh");
        File.WriteAllText(path, "#!/bin/sh\nsleep 30\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    private static string ManifestJson(string fileName, string sha256, long size) =>
        "{\"latestVersion\":\"1.0.0\",\"versions\":{\"1.0.0\":{" +
        "\"version\":\"1.0.0\",\"fileName\":\"" + fileName + "\"," +
        "\"url\":\"https://assets.hellonexus.com/test-tool/v1/" + fileName + "\"," +
        "\"sha256\":\"" + sha256 + "\",\"size\":" + size + "}}}";

    private static string BundledPinJson(string fileName, string sha256, long size) =>
        "{\"fileName\":\"" + fileName + "\",\"sha256\":\"" + sha256 + "\",\"size\":" + size + "}";

    /// <summary>Distinct payloads need distinct seeds: a fixed seed makes two
    /// same-length calls return identical bytes, which hash-pins then treat as the
    /// same binary.</summary>
    private static byte[] RandomBytes(int n, int seed = 7)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly string? _manifestJson;
        private readonly byte[]? _payload;
        private readonly HttpStatusCode _status;

        public RouteHandler(string? manifestJson, byte[]? payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            _manifestJson = manifestJson;
            _payload = payload;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(_status));

            var url = request.RequestUri!.ToString();
            if (url.EndsWith("latest.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(_manifestJson ?? "", Encoding.UTF8, "application/json") });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(_payload ?? Array.Empty<byte>()) });
        }
    }

    /// <summary>No network: the shape a DNS/connect failure takes, i.e. an
    /// <see cref="HttpRequestException"/> carrying no status - the host never
    /// answered. Distinct from a handler returning an error status, which is an
    /// answer.</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("No such host is known. (assets.hellonexus.com:443)");
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("network should not be touched on this path");
        }
    }

    private sealed class FakeStrategy : IToolInstallStrategy
    {
        private readonly ToolStatus _status;
        public FakeStrategy(ToolTarget target, ToolStatus status = ToolStatus.NotRunning)
        {
            Target = target;
            _status = status;
        }
        public ToolTarget Target { get; }
        public int LaunchCount { get; private set; }
        public int TerminateCount { get; private set; }
        public Task LaunchAsync(ExternalToolSpec spec, IToolResolver resolver, CancellationToken ct)
        {
            LaunchCount++;
            return Task.CompletedTask;
        }
        public ToolStatus GetStatus(string toolId) => _status;
        public bool Terminate(string toolId) { TerminateCount++; return true; }
        public void TerminateAll() { }
    }
}

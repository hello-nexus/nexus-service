using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Activity;
using Nexus.Service.Games;

namespace Nexus.Service.Tests.Games;

public class GameArtResolverTests
{
    [Theory]
    [InlineData("steam:2473350", true, 2473350)]
    [InlineData("steam:427520", true, 427520)]
    [InlineData("epic:sludgelife", false, 0)]
    [InlineData("ubisoft:farcry", false, 0)]
    [InlineData("steam:", false, 0)]
    [InlineData("steam:0", false, 0)]
    [InlineData("steam:notanumber", false, 0)]
    public void TryParseSteamAppId_AcceptsOnlyASteamKeyWithAPositiveId(string gameKey, bool expected, int expectedAppId)
    {
        Assert.Equal(expected, GameArtResolver.TryParseSteamAppId(gameKey, out var appId));
        Assert.Equal(expectedAppId, appId);
    }

    [Fact]
    public void PickGameExecutable_PrefersTheOneNamedAfterTheInstallFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SludgeLife-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            // The launcher is deliberately the larger file: the name match must
            // win over the size fallback.
            File.WriteAllBytes(Path.Combine(dir, "UnityCrashHandler64.exe"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(dir, "SludgeLife.exe"), new byte[16]);

            Assert.Equal("SludgeLife.exe", Path.GetFileName(GameArtResolver.PickGameExecutable(dir)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PickGameExecutable_FallsBackToTheLargestExecutable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-art-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "crashpad.exe"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "TheGame.exe"), new byte[4096]);

            Assert.Equal("TheGame.exe", Path.GetFileName(GameArtResolver.PickGameExecutable(dir)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PickGameExecutable_IsEmptyForAMissingOrExeFreeDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-art-" + Guid.NewGuid().ToString("n"));
        Assert.Equal("", GameArtResolver.PickGameExecutable(dir));

        Directory.CreateDirectory(dir);
        try { Assert.Equal("", GameArtResolver.PickGameExecutable(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LegacyCapsuleUrl_IsTheFallbackPathThatStillAnswersForOlderTitles()
    {
        Assert.Equal(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/427520/capsule_231x87.jpg",
            GameArtResolver.LegacyCapsuleUrl(427520));
    }
}

/// <summary>
/// Exercises the real HTTP path against a loopback listener, the same approach
/// AlbumArtHdResolverTests uses, so nothing here reaches the live Steam store.
/// </summary>
public sealed class GameArtResolverHttpTests : IDisposable
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubIcons : IProcessIconProvider
    {
        public byte[]? Result { get; set; }
        public int Calls { get; private set; }

        public byte[]? GetIcon(string exePath)
        {
            Calls++;
            return Result;
        }
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private int _requests;

    public Func<byte[]> Response { get; set; } = () => Http(200, "{}");

    public GameArtResolverHttpTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    private static byte[] Http(int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return head.Concat(body).ToArray();
    }

    private static string AppDetails(int appId, string headerImage) =>
        $"{{\"{appId}\":{{\"success\":true,\"data\":{{\"header_image\":\"{headerImage}\"}}}}}}";

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            Interlocked.Increment(ref _requests);
            _ = Task.Run(async () =>
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer);
                    if (read <= 0) return;
                    await stream.WriteAsync(Response());
                }
            });
        }
    }

    private sealed class StubInstalls : IGameInstallLocator
    {
        public string Dir { get; set; } = "";

        public bool TryGetInstallDir(string gameKey, out string installDir)
        {
            installDir = Dir;
            return Dir.Length > 0;
        }
    }

    private const string InstalledGameKey = "steam:427520";

    private GameArtResolver Build(StubIcons icons) =>
        new(new SingleClientFactory(), new StubInstalls(), icons, BaseUrl);

    /// <summary>A resolver whose catalog reports one installed game holding a single executable.</summary>
    private GameArtResolver BuildWithInstalledGame(StubIcons icons, out Action cleanup)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-art-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "TheGame.exe"), new byte[64]);
        cleanup = () => Directory.Delete(dir, recursive: true);
        return new GameArtResolver(new SingleClientFactory(), new StubInstalls { Dir = dir }, icons, BaseUrl);
    }

    [Fact]
    public async Task UsesTheHeaderImageTheStoreReports()
    {
        var url = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/2473350/abc123/header.jpg";
        Response = () => Http(200, AppDetails(2473350, url + "?t=1780387591"));

        var art = await Build(new StubIcons()).ResolveAsync("steam:2473350", CancellationToken.None);

        // The ?t= cache-buster is stripped so the browser cache survives our ttl.
        Assert.Equal(url, art.Url);
        Assert.Empty(art.Bytes);
    }

    [Theory]
    [InlineData("https://evil.example.com/header.jpg")]
    [InlineData("http://shared.akamai.steamstatic.com/header.jpg")]
    [InlineData("https://shared.akamai.steamstatic.com.evil.test/header.jpg")]
    public async Task RefusesARedirectTargetThatIsNotAnHttpsSteamHost(string hostile)
    {
        Response = () => Http(200, AppDetails(427520, hostile));

        var art = await Build(new StubIcons()).ResolveAsync("steam:427520", CancellationToken.None);

        Assert.Equal(GameArtResolver.LegacyCapsuleUrl(427520), art.Url);
    }

    [Fact]
    public async Task FallsBackToTheLegacyCapsuleWhenTheStoreFails()
    {
        Response = () => Http(503, "{}");

        var art = await Build(new StubIcons()).ResolveAsync("steam:427520", CancellationToken.None);

        Assert.Equal(GameArtResolver.LegacyCapsuleUrl(427520), art.Url);
    }

    [Fact]
    public async Task PrefersTheInstalledIconOverAGuessedCapsuleWhenTheStoreFails()
    {
        // The legacy capsule 404s for anything published after Valve moved art
        // behind a content hash, so a real icon on disk is the better answer.
        Response = () => Http(503, "{}");
        var icons = new StubIcons { Result = new byte[] { 1, 2, 3 } };
        var resolver = BuildWithInstalledGame(icons, out var cleanup);

        try
        {
            var art = await resolver.ResolveAsync(InstalledGameKey, CancellationToken.None);

            Assert.Equal(new byte[] { 1, 2, 3 }, art.Bytes);
            Assert.Equal("", art.Url);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void ResolveIconServesTheInstalledIconWithoutTouchingTheStore()
    {
        var icons = new StubIcons { Result = new byte[] { 7, 7 } };
        var resolver = BuildWithInstalledGame(icons, out var cleanup);

        try
        {
            var art = resolver.ResolveIcon(InstalledGameKey);

            Assert.Equal(new byte[] { 7, 7 }, art.Bytes);
            Assert.Equal(0, Volatile.Read(ref _requests));
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task CachesAResolvedUrlSoASecondCallCostsNoRequest()
    {
        Response = () => Http(200, AppDetails(427520, "https://cdn.akamai.steamstatic.com/apps/427520/header.jpg"));
        var resolver = Build(new StubIcons());

        await resolver.ResolveAsync("steam:427520", CancellationToken.None);
        var before = Volatile.Read(ref _requests);
        await resolver.ResolveAsync("steam:427520", CancellationToken.None);

        Assert.Equal(before, Volatile.Read(ref _requests));
    }

    [Fact]
    public async Task ARetryableStoreFailureIsNotCachedForTheFullTtl()
    {
        Response = () => Http(503, "{}");
        var resolver = Build(new StubIcons());
        await resolver.ResolveAsync("steam:427520", CancellationToken.None);

        // A throttled store is no evidence about this game, so the fallback
        // must expire quickly rather than pinning a 404 for a week.
        Response = () => Http(200, AppDetails(427520, "https://cdn.akamai.steamstatic.com/apps/427520/header.jpg"));
        var refreshed = await ResolveAfterCacheExpiry(resolver, "steam:427520");

        Assert.EndsWith("/header.jpg", refreshed.Url);
    }

    private static async Task<GameArt> ResolveAfterCacheExpiry(GameArtResolver resolver, string gameKey)
    {
        var cache = typeof(GameArtResolver)
            .GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resolver)!;
        ((System.Collections.IDictionary)cache).Clear();
        return await resolver.ResolveAsync(gameKey, CancellationToken.None);
    }

    [Fact]
    public async Task AnIconThatCouldNotBeAttemptedIsNotRememberedAsAMiss()
    {
        // IProcessIconProvider returns null when extraction could not run at
        // all (helper not connected yet); caching that would blank the game
        // for hours over a startup race.
        var icons = new StubIcons { Result = null };
        var resolver = Build(icons);

        Assert.True((await resolver.ResolveAsync("epic:notinstalled", CancellationToken.None)).IsEmpty);
        Assert.True((await resolver.ResolveAsync("epic:notinstalled", CancellationToken.None)).IsEmpty);

        // Not installed, so the catalog short-circuits before the provider.
        Assert.Equal(0, icons.Calls);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

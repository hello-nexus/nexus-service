using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>Exercises SiteIconResolver against a local raw TCP listener (same approach as AlbumArtHdResolverTests) so these run fully offline.</summary>
public sealed class SiteIconResolverTests : IDisposable
{
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x11 };
    private static readonly byte[] IcoBytes = { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10 };

    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), "nexus-site-icon-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SiteIconResolver NewResolver() =>
        new(new SingleClientFactory(), _cacheDir, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    private SiteIconResolver NewResolver(TimeSpan positiveTtl, TimeSpan negativeTtl, TimeSpan? failureTtl = null) =>
        new(new SingleClientFactory(), _cacheDir, positiveTtl, negativeTtl, failureTtl);

    // ── OriginKey ──

    [Theory]
    [InlineData("https://Example.COM/some/page?q=1", "https://example.com")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("http://192.168.1.1:8080/admin", "http://192.168.1.1:8080")]
    public void OriginKeyReducesAUrlToItsOrigin(string url, string expected)
    {
        Assert.Equal(expected, SiteIconResolver.OriginKey(url));
    }

    [Theory]
    [InlineData("steam://rungameid/440")]
    [InlineData("file:///C:/tmp/x.html")]
    [InlineData("   ")]
    public void OriginKeyRejectsNonHttpUrls(string url)
    {
        Assert.Null(SiteIconResolver.OriginKey(url));
    }

    // ── Candidate ranking ──

    [Fact]
    public void RankLinkIconsPrefersTheLargestRasterAndResolvesRelativeHrefs()
    {
        const string html = """
            <link rel="icon" sizes="16x16" href="/small.png">
            <link rel="apple-touch-icon" href="touch.png">
            <link rel='icon' type='image/svg+xml' href='/vector.svg'>
            <link rel="mask-icon" href="/mask.svg" color="#000">
            """;

        var ranked = SiteIconResolver.RankLinkIcons(html, new Uri("https://example.com/en/"));

        Assert.Equal(
            new[]
            {
                "https://example.com/en/touch.png",
                "https://example.com/small.png",
                "https://example.com/vector.svg",
            },
            ranked);
    }

    [Fact]
    public void RankLinkIconsIgnoresNonIconLinks()
    {
        const string html = """<link rel="stylesheet" href="/site.css"><link rel="preload" href="/a.png">""";
        Assert.Empty(SiteIconResolver.RankLinkIcons(html, new Uri("https://example.com/")));
    }

    [Theory]
    [InlineData("32x32 16x16", 32)]
    [InlineData("180X180", 180)]
    [InlineData("any", null)]
    [InlineData("", null)]
    public void ParseSizeTakesTheLargestDeclaredWidth(string sizes, int? expected)
    {
        Assert.Equal(expected, SiteIconResolver.ParseSize(sizes));
    }

    // ── Magic-byte sniffing ──

    [Fact]
    public void SniffContentTypeRecognizesTheImageFormatsAWebViewDecodes()
    {
        Assert.Equal("image/png", SiteIconResolver.SniffContentType(PngBytes));
        Assert.Equal("image/x-icon", SiteIconResolver.SniffContentType(IcoBytes));
        Assert.Equal("image/jpeg", SiteIconResolver.SniffContentType(new byte[] { 0xFF, 0xD8, 0xFF, 0x01 }));
        Assert.Equal("image/svg+xml", SiteIconResolver.SniffContentType(Encoding.UTF8.GetBytes("<svg viewBox='0 0 8 8'/>")));
        Assert.Equal("image/svg+xml", SiteIconResolver.SniffContentType(Encoding.UTF8.GetBytes("<?xml version='1.0'?><svg/>")));
    }

    [Fact]
    public void SniffContentTypeRejectsANonImageBody()
    {
        // A site answering /favicon.ico with its SPA shell is the common miss.
        Assert.Null(SiteIconResolver.SniffContentType(Encoding.UTF8.GetBytes("<!doctype html><html>")));
        Assert.Null(SiteIconResolver.SniffContentType(Array.Empty<byte>()));
    }

    // ── End to end ──

    [Fact]
    public async Task ResolveDownloadsTheLinkedIconAndServesTheSecondCallFromDisk()
    {
        using var server = new StubServer();
        server.ResponseFactory = line => line switch
        {
            var l when l.StartsWith("GET / ", StringComparison.Ordinal) =>
                HttpHtml("""<link rel="apple-touch-icon" href="/touch.png">"""),
            var l when l.StartsWith("GET /touch.png ", StringComparison.Ordinal) => HttpBody("image/png", PngBytes),
            _ => HttpStatus(404),
        };

        var resolver = NewResolver();
        var first = await resolver.ResolveAsync($"{server.BaseUrl}/deep/page", CancellationToken.None);

        Assert.Equal(PngBytes, first.Bytes);
        Assert.Equal("image/png", first.ContentType);
        var afterFirst = server.RequestCount;

        // A second resolver proves the hit came off disk, not the in-memory coalescer.
        var second = await NewResolver().ResolveAsync($"{server.BaseUrl}/other", CancellationToken.None);
        Assert.Equal(PngBytes, second.Bytes);
        Assert.Equal(afterFirst, server.RequestCount);
    }

    [Fact]
    public async Task ResolveFallsBackToFaviconIcoWhenThePageLinksNoIcon()
    {
        using var server = new StubServer();
        server.ResponseFactory = line => line switch
        {
            var l when l.StartsWith("GET / ", StringComparison.Ordinal) => HttpHtml("<title>no icons here</title>"),
            var l when l.StartsWith("GET /favicon.ico ", StringComparison.Ordinal) => HttpBody("image/x-icon", IcoBytes),
            _ => HttpStatus(404),
        };

        var resolved = await NewResolver().ResolveAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(IcoBytes, resolved.Bytes);
        Assert.Equal("image/x-icon", resolved.ContentType);
    }

    [Fact]
    public async Task ResolveFallsBackToFaviconIcoWhenThePageItselfIsUnreachable()
    {
        using var server = new StubServer();
        server.ResponseFactory = line =>
            line.StartsWith("GET /favicon.ico ", StringComparison.Ordinal) ? HttpBody("image/x-icon", IcoBytes) : HttpStatus(500);

        var resolved = await NewResolver().ResolveAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(IcoBytes, resolved.Bytes);
    }

    [Fact]
    public async Task ResolveNegativeCachesASiteWithNoIconAtAll()
    {
        using var server = new StubServer();
        server.ResponseFactory = _ => HttpStatus(404);

        var resolved = await NewResolver().ResolveAsync(server.BaseUrl, CancellationToken.None);
        Assert.Empty(resolved.Bytes);
        var afterFirst = server.RequestCount;

        Assert.Empty((await NewResolver().ResolveAsync(server.BaseUrl, CancellationToken.None)).Bytes);
        Assert.Equal(afterFirst, server.RequestCount);
    }

    [Fact]
    public async Task ResolveReturnsEmptyForANonHttpUrlWithoutTouchingTheNetwork()
    {
        using var server = new StubServer();
        server.ResponseFactory = _ => HttpBody("image/png", PngBytes);

        var resolved = await NewResolver().ResolveAsync("steam://rungameid/440", CancellationToken.None);

        Assert.Empty(resolved.Bytes);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task ResolveRejectsAnIconBodyThatIsNotAnImage()
    {
        using var server = new StubServer();
        // Declaring image/png does not make it one; only the magic bytes count.
        server.ResponseFactory = line =>
            line.StartsWith("GET / ", StringComparison.Ordinal)
                ? HttpHtml("""<link rel="icon" href="/icon.png">""")
                : HttpBody("image/png", Encoding.UTF8.GetBytes("<!doctype html><html>"));

        var resolved = await NewResolver().ResolveAsync(server.BaseUrl, CancellationToken.None);

        Assert.Empty(resolved.Bytes);
    }


    [Fact]
    public async Task ResolveCoalescesConcurrentCallsForOneOrigin()
    {
        using var server = new StubServer();
        server.ResponseDelay = TimeSpan.FromMilliseconds(150);
        server.ResponseFactory = line =>
            line.StartsWith("GET / ", StringComparison.Ordinal)
                ? HttpHtml("""<link rel="icon" href="/i.png">""")
                : HttpBody("image/png", PngBytes);

        var resolver = NewResolver();
        var all = await Task.WhenAll(
            resolver.ResolveAsync($"{server.BaseUrl}/a", CancellationToken.None),
            resolver.ResolveAsync($"{server.BaseUrl}/b", CancellationToken.None),
            resolver.ResolveAsync($"{server.BaseUrl}/c", CancellationToken.None));

        Assert.All(all, r => Assert.Equal(PngBytes, r.Bytes));
        // One page + one icon, not three of each.
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task ACancelledRequestDoesNotPinTheOriginIconless()
    {
        using var server = new StubServer();
        server.ResponseDelay = TimeSpan.FromSeconds(5);
        server.ResponseFactory = _ => HttpBody("image/x-icon", IcoBytes);

        // A short failure TTL is what separates "cancelled mid-fetch" from a
        // resolved miss; without the split this origin would stay empty.
        var resolver = NewResolver(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.Zero);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        // The route swallows an abort and answers empty; what must not happen is
        // that empty being written as a full-length negative cache entry.
        Assert.Empty((await resolver.ResolveAsync(server.BaseUrl, cts.Token)).Bytes);

        server.ResponseDelay = TimeSpan.Zero;
        var retried = await NewResolver(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.Zero)
            .ResolveAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(IcoBytes, retried.Bytes);
    }

    [Fact]
    public async Task AnExpiredPositiveEntryIsRefetched()
    {
        using var server = new StubServer();
        server.ResponseFactory = _ => HttpBody("image/x-icon", IcoBytes);

        var resolver = NewResolver(TimeSpan.Zero, TimeSpan.FromMinutes(5));
        Assert.Equal(IcoBytes, (await resolver.ResolveAsync(server.BaseUrl, CancellationToken.None)).Bytes);
        var afterFirst = server.RequestCount;

        Assert.Equal(IcoBytes, (await NewResolver(TimeSpan.Zero, TimeSpan.FromMinutes(5))
            .ResolveAsync(server.BaseUrl, CancellationToken.None)).Bytes);
        Assert.True(server.RequestCount > afterFirst);
    }

    [Fact]
    public async Task TheFaviconFallbackSurvivesAPageDeclaringManyIconLinks()
    {
        using var server = new StubServer();
        var links = string.Concat(Enumerable.Range(0, 6).Select(i => $"<link rel=\"icon\" sizes=\"{64 + i}x{64 + i}\" href=\"/dead{i}.png\">"));
        server.ResponseFactory = line => line switch
        {
            var l when l.StartsWith("GET / ", StringComparison.Ordinal) => HttpHtml(links),
            var l when l.StartsWith("GET /favicon.ico ", StringComparison.Ordinal) => HttpBody("image/x-icon", IcoBytes),
            _ => HttpStatus(404),
        };

        var resolved = await NewResolver().ResolveAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(IcoBytes, resolved.Bytes);
    }

    [Fact]
    public async Task ALargePageStillGetsItsLinkTagsRanked()
    {
        using var server = new StubServer();
        // Past the icon cap: a shared cap would truncate the read and silently
        // degrade every routine page to /favicon.ico.
        var filler = new string('x', 400 * 1024);
        server.ResponseFactory = line => line switch
        {
            var l when l.StartsWith("GET / ", StringComparison.Ordinal) =>
                HttpHtml($"<!-- {filler} --><link rel=\"apple-touch-icon\" href=\"/touch.png\">"),
            var l when l.StartsWith("GET /touch.png ", StringComparison.Ordinal) => HttpBody("image/png", PngBytes),
            _ => HttpBody("image/x-icon", IcoBytes),
        };

        var resolved = await NewResolver().ResolveAsync(server.BaseUrl, CancellationToken.None);

        Assert.Equal(PngBytes, resolved.Bytes);
    }

    // ── Harness ──

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static byte[] HttpHtml(string body) => HttpBody("text/html; charset=utf-8", Encoding.UTF8.GetBytes(body));

    private static byte[] HttpBody(string contentType, byte[] body)
    {
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, header.Length);
        return result;
    }

    private static byte[] HttpStatus(int code) =>
        Encoding.UTF8.GetBytes($"HTTP/1.1 {code} Err\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    /// <summary>Replies with whatever ResponseFactory returns for the request line.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private readonly List<string> _requestLines = new();

        public int Port { get; }
        public Func<string, byte[]> ResponseFactory { get; set; } = _ => HttpStatus(404);

        /// <summary>Held before replying, so a test can cancel mid-flight.</summary>
        public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

        public int RequestCount
        {
            get { lock (_lock) { return _requestLines.Count; } }
        }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public StubServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var requestLine = await ReadRequestLineAsync(stream).ConfigureAwait(false);
                lock (_lock)
                {
                    _requestLines.Add(requestLine);
                }
                if (ResponseDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ResponseDelay).ConfigureAwait(false);
                }
                await stream.WriteAsync(ResponseFactory(requestLine)).ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var eol = text.IndexOf('\r');
            return eol < 0 ? text : text[..eol];
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

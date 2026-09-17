using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Klipy;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Klipy;

/// <summary>
/// KlipyCatalog's HTTP path against a local raw TCP listener (same approach as
/// YahooStockQuoteProviderTests), so these run offline with no live Klipy call.
/// </summary>
public sealed class KlipyCatalogTests
{
    private const string AppKey = "test-key";

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class InertConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly object _lock = new();
        private readonly List<string> _requestLines = new();

        public int Port { get; }
        public Func<string, string> ResponseFactory { get; set; } = _ => Json("{}");

        public IReadOnlyList<string> RequestLines
        {
            get { lock (_lock) { return _requestLines.ToArray(); } }
        }

        public StubServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
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
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
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
                var response = ResponseFactory(requestLine);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response)).ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();
            while (!text.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            var full = text.ToString();
            var idx = full.IndexOf("\r\n", StringComparison.Ordinal);
            return idx >= 0 ? full[..idx] : full;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _cts.Dispose();
        }
    }

    private static string Json(string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static string Binary(byte[] bytes, string contentType)
    {
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        var buffer = new byte[header.Length + bytes.Length];
        header.CopyTo(buffer, 0);
        bytes.CopyTo(buffer, header.Length);
        return Encoding.Latin1.GetString(buffer);
    }

    private static string Item(string slug, string host, string title = "A cat", string type = "gif") =>
        "{\"slug\":\"" + slug + "\",\"title\":\"" + title + "\",\"type\":\"" + type + "\"," +
        "\"blur_preview\":\"data:image/jpeg;base64,AAA\"," +
        "\"file\":{\"hd\":{\"gif\":{\"url\":\"" + host + "/" + slug + ".gif\",\"width\":220,\"height\":164,\"size\":244409}," +
        "\"mp4\":{\"url\":\"" + host + "/" + slug + ".mp4\",\"width\":220,\"height\":164,\"size\":56139}," +
        "\"webp\":{\"url\":\"" + host + "/" + slug + "-hd.webp\",\"width\":220,\"height\":164,\"size\":140740}}," +
        "\"sm\":{\"webp\":{\"url\":\"" + host + "/" + slug + ".webp\",\"width\":220,\"height\":164,\"size\":137582}}}}";

    private static string Page(string itemsJson, bool hasNext = false) =>
        "{\"result\":true,\"data\":{\"data\":[" + itemsJson + "],\"has_next\":" + (hasNext ? "true" : "false") + "}}";

    // The stub serves media from the same loopback origin, so these use the
    // relaxed guard; the production guard has its own tests below.
    private static KlipyCatalog Make(StubServer server) =>
        new(new SingleClientFactory(), new InertConfigStore(), $"http://127.0.0.1:{server.Port}", () => AppKey,
            url => !string.IsNullOrEmpty(url));

    private static KlipyCatalog MakeGuarded(StubServer server) =>
        new(new SingleClientFactory(), new InertConfigStore(), $"http://127.0.0.1:{server.Port}", () => AppKey);

    [Fact]
    public async Task Search_maps_items_and_keeps_has_next()
    {
        using var server = new StubServer();
        var host = $"http://127.0.0.1:{server.Port}";
        server.ResponseFactory = _ => Json(Page(Item("happy-cat", host), hasNext: true));
        var catalog = Make(server);

        var result = await catalog.SearchAsync("cat", 1, CancellationToken.None);

        Assert.False(result.Error);
        Assert.True(result.HasNext);
        var item = Assert.Single(result.Items);
        Assert.Equal("happy-cat", item.Slug);
        Assert.Equal("A cat", item.Title);
        Assert.Equal(220, item.Width);
        Assert.Equal(164, item.Height);
        Assert.StartsWith("data:image/jpeg;base64,", item.BlurPreview);
    }

    [Fact]
    public async Task Empty_query_asks_for_trending_not_search()
    {
        using var server = new StubServer();
        server.ResponseFactory = _ => Json(Page(""));
        var catalog = Make(server);

        await catalog.SearchAsync("   ", 1, CancellationToken.None);

        var line = Assert.Single(server.RequestLines);
        Assert.Contains("/gifs/trending?", line);
        Assert.DoesNotContain("/gifs/search", line);
    }

    [Fact]
    public async Task Search_sends_the_app_key_as_a_path_segment_and_escapes_the_query()
    {
        using var server = new StubServer();
        server.ResponseFactory = _ => Json(Page(""));
        var catalog = Make(server);

        await catalog.SearchAsync("black cat", 3, CancellationToken.None);

        var line = Assert.Single(server.RequestLines);
        Assert.Contains($"/{AppKey}/gifs/search", line);
        Assert.Contains("q=black%20cat", line);
        Assert.Contains("page=3", line);
        Assert.Contains("customer_id=", line);
    }

    [Fact]
    public async Task Ad_slots_are_dropped_from_results()
    {
        using var server = new StubServer();
        var host = $"http://127.0.0.1:{server.Port}";
        server.ResponseFactory = _ => Json(Page(
            "{\"type\":\"ad\",\"content\":\"<iframe></iframe>\",\"width\":300,\"height\":250}," + Item("real-gif", host)));
        var catalog = Make(server);

        var result = await catalog.SearchAsync("cat", 1, CancellationToken.None);

        Assert.Equal("real-gif", Assert.Single(result.Items).Slug);
    }

    [Fact]
    public async Task Missing_app_key_reports_unavailable_without_calling_out()
    {
        using var server = new StubServer();
        var catalog = new KlipyCatalog(
            new SingleClientFactory(), new InertConfigStore(), $"http://127.0.0.1:{server.Port}", () => null,
            url => !string.IsNullOrEmpty(url));

        var result = await catalog.SearchAsync("cat", 1, CancellationToken.None);

        Assert.True(result.Error);
        Assert.Empty(server.RequestLines);
    }

    [Fact]
    public async Task Upstream_failure_reports_error_rather_than_throwing()
    {
        using var server = new StubServer();
        server.ResponseFactory = _ => "HTTP/1.1 500 Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var catalog = Make(server);

        var result = await catalog.SearchAsync("cat", 1, CancellationToken.None);

        Assert.True(result.Error);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Thumbnail_for_an_unsearched_slug_fetches_nothing()
    {
        using var server = new StubServer();
        var catalog = Make(server);

        var bytes = await catalog.GetThumbAsync("never-searched");

        Assert.Empty(bytes);
        Assert.Empty(server.RequestLines);
    }

    [Fact]
    public async Task Thumbnail_is_fetched_once_and_then_served_from_cache()
    {
        using var server = new StubServer();
        var host = $"http://127.0.0.1:{server.Port}";
        var payload = new byte[] { 1, 2, 3, 4 };
        server.ResponseFactory = line => line.Contains(".webp")
            ? Binary(payload, "image/webp")
            : Json(Page(Item("happy-cat", host)));
        var catalog = Make(server);
        await catalog.SearchAsync("cat", 1, CancellationToken.None);

        var first = await catalog.GetThumbAsync("happy-cat");
        var second = await catalog.GetThumbAsync("happy-cat");

        Assert.Equal(payload, first);
        Assert.Equal(payload, second);
        Assert.Equal(2, server.RequestLines.Count); // the search + one thumbnail fetch
    }

    [Fact]
    public async Task Download_prefers_the_mp4_and_names_the_file_by_its_container()
    {
        using var server = new StubServer();
        var host = $"http://127.0.0.1:{server.Port}";
        var mp4 = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 };
        server.ResponseFactory = line => line.Contains(".mp4")
            ? Binary(mp4, "video/mp4")
            : Json(Page(Item("happy-cat", host)));
        var catalog = Make(server);
        await catalog.SearchAsync("cat", 1, CancellationToken.None);
        var dir = Path.Combine(Path.GetTempPath(), $"klipy-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);

        try
        {
            var path = await catalog.DownloadAsync("happy-cat", dir, CancellationToken.None);

            Assert.NotNull(path);
            Assert.Equal(".mp4", Path.GetExtension(path));
            Assert.Equal(mp4, await File.ReadAllBytesAsync(path!));
            Assert.DoesNotContain(server.RequestLines, l => l.Contains(".gif"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Download_falls_back_to_the_gif_when_there_is_no_mp4()
    {
        using var server = new StubServer();
        var host = $"http://127.0.0.1:{server.Port}";
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        var gifOnly = Item("old-cat", host).Replace(
            "\"mp4\":{\"url\":\"" + host + "/old-cat.mp4\",\"width\":220,\"height\":164,\"size\":56139},", "");
        server.ResponseFactory = line => line.Contains(".gif")
            ? Binary(gif, "image/gif")
            : Json(Page(gifOnly));
        var catalog = Make(server);
        await catalog.SearchAsync("cat", 1, CancellationToken.None);
        var dir = Path.Combine(Path.GetTempPath(), $"klipy-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);

        try
        {
            var path = await catalog.DownloadAsync("old-cat", dir, CancellationToken.None);

            Assert.NotNull(path);
            Assert.Equal(".gif", Path.GetExtension(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Download_refuses_a_slug_no_search_produced()
    {
        using var server = new StubServer();
        var catalog = Make(server);

        var path = await catalog.DownloadAsync("never-searched", Path.GetTempPath(), CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(server.RequestLines);
    }

    [Theory]
    [InlineData("https://static.klipy.com/ii/abc/x.gif", true)]
    [InlineData("https://klipy.com/x.gif", true)]
    [InlineData("http://static.klipy.com/x.gif", false)]
    [InlineData("https://static.klipy.com.evil.test/x.gif", false)]
    [InlineData("https://127.0.0.1:9400/media/library", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_https_klipy_hosts_are_accepted_as_media_urls(string? url, bool expected)
    {
        Assert.Equal(expected, KlipyCatalog.IsKlipyMediaUrl(url));
    }

    [Fact]
    public async Task An_item_without_dimensions_is_dropped_rather_than_cropped_blind()
    {
        using var server = new StubServer();
        var host = $"http://127.0.0.1:{server.Port}";
        var sized = Item("has-size", host);
        // Every variant loses its size.
        var unsized = Item("no-size", host).Replace("\"width\":220,\"height\":164,", "");
        server.ResponseFactory = _ => Json(Page(unsized + "," + sized));
        var catalog = Make(server);

        var result = await catalog.SearchAsync("cat", 1, CancellationToken.None);

        Assert.Equal("has-size", Assert.Single(result.Items).Slug);
        Assert.Null(catalog.Resolve("no-size"));
    }

    [Fact]
    public async Task An_item_pointing_off_host_is_dropped_from_results()
    {
        using var server = new StubServer();
        var evil = Item("evil-gif", "http://127.0.0.1:9");
        server.ResponseFactory = _ => Json(Page(evil));
        var catalog = MakeGuarded(server);

        var result = await catalog.SearchAsync("cat", 1, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Null(catalog.Resolve("evil-gif"));
    }

    [Theory]
    [InlineData("happy-cat", true)]
    [InlineData("a_b-C9", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    public void Slug_validation_refuses_anything_outside_the_safe_set(string? slug, bool expected)
    {
        Assert.Equal(expected, KlipyCatalog.IsValidSlug(slug));
    }
}

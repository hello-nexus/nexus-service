using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

public class AppProxyServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppRegistry _registry;

    public AppProxyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-proxy-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        WriteWidget("com.hellonexus.allowed", new[] { "api.weather.gov", "*.example.com" });
        WriteWidget("com.hellonexus.empty",   Array.Empty<string>());
        _registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteWidget(string id, string[] netFetch)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var manifest = new
        {
            schema = "nexus.app/1",
            id,
            name = id,
            version = "1.0.0",
            min_nexus_version = "0.42.0",
            surfaces = new[] { "dashboard" },
            sizes = new[] { "2x2" },
            capabilities = new Dictionary<string, object>
            {
                ["sensors.read"] = Array.Empty<string>(),
                ["rgb.read"] = false,
                ["rgb.write"] = false,
                ["net.fetch"] = netFetch,
                ["config"] = true,
            },
            runtime = "sdk",
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export const mount = () => {};");
    }

    private AppProxyService MakeService(HttpMessageHandler handler)
    {
        var factory = new StubHttpFactory(new HttpClient(handler));
        return new AppProxyService(factory, _registry);
    }

    private (AppProxyService Service, StubHttpFactory Factory) MakeTracked(HttpMessageHandler handler)
    {
        var factory = new StubHttpFactory(new HttpClient(handler));
        return (new AppProxyService(factory, _registry), factory);
    }

    [Fact]
    public async Task Rejects_unknown_widget()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.unknown", Url = "https://api.weather.gov" });
        Assert.False(resp.Ok);
        Assert.Contains("not installed", resp.Error);
    }

    [Fact]
    public async Task Rejects_non_https_url()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "http://api.weather.gov" });
        Assert.False(resp.Ok);
        Assert.Contains("https", resp.Error);
    }

    [Fact]
    public async Task Rejects_host_outside_manifest_allowlist()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://evil.test/data" });
        Assert.False(resp.Ok);
        Assert.Contains("allowlist", resp.Error);
    }

    [Fact]
    public async Task Accepts_wildcard_subdomain_match()
    {
        var svc = MakeService(new StubHandler("application/json", "{\"x\":1}"));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://www.example.com/data" });
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Body);
        Assert.Equal(1, resp.Body!.Value.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task Caps_response_body_at_1_MiB()
    {
        var big = new string('a', 1024 * 1024 + 200);
        var svc = MakeService(new StubHandler("text/plain", big));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/big" });
        Assert.NotNull(resp.Error);
        Assert.Contains("truncated", resp.Error);
        Assert.NotNull(resp.BodyText);
        Assert.True(resp.BodyText!.Length <= AppProxyService.MaxBodyBytes);
    }

    [Fact]
    public async Task Binary_body_arrives_byte_exact_as_base64()
    {
        var bytes = new byte[256];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        var svc = MakeService(new BytesHandler("application/vnd.mapbox-vector-tile", bytes));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/tile" });
        Assert.True(resp.Ok);
        Assert.Null(resp.BodyText);
        Assert.Null(resp.Body);
        Assert.Equal(bytes, Convert.FromBase64String(resp.BodyBase64!));
    }

    [Fact]
    public async Task Binary_body_over_cap_is_truncated_with_error()
    {
        var bytes = new byte[AppProxyService.MaxBinaryBodyBytes + 500];
        var svc = MakeService(new BytesHandler("application/octet-stream", bytes));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/big" });
        Assert.Contains("truncated", resp.Error);
        Assert.Equal(AppProxyService.MaxBinaryBodyBytes, Convert.FromBase64String(resp.BodyBase64!).Length);
    }

    [Fact]
    public async Task Binary_body_above_text_cap_but_within_binary_cap_is_delivered_whole()
    {
        var bytes = new byte[AppProxyService.MaxBodyBytes * 3];
        new Random(7).NextBytes(bytes);
        var svc = MakeService(new BytesHandler("application/vnd.mapbox-vector-tile", bytes));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/tile" });
        Assert.True(resp.Ok);
        Assert.Null(resp.Error);
        Assert.Equal(bytes, Convert.FromBase64String(resp.BodyBase64!));
    }

    [Fact]
    public async Task Rate_limit_blocks_the_request_after_the_cap_for_one_app_only()
    {
        var svc = new AppProxyService(new FreshClientFactory(new StubHandler("text/plain", "x")), _registry);
        var req = new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/r" };
        for (int i = 0; i < AppProxyService.MaxRequestsPerMinute; i++)
        {
            Assert.True((await svc.ExecuteAsync(req)).Ok);
        }
        var over = await svc.ExecuteAsync(req);
        Assert.Contains("rate limit", over.Error);
    }

    [Fact]
    public async Task Concurrency_is_bounded_per_app_and_a_waiter_proceeds_when_a_slot_frees()
    {
        var gate = new TaskCompletionSource();
        var handler = new GatedHandler(gate.Task);
        var svc = new AppProxyService(new FreshClientFactory(handler), _registry) { ConcurrencyWait = TimeSpan.FromMilliseconds(100) };
        var req = new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/c" };
        var running = new List<Task<AppProxyResponse>>();
        for (int i = 0; i < AppProxyService.MaxConcurrentPerApp; i++) running.Add(svc.ExecuteAsync(req));
        while (handler.Started < AppProxyService.MaxConcurrentPerApp) await Task.Delay(5);

        var refused = await svc.ExecuteAsync(req);
        Assert.Contains("concurrent", refused.Error);
        Assert.Equal(AppProxyService.MaxConcurrentPerApp, handler.Started);

        svc.ConcurrencyWait = TimeSpan.FromSeconds(30);
        var waiter = svc.ExecuteAsync(req);
        gate.SetResult();
        Assert.True((await waiter).Ok);
        foreach (var t in running) Assert.True((await t).Ok);
    }

    [Fact]
    public async Task Handler_decodes_gzip_upstream_bodies_byte_exact()
    {
        var original = new byte[20_000];
        new Random(11).NextBytes(original);
        Array.Fill(original, (byte)7, 5000, 10_000);
        using var gz = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(original);
        }
        var compressed = gz.ToArray();

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string? acceptEncoding = null;
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            acceptEncoding = ctx.Request.Headers["Accept-Encoding"];
            ctx.Response.ContentType = "application/vnd.mapbox-vector-tile";
            ctx.Response.AddHeader("Content-Encoding", "gzip");
            ctx.Response.ContentLength64 = compressed.Length;
            await ctx.Response.OutputStream.WriteAsync(compressed);
            ctx.Response.Close();
        });

        using var http = new HttpClient(AppProxyService.CreateHandler());
        var got = await http.GetByteArrayAsync($"http://127.0.0.1:{port}/t");
        await serve;
        Assert.Equal(original, got);
        Assert.Contains("gzip", acceptEncoding);
    }

    [Fact]
    public void Handler_keeps_redirects_off_and_enables_all_decompression()
    {
        using var h = AppProxyService.CreateHandler();
        Assert.False(h.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.All, h.AutomaticDecompression);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("application/ld+json")]
    [InlineData("application/xml")]
    [InlineData("image/svg+xml")]
    [InlineData("application/javascript")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("")]
    public void Text_media_types_are_not_binary(string mediaType)
    {
        Assert.False(AppProxyService.IsBinaryMediaType(mediaType));
    }

    [Theory]
    [InlineData("application/vnd.mapbox-vector-tile")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    [InlineData("application/x-protobuf")]
    public void Other_media_types_are_binary(string mediaType)
    {
        Assert.True(AppProxyService.IsBinaryMediaType(mediaType));
    }

    [Fact]
    public async Task Text_body_never_carries_base64()
    {
        var svc = MakeService(new StubHandler("text/plain", "hello"));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/t" });
        Assert.Equal("hello", resp.BodyText);
        Assert.Null(resp.BodyBase64);
    }

    [Fact]
    public async Task Json_body_is_decoded_into_Body()
    {
        var svc = MakeService(new StubHandler("application/json", "{\"a\": [1, 2], \"b\": \"hi\"}"));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/json" });
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Body);
        var b = resp.Body!.Value.GetProperty("b").GetString();
        Assert.Equal("hi", b);
    }

    [Fact]
    public async Task Drops_forbidden_request_headers()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer leaked",
            ["Cookie"] = "session=evil",
            ["X-Custom"] = "ok",
        };
        await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/x",
            Headers = headers,
        });
        Assert.NotNull(capture.LastRequest);
        Assert.False(capture.LastRequest!.Headers.Contains("Authorization"));
        Assert.False(capture.LastRequest.Headers.Contains("Cookie"));
        Assert.True(capture.LastRequest.Headers.Contains("X-Custom"));
    }

    [Fact]
    public async Task Refuses_when_manifest_allowlist_is_empty()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.empty", Url = "https://api.weather.gov" });
        Assert.False(resp.Ok);
        Assert.Contains("allowlist", resp.Error);
    }

    [Fact]
    public async Task Sets_default_user_agent_when_widget_does_not_supply_one()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/x",
        });
        Assert.NotNull(capture.LastRequest);
        Assert.True(capture.LastRequest!.Headers.Contains("User-Agent"));
        var ua = string.Join(',', capture.LastRequest.Headers.GetValues("User-Agent"));
        Assert.Contains("Nexus-Widget-Proxy", ua);
    }

    [Fact]
    public async Task Widget_supplied_user_agent_overrides_the_default()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/x",
            Headers = new Dictionary<string, string> { ["User-Agent"] = "MyCoolWidget/2.0" },
        });
        var ua = string.Join(',', capture.LastRequest!.Headers.GetValues("User-Agent"));
        Assert.Equal("MyCoolWidget/2.0", ua);
        Assert.DoesNotContain("Nexus-Widget-Proxy", ua);
    }

    [Theory]
    [InlineData("https://api.hellonexus.com/hyte/me", "api.hellonexus.com", true)]
    [InlineData("https://API.HelloNexus.com/hyte/me", "api.hellonexus.com", true)]
    [InlineData("https://hyte.com/account", "api.hellonexus.com", false)]
    [InlineData("https://api.hellonexus.com.evil.test/x", "api.hellonexus.com", false)]
    [InlineData("https://evil.test/api.hellonexus.com", "api.hellonexus.com", false)]
    [InlineData("https://api.hellonexus.com/x", "", false)]
    public void Cloud_api_host_is_matched_whole(string url, string cloudHost, bool expected)
    {
        Assert.Equal(expected, AppProxyService.IsCloudApiHost(new Uri(url), cloudHost));
    }

    [Theory]
    [InlineData("https://api.hellonexus.com", "api.hellonexus.com")]
    [InlineData("https://staging.hellonexus.com:8443/", "staging.hellonexus.com")]
    [InlineData("not-a-url", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Endpoint_host_parsing_is_tolerant(string? baseUrl, string expected)
    {
        Assert.Equal(expected, CloudApiEndpoint.HostOf(baseUrl));
    }

    [Fact]
    public void Credential_targets_whichever_host_the_endpoint_resolves_to()
    {
        // Tolerant on purpose: the base URL is environment-derived, so the test
        // pins the relationship rather than a value.
        var host = CloudApiEndpoint.HostOf(CloudApiEndpoint.BaseUrl);

        Assert.Equal(
            string.Equals(host, "api.hellonexus.com", StringComparison.OrdinalIgnoreCase),
            AppProxyService.IsCloudApiHost(new Uri("https://api.hellonexus.com/hyte/me")));
        Assert.False(AppProxyService.IsCloudApiHost(new Uri("https://hyte.com/account")));
    }

    [Fact]
    public async Task Every_fetch_runs_on_the_client_registered_without_redirects()
    {
        var (svc, factory) = MakeTracked(new CapturingHandler());

        await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/points",
        });

        Assert.Equal(AppProxyService.ClientName, factory.LastName);
    }

    [Theory]
    [InlineData("application/json", "application/json", null)]
    [InlineData("application/json; charset=utf-8", "application/json", "utf-8")]
    [InlineData("application/json; charset=UTF-8", "application/json", "UTF-8")]
    [InlineData("application/json; charset=iso-8859-1", "application/json", "utf-8")]
    [InlineData("text/csv; charset=", "text/plain", "utf-8")]
    [InlineData("text/csv; charset=\"\"", "text/csv", "utf-8")]
    [InlineData("APPLICATION/JSON", "APPLICATION/JSON", null)]
    [InlineData(null, "text/plain", "utf-8")]
    [InlineData("", "text/plain", "utf-8")]
    [InlineData("   ", "text/plain", "utf-8")]
    [InlineData("nonsense", "text/plain", "utf-8")]
    public void Widget_content_type_is_honoured_with_a_plain_text_fallback(string? declared, string media, string? charset)
    {
        var resolved = AppProxyService.ResolveContentType(declared);

        Assert.Equal(media, resolved.MediaType);
        Assert.Equal(charset, resolved.CharSet);
    }

    [Fact]
    public async Task Widget_supplied_client_credential_is_dropped_and_its_content_type_kept()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        var resp = await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/points",
            Method = "POST",
            Body = "{\"a\":1}",
            Headers = new Dictionary<string, string>
            {
                ["X-Nexus-Client"] = "forged",
                ["content-type"] = "application/json",
            },
        });

        Assert.True(resp.Ok);
        Assert.NotNull(capture.LastRequest);
        Assert.False(capture.LastRequest!.Headers.Contains("X-Nexus-Client"));
        Assert.Equal("application/json", capture.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Body_without_a_declared_content_type_stays_plain_text()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        var resp = await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/points",
            Method = "POST",
            Body = "plain",
        });

        Assert.True(resp.Ok);
        Assert.Equal("text/plain", capture.LastRequest!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", capture.LastRequest.Content.Headers.ContentType!.CharSet);
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly Task _gate;
        private int _started;
        public GatedHandler(Task gate) { _gate = gate; }
        public int Started => Volatile.Read(ref _started);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            await _gate;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x", Encoding.UTF8, "text/plain") };
        }
    }

    private sealed class FreshClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FreshClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpFactory(HttpClient client) { _client = client; }
        public string? LastName { get; private set; }
        public HttpClient CreateClient(string name) { LastName = name; return _client; }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _contentType;
        private readonly string _body;
        public StubHandler(string contentType, string body) { _contentType = contentType; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            };
            return Task.FromResult(msg);
        }
    }

    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly string _contentType;
        private readonly byte[] _body;
        public BytesHandler(string contentType, byte[] body) { _contentType = contentType; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(msg);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("network should not be invoked");
        }
    }
}

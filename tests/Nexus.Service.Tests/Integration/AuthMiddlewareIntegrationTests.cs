using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Exercises the composed auth/CORS/security pipeline end-to-end through the
/// real Program.cs middleware chain (UseRouting → UseCors → UseNexusPathAuth)
/// - the wiring the per-helper unit tests (AuthRequestPolicyTests,
/// CorsConfigTests) could not cover. Uses <see cref="TestServer.SendAsync"/>
/// to set RemoteIpAddress for the loopback-gated paths.
/// </summary>
public sealed class AuthMiddlewareIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public AuthMiddlewareIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    // ── Token gate on a mapped API endpoint ──────────────────────────────────

    [Fact]
    public async Task Protected_endpoint_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/defaults");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_with_desktop_token_returns_200()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);

        var res = await client.GetAsync("/defaults");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_with_wrong_token_returns_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-the-token");

        var res = await client.GetAsync("/defaults");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── /pair is loopback-only ───────────────────────────────────────────────

    [Fact]
    public async Task Pair_from_lan_is_forbidden()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/pair";
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Pair_from_loopback_returns_ok()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/pair";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // ── LocalhostOnly route: 404 from LAN, token still required on loopback ───

    [Fact]
    public async Task LocalhostOnly_route_404s_from_lan()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/service/startup-mode";
            c.Request.Headers.Authorization = "Bearer " + Token; // valid token, wrong network
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        // 404 (not 401/403): the route's existence is never leaked to the LAN.
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task LocalhostOnly_route_401s_from_loopback_without_token()
    {
        // Neutralizes DNS-rebind: a rebound page reaches 127.0.0.1 but has no token.
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/service/startup-mode";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task LocalhostOnly_route_allows_loopback_with_token()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/service/startup-mode";
            c.Request.Headers.Authorization = "Bearer " + Token;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // ── CORS ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cors_rejects_foreign_origin()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/ping");
        req.Headers.Add("Origin", "https://evil.com");

        var res = await client.SendAsync(req);

        var acao = res.Headers.TryGetValues("Access-Control-Allow-Origin", out var v)
            ? string.Join(",", v) : null;
        Assert.DoesNotContain("evil.com", acao ?? string.Empty);
    }

    [Fact]
    public async Task Cors_allows_service_own_origin()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/ping");
        req.Headers.Add("Origin", "http://localhost:9400");

        var res = await client.SendAsync(req);

        Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"),
            "expected an Access-Control-Allow-Origin header for the service's own loopback origin");
    }

    // ── Security headers ─────────────────────────────────────────────────────

    [Fact]
    public async Task Security_headers_present_on_panel_shell()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/panel/phone");

        Assert.True(res.Headers.Contains("Content-Security-Policy"),
            "panel shell must carry a CSP header");
        Assert.True(res.Headers.Contains("X-Content-Type-Options"),
            "panel shell must carry X-Content-Type-Options: nosniff");
    }

    // Pins frame-src: without it child-src governs frames and the YouTube embed
    // the avatar immersive view docks is refused.
    [Fact]
    public async Task Csp_frame_src_admits_the_youtube_embed_player()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/panel/phone");

        var csp = string.Join(" ", res.Headers.GetValues("Content-Security-Policy"));
        var frameSrc = csp.Split(';').Select(d => d.Trim()).FirstOrDefault(d => d.StartsWith("frame-src ", StringComparison.Ordinal));
        Assert.NotNull(frameSrc);
        Assert.Contains("https://www.youtube.com/embed/", frameSrc);
        Assert.Contains("https://www.youtube-nocookie.com/embed/", frameSrc);
        Assert.Contains("https://build.hellonexus.com", frameSrc);
        Assert.DoesNotContain("frame-src *", csp);
    }

    // ── Desktop token is loopback-only (a leaked token can't drive from LAN) ──

    [Fact]
    public async Task Desktop_token_from_lan_is_rejected()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/defaults";
            c.Request.Headers.Authorization = "Bearer " + Token; // valid token, wrong network
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Desktop_token_from_loopback_is_accepted()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/defaults";
            c.Request.Headers.Authorization = "Bearer " + Token;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // ── Host header: a DNS-rebound browser page is a loopback remote too ──────

    [Fact]
    public async Task Pair_from_loopback_with_a_foreign_host_header_is_forbidden()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/pair";
            c.Request.Host = new HostString("evil.example", 9400);
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Desktop_token_with_a_foreign_host_header_is_rejected()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/defaults";
            c.Request.Headers.Authorization = "Bearer " + Token;
            c.Request.Host = new HostString("evil.example", 9400);
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Desktop_token_with_an_ip_literal_host_header_is_accepted()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/defaults";
            c.Request.Headers.Authorization = "Bearer " + Token;
            c.Request.Host = new HostString("127.0.0.1", 9400);
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // ── A static-asset suffix never opens a mutation or a LocalhostOnly route ──

    [Fact]
    public async Task Static_suffix_on_a_mutation_still_requires_a_token()
    {
        // POST /streamdeck/decks/{serial} upserts a record for any serial; a
        // ".json" serial used to ride the static-asset lane past every gate.
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/streamdeck/decks/x.json";
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Static_suffix_on_a_localhost_only_route_from_lan_is_404()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/streamdeck/decks/x.json";
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    // ── Dashboard shell / index.html not served off the loopback interface ────

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/\\index.html")]            // Windows backslash separator
    [InlineData("/\\panel\\..\\index.html")] // backslash-smuggled parent segment
    public async Task Dashboard_shell_root_from_lan_is_404(string path)
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = path;
            c.Request.Headers.Accept = "text/html";
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_client_route_from_lan_is_404()
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/monitoring";
            c.Request.Headers.Accept = "text/html";
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }
}

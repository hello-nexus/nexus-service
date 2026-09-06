using Nexus.Service.Auth;
using Nexus.Service.Routes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Nexus.Service.Tests;

public class AuthRequestPolicyTests
{
    [Fact]
    public void ExtractBearerOrQueryToken_AcceptsBearerCaseInsensitive()
    {
        var ctx = NewContext("GET", "/system/cpu/model");
        ctx.Request.Headers.Authorization = "bearer abc123";

        Assert.Equal("abc123", AuthRequestPolicy.ExtractBearerOrQueryToken(ctx));
    }

    [Fact]
    public void PanelSession_AllowsPanelWidgetRoutes()
    {
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("GET", "/ws")));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/lighting/animate/headless-start", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/cooling/profile/Balanced", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("GET", "/api/media/spotify/album-art", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/api/obs/recording/toggle", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/preferences", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("GET", "/displays", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/displays/display1/brightness", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/system/volume", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/system/volume/mute", allowPanel: true)));
    }

    [Fact]
    public async Task SystemRoutes_MarksVolumeWritesAsPanelAllowed()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.MapSystemEndpoints();

        AssertPanelAllowedRoute(app, "GET", "/system/volume");
        AssertPanelAllowedRoute(app, "POST", "/system/volume");
        AssertPanelAllowedRoute(app, "POST", "/system/volume/mute");
    }

    /// <summary>
    /// Guards every route the SystemActions extraction rewired (deck executor
    /// + /system/* routes now share one implementation): a refactor that
    /// silently drops an AllowPanel/LocalhostOnly marker changes endpoint
    /// metadata, which DeckActionRoutesTests (fixed client identity, real
    /// requests) cannot see. This caught a real regression - the extraction
    /// dropped AllowPanel from /system/open-path.
    /// </summary>
    [Fact]
    public async Task SystemActionsExtraction_PreservesEveryRoutesAuthMetadata()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.MapSystemEndpoints();

        // Free-form keystroke / text injection and open-path are desktop-only;
        // a panel's deck keys reach them through POST /panel/deck/dispatch.
        AssertPanelDeniedRoute(app, "POST", "/system/input/keys");
        AssertPanelDeniedRoute(app, "POST", "/system/input/text");
        AssertPanelDeniedRoute(app, "POST", "/system/audio/play");
        AssertPanelAllowedRoute(app, "POST", "/system/open-settings");
        AssertPanelAllowedRoute(app, "POST", "/system/open-url");
        AssertPanelDeniedRoute(app, "POST", "/system/open-path");
        AssertPanelAllowedRoute(app, "POST", "/system/open-task-manager");
        AssertPanelAllowedRoute(app, "POST", "/system/power/lock");
        AssertPanelAllowedRoute(app, "POST", "/system/power/sleep");
        AssertPanelAllowedRoute(app, "POST", "/system/power/shutdown");
        AssertPanelAllowedRoute(app, "POST", "/system/power/restart");
        AssertPanelAllowedRoute(app, "POST", "/system/power/logout");
        AssertPanelAllowedRoute(app, "GET", "/system/audio/devices");
        AssertPanelAllowedRoute(app, "POST", "/system/audio/default-output");
        AssertPanelAllowedRoute(app, "POST", "/system/audio/default-input");

        // pick-path opens a native dialog on the host's screen - still desktop-only.
        AssertPanelDeniedRoute(app, "POST", "/system/pick-path");
    }

    [Fact]
    public async Task PanelRoutes_MarksServiceInfoAsPanelAllowed()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.MapPanelEndpoints();

        AssertPanelAllowedRoute(app, "GET", "/panel/phone/service-info");
    }

    /// <summary>
    /// The phone panel's lighting Devices tab reads this list and drives the
    /// per-device controls beside it; without the markers it 403s and renders
    /// as "no lighting devices detected". The denied half is the desktop
    /// editor surface, which stays off the panel.
    /// </summary>
    [Fact]
    public async Task LightingDeviceRoutes_MarksPanelDeviceListAndControlsAsPanelAllowed()
    {
        var builder = WebApplication.CreateSlimBuilder();
        // A handler parameter no container knows is inferred as a JSON body, and
        // resolving LightingEngine's metadata walks DeviceFrame.LedBytes - a
        // ReadOnlySpan, which System.Text.Json refuses. Registering it keeps the
        // endpoint buildable; these assertions only read metadata.
        builder.Services.AddSingleton<Nexus.Service.Lighting.Engine.LightingEngine>();
        await using var app = builder.Build();
        app.MapDevicesEndpoints();

        AssertPanelAllowedRoute(app, "GET", "/devices/lighting-devices/all");
        AssertPanelAllowedRoute(app, "GET", "/devices/lighting-devices/static-looks");
        AssertPanelAllowedRoute(app, "GET", "/devices/lighting-devices/{id}/led-map");
        AssertPanelAllowedRoute(app, "POST", "/devices/lighting-devices/power");
        AssertPanelAllowedRoute(app, "POST", "/devices/lighting-devices/controlled");
        AssertPanelAllowedRoute(app, "POST", "/devices/lighting-devices/brightness");
        AssertPanelAllowedRoute(app, "POST", "/devices/lighting-devices/color");
        AssertPanelAllowedRoute(app, "POST", "/devices/lighting-devices/identify");
        // Read + activate only, for a Deck widget key bound to a lighting
        // preset. Creating, renaming and deleting presets stay desktop-only.
        AssertPanelAllowedRoute(app, "GET", "/devices/lighting-devices/layout-presets");
        AssertPanelAllowedRoute(app, "POST", "/devices/lighting-devices/layout-presets/{id}/activate");

        AssertPanelDeniedRoute(app, "POST", "/devices/lighting-devices/layout");
        AssertPanelDeniedRoute(app, "POST", "/devices/lighting-devices/layout-presets");
        AssertPanelDeniedRoute(app, "PUT", "/devices/lighting-devices/layout-presets/{id}");
        AssertPanelDeniedRoute(app, "DELETE", "/devices/lighting-devices/layout-presets/{id}");
        AssertPanelDeniedRoute(app, "POST", "/devices/lighting-devices/zone-size");
        AssertPanelDeniedRoute(app, "POST", "/devices/lighting-devices/rescan");
        AssertPanelDeniedRoute(app, "POST", "/devices/lighting-devices/{id}/led-map");
        AssertPanelDeniedRoute(app, "POST", "/devices/lighting-devices/{id}/led-test-pattern");
    }

    [Fact]
    public void PanelSession_BlocksAdminRoutes()
    {
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/shutdown")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/devices/update")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/profiles/import")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/media/import")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/displays/display1/vcp/4")));
    }

    [Fact]
    public void SpaShellFallback_AllowsUnmatchedSameOriginHtmlNavigation()
    {
        var ctx = NewContext("GET", "/settings");
        ctx.Request.Headers.Accept = "text/html";
        ctx.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        Assert.True(AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx));
    }

    [Fact]
    public void SpaShellFallback_BlocksMatchedApiRoutes()
    {
        var ctx = NewContext("GET", "/system/cpu/model");
        ctx.Request.Headers.Accept = "text/html";
        ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "system"));

        Assert.False(AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx));
    }

    [Fact]
    public void SpaShellFallback_AllowsCrossSiteHtmlNavigation()
    {
        // Android System WebView (and Chromium-based kiosks) send
        // `Sec-Fetch-Site: cross-site` on top-level navigations to a new
        // origin, even when the user originated the request - there is no
        // prior origin to compare against. Accepting that value is safe
        // for the SPA-shell GET path: it only returns index.html, not API
        // data. CSRF on state-changing endpoints is enforced separately by
        // RejectsInsecureCsrf.
        var ctx = NewContext("GET", "/settings");
        ctx.Request.Headers.Accept = "text/html";
        ctx.Request.Headers["Sec-Fetch-Site"] = "cross-site";

        Assert.True(AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_LetsHttpsTrafficThrough()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: true);
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_LetsSafeMethodsThroughOverHttp()
    {
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(NewContext("GET", "/system/cpu", isHttps: false)));
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(NewContext("HEAD", "/ping", isHttps: false)));
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(NewContext("OPTIONS", "/api/media", isHttps: false)));
    }

    [Fact]
    public void RejectsInsecureCsrf_AllowsSameOriginFetchOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_AllowsMatchingOriginHeaderOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Host = new HostString("192.168.1.235", 9400);
        ctx.Request.Headers.Origin = "http://192.168.1.235:9400";
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_BlocksCrossSiteFetchOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.True(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_BlocksMismatchedOriginOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Host = new HostString("192.168.1.235", 9400);
        ctx.Request.Headers.Origin = "http://malicious.lan";
        Assert.True(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_BlocksMissingHeadersOverHttp()
    {
        // No Sec-Fetch-Site (older browser), no Origin (curl, scripted): on
        // a state-changing HTTP request we err on the side of rejecting.
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        Assert.True(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public async Task WellKnownPath_ServesWithoutToken()
    {
        // RFC 8615 discovery docs (apple-app-site-association) must be reachable
        // un-authenticated so Universal-Link verification can fetch them. The
        // public-path short-circuit runs before any DI resolution, so an empty
        // provider is enough to drive the real UseNexusPathAuth pipeline.
        var (reached, ctx) = await RunPathAuth("GET", "/.well-known/apple-app-site-association");

        Assert.True(reached);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    // ── Loopback detection (backs the desktop-token + shell gates) ────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IsLoopbackRemote_TrueForLoopback(string ip)
        => Assert.True(AuthRequestPolicy.IsLoopbackRemote(WithRemote(ip)));

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.4")]
    [InlineData("172.16.9.9")]
    public void IsLoopbackRemote_FalseForLan(string ip)
        => Assert.False(AuthRequestPolicy.IsLoopbackRemote(WithRemote(ip)));

    [Fact]
    public void IsLoopbackRemote_FalseForNullRemote_FailsClosed()
        => Assert.False(AuthRequestPolicy.IsLoopbackRemote(NewContext("GET", "/")));

    // ── Dashboard shell is loopback-only; panel/pairing reach the LAN ─────────

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/monitoring")]
    [InlineData("/system/settings")]
    [InlineData("/lighting")]
    public void IsShellReachable_AllowsAnyPathFromLoopback(string path)
        => Assert.True(AuthRequestPolicy.IsShellReachable(WithRemote("127.0.0.1", path)));

    [Theory]
    [InlineData("/panel")]
    [InlineData("/panel/phone")]
    [InlineData("/panel/phone/pair")]
    [InlineData("/panel/xyz")]
    [InlineData("/r")]
    [InlineData("/r/pair")]
    public void IsShellReachable_AllowsPanelAndPairingSurfacesFromLan(string path)
        => Assert.True(AuthRequestPolicy.IsShellReachable(WithRemote("192.168.1.50", path)));

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/monitoring")]
    [InlineData("/system/settings")]
    [InlineData("/lighting")]
    [InlineData("/panelX")]  // not a /panel segment boundary
    [InlineData("/rogue")]   // not a /r segment boundary
    public void IsShellReachable_BlocksDashboardShellFromLan(string path)
        => Assert.False(AuthRequestPolicy.IsShellReachable(WithRemote("192.168.1.50", path)));

    // ── Root shell document matcher: every slash-spelling of the shell is caught ─

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/INDEX.HTML")]
    [InlineData("//")]             // repeated-slash root -> default-doc index.html
    [InlineData("//index.html")]   // Kestrel does NOT collapse repeated slashes
    [InlineData("///index.html")]
    [InlineData("/index.html/")]   // trailing slash
    [InlineData("/index.html//")]
    [InlineData("/\\index.html")]  // Windows treats backslash as a separator
    [InlineData("/\\")]
    [InlineData("/\\/index.html")]
    public void TargetsRootShellDocument_TrueForEveryRootShellSpelling(string path)
        => Assert.True(AuthRequestPolicy.TargetsRootShellDocument(new PathString(path)));

    [Theory]
    [InlineData("/assets/index-abc.js")]
    [InlineData("/panel/phone")]
    [InlineData("/monitoring")]
    [InlineData("/favicon.ico")]
    [InlineData("/index.htmlx")]
    [InlineData("/foo/index.html")] // a non-root index.html must not match the root gate
    public void TargetsRootShellDocument_FalseForAssetsAndNonRoot(string path)
        => Assert.False(AuthRequestPolicy.TargetsRootShellDocument(new PathString(path)));

    // ── Off-loopback static block: shell (any spelling) + any backslash path ──

    [Theory]
    [InlineData("192.168.1.50", "/")]
    [InlineData("192.168.1.50", "//index.html")]
    [InlineData("192.168.1.50", "/\\index.html")]              // Windows backslash separator
    [InlineData("192.168.1.50", "/\\panel\\..\\index.html")]   // backslash-smuggled parent segment
    [InlineData("192.168.1.50", "/foo\\..\\index.html")]
    public void BlocksOffLoopbackStatic_BlocksShellAndBackslashFromLan(string ip, string path)
        => Assert.True(AuthRequestPolicy.BlocksOffLoopbackStatic(WithRemote(ip, path)));

    [Theory]
    [InlineData("192.168.1.50", "/assets/index-abc.js")]
    [InlineData("192.168.1.50", "/panel/phone")]
    [InlineData("192.168.1.50", "/r/pair")]
    [InlineData("192.168.1.50", "/favicon.ico")]
    public void BlocksOffLoopbackStatic_AllowsAssetsAndPanelFromLan(string ip, string path)
        => Assert.False(AuthRequestPolicy.BlocksOffLoopbackStatic(WithRemote(ip, path)));

    [Theory]
    [InlineData("127.0.0.1", "/")]
    [InlineData("127.0.0.1", "/\\panel\\..\\index.html")] // own machine may use any spelling
    [InlineData("::1", "//index.html")]
    public void BlocksOffLoopbackStatic_NeverBlocksLoopback(string ip, string path)
        => Assert.False(AuthRequestPolicy.BlocksOffLoopbackStatic(WithRemote(ip, path)));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.235", true)]
    [InlineData("evil.example", false)]
    [InlineData("localhost.evil.example", false)]
    [InlineData("my.localhost", false)]
    public void IsLocalHostHeader_AcceptsOnlyNamesThatCannotBeRebound(string host, bool expected)
    {
        var ctx = NewContext("GET", "/pair");
        ctx.Request.Host = new HostString(host, 9400);

        Assert.Equal(expected, AuthRequestPolicy.IsLocalHostHeader(ctx));
    }

    [Fact]
    public void IsLocalHostHeader_AcceptsTheMachineName_AndRejectsAnEmptyHost()
    {
        var named = NewContext("GET", "/pair");
        named.Request.Host = new HostString(Environment.MachineName, 9400);
        Assert.True(AuthRequestPolicy.IsLocalHostHeader(named));

        var empty = NewContext("GET", "/pair");
        empty.Request.Host = new HostString("");
        Assert.False(AuthRequestPolicy.IsLocalHostHeader(empty));
    }

    private static DefaultHttpContext WithRemote(string ip, string path = "/")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return ctx;
    }

    // ── Shell gate through the real UseNexusPathAuth pipeline ─────────────────
    // The SPA-shell branch runs before any DI, so an empty provider drives it.
    // A terminal Run(200) stands in for MapFallbackToFile: "reached" == the shell
    // would have been served.

    [Fact]
    public async Task ShellGate_ServesDashboardShellToLoopback()
    {
        var (reached, ctx) = await RunShellAuth("/monitoring", "127.0.0.1");
        Assert.True(reached);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/monitoring")]
    [InlineData("/system/settings")]
    public async Task ShellGate_BlocksDashboardShellFromLan(string path)
    {
        var (reached, ctx) = await RunShellAuth(path, "192.168.1.50");
        Assert.False(reached);
        Assert.Equal(404, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/panel/phone/settings")]
    [InlineData("/r/pair")]
    public async Task ShellGate_ServesPanelAndPairingShellToLan(string path)
    {
        var (reached, ctx) = await RunShellAuth(path, "192.168.1.50");
        Assert.True(reached);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    private static Task<(bool reached, DefaultHttpContext ctx)> RunShellAuth(string path, string remoteIp)
        => RunPathAuth("GET", path, remoteIp, accept: "text/html");

    private static async Task<(bool reached, DefaultHttpContext ctx)> RunPathAuth(
        string method, string path, string? remoteIp = null, string? accept = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseNexusPathAuth();
        var reached = false;
        builder.Run(c =>
        {
            reached = true;
            c.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        var pipeline = builder.Build();

        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Scheme = "http";
        if (accept is not null)
            ctx.Request.Headers.Accept = accept;
        if (remoteIp is not null)
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        await pipeline(ctx);
        return (reached, ctx);
    }

    private static DefaultHttpContext NewContext(string method, string path, bool allowPanel = false, bool isHttps = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Scheme = isHttps ? "https" : "http";
        if (allowPanel)
        {
            ctx.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(AllowPanelAccess.Instance),
                "allow-panel"));
        }
        return ctx;
    }

    private static void AssertPanelAllowedRoute(WebApplication app, string method, string pattern)
    {
        var endpoint = FindEndpoint(app, method, pattern);
        Assert.NotNull(endpoint);
        Assert.NotNull(endpoint.Metadata.GetMetadata<AllowPanelAccess>());
    }

    private static void AssertPanelDeniedRoute(WebApplication app, string method, string pattern)
    {
        var endpoint = FindEndpoint(app, method, pattern);
        Assert.NotNull(endpoint);
        Assert.Null(endpoint.Metadata.GetMetadata<AllowPanelAccess>());
    }

    private static RouteEndpoint? FindEndpoint(WebApplication app, string method, string pattern) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault(e =>
                string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal)
                && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));
}

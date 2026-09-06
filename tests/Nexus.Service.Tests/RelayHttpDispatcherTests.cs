using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Routes;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// In-process integration for the REST-over-relay dispatcher. Stands up a real
/// <see cref="WebApplication"/> wired exactly like Program.cs at the auth seam -
/// the capture middleware first, then <c>UseRouting</c> + <c>UseNexusPathAuth</c>
/// + mapped routes - primes the pipeline the same way, and drives requests
/// through <see cref="RelayHttpDispatcher.DispatchAsync"/> (what the rid_http
/// relay link calls). Proves the dispatch runs the service's OWN handlers and
/// that the trusted in-process marker authorizes a protected panel route as the
/// session WITHOUT any phone bearer, while the killswitch + path allowlist hold.
/// </summary>
public sealed class RelayHttpDispatcherTests
{
    private const string SessionId = "sess-http-tunnel";
    private const string ProtectedRoute = "/panel/probe";
    private const string ProtectedBody = "panel-probe-ok";
    private const string BinaryRoute = "/panel/blob";
    private const string DesktopOnlyRoute = "/panel/desktop-only";
    // Includes 0xC3 0x28 - an invalid UTF-8 sequence a string round-trip would
    // mangle into U+FFFD; the byte-for-byte assert proves binary survives.
    private static readonly byte[] BinaryProbe = { 0x00, 0xFF, 0xC3, 0x28, 0x80, 0x01, 0xFE, 0x7F };

    /// <summary>Decode a tunneled response body back to text (base64 when flagged).</summary>
    private static string DecodeText(RelayHttpResponse r)
        => r.Base64 ? Encoding.UTF8.GetString(Convert.FromBase64String(r.Body)) : r.Body;

    private static async Task<WebApplication> BuildAppAsync(IConfigStore store, MultiplexHub hub)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Ephemeral loopback port so parallel test apps never collide.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(new TokenService(store));
        builder.Services.AddSingleton(new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" });
        builder.Services.AddSingleton<RelayHttpDispatcher>();
        // Mirror Program.cs: source-generated JSON resolver for minimal-API binding.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, Nexus.Service.Serialization.AppJsonContext.Default));

        var app = builder.Build();

        // Capture middleware FIRST - identical to Program.cs.
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();
        app.Use(async (ctx, next) =>
        {
            if (!dispatcher.IsReady)
            {
                dispatcher.SetPipeline(c => next(c));
            }
            await next(ctx);
        });

        app.UseRouting();
        app.UseNexusPathAuth();

        // A protected panel route (.AllowPanel) that requires a phone-session.
        app.MapGet(ProtectedRoute, () => Results.Text(ProtectedBody)).AllowPanel();
        // A protected POST that BINDS a JSON body - mirrors /lighting/animate/
        // headless-start, /system/volume, etc. (the real control commands).
        // Echoes the bound field so the test can prove the body survived the
        // tunnel's synthetic HttpContext and reached minimal-API model binding.
        app.MapPost("/panel/echo", (Nexus.Service.Models.Panel.PanelHostNameBody b) => Results.Text("echo:" + b.Name)).AllowPanel();
        // A protected route returning raw binary (like an effect-thumbnail BMP) to
        // prove the tunnel carries non-UTF-8 bytes intact.
        app.MapGet(BinaryRoute, () => Results.Bytes(BinaryProbe, "image/bmp")).AllowPanel();
        // A route under an allowed prefix WITHOUT .AllowPanel - desktop only.
        app.MapGet(DesktopOnlyRoute, () => Results.Text("desktop-only"));
        // A public route (no auth) to prove a tunneled GET to a real route works.
        app.MapGet("/ping", () => Results.Text("pong"));

        // Start the host first: WebApplication only appends the endpoint-execution
        // terminal to the built pipeline once started, so the capture must prime
        // after StartAsync - exactly the ApplicationStarted hook Program.cs uses.
        await app.StartAsync();

        var primeCtx = new DefaultHttpContext { RequestServices = app.Services };
        primeCtx.Request.Method = "GET";
        primeCtx.Request.Path = RelayHttpDispatcher.PrimePath;
        primeCtx.Response.Body = System.IO.Stream.Null;
        await ((IApplicationBuilder)app).Build()(primeCtx);

        Assert.True(dispatcher.IsReady, "relay http pipeline was not captured on prime");
        return app;
    }

    private static IConfigStore StoreWithSession(bool remoteOn = true)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = remoteOn;
            s.Auth.RelayEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>
            {
                new()
                {
                    Id = SessionId,
                    Hash = "hash-placeholder",
                    Name = "iPhone",
                    UserAgent = "iPhone",
                    RemoteAddress = "192.168.1.50",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ClaimedOverHttps = true,
                },
            };
        });
        return store;
    }

    [Fact]
    public async Task Tunneled_PublicRoute_ReturnsRealResponse()
    {
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        var resp = await dispatcher.DispatchAsync(
            new RelayHttpRequest { Id = 7, Method = "GET", Path = "/ping" },
            SessionId, CancellationToken.None);

        Assert.Equal(7, resp.Id);
        Assert.Equal(StatusCodes.Status200OK, resp.Status);
        // Text rides as a plain string (NOT base64) so a panel predating the
        // Base64 flag still parses it - the backward-compat contract.
        Assert.False(resp.Base64, "text response must ride as a plain UTF-8 string");
        Assert.Equal("pong", DecodeText(resp));
    }

    [Fact]
    public async Task Tunneled_BinaryResponse_SurvivesIntact_Base64()
    {
        // A binary response (effect thumbnail / icon) must arrive byte-for-byte.
        // The body is base64 on the wire so the bytes can't be corrupted by the
        // tunnel's UTF-8 JSON string channel.
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        var resp = await dispatcher.DispatchAsync(
            new RelayHttpRequest { Id = 21, Method = "GET", Path = BinaryRoute },
            SessionId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, resp.Status);
        Assert.True(resp.Base64, "binary response must be base64-flagged");
        Assert.Equal(BinaryProbe, Convert.FromBase64String(resp.Body));
    }

    [Fact]
    public async Task Tunneled_ProtectedRoute_Succeeds_WithoutBearer_AuthorizedAsSession()
    {
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        // No bearer / cookie / token anywhere - the only thing authorizing this
        // is the in-process trusted-relay marker the dispatcher sets, proving the
        // relay session's authentication is honored without the phone re-presenting
        // its credential.
        var resp = await dispatcher.DispatchAsync(
            new RelayHttpRequest { Id = 11, Method = "GET", Path = ProtectedRoute },
            SessionId, CancellationToken.None);

        Assert.Equal(11, resp.Id);
        Assert.Equal(StatusCodes.Status200OK, resp.Status);
        Assert.Equal(ProtectedBody, DecodeText(resp));
    }

    [Fact]
    public async Task Tunneled_NonPanelRoute_Is403_EvenWithTrustedMarker()
    {
        // The relay lane authorizes AS a phone session, so it reaches exactly
        // what a LAN phone session reaches: a route without .AllowPanel() under
        // an allowed prefix (e.g. /panel/phone/pair-qr in production) is 403.
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        var resp = await dispatcher.DispatchAsync(
            new RelayHttpRequest { Id = 12, Method = "GET", Path = DesktopOnlyRoute },
            SessionId, CancellationToken.None);

        Assert.Equal(12, resp.Id);
        Assert.Equal(StatusCodes.Status403Forbidden, resp.Status);
    }

    [Fact]
    public async Task Tunneled_ProtectedRoute_WithoutTrustedMarker_Is401()
    {
        // Drive the SAME protected route through the real pipeline as a plain
        // request (no trusted marker, no token). It must 401 - proving the route
        // is actually protected and the success above is solely the marker.
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);

        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Request.Method = "GET";
        ctx.Request.Path = ProtectedRoute;
        ctx.Response.Body = new System.IO.MemoryStream();

        // Re-run the captured pipeline directly (it's internal to the dispatcher),
        // so instead exercise the public dispatch with a marker but assert the
        // negative via a bearerless non-relay request through the app pipeline.
        // We use the dispatcher's captured pipeline by sending through Build().
        await ((IApplicationBuilder)app).Build()(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Tunneled_PostWithJsonBody_BindsBody_AndRunsHandler()
    {
        // A relayed POST whose handler binds a JSON body (lighting effect, volume,
        // ...). The synthetic HttpContext must report it can have a body or minimal-
        // API binding skips the body and 400s the required parameter as missing -
        // the regression that silently broke every body-bound control over relay.
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        var resp = await dispatcher.DispatchAsync(
            new RelayHttpRequest { Id = 71, Method = "POST", Path = "/panel/echo", Body = "{\"name\":\"hi\"}", ContentType = "application/json" },
            SessionId, CancellationToken.None);

        Assert.Equal(71, resp.Id);
        Assert.Equal(StatusCodes.Status200OK, resp.Status);
        Assert.Equal("echo:hi", DecodeText(resp));
    }

    [Fact]
    public async Task Tunneled_OffAllowlistPath_IsRejected_403()
    {
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        foreach (var path in new[] { "/ws", "/lighting/output", "/lighting/screen/monitors" })
        {
            var resp = await dispatcher.DispatchAsync(
                new RelayHttpRequest { Id = 99, Method = "GET", Path = path },
                SessionId, CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, resp.Status);
            Assert.Equal(99, resp.Id);
        }
    }

    [Fact]
    public async Task Tunneled_ProtectedRoute_KillswitchOff_Is403()
    {
        // Remote control OFF: even a trusted relay dispatch is rejected (OFF
        // means OFF), via the same killswitch the LAN auth path enforces.
        var store = StoreWithSession(remoteOn: false);
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        var resp = await dispatcher.DispatchAsync(
            new RelayHttpRequest { Id = 3, Method = "GET", Path = ProtectedRoute },
            SessionId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, resp.Status);
    }

    [Fact]
    public async Task Tunneled_OversizedRequestBody_Is413()
    {
        var store = StoreWithSession();
        var hub = new MultiplexHub();
        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();

        var big = new string('x', RelayHttpDispatcher.MaxBodyBytes + 1);
        var resp = await dispatcher.DispatchAsync(
            new RelayHttpRequest { Id = 5, Method = "POST", Path = "/panel/host-name", Body = big, ContentType = "application/json" },
            SessionId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, resp.Status);
    }

    [Fact]
    public async Task FullWire_RidHttpLink_TunnelsProtectedRoute_Over_FakeRelay()
    {
        // End-to-end over the wire: RelayConnectionService registers a SECOND host
        // link on rid_http, a client peers up there, sends a sealed HTTP request,
        // and the sealed reply carries the REAL authorized panel response -
        // proving the rid_http leg + sealed framing + in-process authorized
        // dispatch all line up.
        const string token = "test-session-token-0123456789";
        var relayRoot = RelayCrypto.DeriveRelayRoot(token);
        var ridHttp = RelayCrypto.DeriveHttpRid(relayRoot);

        var store = new InMemoryConfigStore();
        var hub = new MultiplexHub();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.RelayEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>
            {
                new()
                {
                    Id = SessionId,
                    Hash = "hash-placeholder",
                    RelayKey = Convert.ToBase64String(relayRoot),
                    Name = "iPhone",
                    UserAgent = "iPhone",
                    RemoteAddress = "192.168.1.50",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ClaimedOverHttps = true,
                },
            };
        });

        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();
        var pairing = app.Services.GetRequiredService<Nexus.Service.Panel.PanelPhonePairingService>();

        using var relay = new MultiRidFakeRelay();
        await relay.StartAsync();

        using var connection = new RelayConnectionService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RelayConnectionService>.Instance,
            pairing, store, hub, dispatcher)
        {
            Endpoint = relay.Uri,
        };
        await connection.StartAsync(CancellationToken.None);

        // The PC must register a host link on rid_http for the active session.
        await relay.WaitForHostAsync(ridHttp, MultiRidFakeRelay.HostWait);

        // Client peers up on rid_http, derives the per-conn key, sends a sealed
        // request (dir=2) for the protected panel route.
        var connSalt = new byte[RelayCrypto.ConnSaltLength];
        for (var i = 0; i < connSalt.Length; i++) connSalt[i] = (byte)(i + 0x70);
        var key = RelayCrypto.DeriveAeadKey(relayRoot, connSalt);
        await relay.SendPeerUpAsync(ridHttp, connSalt);

        var req = new RelayHttpRequest { Id = 42, Method = "GET", Path = ProtectedRoute };
        var reqBytes = JsonSerializer.SerializeToUtf8Bytes(req, Nexus.Service.Serialization.AppJsonContext.Default.RelayHttpRequest);
        var sealedReq = RelayCrypto.Seal(key, RelayCrypto.DirClientToHost, counter: 0, reqBytes);
        await relay.ForwardToHostAsync(ridHttp, sealedReq);

        // The PC replies with one sealed response (dir=1) carrying the real body.
        var replyFrame = await relay.WaitForHostFrameAsync(ridHttp, TimeSpan.FromSeconds(5));
        var (dir, _, plain) = RelayCrypto.Open(key, replyFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);

        var reply = JsonSerializer.Deserialize(plain, Nexus.Service.Serialization.AppJsonContext.Default.RelayHttpResponse);
        Assert.NotNull(reply);
        Assert.Equal(42, reply!.Id);
        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.Equal(ProtectedBody, DecodeText(reply));

        // Off-allowlist over the same wire ⇒ sealed 403.
        var badReq = new RelayHttpRequest { Id = 43, Method = "GET", Path = "/ws" };
        var badBytes = JsonSerializer.SerializeToUtf8Bytes(badReq, Nexus.Service.Serialization.AppJsonContext.Default.RelayHttpRequest);
        var sealedBad = RelayCrypto.Seal(key, RelayCrypto.DirClientToHost, counter: 1, badBytes);
        await relay.ForwardToHostAsync(ridHttp, sealedBad);

        var badReplyFrame = await relay.WaitForHostFrameAsync(ridHttp, TimeSpan.FromSeconds(5));
        var (_, _, badPlain) = RelayCrypto.Open(key, badReplyFrame);
        var badReply = JsonSerializer.Deserialize(badPlain, Nexus.Service.Serialization.AppJsonContext.Default.RelayHttpResponse);
        Assert.NotNull(badReply);
        Assert.Equal(43, badReply!.Id);
        Assert.Equal(StatusCodes.Status403Forbidden, badReply.Status);

        await connection.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FullWire_RidHttpLink_RekeysOnHello2_ThenServesUnderTheRekeyedKey()
    {
        // Protocol v2 over the real HttpChannel: hello2 under the connection key
        // is answered with the host nonce, and the request/reply pair after it
        // rides the rekeyed key with counters restarted at 0.
        const string token = "test-session-token-rekey-0123";
        var relayRoot = RelayCrypto.DeriveRelayRoot(token);
        var ridHttp = RelayCrypto.DeriveHttpRid(relayRoot);

        var store = new InMemoryConfigStore();
        var hub = new MultiplexHub();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.RelayEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>
            {
                new()
                {
                    Id = SessionId,
                    Hash = "hash-placeholder",
                    RelayKey = Convert.ToBase64String(relayRoot),
                    Name = "iPhone",
                    UserAgent = "iPhone",
                    RemoteAddress = "192.168.1.50",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ClaimedOverHttps = true,
                },
            };
        });

        await using var app = await BuildAppAsync(store, hub);
        var dispatcher = app.Services.GetRequiredService<RelayHttpDispatcher>();
        var pairing = app.Services.GetRequiredService<Nexus.Service.Panel.PanelPhonePairingService>();

        using var relay = new MultiRidFakeRelay();
        await relay.StartAsync();

        using var connection = new RelayConnectionService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RelayConnectionService>.Instance,
            pairing, store, hub, dispatcher)
        {
            Endpoint = relay.Uri,
        };
        await connection.StartAsync(CancellationToken.None);
        await relay.WaitForHostAsync(ridHttp, MultiRidFakeRelay.HostWait);

        var connSalt = new byte[RelayCrypto.ConnSaltLength];
        for (var i = 0; i < connSalt.Length; i++) connSalt[i] = (byte)(i + 0x30);
        var k0 = RelayCrypto.DeriveAeadKey(relayRoot, connSalt);
        await relay.SendPeerUpAsync(ridHttp, connSalt);

        await relay.ForwardToHostAsync(ridHttp, RelayCrypto.Seal(k0, RelayCrypto.DirClientToHost, counter: 0, Encoding.UTF8.GetBytes("{\"c\":\"hello2\"}")));
        var hnFrame = await relay.WaitForHostFrameAsync(ridHttp, TimeSpan.FromSeconds(5));
        var (hnDir, hnCounter, hnPlain) = RelayCrypto.Open(k0, hnFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, hnDir);
        Assert.Equal(0UL, hnCounter);
        using var hnDoc = JsonDocument.Parse(hnPlain);
        Assert.Equal("hn", hnDoc.RootElement.GetProperty("c").GetString());
        var hostNonce = RelayCrypto.FromBase64UrlNoPad(hnDoc.RootElement.GetProperty("hn").GetString()!);
        var k1 = RelayCrypto.DeriveRekeyedAeadKey(relayRoot, connSalt, hostNonce);

        var req = new RelayHttpRequest { Id = 7, Method = "GET", Path = ProtectedRoute };
        var reqBytes = JsonSerializer.SerializeToUtf8Bytes(req, Nexus.Service.Serialization.AppJsonContext.Default.RelayHttpRequest);
        await relay.ForwardToHostAsync(ridHttp, RelayCrypto.Seal(k1, RelayCrypto.DirClientToHost, counter: 0, reqBytes));

        var replyFrame = await relay.WaitForHostFrameAsync(ridHttp, TimeSpan.FromSeconds(5));
        var (dir, counter, plain) = RelayCrypto.Open(k1, replyFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0UL, counter);
        var reply = JsonSerializer.Deserialize(plain, Nexus.Service.Serialization.AppJsonContext.Default.RelayHttpResponse);
        Assert.NotNull(reply);
        Assert.Equal(7, reply!.Id);
        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.Equal(ProtectedBody, DecodeText(reply));

        await connection.StopAsync(CancellationToken.None);
    }
}

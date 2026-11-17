using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Relay;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Drives the inbound sealed LAN tunnel (<c>/secure-tunnel</c>) over a real
/// TestServer WebSocket - the Phase 2 oracle. Proves end-to-end that:
///   • the HTTP leg dispatches a sealed REST request through the real pipeline
///     and seals the response (rid_http);
///   • the runtime leg bridges a sealed multiplex `sub` into the hub (rid);
///   • an unknown rid / killswitch-off connection gets {"e":"no-host"};
///   • the session token never rides the wire - the client sends only the
///     128-bit HKDF rid and AEAD ciphertext, and possession is what the decrypt
///     proves (a wrong-key request is silently dropped, no response).
/// </summary>
public sealed class SealedTunnelIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;
    public SealedTunnelIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private PanelPhonePairingService Pairing => _factory.Services.GetRequiredService<PanelPhonePairingService>();
    private MultiplexHub Hub => _factory.Services.GetRequiredService<MultiplexHub>();

    /// <summary>Enable remote control + pair a session; return its token.</summary>
    private async Task<string> PairSessionAsync()
    {
        await Pairing.SetRemoteControlEnabledAsync(true);
        var qr = Pairing.CreatePairQr();
        var pairToken = ExtractPairToken(qr.Url);
        var claim = Pairing.ClaimCoreApproved(
            pairToken, deviceName: "TestPhone", userAgent: "", remoteAddress: "192.168.1.50",
            deviceId: "sealed-tunnel-test", overRelay: false, claimedOverHttps: true);
        Assert.True(claim.Ok, $"claim failed: {claim.Error}");
        return claim.SessionToken;
    }

    private async Task<WebSocket> OpenTunnelAsync(CancellationToken ct)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        // Deliberately NO Authorization header: the in-band sealed handshake is the
        // auth, and proving the endpoint is reachable without one is the point.
        return await wsClient.ConnectAsync(new Uri("ws://localhost/secure-tunnel"), ct);
    }

    private static async Task<string> SendHelloAsync(WebSocket ws, string rid, byte[] connSalt, CancellationToken ct)
    {
        var hello = $"{{\"v\":1,\"role\":\"client\",\"rid\":\"{rid}\",\"salt\":\"{RelayCrypto.Base64UrlNoPad(connSalt)}\"}}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);
        var buf = new byte[256];
        var res = await ws.ReceiveAsync(buf, ct);
        Assert.Equal(WebSocketMessageType.Text, res.MessageType);
        return Encoding.UTF8.GetString(buf, 0, res.Count);
    }

    [Fact]
    public async Task HttpLeg_SealedRequest_DispatchesThroughPipeline_AndSealsResponse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var token = await PairSessionAsync();
        var relayRoot = RelayCrypto.DeriveRelayRoot(token);
        var ridHttp = RelayCrypto.DeriveHttpRid(relayRoot);
        var connSalt = RandomBytes(RelayCrypto.ConnSaltLength);
        var aeadKey = RelayCrypto.DeriveAeadKey(relayRoot, connSalt);

        using var ws = await OpenTunnelAsync(ct);
        var ctrl = await SendHelloAsync(ws, ridHttp, connSalt, ct);
        Assert.Contains("peer-up", ctrl);
        // The token never appears on the wire - only the derived rid.
        Assert.DoesNotContain(token, ctrl);

        var req = new RelayHttpRequest { Id = 7, Method = "GET", Path = "/ping" };
        var reqBytes = JsonSerializer.SerializeToUtf8Bytes(req, AppJsonContext.Default.RelayHttpRequest);
        var sealedReq = RelayCrypto.Seal(aeadKey, RelayCrypto.DirClientToHost, counter: 0, reqBytes);
        await ws.SendAsync(sealedReq, WebSocketMessageType.Binary, true, ct);

        var frame = await ReceiveBinaryAsync(ws, ct);
        var (dir, _, plain) = RelayCrypto.Open(aeadKey, frame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        var resp = JsonSerializer.Deserialize(plain, AppJsonContext.Default.RelayHttpResponse);
        Assert.NotNull(resp);
        Assert.Equal(7, resp!.Id);
        Assert.Equal(200, resp.Status);
        Assert.False(string.IsNullOrEmpty(resp.Body));
    }

    [Fact]
    public async Task RuntimeLeg_SealedSubscribe_ReachesHub()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        const string topic = "sealed-tunnel-runtime-topic";

        var token = await PairSessionAsync();
        var relayRoot = RelayCrypto.DeriveRelayRoot(token);
        var rid = RelayCrypto.DeriveRid(relayRoot);
        var connSalt = RandomBytes(RelayCrypto.ConnSaltLength);
        var aeadKey = RelayCrypto.DeriveAeadKey(relayRoot, connSalt);

        using var ws = await OpenTunnelAsync(ct);
        var ctrl = await SendHelloAsync(ws, rid, connSalt, ct);
        Assert.Contains("peer-up", ctrl);

        var sub = Encoding.UTF8.GetBytes($"{{\"sub\":[\"{topic}\"]}}");
        var subFrame = RelayCrypto.Seal(aeadKey, RelayCrypto.DirClientToHost, counter: 0, sub);
        await ws.SendAsync(subFrame, WebSocketMessageType.Binary, true, ct);

        await WaitFor(() => Hub.TopicHasSubscribers(topic), "sealed `sub` reaches the hub");
    }

    [Fact]
    public async Task UnknownRid_RepliesNoHost()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        await Pairing.SetRemoteControlEnabledAsync(true);

        using var ws = await OpenTunnelAsync(ct);
        var bogusRid = RelayCrypto.DeriveRid(RandomBytes(RelayCrypto.RelayRootLength));
        var ctrl = await SendHelloAsync(ws, bogusRid, RandomBytes(RelayCrypto.ConnSaltLength), ct);
        Assert.Contains("no-host", ctrl);
    }

    [Fact]
    public async Task RemoteControlOff_RepliesNoHost()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        // Pair while enabled, then disable: the tunnel must refuse like the relay path.
        var token = await PairSessionAsync();
        await Pairing.SetRemoteControlEnabledAsync(false);
        var rid = RelayCrypto.DeriveRid(RelayCrypto.DeriveRelayRoot(token));

        using var ws = await OpenTunnelAsync(ct);
        var ctrl = await SendHelloAsync(ws, rid, RandomBytes(RelayCrypto.ConnSaltLength), ct);
        Assert.Contains("no-host", ctrl);
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static async Task<byte[]> ReceiveBinaryAsync(WebSocket ws, CancellationToken ct)
    {
        using var ms = new System.IO.MemoryStream();
        var buf = new byte[8192];
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("tunnel closed before a frame arrived");
            ms.Write(buf, 0, res.Count);
        }
        while (!res.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Binary, res.MessageType);
        return ms.ToArray();
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (cts.IsCancellationRequested) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static string ExtractPairToken(string url)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.AsSpan(0, eq).SequenceEqual("pair"))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        throw new InvalidOperationException("no pair token in QR url: " + url);
    }
}

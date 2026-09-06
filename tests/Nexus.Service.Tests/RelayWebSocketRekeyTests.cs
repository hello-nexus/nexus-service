using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Relay;

namespace Nexus.Service.Tests;

/// <summary>
/// The hub-facing relay socket runs the v2 rekey transparently: the hub never
/// sees hello2 or the host nonce, and everything after them rides the rekeyed key.
/// </summary>
public sealed class RelayWebSocketRekeyTests
{
    private static readonly byte[] Root = RandomNumberGenerator.GetBytes(RelayCrypto.RelayRootLength);
    private static readonly byte[] Salt = RandomNumberGenerator.GetBytes(RelayCrypto.ConnSaltLength);
    private static readonly byte[] K0 = RelayCrypto.DeriveAeadKey(Root, Salt);

    private static RelayWebSocket HostSocket(CapturingWebSocket transport) => new(
        transport, K0, RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost,
        hn => RelayCrypto.DeriveRekeyedAeadKey(Root, Salt, hn));

    private static byte[] ClientFrame(byte[] key, ulong counter, string text)
        => RelayCrypto.Seal(key, RelayCrypto.DirClientToHost, counter, Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReceiveTextAsync(RelayWebSocket ws)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Fact]
    public async Task Hello2_is_answered_with_a_host_nonce_and_the_rest_rides_the_rekeyed_key()
    {
        var transport = new CapturingWebSocket();
        var ws = HostSocket(transport);

        ws.EnqueueInbound(ClientFrame(K0, 0, "{\"c\":\"hello2\"}"));
        var receive = ReceiveTextAsync(ws);
        // The host nonce goes out before the hub sees anything.
        var hnFrame = await transport.NextSentAsync();
        var (dir, counter, plaintext) = RelayCrypto.Open(K0, hnFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0UL, counter);
        using var doc = JsonDocument.Parse(plaintext);
        var hn = RelayCrypto.FromBase64UrlNoPad(doc.RootElement.GetProperty("hn").GetString()!);
        Assert.True(ws.Rekeyed);
        Assert.False(receive.IsCompleted);

        var k1 = RelayCrypto.DeriveRekeyedAeadKey(Root, Salt, hn);
        ws.EnqueueInbound(ClientFrame(k1, 0, "{\"sub\":[\"volume\"]}"));
        Assert.Equal("{\"sub\":[\"volume\"]}", await receive);

        // Hub sends after the rekey are sealed under the rekeyed key, counter restarted.
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"volume\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var hostFrame = await transport.NextSentAsync();
        var (hostDir, hostCounter, hostPlain) = RelayCrypto.Open(k1, hostFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, hostDir);
        Assert.Equal(0UL, hostCounter);
        Assert.Equal("{\"t\":\"volume\"}", Encoding.UTF8.GetString(hostPlain));

        // A frame recorded under the connection key no longer decrypts: the socket aborts.
        ws.EnqueueInbound(ClientFrame(K0, 1, "{\"sub\":[\"audio\"]}"));
        var closed = await ws.ReceiveAsync(new ArraySegment<byte>(new byte[64]), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(WebSocketState.Aborted, ws.State);
    }

    [Fact]
    public async Task A_v1_client_is_served_on_the_connection_key()
    {
        var transport = new CapturingWebSocket();
        var ws = HostSocket(transport);

        ws.EnqueueInbound(ClientFrame(K0, 0, "{\"sub\":[\"volume\"]}"));
        Assert.Equal("{\"sub\":[\"volume\"]}", await ReceiveTextAsync(ws));
        Assert.False(ws.Rekeyed);
        Assert.Equal(0, transport.SentCount);

        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"volume\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var (dir, counter, _) = RelayCrypto.Open(K0, await transport.NextSentAsync());
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0UL, counter);
    }

    private sealed class CapturingWebSocket : WebSocket
    {
        private readonly Queue<byte[]> _sent = new();
        private readonly SemaphoreSlim _signal = new(0);
        private WebSocketState _state = WebSocketState.Open;

        public int SentCount { get { lock (_sent) return _sent.Count; } }

        public async Task<byte[]> NextSentAsync()
        {
            Assert.True(await _signal.WaitAsync(TimeSpan.FromSeconds(5)), "no frame was sent");
            lock (_sent) return _sent.Dequeue();
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Assert.Equal(WebSocketMessageType.Binary, messageType);
            lock (_sent) _sent.Enqueue(buffer.ToArray());
            _signal.Release();
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException("the relay socket reads from its inbound queue, not the transport");

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }
}

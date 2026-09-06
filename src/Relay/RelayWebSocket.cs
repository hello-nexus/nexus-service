using System;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nexus.Service.Relay;

/// <summary>
/// A <see cref="WebSocket"/> the <c>MultiplexHub</c> drives unchanged, sitting
/// on top of an underlying relay transport (a <see cref="ClientWebSocket"/> to
/// the cloud relay). It is the end-to-end AEAD codec for one relayed panel
/// session:
///
///   • <see cref="SendAsync"/> - the hub hands us a plaintext multiplex frame
///     (<c>{"t":..,"d":..}</c>). We <see cref="RelayCrypto.Seal"/> it with
///     dir=1 (host→client) and a monotonically increasing counter, then write
///     it as a single BINARY message to the underlying relay socket.
///
///   • <see cref="ReceiveAsync"/> - the connection loop in
///     <c>RelayConnectionService</c> feeds inbound BINARY frames via
///     <see cref="EnqueueInbound"/>. We dequeue the next one,
///     <see cref="RelayCrypto.Open"/> it (verifying dir=2 client→host and a
///     non-replayed counter), and present the decrypted bytes to the hub as a
///     TEXT message.
///
/// The hub thus speaks the same plaintext multiplex protocol it does on a LAN
/// socket; all the crypto + relay framing is invisible to it. A tag-verify
/// failure (tamper / wrong key) or a protocol violation aborts the socket so
/// the hub's receive loop exits and the session ends.
/// </summary>
public sealed class RelayWebSocket : WebSocket
{
    private readonly WebSocket _transport;
    private readonly SealedChannelKeys _keys;
    private readonly byte _sendDir;
    private readonly byte _expectRecvDir;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<byte[]> _inbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    // Carries the remainder of a decrypted message that didn't fit the hub's
    // receive buffer, so the next ReceiveAsync continues it (EndOfMessage
    // semantics). The hub uses a fixed 4 KB buffer and one Receive per message,
    // so in practice this is rarely hit, but a correct WebSocket must honor it.
    private byte[]? _partial;
    private int _partialOffset;

    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;

    /// <param name="transport">The underlying relay socket BINARY frames flow over.</param>
    /// <param name="aeadKey">Per-connection AES-256-GCM key (HKDF(relayRoot, connSalt)).</param>
    /// <param name="sendDir">dir byte stamped on outbound frames. The host uses 1.</param>
    /// <param name="expectRecvDir">dir byte required on inbound frames. The host expects 2.</param>
    /// <param name="rekeyDerive">hostNonce to rekeyed key (SealedChannelKeys); null disables the v2 rekey.</param>
    public RelayWebSocket(WebSocket transport, byte[] aeadKey, byte sendDir, byte expectRecvDir, Func<byte[], byte[]>? rekeyDerive = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _keys = new SealedChannelKeys(aeadKey ?? throw new ArgumentNullException(nameof(aeadKey)), rekeyDerive);
        _sendDir = sendDir;
        _expectRecvDir = expectRecvDir;
    }

    /// <summary>True once the v2 rekey completed on this channel.</summary>
    public bool Rekeyed => _keys.Rekeyed;

    /// <summary>
    /// Push a raw inbound BINARY relay frame for the next <see cref="ReceiveAsync"/>
    /// to decrypt. Called by the connection loop that owns the relay socket.
    /// </summary>
    public void EnqueueInbound(byte[] frame) => _inbound.Writer.TryWrite(frame);

    /// <summary>
    /// Signal that no more inbound frames will arrive (peer-down / relay socket
    /// closed). A pending / next <see cref="ReceiveAsync"/> then returns a Close
    /// result so the hub's receive loop exits cleanly.
    /// </summary>
    public void CompleteInbound() => _inbound.Writer.TryComplete();

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeStatusDescription;
    public override string? SubProtocol => null;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        // Drain any buffered remainder from a prior oversized message first.
        if (_partial is not null)
            return ContinuePartial(buffer);

        while (true)
        {
            byte[] frame;
            try
            {
                frame = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Inbound completed (peer-down / relay drop): present an orderly close.
                return CloseResult();
            }
            catch (OperationCanceledException)
            {
                return CloseResult();
            }

            byte[] plaintext;
            try
            {
                // The peer must use the agreed inbound direction and never replay or
                // reorder a counter. Either ⇒ tampering / a confused relay; abort.
                var kind = _keys.Open(frame, _expectRecvDir, out plaintext);
                if (kind == SealedChannelKeys.InboundKind.Rejected)
                {
                    Abort();
                    return CloseResult();
                }
                if (kind == SealedChannelKeys.InboundKind.RekeyRequest)
                {
                    if (!await SendHostNonceAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Abort();
                        return CloseResult();
                    }
                    continue;
                }
            }
            catch (CryptographicException)
            {
                // Tag-verify failed: drop the frame AND close the channel (spec).
                Abort();
                return CloseResult();
            }

            return DeliverPlaintext(plaintext, buffer);
        }
    }

    /// <summary>False when the nonce could not be sent (cancelled or the transport failed); the channel must then close, never continue on a half-switched key.</summary>
    private async Task<bool> SendHostNonceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
        try
        {
            var frame = _keys.SealHostNonceAndRekey(_sendDir);
            await _transport
                .SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private WebSocketReceiveResult DeliverPlaintext(byte[] plaintext, ArraySegment<byte> buffer)
    {
        if (plaintext.Length <= buffer.Count)
        {
            Buffer.BlockCopy(plaintext, 0, buffer.Array!, buffer.Offset, plaintext.Length);
            return new WebSocketReceiveResult(plaintext.Length, WebSocketMessageType.Text, endOfMessage: true);
        }

        // Too big for one read: hand back what fits, stash the rest.
        Buffer.BlockCopy(plaintext, 0, buffer.Array!, buffer.Offset, buffer.Count);
        _partial = plaintext;
        _partialOffset = buffer.Count;
        return new WebSocketReceiveResult(buffer.Count, WebSocketMessageType.Text, endOfMessage: false);
    }

    private WebSocketReceiveResult ContinuePartial(ArraySegment<byte> buffer)
    {
        var remaining = _partial!.Length - _partialOffset;
        var take = Math.Min(remaining, buffer.Count);
        Buffer.BlockCopy(_partial, _partialOffset, buffer.Array!, buffer.Offset, take);
        _partialOffset += take;
        var done = _partialOffset >= _partial.Length;
        if (done)
        {
            _partial = null;
            _partialOffset = 0;
        }
        return new WebSocketReceiveResult(take, WebSocketMessageType.Text, endOfMessage: done);
    }

    private WebSocketReceiveResult CloseResult()
    {
        if (_state == WebSocketState.Open)
            _state = WebSocketState.CloseReceived;
        return new WebSocketReceiveResult(
            0, WebSocketMessageType.Close, endOfMessage: true,
            _closeStatus ?? WebSocketCloseStatus.NormalClosure, _closeStatusDescription);
    }

    public override async Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        // The hub only ever sends complete TEXT envelopes; ignore anything else
        // (e.g. control frames) rather than leaking them onto the wire.
        if (messageType != WebSocketMessageType.Text)
            return;
        if (_state != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = _keys.Seal(_sendDir, buffer.AsSpan());
            await _transport
                .SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (_state != WebSocketState.Open)
        {
            // Racing teardown - swallow.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        _state = WebSocketState.Closed;
        CompleteInbound();

        // The hub calls this from inside its per-client write lock (the Pair
        // Remote killswitch path), so it must return promptly. Tear the relay
        // leg down with Abort rather than the WebSocket closing HANDSHAKE: a
        // CloseAsync would await the relay echoing a close frame, which can
        // stall the kill ("OFF means OFF") on a slow / gone relay. The host-side
        // session is over either way, so an abrupt transport close is correct.
        try { _transport.Abort(); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        _closeStatus ??= WebSocketCloseStatus.NormalClosure;
        _state = WebSocketState.Aborted;
        CompleteInbound();
        try { _transport.Abort(); } catch { /* best effort */ }
    }

    public override void Dispose()
    {
        if (_state == WebSocketState.Open)
            _state = WebSocketState.Closed;
        CompleteInbound();
        _sendLock.Dispose();
        // The relay socket is owned by RelayConnectionService, which disposes it
        // when the host leg tears down; don't dispose it here.
    }
}

using System;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Relay;

/// <summary>
/// Key and counter state of one sealed channel, with the protocol-v2 in-band
/// rekey. Wire: a v2 client's FIRST sealed frame is the exact bytes
/// <c>{"c":"hello2"}</c>; the host answers <c>{"c":"hn","hn":&lt;base64url 16 bytes&gt;}</c>
/// sealed under the connection key, and both sides switch to
/// <see cref="RelayCrypto.DeriveRekeyedAeadKey"/> with both counters restarted
/// at 0. A v1 client's first frame is data and the channel stays on the
/// connection key. The host nonce is what stops a recorded connection (client
/// salt plus frames) from replaying: the rekeyed key differs every time.
/// Not thread-safe: the owner processes inbound frames serially and holds its
/// send lock around <see cref="Seal"/> and <see cref="SealHostNonceAndRekey"/>.
/// Load-bearing invariant: the host sends nothing on a channel before the
/// client's first frame (the hub sends only to subscribers, the HTTP legs only
/// reply). A v2 client treats any host frame that is not the nonce as a v1 host
/// and stays on the connection key, so a host frame sent ahead of hello2 would
/// break every v2 session.
/// </summary>
public sealed class SealedChannelKeys
{
    /// <summary>Set once every shipped client sends hello2: a v1 client's frames are then all rejected (RelayWebSocket aborts, the HTTP legs drop) instead of served unrekeyed.</summary>
    public static bool RequireRekey { get; set; }

    private static readonly byte[] Hello2 = Encoding.UTF8.GetBytes("{\"c\":\"hello2\"}");

    private readonly Func<byte[], byte[]>? _rekeyDerive;
    private byte[] _aeadKey;
    private ulong _sendCounter;
    private long _lastRecvCounter = -1;

    public enum InboundKind { Data, RekeyRequest, Rejected }

    /// <param name="connectionKey">HKDF(relayRoot, connSalt), the v1 key.</param>
    /// <param name="rekeyDerive">hostNonce to rekeyed key; null when the channel cannot rekey (a v2 hello2 is then plain data).</param>
    public SealedChannelKeys(byte[] connectionKey, Func<byte[], byte[]>? rekeyDerive)
    {
        _aeadKey = connectionKey ?? throw new ArgumentNullException(nameof(connectionKey));
        _rekeyDerive = rekeyDerive;
    }

    public bool Rekeyed { get; private set; }

    /// <summary>
    /// Opens one inbound frame under the current key. Rejected means a wrong
    /// direction, a replayed or reordered counter, or (with RequireRekey) a v1
    /// first frame. Throws CryptographicException on a tag failure.
    /// </summary>
    public InboundKind Open(byte[] frame, byte expectDir, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        var (dir, counter, opened) = RelayCrypto.Open(_aeadKey, frame);
        if (dir != expectDir || (long)counter <= _lastRecvCounter)
        {
            return InboundKind.Rejected;
        }
        var first = _lastRecvCounter == -1 && !Rekeyed;
        if (first && _rekeyDerive is not null && opened.AsSpan().SequenceEqual(Hello2))
        {
            _lastRecvCounter = (long)counter;
            return InboundKind.RekeyRequest;
        }
        if (first && RequireRekey)
        {
            return InboundKind.Rejected; // counter not consumed: every v1 frame stays a rejected first frame
        }
        _lastRecvCounter = (long)counter;
        plaintext = opened;
        return InboundKind.Data;
    }

    /// <summary>Under the owner's send lock: the next outbound frame with the current key and counter.</summary>
    public byte[] Seal(byte sendDir, ReadOnlySpan<byte> plaintext)
        => RelayCrypto.Seal(_aeadKey, sendDir, _sendCounter++, plaintext);

    /// <summary>
    /// Under the owner's send lock, after <see cref="Open"/> returned
    /// RekeyRequest: the host-nonce reply sealed with the CURRENT key and
    /// counter, after which the channel is on the rekeyed key with both counters
    /// restarted. The returned frame must reach the wire before any frame sealed
    /// after this call.
    /// </summary>
    public byte[] SealHostNonceAndRekey(byte sendDir)
    {
        if (_rekeyDerive is null || Rekeyed)
        {
            throw new InvalidOperationException("channel is not awaiting a rekey");
        }
        var hostNonce = RandomNumberGenerator.GetBytes(RelayCrypto.HostNonceLength);
        var reply = Encoding.UTF8.GetBytes("{\"c\":\"hn\",\"hn\":\"" + RelayCrypto.Base64UrlNoPad(hostNonce) + "\"}");
        var frame = Seal(sendDir, reply);
        _aeadKey = _rekeyDerive(hostNonce);
        _sendCounter = 0;
        _lastRecvCounter = -1;
        Rekeyed = true;
        return frame;
    }
}

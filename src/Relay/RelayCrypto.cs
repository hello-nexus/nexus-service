using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Relay;

/// <summary>
/// End-to-end crypto for the cloud relay transport. The relay (nexus-api) is a
/// dumb opaque byte-forwarder; every security property lives here, between this
/// service (the "host") and the browser/WKWebView client.
///
/// Must interop byte-for-byte with the WebCrypto implementation in nexus-web.
/// The known-answer vectors in the relay-transport spec are pinned in
/// <c>RelayCryptoTests</c>; do NOT change a derivation without updating both
/// ends and the vectors.
///
/// Derivations (HKDF-SHA256, RFC 5869; empty salt = zero-length):
///   relayRoot = HKDF(IKM=utf8(token),    salt=∅,        info="nexus-relay-root-v1",       L=32)
///   rid       = b64url-nopad( HKDF(IKM=relayRoot, salt=∅, info="nexus-relay-rendezvous-v1", L=16) )
///   aeadKey   = HKDF(IKM=relayRoot,       salt=connSalt, info="nexus-relay-aead-v1",        L=32)
///   aeadKey2  = HKDF(IKM=relayRoot,       salt=connSalt||hostNonce, info="nexus-relay-aead-v2", L=32)
///               (protocol v2 in-band rekey, see SealedChannelKeys)
///
/// Frame (AES-256-GCM, AAD empty):
///   frame = nonce(12) || ciphertext || tag(16)
///   nonce = [dir(1)] [counter(8 big-endian)] [0,0,0]
///   dir = 1 host→client, 2 client→host; counter starts 0, +1 per frame per
///   sender per connection. Fresh aeadKey per connection ⇒ no nonce reuse.
/// </summary>
public static class RelayCrypto
{
    public const int RelayRootLength = 32;
    public const int RidLength = 16;
    public const int AeadKeyLength = 32;
    public const int ConnSaltLength = 16;
    public const int HostNonceLength = 16;
    public const int NonceLength = 12;
    public const int TagLength = 16;

    /// <summary>host→client direction byte.</summary>
    public const byte DirHostToClient = 1;
    /// <summary>client→host direction byte.</summary>
    public const byte DirClientToHost = 2;

    private static readonly byte[] InfoRoot = Encoding.UTF8.GetBytes("nexus-relay-root-v1");
    private static readonly byte[] InfoRendezvous = Encoding.UTF8.GetBytes("nexus-relay-rendezvous-v1");
    private static readonly byte[] InfoHttpRendezvous = Encoding.UTF8.GetBytes("nexus-relay-http-rendezvous-v1");
    private static readonly byte[] InfoAead = Encoding.UTF8.GetBytes("nexus-relay-aead-v1");
    private static readonly byte[] InfoAeadV2 = Encoding.UTF8.GetBytes("nexus-relay-aead-v2");
    private static readonly byte[] InfoPairRoot = Encoding.UTF8.GetBytes("nexus-relay-pairroot-v1");

    /// <summary>
    /// relayRoot = HKDF-SHA256(IKM=utf8(token), salt=∅, info="nexus-relay-root-v1", L=32).
    /// The PC stores this (base64) per session; the token itself stays hash-only.
    /// </summary>
    public static byte[] DeriveRelayRoot(string sessionToken)
    {
        ArgumentNullException.ThrowIfNull(sessionToken);
        var ikm = Encoding.UTF8.GetBytes(sessionToken);
        var output = new byte[RelayRootLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt: ReadOnlySpan<byte>.Empty, info: InfoRoot);
        return output;
    }

    /// <summary>
    /// pairRoot = HKDF-SHA256(IKM=utf8(pairToken), salt=∅, info="nexus-relay-pairroot-v1", L=32).
    /// The pre-pair analogue of <see cref="DeriveRelayRoot"/>, keyed off the
    /// short-lived QR <c>pair</c> token. <c>rid_pair = DeriveRid(pairRoot)</c> and
    /// <c>claimKey = DeriveAeadKey(pairRoot, connSalt)</c> reuse the runtime
    /// derivations unchanged; the distinct <c>info</c> (and a pair token never
    /// being a session token) guarantees a pair rid can never collide with a
    /// session rid. The PC derives this from each outstanding pair token to
    /// register the pair rendezvous; the relay only ever sees the rid.
    /// </summary>
    public static byte[] DerivePairRoot(string pairToken)
    {
        ArgumentNullException.ThrowIfNull(pairToken);
        var ikm = Encoding.UTF8.GetBytes(pairToken);
        var output = new byte[RelayRootLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt: ReadOnlySpan<byte>.Empty, info: InfoPairRoot);
        return output;
    }

    /// <summary>
    /// rid = base64url-nopad( HKDF-SHA256(IKM=relayRoot, salt=∅, info="nexus-relay-rendezvous-v1", L=16) ).
    /// 128-bit unguessable rendezvous id; the relay sees only this (HKDF is one-way).
    /// </summary>
    public static string DeriveRid(byte[] relayRoot)
    {
        ArgumentNullException.ThrowIfNull(relayRoot);
        Span<byte> raw = stackalloc byte[RidLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, relayRoot, raw, salt: ReadOnlySpan<byte>.Empty, info: InfoRendezvous);
        return Base64UrlNoPad(raw);
    }

    /// <summary>
    /// rid_http = base64url-nopad( HKDF-SHA256(IKM=relayRoot, salt=∅, info="nexus-relay-http-rendezvous-v1", L=16) ).
    /// A SECOND rendezvous id per session, distinct from the runtime <see cref="DeriveRid"/>
    /// (different HKDF info ⇒ the two can never collide), so the REST-over-relay
    /// HTTP tunnel rides its own host link without touching the proven <c>/ws</c>
    /// runtime channel. The per-connection AEAD key reuses
    /// <see cref="DeriveAeadKey"/> off the same relayRoot.
    /// </summary>
    public static string DeriveHttpRid(byte[] relayRoot)
    {
        ArgumentNullException.ThrowIfNull(relayRoot);
        Span<byte> raw = stackalloc byte[RidLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, relayRoot, raw, salt: ReadOnlySpan<byte>.Empty, info: InfoHttpRendezvous);
        return Base64UrlNoPad(raw);
    }

    /// <summary>
    /// aeadKey = HKDF-SHA256(IKM=relayRoot, salt=connSalt(16 random bytes), info="nexus-relay-aead-v1", L=32).
    /// A fresh connSalt per connection yields a fresh key, so the (dir,counter)
    /// nonce can safely restart at 0 each connection without ever repeating.
    /// </summary>
    public static byte[] DeriveAeadKey(byte[] relayRoot, byte[] connSalt)
    {
        ArgumentNullException.ThrowIfNull(relayRoot);
        ArgumentNullException.ThrowIfNull(connSalt);
        var output = new byte[AeadKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, relayRoot, output, salt: connSalt, info: InfoAead);
        return output;
    }

    /// <summary>
    /// aeadKey2 = HKDF-SHA256(IKM=relayRoot, salt=connSalt||hostNonce, info="nexus-relay-aead-v2", L=32).
    /// The host's 16 random bytes keep the key fresh even when a client salt
    /// repeats, so a recorded connection cannot replay (<see cref="SealedChannelKeys"/>).
    /// </summary>
    public static byte[] DeriveRekeyedAeadKey(byte[] relayRoot, byte[] connSalt, byte[] hostNonce)
    {
        ArgumentNullException.ThrowIfNull(relayRoot);
        ArgumentNullException.ThrowIfNull(connSalt);
        ArgumentNullException.ThrowIfNull(hostNonce);
        var salt = new byte[connSalt.Length + hostNonce.Length];
        connSalt.CopyTo(salt, 0);
        hostNonce.CopyTo(salt, connSalt.Length);
        var output = new byte[AeadKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, relayRoot, output, salt: salt, info: InfoAeadV2);
        return output;
    }

    /// <summary>
    /// Seal one plaintext frame: builds the (dir,counter) nonce, AES-256-GCM
    /// encrypts (AAD empty), and returns <c>nonce(12) || ciphertext || tag(16)</c>.
    /// </summary>
    public static byte[] Seal(byte[] aeadKey, byte dir, ulong counter, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(aeadKey);

        var frame = new byte[NonceLength + plaintext.Length + TagLength];
        var nonce = frame.AsSpan(0, NonceLength);
        WriteNonce(nonce, dir, counter);

        var ciphertext = frame.AsSpan(NonceLength, plaintext.Length);
        var tag = frame.AsSpan(NonceLength + plaintext.Length, TagLength);

        using var gcm = new AesGcm(aeadKey, TagLength);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        return frame;
    }

    /// <summary>
    /// Open one frame: parses the nonce (recovering dir + counter), AES-256-GCM
    /// decrypts and verifies the tag (AAD empty). Throws
    /// <see cref="CryptographicException"/> on a tampered tag / wrong key - the
    /// caller must drop the frame and close the channel on failure.
    /// </summary>
    public static (byte Dir, ulong Counter, byte[] Plaintext) Open(byte[] aeadKey, ReadOnlySpan<byte> frame)
    {
        ArgumentNullException.ThrowIfNull(aeadKey);
        if (frame.Length < NonceLength + TagLength)
            throw new CryptographicException("relay frame shorter than nonce + tag");

        var nonce = frame.Slice(0, NonceLength);
        var (dir, counter) = ReadNonce(nonce);

        var ctLength = frame.Length - NonceLength - TagLength;
        var ciphertext = frame.Slice(NonceLength, ctLength);
        var tag = frame.Slice(NonceLength + ctLength, TagLength);

        var plaintext = new byte[ctLength];
        using var gcm = new AesGcm(aeadKey, TagLength);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return (dir, counter, plaintext);
    }

    /// <summary>nonce = [dir(1)] [counter(8 big-endian)] [0,0,0].</summary>
    private static void WriteNonce(Span<byte> nonce, byte dir, ulong counter)
    {
        nonce.Clear();
        nonce[0] = dir;
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(1, 8), counter);
        // bytes [9..11] stay zero
    }

    private static (byte Dir, ulong Counter) ReadNonce(ReadOnlySpan<byte> nonce)
    {
        var dir = nonce[0];
        var counter = BinaryPrimitives.ReadUInt64BigEndian(nonce.Slice(1, 8));
        return (dir, counter);
    }

    /// <summary>base64url with the '+/' alphabet swapped to '-_' and '=' padding stripped.</summary>
    public static string Base64UrlNoPad(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Decode a base64url-no-pad string (re-pads + un-swaps the alphabet).</summary>
    public static byte[] FromBase64UrlNoPad(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

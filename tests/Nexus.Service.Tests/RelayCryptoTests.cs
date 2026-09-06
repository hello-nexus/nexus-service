using System;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Relay;

namespace Nexus.Service.Tests;

/// <summary>
/// Known-answer tests pinning the relay crypto contract to the exact hex
/// vectors in the relay-transport spec. These vectors are shared verbatim with
/// the WebCrypto implementation in nexus-web; if any assertion here changes, the
/// two ends have diverged and the channel will silently fail to decrypt.
/// </summary>
public class RelayCryptoTests
{
    // ── Spec vectors ────────────────────────────────────────────────────────
    private const string Token = "test-session-token-0123456789";
    private const string RelayRootHex = "36557d360330aad63010a257c870deb9f57c19d222ea630a9a204889eb435270";
    private const string Rid = "E5HaHgqqZJGdG5QZQR_LTQ";
    private const string ConnSaltHex = "000102030405060708090a0b0c0d0e0f";
    private const string AeadKeyHex = "2af213994553c206b634442d19b45b796710fd2e69efc060a9c10de16bb29e5f";
    private const string NonceHex = "010000000000000000000000";
    private const string Plaintext = "{\"t\":\"ping\",\"d\":1}";
    private const string CiphertextHex = "b62dce75421fdfc7760de887f5675c9f1aba";
    private const string TagHex = "87de074f45dc48f1bd17e54d146b70b3";
    private const string FrameHex =
        "010000000000000000000000b62dce75421fdfc7760de887f5675c9f1aba87de074f45dc48f1bd17e54d146b70b3";

    [Fact]
    public void DeriveRelayRoot_MatchesVector()
    {
        var root = RelayCrypto.DeriveRelayRoot(Token);
        Assert.Equal(RelayRootHex, Hex(root));
    }

    [Fact]
    public void DeriveRid_MatchesVector()
    {
        var root = FromHex(RelayRootHex);
        Assert.Equal(Rid, RelayCrypto.DeriveRid(root));
    }

    // ── Phase-2 REST-over-relay rid_http vector ──────────────────────────────
    // rid_http = base64url-nopad(HKDF(relayRoot, ∅, "nexus-relay-http-rendezvous-v1", 16)).
    private const string RidHttp = "0jI7tzgoE89ewOpZng6rlA";

    [Fact]
    public void DeriveHttpRid_MatchesVector()
    {
        var root = FromHex(RelayRootHex);
        Assert.Equal(RidHttp, RelayCrypto.DeriveHttpRid(root));
    }

    [Fact]
    public void DeriveHttpRid_FromToken_EndToEnd()
    {
        // Token → relayRoot → rid_http, all the way through.
        var root = RelayCrypto.DeriveRelayRoot(Token);
        Assert.Equal(RidHttp, RelayCrypto.DeriveHttpRid(root));
    }

    [Fact]
    public void HttpRid_DiffersFromRuntimeRid_SameRoot()
    {
        // The HTTP tunnel rides a SECOND rendezvous off the same relayRoot; the
        // distinct HKDF info guarantees it never collides with the runtime rid,
        // so the proven /ws channel is untouched.
        var root = FromHex(RelayRootHex);
        Assert.NotEqual(RelayCrypto.DeriveRid(root), RelayCrypto.DeriveHttpRid(root));
    }

    // Protocol v2 rekey vector, shared verbatim with nexus-web's relayCrypto.test.ts.
    private const string RekeyRootHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";
    private const string RekeyConnSaltHex = "a0a1a2a3a4a5a6a7a8a9aaabacadaeaf";
    private const string RekeyHostNonceHex = "b0b1b2b3b4b5b6b7b8b9babbbcbdbebf";
    private const string RekeyedAeadKeyHex = "c4c69c871369455a57f8dde385b7ace9bb9695b9d96ea63828e3a3cfb115c173";
    private const string RekeyV1AeadKeyHex = "3ee9775c9c7a4501e0a9992ba5a1724289b8a5168fa6e32ab3a5eb79de0545af";

    [Fact]
    public void DeriveRekeyedAeadKey_MatchesVector_AndDiffersFromTheConnectionKey()
    {
        var root = FromHex(RekeyRootHex);
        var salt = FromHex(RekeyConnSaltHex);
        Assert.Equal(RekeyV1AeadKeyHex, Hex(RelayCrypto.DeriveAeadKey(root, salt)));
        Assert.Equal(RekeyedAeadKeyHex, Hex(RelayCrypto.DeriveRekeyedAeadKey(root, salt, FromHex(RekeyHostNonceHex))));
        // A different host nonce is a different key: the property the replay fix rests on.
        Assert.NotEqual(RekeyedAeadKeyHex, Hex(RelayCrypto.DeriveRekeyedAeadKey(root, salt, new byte[RelayCrypto.HostNonceLength])));
    }

    [Fact]
    public void DeriveAeadKey_MatchesVector()
    {
        var root = FromHex(RelayRootHex);
        var key = RelayCrypto.DeriveAeadKey(root, FromHex(ConnSaltHex));
        Assert.Equal(AeadKeyHex, Hex(key));
    }

    [Fact]
    public void Seal_ProducesVectorNonceCiphertextTagAndFrame()
    {
        var key = FromHex(AeadKeyHex);
        var plaintext = Encoding.UTF8.GetBytes(Plaintext);

        var frame = RelayCrypto.Seal(key, RelayCrypto.DirHostToClient, counter: 0, plaintext);

        // Whole frame must equal the pinned hex byte-for-byte.
        Assert.Equal(FrameHex, Hex(frame));

        // And each segment in isolation.
        var nonce = frame.AsSpan(0, RelayCrypto.NonceLength);
        var ciphertext = frame.AsSpan(RelayCrypto.NonceLength, plaintext.Length);
        var tag = frame.AsSpan(RelayCrypto.NonceLength + plaintext.Length, RelayCrypto.TagLength);
        Assert.Equal(NonceHex, Hex(nonce));
        Assert.Equal(CiphertextHex, Hex(ciphertext));
        Assert.Equal(TagHex, Hex(tag));
    }

    [Fact]
    public void Open_DecryptsVectorFrame()
    {
        var key = FromHex(AeadKeyHex);
        var (dir, counter, plaintext) = RelayCrypto.Open(key, FromHex(FrameHex));

        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0ul, counter);
        Assert.Equal(Plaintext, Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void SealOpen_RoundTrips_AcrossDirectionsAndCounters()
    {
        var key = RandomNumberGenerator.GetBytes(RelayCrypto.AeadKeyLength);
        var payload = Encoding.UTF8.GetBytes("{\"sub\":[\"processes\",\"network\"]}");

        foreach (var dir in new[] { RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost })
        {
            foreach (var counter in new ulong[] { 0, 1, 42, ulong.MaxValue })
            {
                var frame = RelayCrypto.Seal(key, dir, counter, payload);
                var (gotDir, gotCounter, gotPlain) = RelayCrypto.Open(key, frame);
                Assert.Equal(dir, gotDir);
                Assert.Equal(counter, gotCounter);
                Assert.Equal(payload, gotPlain);
            }
        }
    }

    [Fact]
    public void Open_RejectsTamperedTag()
    {
        var key = FromHex(AeadKeyHex);
        var frame = FromHex(FrameHex);
        // Flip one bit in the authentication tag (last byte).
        frame[^1] ^= 0x01;

        // AuthenticationTagMismatchException derives from CryptographicException;
        // assert on the base so the contract ("crypto failure ⇒ drop the frame")
        // holds regardless of the platform's concrete subclass.
        Assert.ThrowsAny<CryptographicException>(() => RelayCrypto.Open(key, frame));
    }

    [Fact]
    public void Open_RejectsTamperedCiphertext()
    {
        var key = FromHex(AeadKeyHex);
        var frame = FromHex(FrameHex);
        // Flip one bit inside the ciphertext body.
        frame[RelayCrypto.NonceLength] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => RelayCrypto.Open(key, frame));
    }

    [Fact]
    public void Open_RejectsWrongKey()
    {
        var wrongKey = RandomNumberGenerator.GetBytes(RelayCrypto.AeadKeyLength);
        Assert.ThrowsAny<CryptographicException>(() => RelayCrypto.Open(wrongKey, FromHex(FrameHex)));
    }

    [Fact]
    public void Base64Url_RoundTrips_NoPadding()
    {
        var bytes = FromHex(RelayRootHex);
        var encoded = RelayCrypto.Base64UrlNoPad(bytes);
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(bytes, RelayCrypto.FromBase64UrlNoPad(encoded));
    }

    [Fact]
    public void RidIsDerivableFromRoot_DerivedFromToken_EndToEnd()
    {
        // Token → relayRoot → rid, all the way through, matches the pinned rid.
        var root = RelayCrypto.DeriveRelayRoot(Token);
        Assert.Equal(Rid, RelayCrypto.DeriveRid(root));
    }

    // ── Phase-1 pre-pair vectors (claim-over-relay) ──────────────────────────
    // pairRoot = HKDF(utf8(pairToken), ∅, "nexus-relay-pairroot-v1", 32);
    // rid_pair = DeriveRid(pairRoot); claimKey = DeriveAeadKey(pairRoot, connSalt).
    private const string PairToken = "PAIRTOK-abcdef0123456789";
    private const string PairRootHex = "c0927b2211eef59afdcaf397ac8e97a9e966a1cfcd23c201d882724dd83361ad";
    private const string RidPair = "0BwEM0g8zmt-2llFkxrdrw";
    private const string ClaimKeyHex = "0a195060337f3a6dbd4ade2d600cefb7decae749d5596d78a6d2b777261f1cbf";

    [Fact]
    public void DerivePairRoot_MatchesVector()
    {
        var pairRoot = RelayCrypto.DerivePairRoot(PairToken);
        Assert.Equal(PairRootHex, Hex(pairRoot));
    }

    [Fact]
    public void RidPair_MatchesVector()
    {
        // rid_pair reuses DeriveRid off the pairRoot; distinct IKM from any
        // session rid, so the two rendezvous namespaces never collide.
        var pairRoot = FromHex(PairRootHex);
        Assert.Equal(RidPair, RelayCrypto.DeriveRid(pairRoot));
    }

    [Fact]
    public void ClaimKey_MatchesVector()
    {
        // claimKey reuses DeriveAeadKey(pairRoot, connSalt) with the shared
        // connSalt vector (00 01 … 0f).
        var pairRoot = FromHex(PairRootHex);
        var claimKey = RelayCrypto.DeriveAeadKey(pairRoot, FromHex(ConnSaltHex));
        Assert.Equal(ClaimKeyHex, Hex(claimKey));
    }

    [Fact]
    public void PairDerivations_EndToEnd_FromToken()
    {
        // pairToken → pairRoot → rid_pair + claimKey, all the way through.
        var pairRoot = RelayCrypto.DerivePairRoot(PairToken);
        Assert.Equal(PairRootHex, Hex(pairRoot));
        Assert.Equal(RidPair, RelayCrypto.DeriveRid(pairRoot));
        Assert.Equal(ClaimKeyHex, Hex(RelayCrypto.DeriveAeadKey(pairRoot, FromHex(ConnSaltHex))));
    }

    [Fact]
    public void PairRid_NeverCollidesWithSessionRid_ForSameTokenString()
    {
        // The same string fed as a session token vs a pair token yields disjoint
        // rids (distinct HKDF info), so a pair rendezvous can't shadow a session.
        var asSession = RelayCrypto.DeriveRid(RelayCrypto.DeriveRelayRoot(PairToken));
        var asPair = RelayCrypto.DeriveRid(RelayCrypto.DerivePairRoot(PairToken));
        Assert.NotEqual(asSession, asPair);
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}

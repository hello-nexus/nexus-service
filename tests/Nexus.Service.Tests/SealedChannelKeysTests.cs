using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Relay;

namespace Nexus.Service.Tests;

/// <summary>
/// Protocol-v2 in-band rekey: a v2 client's first frame is hello2, the host
/// answers with a sealed host nonce, both switch keys and restart counters; a
/// v1 client's first frame is data and nothing changes.
/// </summary>
public sealed class SealedChannelKeysTests
{
    private static readonly byte[] Root = RandomNumberGenerator.GetBytes(RelayCrypto.RelayRootLength);
    private static readonly byte[] Salt = RandomNumberGenerator.GetBytes(RelayCrypto.ConnSaltLength);
    private static readonly byte[] K0 = RelayCrypto.DeriveAeadKey(Root, Salt);
    private static readonly byte[] Hello2 = Encoding.UTF8.GetBytes("{\"c\":\"hello2\"}");

    private static SealedChannelKeys HostKeys() =>
        new(K0, hn => RelayCrypto.DeriveRekeyedAeadKey(Root, Salt, hn));

    private static byte[] ClientFrame(byte[] key, ulong counter, string text)
        => RelayCrypto.Seal(key, RelayCrypto.DirClientToHost, counter, Encoding.UTF8.GetBytes(text));

    private static byte[] ParseHostNonce(byte[] hnFrame, out ulong counter)
    {
        var (dir, c, plaintext) = RelayCrypto.Open(K0, hnFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        counter = c;
        using var doc = JsonDocument.Parse(plaintext);
        Assert.Equal("hn", doc.RootElement.GetProperty("c").GetString());
        var hn = RelayCrypto.FromBase64UrlNoPad(doc.RootElement.GetProperty("hn").GetString()!);
        Assert.Equal(RelayCrypto.HostNonceLength, hn.Length);
        return hn;
    }

    [Fact]
    public void V1_first_frame_is_data_and_stays_on_the_connection_key()
    {
        var keys = HostKeys();
        Assert.Equal(SealedChannelKeys.InboundKind.Data, keys.Open(ClientFrame(K0, 0, "{\"sub\":[\"volume\"]}"), RelayCrypto.DirClientToHost, out var plaintext));
        Assert.Equal("{\"sub\":[\"volume\"]}", Encoding.UTF8.GetString(plaintext));
        Assert.False(keys.Rekeyed);
        // A late hello2 is plain data, not a rekey.
        Assert.Equal(SealedChannelKeys.InboundKind.Data, keys.Open(ClientFrame(K0, 1, "{\"c\":\"hello2\"}"), RelayCrypto.DirClientToHost, out var late));
        Assert.Equal(Hello2, late);
        Assert.False(keys.Rekeyed);
    }

    [Fact]
    public void Hello2_rekeys_both_directions_and_restarts_counters()
    {
        var keys = HostKeys();
        // A host frame sent before the rekey uses the connection key and counter 0.
        var early = keys.Seal(RelayCrypto.DirHostToClient, "early"u8);
        Assert.Equal((RelayCrypto.DirHostToClient, 0UL), Open(K0, early).DirCounter);

        Assert.Equal(SealedChannelKeys.InboundKind.RekeyRequest, keys.Open(ClientFrame(K0, 0, "{\"c\":\"hello2\"}"), RelayCrypto.DirClientToHost, out _));
        var hnFrame = keys.SealHostNonceAndRekey(RelayCrypto.DirHostToClient);
        var hn = ParseHostNonce(hnFrame, out var hnCounter);
        Assert.Equal(1UL, hnCounter); // still the connection key's counter sequence
        Assert.True(keys.Rekeyed);

        var k1 = RelayCrypto.DeriveRekeyedAeadKey(Root, Salt, hn);
        // Client data under the rekeyed key restarts at counter 0.
        Assert.Equal(SealedChannelKeys.InboundKind.Data, keys.Open(ClientFrame(k1, 0, "{\"sub\":[\"volume\"]}"), RelayCrypto.DirClientToHost, out var plaintext));
        Assert.Equal("{\"sub\":[\"volume\"]}", Encoding.UTF8.GetString(plaintext));
        // Host frames restart at counter 0 under the rekeyed key.
        var sealed_ = keys.Seal(RelayCrypto.DirHostToClient, "late"u8);
        Assert.Equal((RelayCrypto.DirHostToClient, 0UL), Open(k1, sealed_).DirCounter);
        // The connection key is dead: a replayed hello2 or any K0 frame fails the tag.
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(ClientFrame(K0, 0, "{\"c\":\"hello2\"}"), RelayCrypto.DirClientToHost, out _));
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(ClientFrame(K0, 5, "{}"), RelayCrypto.DirClientToHost, out _));
        // Rekeying twice is a protocol error.
        Assert.Throws<InvalidOperationException>(() => keys.SealHostNonceAndRekey(RelayCrypto.DirHostToClient));
        // Replay under the rekeyed key is still caught by the counter.
        Assert.Equal(SealedChannelKeys.InboundKind.Rejected, keys.Open(ClientFrame(k1, 0, "{}"), RelayCrypto.DirClientToHost, out _));
    }

    [Fact]
    public void Without_a_rekey_derivation_hello2_is_plain_data()
    {
        var keys = new SealedChannelKeys(K0, rekeyDerive: null);
        Assert.Equal(SealedChannelKeys.InboundKind.Data, keys.Open(ClientFrame(K0, 0, "{\"c\":\"hello2\"}"), RelayCrypto.DirClientToHost, out var plaintext));
        Assert.Equal(Hello2, plaintext);
    }

    [Fact]
    public void Wrong_direction_is_rejected()
    {
        var keys = HostKeys();
        var hostDirFrame = RelayCrypto.Seal(K0, RelayCrypto.DirHostToClient, 0, "{}"u8);
        Assert.Equal(SealedChannelKeys.InboundKind.Rejected, keys.Open(hostDirFrame, RelayCrypto.DirClientToHost, out _));
    }

    [Fact]
    public void RequireRekey_closes_a_v1_client_on_its_first_frame()
    {
        SealedChannelKeys.RequireRekey = true;
        try
        {
            var keys = HostKeys();
            Assert.Equal(SealedChannelKeys.InboundKind.Rejected, keys.Open(ClientFrame(K0, 0, "{\"sub\":[\"volume\"]}"), RelayCrypto.DirClientToHost, out _));
            // Sticky: the HTTP legs drop a rejected frame and keep reading, so the next v1 frame must be rejected too.
            Assert.Equal(SealedChannelKeys.InboundKind.Rejected, keys.Open(ClientFrame(K0, 1, "{\"sub\":[\"volume\"]}"), RelayCrypto.DirClientToHost, out _));
            var v2 = HostKeys();
            Assert.Equal(SealedChannelKeys.InboundKind.RekeyRequest, v2.Open(ClientFrame(K0, 0, "{\"c\":\"hello2\"}"), RelayCrypto.DirClientToHost, out _));
        }
        finally
        {
            SealedChannelKeys.RequireRekey = false;
        }
    }

    private static (( byte, ulong ) DirCounter, byte[] Plaintext) Open(byte[] key, byte[] frame)
    {
        var (dir, counter, plaintext) = RelayCrypto.Open(key, frame);
        return ((dir, counter), plaintext);
    }
}

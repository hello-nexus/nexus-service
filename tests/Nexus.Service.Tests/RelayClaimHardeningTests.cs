using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// The claim-over-relay pair leg on the production path (RelayConnectionService),
/// not just ClaimCore: nonce safety, the capability flag, and one prompt per token.
/// </summary>
public sealed class RelayClaimHardeningTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public MultiRidFakeRelay Relay = new();
        public PanelPhonePairingService Pairing = null!;
        public MultiplexHub Hub = new();
        public RelayConnectionService Service = null!;
        public string Token = "";
        public string Rid = "";
        public byte[] PairRoot = Array.Empty<byte>();
        public int Created;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            await rig.Relay.StartAsync();
            var store = new InMemoryConfigStore();
            store.Update(s =>
            {
                s.Auth ??= new AuthSettings();
                s.Auth.RemoteControlEnabled = true;
                s.Auth.RelayEnabled = true;
                s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>();
            });
            rig.Pairing = new PanelPhonePairingService(store, rig.Hub) { PublicLinkHost = "", SpkiFingerprint = "fp-stub" };
            rig.Pairing.PairRequestCreated += () => Interlocked.Increment(ref rig.Created);
            rig.Service = new RelayConnectionService(
                NullLogger<RelayConnectionService>.Instance, rig.Pairing, store, rig.Hub, RelayTestHelpers.InertHttpDispatcher())
            {
                Endpoint = rig.Relay.Uri,
            };
            await rig.Service.StartAsync(CancellationToken.None);
            var url = rig.Pairing.CreatePairQr().Url;
            foreach (var part in new Uri(url).Query.TrimStart('?').Split('&'))
            {
                if (part.StartsWith("pair=", StringComparison.Ordinal))
                    rig.Token = Uri.UnescapeDataString(part[5..]);
            }
            rig.PairRoot = RelayCrypto.DerivePairRoot(rig.Token);
            rig.Rid = RelayCrypto.DeriveRid(rig.PairRoot);
            await rig.Relay.WaitForHostAsync(rig.Rid, MultiRidFakeRelay.HostWait);
            return rig;
        }

        public static byte[] Salt(byte seed)
        {
            var salt = new byte[RelayCrypto.ConnSaltLength];
            for (var i = 0; i < salt.Length; i++) salt[i] = (byte)(seed + i);
            return salt;
        }

        public async Task<byte[]> PeerUpAsync(byte seed)
        {
            var salt = Salt(seed);
            await Relay.SendPeerUpAsync(Rid, salt);
            return RelayCrypto.DeriveAeadKey(PairRoot, salt);
        }

        public Task SendAsync(byte[] key, ulong counter, RelayClaimRequest request)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, AppJsonContext.Default.RelayClaimRequest);
            return Relay.ForwardToHostAsync(Rid, RelayCrypto.Seal(key, RelayCrypto.DirClientToHost, counter, bytes));
        }

        public async Task<(ulong Counter, RelayClaimResponse Reply)> ReadAsync(byte[] key)
        {
            var frame = await Relay.WaitForHostFrameAsync(Rid, TimeSpan.FromSeconds(5));
            var (dir, counter, plain) = RelayCrypto.Open(key, frame);
            Assert.Equal(RelayCrypto.DirHostToClient, dir);
            return (counter, System.Text.Json.JsonSerializer.Deserialize(plain, AppJsonContext.Default.RelayClaimResponse)!);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
            Relay.Dispose();
        }
    }

    private static RelayClaimRequest Claim(bool flag = true) =>
        new() { Type = RelayClaimMessageTypes.Claim, DeviceName = "Phone", DeviceId = "dev-r", SupportsSasApproval = flag };

    [Fact]
    public async Task ClaimWithoutFlag_GetsUpdateRequired_AndRaisesNoPrompt()
    {
        await using var rig = await Rig.StartAsync();
        var key = await rig.PeerUpAsync(0x10);

        await rig.SendAsync(key, 0, Claim(flag: false));
        var (_, reply) = await rig.ReadAsync(key);

        Assert.Equal(RelayClaimMessageTypes.ClaimErr, reply.Type);
        Assert.Equal("update_required", reply.Error);
        Assert.False(rig.Hub.TryGetTopicSnapshot(PanelTopics.PairCodeRequest, out _));
        Assert.Equal(0, Volatile.Read(ref rig.Created));

        // The link and token survive, so an updated app can claim on the same QR.
        await rig.SendAsync(key, 1, Claim());
        Assert.Equal(RelayClaimMessageTypes.ClaimPending, (await rig.ReadAsync(key)).Reply.Type);
    }

    [Fact]
    public async Task SecondClaimOnPendingToken_IsRefused_NoSecondPrompt()
    {
        await using var rig = await Rig.StartAsync();
        var key = await rig.PeerUpAsync(0x20);
        await rig.SendAsync(key, 0, Claim());
        var pending = (await rig.ReadAsync(key)).Reply;
        Assert.Equal(RelayClaimMessageTypes.ClaimPending, pending.Type);

        await rig.SendAsync(key, 1, Claim());
        var refused = (await rig.ReadAsync(key)).Reply;
        Assert.Equal(RelayClaimMessageTypes.ClaimErr, refused.Type);
        Assert.Equal("claim-in-progress", refused.Error);

        // Deny is final: re-claiming cannot mint a fresh prompt, and the poll learns the denial.
        rig.Pairing.HostDecisionPairCode(pending.RequestId, approved: false);
        await rig.SendAsync(key, 2, Claim());
        Assert.Equal("claim-in-progress", (await rig.ReadAsync(key)).Reply.Error);
        await rig.SendAsync(key, 3, new RelayClaimRequest { Type = RelayClaimMessageTypes.ClaimConfirm, RequestId = pending.RequestId });
        Assert.Equal("denied", (await rig.ReadAsync(key)).Reply.Error);
        Assert.DoesNotContain(rig.Pairing.GetOutstandingPairTokens(), t => t.Token == rig.Token);
        Assert.Equal(1, Volatile.Read(ref rig.Created));
    }

    [Fact]
    public async Task ReplayedPeerUpSalt_AndReplayedInboundCounter_NeverProduceAReply()
    {
        await using var rig = await Rig.StartAsync();
        var keyA = await rig.PeerUpAsync(0x30);
        await rig.SendAsync(keyA, 0, Claim());
        var (firstCounter, pending) = await rig.ReadAsync(keyA);
        Assert.Equal(0ul, firstCounter);

        // The relay replays peer-up with the same salt and the phone's claim: same key,
        // same nonces. The host must stay silent rather than seal a second reply under them.
        await rig.PeerUpAsync(0x30);
        await rig.SendAsync(keyA, 0, Claim());

        // A fresh salt is a fresh key. The next frame out must be its first reply (counter 0),
        // proving nothing was emitted for the replays.
        var keyB = await rig.PeerUpAsync(0x40);
        var confirm = new RelayClaimRequest { Type = RelayClaimMessageTypes.ClaimConfirm, RequestId = pending.RequestId };
        await rig.SendAsync(keyB, 0, confirm);
        var (counter, waiting) = await rig.ReadAsync(keyB);
        Assert.Equal(0ul, counter);
        Assert.Equal(RelayClaimMessageTypes.ClaimWaiting, waiting.Type);

        // Same key, same inbound counter again: ignored. A higher counter is answered with the next outbound one.
        await rig.SendAsync(keyB, 0, confirm);
        await rig.SendAsync(keyB, 1, confirm);
        var (next, _) = await rig.ReadAsync(keyB);
        Assert.Equal(1ul, next);
    }
}

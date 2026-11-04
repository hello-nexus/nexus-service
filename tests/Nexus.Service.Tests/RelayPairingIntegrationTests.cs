using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// End-to-end integration for claim-over-relay (Phase 1: a brand-new phone with
/// no LAN reachability pairs through the cloud relay). Stands up a multi-rid
/// loopback fake relay and proves:
///   • with relay + remote on, the PC registers a host socket on rid_pair for
///     the outstanding QR pair token,
///   • a phone that peers up on rid_pair and sends a sealed claim request gets a
///     sealed claim-ok carrying a usable session token,
///   • the pair token is single-use (a second claim attempt fails),
///   • once the session exists, the PC bridges its RUNTIME rid into the hub
///     (a relayed `sub` command reaches the hub on the new session's rid).
/// </summary>
public sealed class RelayPairingIntegrationTests
{
    private const string DeviceName = "Nicola's iPhone";
    private const string Topic = "relay-pair-roundtrip-topic";

    [Fact]
    public async Task ClaimOverRelay_MintsUsableSession_TokenSingleUse_AndRuntimeRidBridges()
    {
        using var relay = new MultiRidFakeRelay();
        await relay.StartAsync();

        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.RelayEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>();
        });

        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };

        using var service = new RelayConnectionService(
            NullLogger<RelayConnectionService>.Instance, pairing, store, hub,
            RelayTestHelpers.InertHttpDispatcher())
        {
            Endpoint = relay.Uri,
        };
        await service.StartAsync(CancellationToken.None);

        // 1) Mint a QR pair token (this is the real CreatePairQr mint path; it
        //    fires OutstandingPairTokensChanged → the relay desires a pair link).
        var qr = pairing.CreatePairQr();
        var pairToken = ExtractPairToken(qr.Url);
        Assert.False(string.IsNullOrEmpty(pairToken));

        var pairRoot = RelayCrypto.DerivePairRoot(pairToken);
        var ridPair = RelayCrypto.DeriveRid(pairRoot);

        // The PC registers a host socket on rid_pair.
        await relay.WaitForHostAsync(ridPair, MultiRidFakeRelay.HostWait);

        // 2) Phone peers up on rid_pair with a connSalt, derives the claim key,
        //    and sends the sealed claim request (dir=2, client→host).
        var connSalt = new byte[RelayCrypto.ConnSaltLength];
        for (var i = 0; i < connSalt.Length; i++) connSalt[i] = (byte)(i + 0x30);
        var claimKey = RelayCrypto.DeriveAeadKey(pairRoot, connSalt);
        await relay.SendPeerUpAsync(ridPair, connSalt);

        var claimReq = new RelayClaimRequest { Type = RelayClaimMessageTypes.Claim, DeviceName = DeviceName };
        var reqBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            claimReq, Nexus.Service.Serialization.AppJsonContext.Default.RelayClaimRequest);
        var sealedReq = RelayCrypto.Seal(claimKey, RelayCrypto.DirClientToHost, counter: 0, reqBytes);
        await relay.ForwardToHostAsync(ridPair, sealedReq);

        // 3) The claim parks on the host's Allow: the PC replies claim-pending
        //    (counter 0) with the request id + SAS and mints nothing yet.
        var pendingFrame = await relay.WaitForHostFrameAsync(ridPair, TimeSpan.FromSeconds(5));
        var (dir, counter, pendingPlain) = RelayCrypto.Open(claimKey, pendingFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0ul, counter);
        var pending = System.Text.Json.JsonSerializer.Deserialize(
            pendingPlain, Nexus.Service.Serialization.AppJsonContext.Default.RelayClaimResponse);
        Assert.Equal(RelayClaimMessageTypes.ClaimPending, pending!.Type);
        Assert.False(string.IsNullOrEmpty(pending.RequestId));
        Assert.Equal(6, pending.Sas.Length);
        Assert.Empty(pairing.GetActiveRelaySessions());

        // The host clicks Allow; the phone's next claim-confirm poll collects the session.
        Assert.Equal("approved", pairing.HostDecisionPairCode(pending.RequestId, approved: true).Status);
        var confirmReq = new RelayClaimRequest { Type = RelayClaimMessageTypes.ClaimConfirm, RequestId = pending.RequestId };
        var confirmBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            confirmReq, Nexus.Service.Serialization.AppJsonContext.Default.RelayClaimRequest);
        await relay.ForwardToHostAsync(ridPair, RelayCrypto.Seal(claimKey, RelayCrypto.DirClientToHost, counter: 1, confirmBytes));

        var replyFrame = await relay.WaitForHostFrameAsync(ridPair, TimeSpan.FromSeconds(5));
        var (replyDir, replyCounter, replyPlain) = RelayCrypto.Open(claimKey, replyFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, replyDir);
        Assert.Equal(1ul, replyCounter);

        var reply = System.Text.Json.JsonSerializer.Deserialize(
            replyPlain, Nexus.Service.Serialization.AppJsonContext.Default.RelayClaimResponse);
        Assert.NotNull(reply);
        Assert.Equal(RelayClaimMessageTypes.ClaimOk, reply!.Type);
        Assert.False(string.IsNullOrEmpty(reply.SessionToken));
        Assert.False(string.IsNullOrEmpty(reply.MachineName)); // OS / override machine name

        // 4) The minted session token must be a usable session credential.
        Assert.True(pairing.ValidateSessionToken(reply.SessionToken),
            "claim-ok session token did not validate as an active session");

        // The stored session shows up as relayable and carries the phone's name.
        var active = pairing.GetActiveRelaySessions();
        Assert.Single(active);

        // 5) Single-use: claiming the same pair token again fails (it is consumed).
        var second = pairing.ClaimCore(
            pairToken, deviceName: "second", userAgent: "", remoteAddress: "",
            deviceId: "", overRelay: true, claimedOverHttps: false);
        Assert.False(second.Ok);
        Assert.Equal("pairing token expired", second.Error);

        // 6) The new session's RUNTIME rid now bridges into the hub. Derive its
        //    rid from the session token (exactly what the phone would do), wait
        //    for the PC's runtime host socket, peer up, and round-trip a `sub`.
        var sessionRoot = RelayCrypto.DeriveRelayRoot(reply.SessionToken);
        var sessionRid = RelayCrypto.DeriveRid(sessionRoot);
        Assert.NotEqual(ridPair, sessionRid);

        await relay.WaitForHostAsync(sessionRid, MultiRidFakeRelay.HostWait);

        var runtimeSalt = new byte[RelayCrypto.ConnSaltLength];
        for (var i = 0; i < runtimeSalt.Length; i++) runtimeSalt[i] = (byte)(i + 0x50);
        var runtimeKey = RelayCrypto.DeriveAeadKey(sessionRoot, runtimeSalt);
        await relay.SendPeerUpAsync(sessionRid, runtimeSalt);

        var subCommand = Encoding.UTF8.GetBytes($"{{\"sub\":[\"{Topic}\"]}}");
        var subFrame = RelayCrypto.Seal(runtimeKey, RelayCrypto.DirClientToHost, counter: 0, subCommand);
        await relay.ForwardToHostAsync(sessionRid, subFrame);

        await WaitUntilAsync(() => hub.TopicHasSubscribers(Topic), TimeSpan.FromSeconds(5),
            "relayed `sub` on the new session rid never reached the hub");

        await service.StopAsync(CancellationToken.None);
    }

    private static string ExtractPairToken(string url)
    {
        // url is .../panel/phone?pair=<token>&machineName=...  (PublicLinkHost="").
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.AsSpan(0, eq).SequenceEqual("pair"))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failMessage)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
        Assert.Fail(failMessage);
    }
}

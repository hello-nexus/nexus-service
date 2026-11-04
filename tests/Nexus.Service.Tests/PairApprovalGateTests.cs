using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// Every external new pairing parks on the desktop host's Allow before any
/// session is minted. One test per gate.
/// </summary>
public class PairApprovalGateTests
{
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148";

    private static PanelPhonePairingService NewService(MultiplexHub? hub = null) =>
        new(new InMemoryConfigStore(), hub ?? new MultiplexHub())
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
        };

    private static string Token(PanelPhonePairingService service)
    {
        var query = new Uri(service.CreatePairQr().Url).Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0] == "pair")
                return Uri.UnescapeDataString(pieces[1]);
        }
        throw new InvalidOperationException("no pair token");
    }

    private static DefaultHttpContext Ctx(string remote, bool https, string? bearer = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = UserAgent;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Scheme = https ? "https" : "http";
        if (bearer is not null)
            context.Request.Headers.Authorization = "Bearer " + bearer;
        return context;
    }

    [Fact]
    public void FlaglessClaim_IsRefusedWithUpdateRequired_NoMint_TokenStaysUsable()
    {
        var service = NewService();
        var token = Token(service);

        var refused = service.ClaimCore(token, "Phone", UserAgent, "192.168.1.50", "dev-old", overRelay: false, claimedOverHttps: true);

        Assert.False(refused.Ok);
        Assert.Equal("update_required", refused.Error);
        Assert.Equal("", refused.SessionToken);
        Assert.Equal(0, service.GetSessions(0).AuthorizedCount);

        // The token was not spent, so the same QR works from an updated app.
        var retry = service.ClaimCore(token, "Phone", UserAgent, "192.168.1.50", "dev-old", overRelay: false, claimedOverHttps: true, supportsSasApproval: true);
        Assert.True(retry.NeedsApproval);
    }

    [Fact]
    public void FlaglessClaim_ThroughHttpRoute_ReportsUpdateRequired()
    {
        var service = NewService();

        var response = service.Claim(Token(service), "dev-old", "Phone", Ctx("192.168.1.50", https: true));

        Assert.False(response.Paired);
        Assert.Equal("update_required", response.Error);
    }

    [Fact]
    public void HttpClaim_IsGated_AndCompletesOverHttpAfterAllow()
    {
        var service = NewService();
        var phone = Ctx("192.168.1.50", https: false);

        var claim = service.Claim(Token(service), "dev-http", "Phone", phone, supportsSasApproval: true);

        Assert.False(claim.Paired);
        Assert.True(claim.NeedsApproval);
        Assert.Equal(0, service.GetSessions(0).AuthorizedCount);

        Assert.Equal("waiting-host", service.ConfirmPairCode(claim.RequestId!, true, phone).Status);
        Assert.Equal("approved", service.HostDecisionPairCode(claim.RequestId!, true).Status);
        var done = service.ConfirmPairCode(claim.RequestId!, true, phone);
        Assert.Equal("approved", done.Status);
        Assert.False(string.IsNullOrEmpty(done.Token));
        Assert.Equal(1, service.GetSessions(0).AuthorizedCount);
    }

    [Fact]
    public void HttpsClaim_CannotBeConfirmedOverPlainHttp()
    {
        var service = NewService();
        var claim = service.Claim(Token(service), "dev-https", "Phone", Ctx("192.168.1.50", https: true), supportsSasApproval: true);
        service.HostDecisionPairCode(claim.RequestId!, true);

        var overHttp = service.ConfirmPairCode(claim.RequestId!, true, Ctx("192.168.1.50", https: false));

        Assert.Equal("https-required", overHttp.Status);
        Assert.Equal(0, service.GetSessions(0).AuthorizedCount);
    }

    [Fact]
    public void RelayClaim_IsGated_ParksOnHost_AndMintsOnlyAfterAllow()
    {
        var service = NewService();
        var token = Token(service);

        var claim = service.ClaimCore(token, "Phone", "", "", "dev-relay", overRelay: true, claimedOverHttps: false, supportsSasApproval: true);

        Assert.True(claim.Ok);
        Assert.True(claim.NeedsApproval);
        Assert.Equal("", claim.SessionToken);
        Assert.Equal(6, claim.Sas.Length);
        Assert.Equal(0, service.GetSessions(0).AuthorizedCount);
        // The pair rendezvous stays up so the phone can poll over it.
        Assert.Contains(service.GetOutstandingPairTokens(), t => t.Token == token);

        Assert.Equal("waiting-host", service.ConfirmRelayClaim(token, claim.RequestId, true).Status);
        // A different token, or the LAN confirm route, can never collect it.
        Assert.Equal("unknown", service.ConfirmRelayClaim("some-other-token", claim.RequestId, true).Status);
        Assert.Equal("unknown", service.ConfirmCore(claim.RequestId, true, isHttps: true, confirmRemote: "192.168.1.9", relayPairToken: null).Status);

        service.HostDecisionPairCode(claim.RequestId, true);
        var done = service.ConfirmRelayClaim(token, claim.RequestId, true);

        Assert.Equal("approved", done.Status);
        Assert.True(service.ValidateSessionToken(done.Token));
        Assert.DoesNotContain(service.GetOutstandingPairTokens(), t => t.Token == token);
    }

    [Fact]
    public void RelayClaim_Deny_IsReportedToPhone_AndReleasesToken()
    {
        var service = NewService();
        var token = Token(service);
        var claim = service.ClaimCore(token, "Phone", "", "", "dev-relay", overRelay: true, claimedOverHttps: false, supportsSasApproval: true);

        service.HostDecisionPairCode(claim.RequestId, false);
        var denied = service.ConfirmRelayClaim(token, claim.RequestId, true);

        Assert.Equal("denied", denied.Status);
        Assert.Equal(0, service.GetSessions(0).AuthorizedCount);
        Assert.DoesNotContain(service.GetOutstandingPairTokens(), t => t.Token == token);
    }

    [Fact]
    public void RelayClaim_WithoutFlag_IsRefused()
    {
        var service = NewService();

        var claim = service.ClaimCore(Token(service), "Phone", "", "", "dev-relay", overRelay: true, claimedOverHttps: false);

        Assert.False(claim.Ok);
        Assert.Equal("update_required", claim.Error);
    }

    [Fact]
    public void RePair_SkipsApprovalOnlyWithTheDevicesLiveSessionToken()
    {
        var service = NewService();
        var first = service.ClaimApproved(Token(service), "dev-A", "Phone", Ctx("192.168.1.50", https: true));
        Assert.True(first.Paired);

        // Same deviceId and same UA+IP fingerprint, but no token: approval again.
        var spoof = service.Claim(Token(service), "dev-A", "Phone", Ctx("192.168.1.50", https: true), supportsSasApproval: true);
        Assert.True(spoof.NeedsApproval);
        Assert.False(spoof.Paired);
        service.HostDecisionPairCode(spoof.RequestId!, false);
        service.ConfirmPairCode(spoof.RequestId!, true, Ctx("192.168.1.50", https: true));

        // A junk token is not evidence either.
        var junk = service.Claim(Token(service), "dev-A", "Phone", Ctx("192.168.1.50", https: true, bearer: "not-a-session"), supportsSasApproval: true);
        Assert.True(junk.NeedsApproval);
        service.HostDecisionPairCode(junk.RequestId!, false);
        service.ConfirmPairCode(junk.RequestId!, true, Ctx("192.168.1.50", https: true));

        // The real session token skips it, even from a client that predates the flag.
        var viaToken = service.Claim(Token(service), "dev-A", "Phone", Ctx("192.168.1.50", https: true, bearer: first.Token), supportsSasApproval: false);
        Assert.True(viaToken.Paired);
        Assert.False(string.IsNullOrEmpty(viaToken.Token));
    }

    [Fact]
    public void Eviction_HappensOnlyAtMint_AfterAllow()
    {
        var service = NewService();
        var existing = service.ClaimApproved(Token(service), "dev-A", "Phone", Ctx("192.168.1.50", https: true));
        Assert.True(service.ValidateSessionToken(existing.Token));

        // A claim reusing the victim's deviceId and fingerprint is only pending.
        var pending = service.Claim(Token(service), "dev-A", "Phone", Ctx("192.168.1.50", https: true), supportsSasApproval: true);
        Assert.True(pending.NeedsApproval);
        Assert.True(service.ValidateSessionToken(existing.Token));
        Assert.Equal(1, service.GetSessions(0).AuthorizedCount);

        // Denied: the victim's session survives.
        service.HostDecisionPairCode(pending.RequestId!, false);
        service.ConfirmPairCode(pending.RequestId!, true, Ctx("192.168.1.50", https: true));
        Assert.True(service.ValidateSessionToken(existing.Token));

        // Allowed: replacement happens at mint.
        var again = service.Claim(Token(service), "dev-A", "Phone", Ctx("192.168.1.50", https: true), supportsSasApproval: true);
        service.HostDecisionPairCode(again.RequestId!, true);
        var minted = service.ConfirmPairCode(again.RequestId!, true, Ctx("192.168.1.50", https: true));
        Assert.Equal("approved", minted.Status);
        Assert.False(service.ValidateSessionToken(existing.Token));
        Assert.True(service.ValidateSessionToken(minted.Token));
        Assert.Equal(1, service.GetSessions(0).AuthorizedCount);
    }

    [Fact]
    public void UsbPhonePort_IsNotLoopbackTrusted_ButQSeriesTunnelPortIs()
    {
        var (httpPort, httpsPort) = PhoneTunnelPorts.Derive(9400);
        Assert.Equal((9402, 9445), (httpPort, httpsPort));
        var ports = new PhoneTunnelPorts(httpPort, httpsPort);
        var tokens = new TokenService(new InMemoryConfigStore());

        HttpContext Loopback(int localPort)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Connection.LocalPort = localPort;
            context.Request.Host = new HostString("127.0.0.1", localPort);
            context.Request.Headers.Authorization = "Bearer " + tokens.Token;
            return context;
        }

        foreach (var phonePort in new[] { httpPort, httpsPort })
        {
            var phone = Loopback(phonePort);
            Assert.True(AuthRequestPolicy.IsLoopbackRemote(phone));
            ports.Reclassify(phone);
            Assert.False(AuthRequestPolicy.IsLoopbackRemote(phone));
            Assert.False(ServiceTokenRequests.HasServiceToken(phone, tokens));
            Assert.False(IPAddress.IsLoopback(phone.Connection.RemoteIpAddress!));
        }

        // Main service port (desktop) and the Q-series tunnel port stay trusted.
        foreach (var trustedPort in new[] { 9400, 9401 })
        {
            var desktop = Loopback(trustedPort);
            ports.Reclassify(desktop);
            Assert.True(AuthRequestPolicy.IsLoopbackRemote(desktop));
            Assert.True(ServiceTokenRequests.HasServiceToken(desktop, tokens));
        }
    }

    [Fact]
    public void Decision_BroadcastsCancelledFrameWithReason_ForEverySubscriber()
    {
        foreach (var (approve, reason) in new[] { (true, "approved"), (false, "host-denied") })
        {
            var hub = new MultiplexHub();
            var service = NewService(hub);
            using var sub = hub.AddTestSubscription(PanelTopics.PairCodeRequest);
            var frames = new List<string>();
            hub.OnBroadcastForTest += (topic, payload) =>
            {
                if (topic == PanelTopics.PairCodeRequest)
                    frames.Add(Encoding.UTF8.GetString(payload.Span));
            };
            var claim = service.Claim(Token(service), "dev-x", "Phone", Ctx("192.168.1.50", https: true), supportsSasApproval: true);

            service.HostDecisionPairCode(claim.RequestId!, approve);

            Assert.Contains(frames, f => f.Contains("\"kind\":\"request\""));
            Assert.Contains(frames, f => f.Contains("\"kind\":\"cancelled\"") && f.Contains($"\"reason\":\"{reason}\"") && f.Contains(claim.RequestId!));
        }
    }

    [Fact]
    public void ExpiredRequest_BroadcastsCancelledExpired_AndDropsSnapshot()
    {
        var hub = new MultiplexHub();
        var service = NewService(hub);
        using var sub = hub.AddTestSubscription(PanelTopics.PairCodeRequest);
        var frames = new List<string>();
        hub.OnBroadcastForTest += (_, payload) => frames.Add(Encoding.UTF8.GetString(payload.Span));
        var resolved = 0;
        service.PairRequestResolved += () => resolved++;
        var claim = service.Claim(Token(service), "dev-x", "Phone", Ctx("192.168.1.50", https: true), supportsSasApproval: true);
        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.PairCodeRequest, out _));

        service.ExpireIfStale(claim.RequestId!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120_000);

        Assert.Contains(frames, f => f.Contains("\"kind\":\"cancelled\"") && f.Contains("\"reason\":\"expired\""));
        Assert.Equal(1, resolved);
        Assert.False(hub.TryGetTopicSnapshot(PanelTopics.PairCodeRequest, out _));
    }

    [Fact]
    public async Task NewRequest_RaisesCreatedEvent_OffThread_AndAtMostOncePerMinute()
    {
        var service = NewService();
        var created = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var callerThread = Environment.CurrentManagedThreadId;
        var handlerThread = -1;
        service.PairRequestCreated += () =>
        {
            handlerThread = Environment.CurrentManagedThreadId;
            // A slow handler (Windows schtasks) must not block the claim.
            Thread.Sleep(300);
            created.TrySetResult(Interlocked.Increment(ref count));
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = service.Claim(Token(service), "dev-x", "Phone", Ctx("192.168.1.50", https: false), supportsSasApproval: true);
        Assert.True(sw.ElapsedMilliseconds < 250, "claim waited on the auto-open handler");
        Assert.Equal(1, await created.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(callerThread, handlerThread);

        // A second request (any source) inside the minute does not open the window again.
        service.HostDecisionPairCode(first.RequestId!, false);
        service.ConfirmPairCode(first.RequestId!, true, Ctx("192.168.1.50", https: false));
        var second = service.Claim(Token(service), "dev-y", "Phone", Ctx("192.168.1.51", https: false), supportsSasApproval: true);
        Assert.True(second.NeedsApproval);
        await Task.Delay(600);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void QrClaim_CannotReplacePendingOrApprovedUncollectedRequest()
    {
        var service = NewService();
        var first = service.Claim(Token(service), "dev-a", "Phone", Ctx("192.168.1.50", https: true), supportsSasApproval: true);
        Assert.True(first.NeedsApproval);

        var whilePending = service.Claim(Token(service), "dev-b", "Phone", Ctx("192.168.1.51", https: true), supportsSasApproval: true);
        Assert.Equal("code-in-use", whilePending.Error);

        service.HostDecisionPairCode(first.RequestId!, true);
        var whileApproved = service.Claim(Token(service), "dev-b", "Phone", Ctx("192.168.1.51", https: true), supportsSasApproval: true);
        Assert.Equal("code-in-use", whileApproved.Error);

        // Collected: free again.
        Assert.Equal("approved", service.ConfirmPairCode(first.RequestId!, true, Ctx("192.168.1.50", https: true)).Status);
        var afterCollect = service.Claim(Token(service), "dev-b", "Phone", Ctx("192.168.1.51", https: true), supportsSasApproval: true);
        Assert.True(afterCollect.NeedsApproval);
    }

    [Fact]
    public void RelayClaim_SecondClaimOnHeldToken_IsRefused_AndSupersedeDropsTheToken()
    {
        var service = NewService();
        var token = Token(service);
        var claim = service.ClaimCore(token, "Phone", "", "", "dev-r", overRelay: true, claimedOverHttps: false, supportsSasApproval: true);
        Assert.True(claim.NeedsApproval);

        var again = service.ClaimCore(token, "Phone", "", "", "dev-r", overRelay: true, claimedOverHttps: false, supportsSasApproval: true);
        Assert.False(again.Ok);
        Assert.Equal("claim-in-progress", again.Error);

        // The host starting a new code supersedes the request; its relay token dies with it.
        service.StartPairCode();
        Assert.DoesNotContain(service.GetOutstandingPairTokens(), t => t.Token == token);
        Assert.Equal("unknown", service.ConfirmRelayClaim(token, claim.RequestId, true).Status);
    }

    [Fact]
    public void RelayClaim_ExpiryDropsTheToken()
    {
        var service = NewService();
        var token = Token(service);
        var claim = service.ClaimCore(token, "Phone", "", "", "dev-r", overRelay: true, claimedOverHttps: false, supportsSasApproval: true);

        service.ExpireIfStale(claim.RequestId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120_000);

        Assert.DoesNotContain(service.GetOutstandingPairTokens(), t => t.Token == token);
    }

    [Fact]
    public void RelayFastPath_AcceptsOnlyHttpsOrRelayClaimedSessions()
    {
        var service = NewService();
        var https = service.ClaimApproved(Token(service), "dev-s", "Phone", Ctx("192.168.1.50", https: true));
        var http = service.ClaimApproved(Token(service), "dev-h", "Phone", Ctx("192.168.1.51", https: false));
        Assert.True(https.Paired);
        Assert.True(http.Paired);

        Assert.True(service.PresentsPortableSession(https.Token));
        // A sniffed plain-HTTP cookie is bound to IP+UA, which a relay request cannot present.
        Assert.False(service.PresentsPortableSession(http.Token));
        Assert.False(service.PresentsPortableSession("junk"));
        Assert.False(service.PresentsPortableSession(""));
    }

    [Fact]
    public void TunnelMarker_IsTheOnlyIdentity_ForTheStandInAddress()
    {
        var ports = new PhoneTunnelPorts(9402, 9445);

        // A LAN host borrowing the stand-in address is refused outright.
        var lan = new DefaultHttpContext();
        lan.Connection.RemoteIpAddress = PhoneTunnelPorts.PhoneRemote;
        lan.Connection.LocalPort = 9443;
        Assert.False(ports.Reclassify(lan));
        Assert.False(PhoneTunnelPorts.IsTunnelRequest(lan));

        // A real tunnel request is relabeled and marked.
        var tunnel = new DefaultHttpContext();
        tunnel.Connection.RemoteIpAddress = IPAddress.Loopback;
        tunnel.Connection.LocalPort = 9445;
        Assert.True(ports.Reclassify(tunnel));
        Assert.True(PhoneTunnelPorts.IsTunnelRequest(tunnel));

        // Ordinary LAN traffic is untouched and unmarked.
        var other = new DefaultHttpContext();
        other.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.9");
        other.Connection.LocalPort = 9443;
        Assert.True(ports.Reclassify(other));
        Assert.False(PhoneTunnelPorts.IsTunnelRequest(other));
    }

    [Fact]
    public void UnmarkedRequestWithStandInAddress_GetsNoConfirmMatch()
    {
        var service = NewService();
        var ports = new PhoneTunnelPorts(9402, 9445);
        var phone = Ctx("127.0.0.1", https: true);
        phone.Connection.LocalPort = 9445;
        ports.Reclassify(phone);
        var claim = service.Claim(Token(service), "dev-usb", "Phone", phone, supportsSasApproval: true);
        Assert.True(claim.NeedsApproval);

        // Same address, but never marked by the tunnel middleware: no identity.
        var spoof = Ctx(PhoneTunnelPorts.PhoneRemote.ToString(), https: true);
        Assert.Equal("unknown", service.ConfirmPairCode(claim.RequestId!, true, spoof).Status);
        Assert.Equal("waiting-host", service.ConfirmPairCode(claim.RequestId!, true, phone).Status);
    }
}

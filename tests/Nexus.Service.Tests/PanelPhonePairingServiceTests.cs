using System.Net;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Tests;

public class PanelPhonePairingServiceTests
{
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148";
    private const string AndroidUserAgent = "Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 Mobile Safari/537.36";
    private const string NativeIosUserAgent = "Nexus/1 CFNetwork/3860.500.112 Darwin/25.4.0";
    // iPadOS Safari sends a desktop Mac UA indistinguishable from a real Mac.
    private const string MacUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";

    [Fact]
    public void CreatePairQr_UsesSixtySecondTtl()
    {
        var service = NewService(new InMemoryConfigStore());

        var qr = service.CreatePairQr();

        Assert.Equal(60, qr.TtlSeconds);
    }

    [Fact]
    public void CreatePairQr_EmitsHttpPortForBrowserFallback()
    {
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Nexus.Service.Sockets.MultiplexHub())
        {
            ServicePort = 9400,
            HttpsPort = 9443,
            SpkiFingerprint = "fp-stub",
            PublicLinkHost = "hellonexus.com",
        };

        var qr = service.CreatePairQr();

        // HttpPort rides the response either way; the query string carrying it
        // is on hellonexus.com/r/pair, which an unofficial build never emits.
        Assert.Equal(9400, qr.HttpPort);
        if (Nexus.Service.Common.ClientCredential.IsOfficial)
        {
            Assert.Contains("httpPort=9400", qr.Url);
            Assert.Contains("port=9443", qr.Url);
        }
        else
        {
            Assert.StartsWith("https://", qr.Url);
            Assert.DoesNotContain("hellonexus.com", qr.Url);
        }
    }

    [Fact]
    public void CreatePairQr_HttpPortTracksServicePortNotHttpsPort()
    {
        // Catches an alias regression where httpPort is accidentally derived
        // from HttpsPort instead of the plain-HTTP service port.
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Nexus.Service.Sockets.MultiplexHub())
        {
            ServicePort = 9500,
            HttpsPort = 9443,
            SpkiFingerprint = "fp-stub",
            PublicLinkHost = "hellonexus.com",
        };

        var qr = service.CreatePairQr();

        Assert.Equal(9500, qr.HttpPort);
        if (Nexus.Service.Common.ClientCredential.IsOfficial)
        {
            Assert.Contains("httpPort=9500", qr.Url);
        }
        Assert.DoesNotContain("httpPort=9443", qr.Url);
    }

    [Fact]
    public void CreatePairQr_EmbedsMachineNameForNativeAppLabel()
    {
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Nexus.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "hellonexus.com",
        };
        service.SetHostDisplayName("Desk PC");

        var qr = service.CreatePairQr();

        Assert.Equal("Desk PC", qr.MachineName);
        Assert.Contains("machineName=Desk%20PC", qr.Url);
    }

    [Fact]
    public void Claim_ReturnsMachineNameForLegacyPairLinks()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SetHostDisplayName("Studio Workstation");

        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));

        Assert.True(result.Paired);
        Assert.Equal("Studio Workstation", result.MachineName);
    }

    [Fact]
    public void Claim_SuccessCarriesSpkiFingerprint()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SpkiFingerprint = "fp-stub";

        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));

        Assert.True(result.Paired);
        Assert.Equal("fp-stub", result.SpkiFingerprint);
    }

    [Fact]
    public void Claim_SuccessOmitsSpkiFingerprintWithoutCert()
    {
        var service = NewService(new InMemoryConfigStore());

        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));

        Assert.True(result.Paired);
        Assert.Null(result.SpkiFingerprint);
    }

    [Fact]
    public void GetServiceInfo_ReturnsNormalizedMachineName()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SetHostDisplayName("  Studio   Mac  ");

        var result = service.GetServiceInfo();

        Assert.Equal("Studio Mac", result.MachineName);
    }

    [Fact]
    public void Claim_ReplacesExistingSessionForSameDeviceFingerprint()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));

        var sessions = service.GetSessions(0);

        Assert.True(first.Paired);
        Assert.True(second.Paired);
        Assert.Equal(1, sessions.AuthorizedCount);
        Assert.Single(sessions.Sessions);
        Assert.True(service.ValidateSessionToken(second.Token));
        Assert.False(service.ValidateSessionToken(first.Token));
    }

    [Fact]
    public void ClaimOverRelay_SameDeviceId_ReplacesPriorSession()
    {
        // Relay path: no client IP/UA ⇒ empty fingerprint ⇒ fingerprint dedup
        // is skipped. The stable deviceId is what collapses re-pairings of the
        // same phone into a single session.
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = ClaimOverRelay(service, "Nicola's iPhone", "device-uuid-A");
        var second = ClaimOverRelay(service, "Nicola's iPhone", "device-uuid-A");

        var sessions = service.GetSessions(0);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(1, sessions.AuthorizedCount);
        Assert.Single(sessions.Sessions);
        Assert.True(service.ValidateSessionToken(second.SessionToken));
        Assert.False(service.ValidateSessionToken(first.SessionToken));
    }

    [Fact]
    public void ClaimOverRelay_DifferentDeviceIds_KeepBothSessions()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = ClaimOverRelay(service, "iPhone A", "device-uuid-A");
        var second = ClaimOverRelay(service, "iPhone B", "device-uuid-B");

        var sessions = service.GetSessions(0);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(2, sessions.AuthorizedCount);
        Assert.True(service.ValidateSessionToken(first.SessionToken));
        Assert.True(service.ValidateSessionToken(second.SessionToken));
    }

    [Fact]
    public void ClaimOverRelay_EmptyDeviceId_DoesNotDedup()
    {
        // Empty deviceId + relay (empty fingerprint) ⇒ no dedup key at all, so
        // each claim is a distinct session - the pre-deviceId behavior. This is
        // the duplicate-accumulation the deviceId is designed to fix, asserted
        // here as the explicit "no regression / no false dedup" baseline.
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = ClaimOverRelay(service, "iPhone", deviceId: "");
        var second = ClaimOverRelay(service, "iPhone", deviceId: "");

        var sessions = service.GetSessions(0);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(2, sessions.AuthorizedCount);
    }

    [Fact]
    public void Claim_HttpWithDeviceId_OverridesIpFingerprintDedup()
    {
        // Same deviceId from two different LAN IPs (DHCP renewal / Wi-Fi roam):
        // the deviceId collapses them even though the fingerprints differ.
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), "device-uuid-A", NewContext(UserAgent, "192.168.1.50"));
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), "device-uuid-A", NewContext(UserAgent, "192.168.1.51"));

        var sessions = service.GetSessions(0);

        Assert.True(first.Paired);
        Assert.True(second.Paired);
        Assert.Equal(1, sessions.AuthorizedCount);
        Assert.False(service.ValidateSessionToken(first.Token));
        Assert.True(service.ValidateSessionToken(second.Token));
    }

    [Fact]
    public void Claim_HttpDeviceIdFromQuery_DedupsWhenBodyEmpty()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var firstCtx = NewContext(UserAgent, "192.168.1.50");
        firstCtx.Request.QueryString = new QueryString("?deviceId=device-uuid-Q");
        var secondCtx = NewContext(UserAgent, "192.168.1.51");
        secondCtx.Request.QueryString = new QueryString("?deviceId=device-uuid-Q");

        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), deviceId: "", firstCtx);
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), deviceId: "", secondCtx);

        var sessions = service.GetSessions(0);

        Assert.True(first.Paired);
        Assert.True(second.Paired);
        Assert.Equal(1, sessions.AuthorizedCount);
    }

    [Fact]
    public void ClaimOverRelay_AbsurdlyLongDeviceId_TreatedAsNoDedupId()
    {
        // An over-length deviceId is rejected (treated as empty) rather than
        // truncated - truncation could collide with a different device.
        var store = new InMemoryConfigStore();
        var service = NewService(store);
        var huge = new string('x', 5000);

        var first = ClaimOverRelay(service, "iPhone", huge);
        var second = ClaimOverRelay(service, "iPhone", huge);

        var sessions = service.GetSessions(0);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(2, sessions.AuthorizedCount);
        Assert.All(store.Load().Auth!.PanelPhoneSessions, s => Assert.Equal("", s.DeviceId));
    }

    private static PanelPhonePairingService.ClaimResult ClaimOverRelay(
        PanelPhonePairingService service, string deviceName, string deviceId)
    {
        return service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: deviceName,
            userAgent: "",
            remoteAddress: "",
            deviceId: deviceId,
            overRelay: true,
            claimedOverHttps: false);
    }

    [Fact]
    public void GetSessions_ReportsDeviceTypeFromUserAgent()
    {
        var service = NewService(new InMemoryConfigStore());
        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(AndroidUserAgent, "192.168.1.52"));

        var session = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(result.Paired);
        Assert.Equal("Android phone", session.Name);
        Assert.Equal("Android phone", session.DeviceType);
    }

    [Fact]
    public void GetSessions_ReportsIphoneForNativeIosUserAgent()
    {
        var service = NewService(new InMemoryConfigStore());
        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));

        var session = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(result.Paired);
        Assert.Equal("iPhone", session.Name);
        Assert.Equal("iPhone", session.DeviceType);
    }

    [Fact]
    public void RenameSession_UpdatesAuthorizedDeviceName()
    {
        var service = NewService(new InMemoryConfigStore());
        service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));
        var session = Assert.Single(service.GetSessions(0).Sessions);

        var renamed = service.RenameSession(session.Id, "Desk iPhone");
        var renamedSession = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(renamed);
        Assert.Equal("Desk iPhone", renamedSession.Name);
        Assert.Equal("iPhone", renamedSession.DeviceType);
    }

    [Fact]
    public void GetSessions_ClientDeviceName_OverridesUserAgentGuess()
    {
        // An iPad reports a Mac UA; the client passes the detected "iPad" label so
        // both the name and the device type read "iPad", not "Mac".
        var service = NewService(new InMemoryConfigStore());
        var result = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            deviceId: "",
            deviceName: "iPad",
            NewContext(MacUserAgent, "192.168.1.54"));

        var session = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(result.Paired);
        Assert.Equal("iPad", session.Name);
        Assert.Equal("iPad", session.DeviceType);
    }

    [Fact]
    public void RenameSession_PreservesClientDeviceType()
    {
        var service = NewService(new InMemoryConfigStore());
        service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            deviceId: "",
            deviceName: "iPad",
            NewContext(MacUserAgent, "192.168.1.54"));
        var session = Assert.Single(service.GetSessions(0).Sessions);

        var renamed = service.RenameSession(session.Id, "Studio iPad");
        var renamedSession = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(renamed);
        Assert.Equal("Studio iPad", renamedSession.Name);
        Assert.Equal("iPad", renamedSession.DeviceType);
    }

    [Fact]
    public void Claim_KeepsDifferentRemoteAddressesSeparate()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.51"));

        var sessions = service.GetSessions(0);

        Assert.True(first.Paired);
        Assert.True(second.Paired);
        Assert.Equal(2, sessions.AuthorizedCount);
        Assert.True(service.ValidateSessionToken(first.Token));
        Assert.True(service.ValidateSessionToken(second.Token));
    }

    [Fact]
    public void GetSessions_NormalizesExistingDuplicateFingerprints()
    {
        var store = new InMemoryConfigStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Update(s =>
        {
            s.Auth = new AuthSettings
            {
                PanelPhoneSessions =
                {
                    Session("old-duplicate", UserAgent, "192.168.1.50", now - 20_000),
                    Session("latest-duplicate", UserAgent, "192.168.1.50", now - 5_000),
                    Session("other-device", UserAgent, "192.168.1.51", now - 10_000),
                },
            };
        });
        var service = NewService(store);

        var sessions = service.GetSessions(0);
        var persisted = store.Load().Auth!.PanelPhoneSessions;

        Assert.Equal(2, sessions.AuthorizedCount);
        Assert.Contains(sessions.Sessions, session => session.Id == "latest-duplicate");
        Assert.DoesNotContain(sessions.Sessions, session => session.Id == "old-duplicate");
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, session => Assert.False(string.IsNullOrWhiteSpace(session.DeviceFingerprint)));
    }

    [Fact]
    public async Task RevokeAllSessions_RemovesEveryAuthorizedDevice()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);
        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.51"));

        var removed = await service.RevokeAllSessionsAsync();

        Assert.Equal(2, removed);
        Assert.Empty(service.GetSessions(0).Sessions);
        Assert.False(service.ValidateSessionToken(first.Token));
        Assert.False(service.ValidateSessionToken(second.Token));
    }

    [Fact]
    public async Task SetRemoteControlEnabled_FalseRefusesNewClaims()
    {
        var service = NewService(new InMemoryConfigStore());

        Assert.True(service.GetRemoteControlEnabled());
        await service.SetRemoteControlEnabledAsync(false);
        Assert.False(service.GetRemoteControlEnabled());

        var result = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(UserAgent, "192.168.1.60"));

        Assert.False(result.Paired);
        Assert.Equal("remote-disabled", result.Error);

        await service.SetRemoteControlEnabledAsync(true);
        var allowed = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(UserAgent, "192.168.1.60"));
        Assert.True(allowed.Paired);
    }

    [Fact]
    public void TryValidateSessionToken_ReturnsMatchedSessionId()
    {
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(NativeIosUserAgent, "192.168.1.53"));

        Assert.True(service.TryValidateSessionToken(claim.Token, context: null, out var sessionId));
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var listed = Assert.Single(service.GetSessions(0).Sessions);
        Assert.Equal(listed.Id, sessionId);
    }

    [Fact]
    public void GetSessions_SerializesAuthorizedDevicesWithAppJsonContext()
    {
        var service = NewService(new InMemoryConfigStore());
        service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));

        var json = JsonSerializer.Serialize(
            service.GetSessions(1),
            AppJsonContext.Default.PanelPhoneSessionsResponse);

        Assert.Contains("\"connectedCount\":1", json);
        Assert.Contains("\"authorizedCount\":1", json);
        Assert.Contains("\"sessions\":[{", json);
        Assert.Contains("\"name\":\"iPhone\"", json);
        Assert.Contains("\"deviceType\":\"iPhone\"", json);
    }

    [Fact]
    public void Validate_HttpClaimedSession_RejectsRequestFromDifferentIp()
    {
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(UserAgent, "192.168.1.50", isHttps: false));
        Assert.True(claim.Paired);

        // Same device: passes
        Assert.True(service.ValidateSessionToken(claim.Token, NewContext(UserAgent, "192.168.1.50", isHttps: false)));

        // Different IP: rejected
        Assert.False(service.ValidateSessionToken(claim.Token, NewContext(UserAgent, "192.168.1.99", isHttps: false)));

        // Different UA: rejected
        Assert.False(service.ValidateSessionToken(claim.Token, NewContext(AndroidUserAgent, "192.168.1.50", isHttps: false)));
    }

    [Fact]
    public void Validate_HttpsClaimedSession_AcceptsRequestFromAnyIp()
    {
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(NativeIosUserAgent, "192.168.1.50", isHttps: true));
        Assert.True(claim.Paired);

        // Different IP from a different LAN run (DHCP renewal, Wi-Fi roam) -
        // legacy 30-day idle behavior must keep working for the native app.
        Assert.True(service.ValidateSessionToken(claim.Token, NewContext(NativeIosUserAgent, "192.168.1.99", isHttps: true)));
        Assert.True(service.ValidateSessionToken(claim.Token));
    }

    [Fact]
    public void Validate_NullContext_StillSucceedsForHttpsClaim()
    {
        // Internal callers (presence WS, contract tests) sometimes validate
        // without an HttpContext. Don't break them - the bind check only
        // runs when context is provided AND the session was HTTP-claimed.
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(NativeIosUserAgent, "192.168.1.50", isHttps: true));

        Assert.True(service.ValidateSessionToken(claim.Token, context: null));
    }

    // -- Manual pair-code (BT-SSP Numeric Comparison) tests --------------

    [Fact]
    public void StartPairCode_ReturnsSixDigitCodeAndHostPort()
    {
        var service = NewService(new InMemoryConfigStore());
        service.ServicePort = 9400;

        var resp = service.StartPairCode();

        Assert.Equal(6, resp.Code.Length);
        Assert.All(resp.Code, c => Assert.InRange(c, '0', '9'));
        Assert.Equal(PanelPhonePairingService.PairCodeTtlSeconds, resp.TtlSeconds);
        Assert.True(resp.Port > 0);
        Assert.True(resp.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void SubmitPairCode_OverPlainHttp_Rejected()
    {
        var service = NewService(new InMemoryConfigStore());
        service.StartPairCode();
        var r = service.SubmitPairCode("123456", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: false));
        Assert.Equal("https-required", r.Error);
    }

    [Fact]
    public void ConfirmPairCode_OverPlainHttp_Rejected()
    {
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        var r = service.ConfirmPairCode(submit.RequestId, true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: false));
        Assert.Equal("https-required", r.Status);
    }

    [Fact]
    public async Task StartPairCode_RemoteDisabled_ReturnsEmpty()
    {
        var service = NewService(new InMemoryConfigStore());
        await service.SetRemoteControlEnabledAsync(false);
        var resp = service.StartPairCode();
        Assert.Equal("", resp.Code);
        Assert.Equal(0, resp.TtlSeconds);
    }

    [Fact]
    public void SubmitPairCode_WrongCode_BurnsAttemptAndLocksOutAtFive()
    {
        var service = NewService(new InMemoryConfigStore());
        service.StartPairCode();

        for (var i = 0; i < 5; i++)
        {
            var r = service.SubmitPairCode("000000", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
            Assert.Equal("invalid-code", r.Error);
        }

        var locked = service.SubmitPairCode("000000", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("rate-limited", locked.Error);
        Assert.True(locked.RetryAfterSeconds > 0);
    }

    [Fact]
    public void SubmitPairCode_RightCode_ReturnsDeterministicSasAndRequestId()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SpkiFingerprint = "fp-stub";
        var start = service.StartPairCode();

        var r = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        Assert.True(r.Accepted);
        Assert.Equal(6, r.Sas.Length);
        Assert.All(r.Sas, c => Assert.InRange(c, '0', '9'));
        Assert.False(string.IsNullOrEmpty(r.RequestId));
        Assert.Equal("fp-stub", r.SpkiFingerprint);
    }

    [Fact]
    public void SubmitPairCode_RightCode_AfterPriorFailures_ClearsLockoutCounter()
    {
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();

        for (var i = 0; i < 4; i++)
            service.SubmitPairCode("000000", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        var ok = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(ok.Accepted);

        // After success the counter is cleared; a fresh /start lets the
        // same IP retry without inheriting the prior session's strikes.
        var start2 = service.StartPairCode();
        for (var i = 0; i < 4; i++)
        {
            var r = service.SubmitPairCode("999999", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
            Assert.Equal("invalid-code", r.Error);
        }
        var ok2 = service.SubmitPairCode(start2.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(ok2.Accepted);
    }

    [Fact]
    public void SubmitPairCode_NoActiveCode_Rejected()
    {
        var service = NewService(new InMemoryConfigStore());
        var r = service.SubmitPairCode("123456", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("no-active-code", r.Error);
    }

    [Fact]
    public void SubmitPairCode_SecondSubmit_WhileFirstInFlight_Rejected()
    {
        // Prevents the race where a second submitter (or attacker who got
        // the same plaintext code from somewhere) overwrites the in-flight
        // RequestId / PhoneRemoteAddress and hijacks the dual-confirm.
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var first = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(first.Accepted);

        var second = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.99", isHttps: true));
        Assert.Equal("code-in-use", second.Error);
    }

    [Fact]
    public void ComputeSas_DependsOnSpki()
    {
        // The MITM defense: same code + same nonce + different SPKI must
        // produce a different SAS. If this ever returns equal, the SPKI
        // binding regressed and the SAS comparison stops being a defense.
        var nonce = new byte[16];
        for (var i = 0; i < nonce.Length; i++) nonce[i] = (byte)i;

        var sasReal = PanelPhonePairingService.ComputeSas("123456", nonce, "real-spki");
        var sasMitm = PanelPhonePairingService.ComputeSas("123456", nonce, "attacker-spki");
        Assert.NotEqual(sasReal, sasMitm);

        // Deterministic for the same triple - the dashboard and the phone
        // both compute it from the same inputs and must agree.
        var sasRealAgain = PanelPhonePairingService.ComputeSas("123456", nonce, "real-spki");
        Assert.Equal(sasReal, sasRealAgain);

        // Shape: 6 digits.
        Assert.Equal(6, sasReal.Length);
        Assert.All(sasReal, c => Assert.InRange(c, '0', '9'));
    }

    [Fact]
    public void ConfirmPairCode_FromAnotherAddress_IsUnknown()
    {
        // The request id also rides the dashboard's WebSocket frame; only the
        // device that submitted the code may poll, cancel, or collect the token.
        var store = new InMemoryConfigStore();
        var service = NewService(store);
        service.SpkiFingerprint = "fp-stub";
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(submit.Accepted);
        Assert.Equal("approved", service.HostDecisionPairCode(submit.RequestId, approved: true).Status);

        var stranger = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.99", isHttps: true));
        Assert.Equal("unknown", stranger.Status);
        Assert.True(string.IsNullOrEmpty(stranger.Token));

        var strangerDeny = service.ConfirmPairCode(submit.RequestId, approved: false, NewContext(NativeIosUserAgent, "192.168.1.99", isHttps: true));
        Assert.Equal("unknown", strangerDeny.Status);

        // The real phone still collects its token afterwards.
        var phone = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("approved", phone.Status);
        Assert.False(string.IsNullOrEmpty(phone.Token));
    }

    [Fact]
    public void HostApprove_ThenPhonePoll_IssuesToken()
    {
        // The phone's polling /confirm IS the wait. There's no separate
        // "phone approves" gesture - the user-visible model is "type code,
        // compare SAS, click Allow on the system, phone connects."
        var store = new InMemoryConfigStore();
        var service = NewService(store);
        service.SpkiFingerprint = "fp-stub";
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(submit.Accepted);

        // Phone polls before host approves -> waiting-host, no token yet.
        var beforeApprove = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("waiting-host", beforeApprove.Status);
        Assert.Equal("", beforeApprove.Token);

        var host = service.HostDecisionPairCode(submit.RequestId, approved: true);
        Assert.Equal("approved", host.Status);

        // Next poll picks up the host approval and gets the token.
        var afterApprove = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("approved", afterApprove.Status);
        Assert.False(string.IsNullOrEmpty(afterApprove.Token));
        Assert.Equal("fp-stub", afterApprove.SpkiFingerprint);

        Assert.True(service.ValidateSessionToken(afterApprove.Token, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true)));
    }

    [Fact]
    public void HostDeny_PhoneSeesDeniedOnNextPoll()
    {
        // On host-deny, state is kept (HostDenied=true) so the phone's next
        // poll reads "denied"; nulling it would return "unknown", which is
        // indistinguishable from a stale requestId.
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        var host = service.HostDecisionPairCode(submit.RequestId, approved: false);
        Assert.Equal("denied", host.Status);

        var confirm = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("denied", confirm.Status);
    }

    [Fact]
    public void PhoneDeny_Cancels_HostSeesUnknownAfter()
    {
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        var phoneDeny = service.ConfirmPairCode(submit.RequestId, approved: false, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("denied", phoneDeny.Status);

        var host = service.HostDecisionPairCode(submit.RequestId, approved: true);
        Assert.Equal("unknown", host.Status);
    }

    [Fact]
    public void Start_AfterInflightSubmit_SupersedesPriorRequest()
    {
        var service = NewService(new InMemoryConfigStore());
        var start1 = service.StartPairCode();
        var submit1 = service.SubmitPairCode(start1.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        service.StartPairCode();
        var followUp = service.ConfirmPairCode(submit1.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("unknown", followUp.Status);
    }

    [Fact]
    public void PendingRequest_RetainedAsSnapshot_OnlyWhileAwaitingHostDecision()
    {
        // A dashboard opened from the tray pairing notification connects
        // AFTER the one-shot live broadcast. The snapshot provider is what
        // lets that late subscriber still receive the pending request and pop
        // the Allow/Deny modal - but only while it's still pending.
        var hub = new Nexus.Service.Sockets.MultiplexHub();
        var service = NewServiceWithHub(hub);
        var start = service.StartPairCode();

        // Code started, but no phone has submitted yet -> nothing to replay.
        Assert.False(hub.TryGetTopicSnapshot(Nexus.Service.Sockets.PanelTopics.PairCodeRequest, out _));

        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(submit.Accepted);

        // Pending host decision -> retained.
        Assert.True(hub.TryGetTopicSnapshot(Nexus.Service.Sockets.PanelTopics.PairCodeRequest, out _));

        // Host allows -> no longer pending -> cleared, so a reconnecting
        // dashboard never resurrects a decided prompt.
        service.HostDecisionPairCode(submit.RequestId, approved: true);
        Assert.False(hub.TryGetTopicSnapshot(Nexus.Service.Sockets.PanelTopics.PairCodeRequest, out _));
    }

    [Fact]
    public void PendingRequest_SnapshotCleared_OnHostDeny()
    {
        var hub = new Nexus.Service.Sockets.MultiplexHub();
        var service = NewServiceWithHub(hub);
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(hub.TryGetTopicSnapshot(Nexus.Service.Sockets.PanelTopics.PairCodeRequest, out _));

        service.HostDecisionPairCode(submit.RequestId, approved: false);
        Assert.False(hub.TryGetTopicSnapshot(Nexus.Service.Sockets.PanelTopics.PairCodeRequest, out _));
    }

    [Fact]
    public void PairRequest_RaisesAttention_WhenNoDashboardSubscribed_AndResolvesOnDecision()
    {
        var hub = new Nexus.Service.Sockets.MultiplexHub();
        var service = NewServiceWithHub(hub);
        var attentions = 0;
        var resolves = 0;
        service.PairRequestNeedsAttention += _ => attentions++;
        service.PairRequestResolved += () => resolves++;

        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        // No dashboard subscribed -> the desktop must be told to surface a
        // notification.
        Assert.Equal(1, attentions);
        Assert.Equal(0, resolves);

        // Any terminal transition dismisses it.
        service.HostDecisionPairCode(submit.RequestId, approved: false);
        Assert.Equal(1, resolves);
    }

    [Fact]
    public void PairRequest_DoesNotRaiseAttention_WhenDashboardSubscribed()
    {
        // A connected dashboard renders the modal itself; a notification
        // would be redundant.
        var hub = new Nexus.Service.Sockets.MultiplexHub();
        using var sub = hub.AddTestSubscription(Nexus.Service.Sockets.PanelTopics.PairCodeRequest);
        var service = NewServiceWithHub(hub);
        var attentions = 0;
        service.PairRequestNeedsAttention += _ => attentions++;

        var start = service.StartPairCode();
        service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        Assert.Equal(0, attentions);
    }

    // -- QR SAS approval gate tests --------------------------------------

    [Fact]
    public void ClaimCore_NewDevice_HttpsSupportsSas_ReturnsSasGate()
    {
        var hub = new Nexus.Service.Sockets.MultiplexHub();
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), hub)
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
        };
        using var sub = hub.AddTestSubscription(Nexus.Service.Sockets.PanelTopics.PairCodeRequest);

        var result = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: NativeIosUserAgent,
            remoteAddress: "192.168.1.50",
            deviceId: "device-uuid-new",
            overRelay: false,
            claimedOverHttps: true,
            supportsSasApproval: true);

        Assert.True(result.Ok);
        Assert.True(result.NeedsApproval);
        Assert.Equal("", result.SessionToken);
        Assert.False(string.IsNullOrEmpty(result.RequestId));
        Assert.Equal(6, result.Sas.Length);
        Assert.All(result.Sas, c => Assert.InRange(c, '0', '9'));
        Assert.Equal("fp-stub", result.Spki);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(0, service.GetSessions(0).AuthorizedCount);
        Assert.True(hub.TryGetTopicSnapshot(Nexus.Service.Sockets.PanelTopics.PairCodeRequest, out _));
    }

    [Fact]
    public void ClaimCore_AlreadyPairedDevice_HttpsSupportsSas_DirectToken()
    {
        var store = new InMemoryConfigStore();
        var service = new PanelPhonePairingService(store, new Nexus.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
        };

        // Seed a live session for device-uuid-A via a relay claim (no fingerprint).
        var first = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: NativeIosUserAgent,
            remoteAddress: "",
            deviceId: "device-uuid-A",
            overRelay: true,
            claimedOverHttps: false);
        Assert.True(first.Ok);
        Assert.False(first.NeedsApproval);

        // Same deviceId over HTTPS with SAS opt-in: already paired -> direct mint.
        var second = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: NativeIosUserAgent,
            remoteAddress: "192.168.1.50",
            deviceId: "device-uuid-A",
            overRelay: false,
            claimedOverHttps: true,
            supportsSasApproval: true);

        Assert.True(second.Ok);
        Assert.False(second.NeedsApproval);
        Assert.False(string.IsNullOrEmpty(second.SessionToken));
    }

    [Fact]
    public void ClaimCore_SupportsSasFalse_OldClient_DirectToken()
    {
        var store = new InMemoryConfigStore();
        var service = new PanelPhonePairingService(store, new Nexus.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
        };

        var result = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: NativeIosUserAgent,
            remoteAddress: "192.168.1.50",
            deviceId: "device-uuid-legacy",
            overRelay: false,
            claimedOverHttps: true,
            supportsSasApproval: false);

        Assert.True(result.Ok);
        Assert.False(result.NeedsApproval);
        Assert.False(string.IsNullOrEmpty(result.SessionToken));
    }

    [Fact]
    public void ClaimCore_OverRelay_SupportsSasTrue_DirectToken()
    {
        var store = new InMemoryConfigStore();
        var service = new PanelPhonePairingService(store, new Nexus.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
        };

        var result = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: "",
            remoteAddress: "",
            deviceId: "device-uuid-relay",
            overRelay: true,
            claimedOverHttps: false,
            supportsSasApproval: true);

        Assert.True(result.Ok);
        Assert.False(result.NeedsApproval);
        Assert.False(string.IsNullOrEmpty(result.SessionToken));
    }

    [Fact]
    public void QrSasApproval_MintsSingleSessionWithDeviceId_AndSkipsApprovalOnReScan()
    {
        var store = new InMemoryConfigStore();
        var service = new PanelPhonePairingService(store, new Nexus.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
        };

        // First claim: new device over HTTPS with SAS opt-in.
        var claim = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: NativeIosUserAgent,
            remoteAddress: "192.168.1.50",
            deviceId: "device-uuid-X",
            overRelay: false,
            claimedOverHttps: true,
            supportsSasApproval: true);

        Assert.True(claim.Ok);
        Assert.True(claim.NeedsApproval);
        Assert.Equal("", claim.SessionToken);

        // Host approves.
        var host = service.HostDecisionPairCode(claim.RequestId, approved: true);
        Assert.Equal("approved", host.Status);

        // Phone confirms -> token minted.
        var confirm = service.ConfirmPairCode(claim.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.50", isHttps: true));
        Assert.Equal("approved", confirm.Status);
        Assert.False(string.IsNullOrEmpty(confirm.Token));

        // Exactly one session, and it carries DeviceId.
        var sessions = service.GetSessions(0);
        Assert.Equal(1, sessions.AuthorizedCount);
        var session = Assert.Single(sessions.Sessions);
        var persisted = store.Load().Auth!.PanelPhoneSessions;
        var persistedSession = Assert.Single(persisted);
        Assert.Equal("device-uuid-X", persistedSession.DeviceId);

        // Second claim with the same deviceId: already paired -> fast-path, no approval modal.
        var reScan = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: NativeIosUserAgent,
            remoteAddress: "192.168.1.50",
            deviceId: "device-uuid-X",
            overRelay: false,
            claimedOverHttps: true,
            supportsSasApproval: true);

        Assert.True(reScan.Ok);
        Assert.False(reScan.NeedsApproval);
        Assert.False(string.IsNullOrEmpty(reScan.SessionToken));

        _ = session; // confirm the local binding is used
    }

    [Fact]
    public void QrSasApproval_SupersedesInFlightWifiRequest()
    {
        var hub = new Nexus.Service.Sockets.MultiplexHub();
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), hub)
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
            ServicePort = 9400,
        };

        // Start a manual Wi-Fi pair-code (host-initiated).
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(submit.Accepted);
        var wifiRequestId = submit.RequestId;

        // A QR-SAS claim from a new device arrives while the Wi-Fi request is in flight.
        var sasResult = service.ClaimCore(
            PairTokenFrom(service.CreatePairQr()),
            deviceName: "iPhone",
            userAgent: NativeIosUserAgent,
            remoteAddress: "192.168.1.50",
            deviceId: "device-uuid-Y",
            overRelay: false,
            claimedOverHttps: true,
            supportsSasApproval: true);

        Assert.True(sasResult.Ok);
        Assert.True(sasResult.NeedsApproval);
        Assert.NotEqual(wifiRequestId, sasResult.RequestId);

        // The prior Wi-Fi request is no longer the live _pairCode.
        var priorCheck = service.ConfirmPairCode(wifiRequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("unknown", priorCheck.Status);

        // The new QR-SAS request is live: host approve + phone confirm work.
        service.HostDecisionPairCode(sasResult.RequestId, approved: true);
        var confirm = service.ConfirmPairCode(sasResult.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.50", isHttps: true));
        Assert.Equal("approved", confirm.Status);
        Assert.False(string.IsNullOrEmpty(confirm.Token));
    }

    private static PanelPhonePairingService NewServiceWithHub(Nexus.Service.Sockets.MultiplexHub hub)
    {
        return new PanelPhonePairingService(new InMemoryConfigStore(), hub)
        {
            PublicLinkHost = "",
            SpkiFingerprint = "fp-stub",
        };
    }

    private static PanelPhonePairingService NewService(InMemoryConfigStore store)
    {
        return new PanelPhonePairingService(store, new Nexus.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "",
        };
    }

    private static DefaultHttpContext NewContext(string userAgent, string remoteAddress, bool isHttps = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = userAgent;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Scheme = isHttps ? "https" : "http";
        return context;
    }

    private static PanelPhoneSessionToken Session(string id, string userAgent, string remoteAddress, long lastSeenAt)
    {
        return new PanelPhoneSessionToken
        {
            Id = id,
            Hash = id,
            Name = "iPhone",
            UserAgent = userAgent,
            RemoteAddress = remoteAddress,
            CreatedAt = lastSeenAt - 1_000,
            LastSeenAt = lastSeenAt,
        };
    }

    private static string PairTokenFrom(PanelPhonePairQrResponse qr)
    {
        var query = new Uri(qr.Url).Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0] == "pair")
                return Uri.UnescapeDataString(pieces[1]);
        }

        throw new InvalidOperationException("Pair QR did not contain a pair token.");
    }
}

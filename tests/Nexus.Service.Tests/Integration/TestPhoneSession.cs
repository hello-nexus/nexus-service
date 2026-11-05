using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

internal static class TestPhoneSession
{
    /// <summary>Mint a real paired-phone session and return a client bearing it.</summary>
    public static HttpClient CreateClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var pairing = factory.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.CreatePairQr();
        var pairToken = pairing.GetOutstandingPairTokens()[0].Token;
        var claim = pairing.ClaimCoreApproved(pairToken, "Test Phone", "TestUA", "192.168.1.50", "itest-device",
            overRelay: false, claimedOverHttps: true);
        Assert.True(claim.Ok, claim.Error);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claim.SessionToken);
        // TestServer traffic is plain-HTTP; without this the insecure-CSRF guard
        // 403s state-changing phone-session requests. Real browsers send it on
        // same-origin fetches; the iOS app is exempt via HTTPS.
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        return client;
    }
}

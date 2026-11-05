using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

/// <summary>POST /system/launch-uri: desktop-token only, scheme allowlist enforced before anything launches.</summary>
public sealed class SystemLaunchUriRoutesTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private HttpClient PanelClient()
    {
        var pairing = _factory.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.CreatePairQr();
        var pairToken = pairing.GetOutstandingPairTokens()[0].Token;
        var claim = pairing.ClaimCoreApproved(pairToken, "Test Phone", "TestUA", "192.168.1.50", "itest-device",
            overRelay: false, claimedOverHttps: true);
        Assert.True(claim.Ok, claim.Error);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claim.SessionToken);
        return client;
    }

    [Fact]
    public async Task PanelSession_IsForbidden()
    {
        var res = await PanelClient().PostAsJsonAsync("/system/launch-uri", new OpenUrlRequest { Url = "steam://rungameid/730" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NoToken_Is401()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/system/launch-uri", new OpenUrlRequest { Url = "steam://rungameid/730" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("ms-msdt:/id x")]
    [InlineData("search-ms:query=a")]
    [InlineData("file:///C:/a.exe")]
    [InlineData("C:\\a.exe")]
    public async Task Desktop_DisallowedSchemeIsRefusedWithoutLaunching(string url)
    {
        var res = await DesktopClient().PostAsJsonAsync("/system/launch-uri", new OpenUrlRequest { Url = url });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"error\":true", (await res.Content.ReadAsStringAsync()).Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotOnRelayAllowlist()
    {
        Assert.False(Nexus.Service.Relay.RelayHttpAllowlist.IsAllowed("POST", "/system/launch-uri"));
    }
}

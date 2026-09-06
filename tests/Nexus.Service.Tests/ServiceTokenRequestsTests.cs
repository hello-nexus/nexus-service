using System.Net;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests;

/// <summary>
/// Handler-level desktop-token checks follow the middleware's desktop lane:
/// loopback remote AND a Host header naming this machine, so neither a relayed
/// request (null remote) nor a DNS-rebound browser page can present the token.
/// </summary>
public sealed class ServiceTokenRequestsTests
{
    private static (TokenService tokens, DefaultHttpContext ctx) Setup(IPAddress? remote, string host)
    {
        var tokens = new TokenService(new InMemoryConfigStore());
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote;
        ctx.Request.Host = new HostString(host, 9400);
        ctx.Request.Headers.Authorization = "Bearer " + tokens.Token;
        return (tokens, ctx);
    }

    [Fact]
    public void Accepted_from_loopback_under_a_local_host()
    {
        var (tokens, ctx) = Setup(IPAddress.Loopback, "localhost");
        Assert.True(ServiceTokenRequests.HasServiceToken(ctx, tokens));
    }

    [Fact]
    public void Rejected_from_the_lan_a_null_remote_or_a_foreign_host()
    {
        var (lanTokens, lan) = Setup(IPAddress.Parse("192.168.1.50"), "localhost");
        Assert.False(ServiceTokenRequests.HasServiceToken(lan, lanTokens));

        var (relayTokens, relayed) = Setup(null, "localhost");
        Assert.False(ServiceTokenRequests.HasServiceToken(relayed, relayTokens));

        var (rebindTokens, rebound) = Setup(IPAddress.Loopback, "evil.example");
        Assert.False(ServiceTokenRequests.HasServiceToken(rebound, rebindTokens));
    }

    [Fact]
    public void Query_token_is_honored_the_same_way()
    {
        var tokens = new TokenService(new InMemoryConfigStore());
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        ctx.Request.Host = new HostString("127.0.0.1", 9400);
        ctx.Request.QueryString = new QueryString("?token=" + tokens.Token);
        Assert.True(ServiceTokenRequests.HasServiceToken(ctx, tokens));
    }
}

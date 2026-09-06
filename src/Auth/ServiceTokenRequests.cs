using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Auth;

/// <summary>
/// Desktop-token check for mutating endpoints that panel surfaces must not
/// reach (Bearer header or ?token=, validated against the /pair token). Same
/// loopback + local-Host contract as the middleware's desktop lane: the token
/// is never honored from another machine, a relayed request (null remote), or
/// a DNS-rebound browser page.
/// </summary>
public static class ServiceTokenRequests
{
    public static bool HasServiceToken(HttpContext ctx, TokenService tokens)
    {
        if (!AuthRequestPolicy.IsLoopbackRemote(ctx) || !AuthRequestPolicy.IsLocalHostHeader(ctx))
            return false;

        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
            return tokens.Validate(authHeader.Substring("Bearer ".Length).Trim());

        var queryToken = ctx.Request.Query["token"].ToString();
        return tokens.Validate(queryToken);
    }
}

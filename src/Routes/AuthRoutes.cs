using System.Net;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static class AuthRoutes
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/pair", (HttpContext ctx, TokenService tokens) =>
        {
            // Loopback only. LAN (RFC1918, link-local) is not a trust
            // boundary - anyone on the user's Wi-Fi could otherwise fetch
            // the service token permanently. The Host header must name this
            // machine too: a web page whose domain is DNS-rebound to 127.0.0.1
            // arrives from loopback, and this is the one request it must not
            // be able to read.
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote) || !AuthRequestPolicy.IsLocalHostHeader(ctx))
            {
                return Results.Json(
                    new ApiResponse { Error = true, Msg = "Pairing is only available from the loopback interface." },
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 403);
            }

            return Results.Ok(new PairResponse(tokens.Token));
        });
    }
}

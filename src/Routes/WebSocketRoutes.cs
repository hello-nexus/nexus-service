using System.Net.WebSockets;
using Nexus.Service.Auth;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// WebSocket endpoints. The multiplexed <c>/ws</c> endpoint handles all JSON
/// topics via dynamic subscriptions. <c>/lighting/output</c> stays separate
/// for binary 60fps RGB frame streaming.
/// </summary>
public static class WebSocketRoutes
{
    public static void MapWebSocketEndpoints(this WebApplication app)
    {
        // Multiplexed WebSocket - single endpoint with dynamic topic subscriptions.
        // ctx.Items["PhoneSessionId"] is populated by the auth middleware when
        // the request authenticated via a phone-session cookie/bearer; null
        // means a desktop/panel-kiosk client and the connection is never
        // eligible for the Pair Remote killswitch.
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":true,\"msg\":\"WebSocket expected\"}");
                return;
            }
            var phoneSessionId = ctx.Items.TryGetValue("PhoneSessionId", out var raw) ? raw as string : null;
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var hub = ctx.RequestServices.GetRequiredService<MultiplexHub>();
            // The Q-series watcher reads the open-socket count as the panel's liveness.
            var tunnel = ctx.RequestServices.GetService<Nexus.Service.Panel.PanelTunnelMonitor>();
            var onTunnel = tunnel?.IsTunnelRequest(ctx) == true;
            if (onTunnel) tunnel!.SocketOpened();
            try
            {
                await hub.HandleClientAsync(socket, phoneSessionId, ctx.RequestAborted);
            }
            finally
            {
                if (onTunnel) tunnel!.SocketClosed();
            }
        });

        // Lighting output - binary 60fps frames (not multiplexed)
        app.Map("/lighting/output", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":true,\"msg\":\"WebSocket expected\"}");
                return;
            }
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var hub = ctx.RequestServices.GetRequiredService<LightingOutputHub>();
            await hub.HandleClientAsync(socket, cancellationToken: ctx.RequestAborted);
        });

        // Webcam stream - binary phone -> service video frames, one frame per
        // message. LAN-only: trusted relay dispatches are refused before the
        // upgrade, mirroring the REST guard in WebcamRoutes.
        app.Map("/webcam/stream", async (HttpContext ctx) =>
        {
            if (Nexus.Service.Webcam.WebcamRelayGuard.IsRelayDispatch(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":true,\"msg\":\"WebSocket expected\"}");
                return;
            }
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var manager = ctx.RequestServices.GetRequiredService<Nexus.Service.Webcam.WebcamSessionManager>();
            await manager.HandleStreamSocketAsync(socket, ctx.RequestAborted);
        }).AllowPanel();

        // Sealed LAN tunnel - the panel's E2E-encrypted transport over plain
        // :9400/:9443. Auth is the in-band sealed handshake (the rid identifies the
        // paired session, the per-connection AEAD key proves possession), so it is
        // anonymous at the middleware and the session token never rides the wire.
        app.Map("/secure-tunnel", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":true,\"msg\":\"WebSocket expected\"}");
                return;
            }
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var relay = ctx.RequestServices.GetRequiredService<Nexus.Service.Relay.RelayConnectionService>();
            await relay.HandleInboundSealedTunnelAsync(socket, ctx.RequestAborted);
        });
    }
}

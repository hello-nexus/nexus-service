using System.Collections.Generic;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Panel;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// What the overlay host reconciles against (state) and the monitor map it posts back.
/// </summary>
public static class OverlayRoutes
{
    public static void MapOverlayEndpoints(this WebApplication app)
    {
        // Everything nexus-overlay reconciles against, in one read: a failed
        // read must leave the host's kiosks and widgets exactly as they are,
        // which per-setting reads could not express.
        app.MapGet("/overlay/state", (HttpContext ctx, IConfigStore store, PanelDeviceRegistry registry, DisplayTopologyService topology, StreamedPanelCoordinator streams, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var s = store.Load();
            var state = new OverlayStateResponse
            {
                AutoLaunch = s.Panel.AutoLaunch,
                ReserveMonitor = s.Panel.ReserveMonitor,
                PanelAlwaysOnTop = s.Panel.AlwaysOnTop,
                Y70Backdrop = registry.GetY70Backdrop(),
                Y70CompatibilityRendering = s.Y70.CompatibilityRendering,
                Y70KeepCursorOff = s.Y70.KeepCursorOff,
                Streams = streams.GetAssignments().Assignments,
            };
            foreach (var (displayId, panelDeviceId, reserveMonitor, backdrop, keepCursorOff, alwaysOnTop, clickThrough, isPrimary) in registry.ListAssignments(topology.IsPrimaryDisplay))
            {
                state.Assignments.Add(new DisplayAssignmentDto
                {
                    DisplayId = displayId,
                    PanelDeviceId = panelDeviceId,
                    ReserveMonitor = reserveMonitor,
                    Backdrop = backdrop,
                    KeepCursorOff = keepCursorOff,
                    AlwaysOnTop = alwaysOnTop,
                    ClickThrough = clickThrough,
                    IsPrimary = isPrimary,
                });
            }
            return Results.Json(state, AppJsonContext.Default.OverlayStateResponse);
        });

        // The Windows host numbers monitors in EnumDisplayMonitors order; only it can map the legacy
        // Overlay.Monitor index to a display id. Posted at boot and on every display change.
        app.MapPost("/overlay/monitors", (OverlayMonitorsBody body, HttpContext ctx, OverlayMonitorMap map, DesktopWidgetsMigration migration, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            map.Set(body.Monitors ?? new List<OverlayMonitorDto>());
            migration.Run();
            return Results.Ok(ApiResponse.Ok("ok"));
        });
    }
}

#if DEV_TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Conflicts;
using Nexus.Service.Dev;
using Nexus.Service.Devices;
using Nexus.Service.Diagnostics;
using Nexus.Service.Models;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public sealed class DevSimCatalogDto
{
    public string Id { get; set; } = "";
    /// <summary>"guard" | "health" | "incident" | "device".</summary>
    public string Category { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class DevSimActiveDto
{
    public string Id { get; set; } = "";
    public long StartedAtUtcMs { get; set; }
}

public sealed class DevSimEventsResponse
{
    public List<DevSimCatalogDto> Catalog { get; set; } = new();
    public List<DevSimActiveDto> Active { get; set; } = new();
}

/// <summary>
/// Dev-tools routes for simulated events. Desktop bearer auth like every other dev route (no
/// AllowPanel). Starting a simulation raises its alert once through the real alert path; every
/// change broadcasts the topics the affected surfaces listen on.
/// </summary>
public static class DevSimRoutes
{
    public static void MapDevSimEndpoints(this WebApplication app)
    {
        app.MapGet("/dev/sim/events", (DevSimEvents sim) => Results.Json(Describe(sim), AppJsonContext.Default.DevSimEventsResponse));

        app.MapPost("/dev/sim/events/{id}", (string id, DevSimEvents sim, DiagnosticsAlertService alerts, Nexus.Service.Persistence.IConfigStore store) =>
        {
            if (!sim.Start(id, out var isNew))
            {
                return Unknown(id);
            }
            if (isNew)
            {
                RaiseAlert(sim, alerts, id, store.Load().Diagnostics);
            }
            return Results.Json(Describe(sim), AppJsonContext.Default.DevSimEventsResponse);
        });

        app.MapDelete("/dev/sim/events/{id}", (string id, DevSimEvents sim) =>
        {
            if (!sim.Stop(id))
            {
                return Unknown(id);
            }
            return Results.Json(Describe(sim), AppJsonContext.Default.DevSimEventsResponse);
        });

        app.MapPost("/dev/sim/clear", (DevSimEvents sim) =>
        {
            sim.Clear();
            return Results.Json(Describe(sim), AppJsonContext.Default.DevSimEventsResponse);
        });
    }

    internal static DevSimEventsResponse Describe(DevSimEvents sim) => new()
    {
        Catalog = DevSimEvents.Catalog.Select(c => new DevSimCatalogDto { Id = c.Id, Category = c.Category, Label = c.Label }).ToList(),
        Active = sim.Active().Select(a => new DevSimActiveDto { Id = a.Id, StartedAtUtcMs = a.StartedAtUtcMs }).ToList(),
    };

    private static IResult Unknown(string id) =>
        Results.Json(ApiResponse.Fail($"Unknown simulated event: {id}"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status404NotFound);

    /// <summary>Raises the sim's own notice once: guard sims through the thermal guard's alert, the rest through the health alert evaluation so the notification switches apply.</summary>
    internal static void RaiseAlert(DevSimEvents sim, DiagnosticsAlertService alerts, string id, Nexus.Service.Persistence.DiagnosticsSettings diagnostics)
    {
        if (sim.GuardNoticeFor(id) is { } notice)
        {
            alerts.Raise(new DiagnosticsAlertNotice(notice.Title, notice.Text, "thermalGuard"));
            return;
        }
        // The same ignore list and domain toggles as a real component.
        var components = DiagnosticsHealthModel.FilterSimulated(sim.AlertComponentsFor(id), diagnostics);
        if (components.Count > 0)
        {
            alerts.RaiseSimulated(components);
        }
    }

    /// <summary>Broadcasts the topics the simulated surfaces listen on (cooling, prefs, update, conflicts, devices).</summary>
    internal static void BroadcastAll(IServiceProvider services)
    {
        var hub = services.GetService<MultiplexHub>();
        if (hub is not null)
        {
            PanelTopics.BroadcastCooling(hub);
            PanelTopics.BroadcastPrefs(hub);
            PanelTopics.BroadcastUpdate(hub);
        }
        services.GetService<ConflictWatcher>()?.RepublishForSimulation();
        services.GetService<DeviceBroadcaster>()?.BroadcastNow();
    }
}
#endif

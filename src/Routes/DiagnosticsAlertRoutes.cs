using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>User sensor threshold rules: CRUD, acknowledge and history. The runtime lives in <see cref="SensorAlertEngine"/>; every mutation ends in its Reconcile so the diagnostics/alerts topic fires.</summary>
public static class DiagnosticsAlertRoutes
{
    public static void MapDiagnosticsAlertEndpoints(this WebApplication app)
    {
        app.MapGet("/diagnostics/alerts", (IConfigStore store, SensorAlertEngine engine) => new SensorAlertsResponse
        {
            Rules = (store.Load().Diagnostics.SensorAlerts ?? new List<SensorAlertRule>()).ToList(),
            States = engine.States(),
            History = engine.History(),
        }).AllowPanel();

        app.MapPost("/diagnostics/alerts", (SensorAlertRule? body, IConfigStore store, SensorAlertEngine engine) =>
        {
            if (body is null)
            {
                return Results.BadRequest(ApiResponse.Fail("A rule is required."));
            }
            SensorAlertRules.Normalize(body);
            if (SensorAlertRules.Validate(body) is { } error)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            body.Id = Guid.NewGuid().ToString("N");
            var full = false;
            store.Update(s =>
            {
                s.Diagnostics.SensorAlerts ??= new List<SensorAlertRule>();
                if (s.Diagnostics.SensorAlerts.Count >= SensorAlertRules.MaxRules)
                {
                    full = true;
                    return;
                }
                s.Diagnostics.SensorAlerts.Add(body);
            });
            if (full)
            {
                return Results.Conflict(ApiResponse.Fail($"At most {SensorAlertRules.MaxRules} rules are allowed."));
            }
            engine.Reconcile();
            return Results.Created($"/diagnostics/alerts/{body.Id}", body);
        });

        app.MapPut("/diagnostics/alerts/{id}", (string id, SensorAlertRule? body, IConfigStore store, SensorAlertEngine engine) =>
        {
            if (body is null)
            {
                return Results.BadRequest(ApiResponse.Fail("A rule is required."));
            }
            body.Id = id;
            SensorAlertRules.Normalize(body);
            if (SensorAlertRules.Validate(body) is { } error)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            var found = false;
            store.Update(s =>
            {
                var list = s.Diagnostics.SensorAlerts;
                var index = list?.FindIndex(r => r.Id == id) ?? -1;
                if (list is null || index < 0)
                {
                    return;
                }
                list[index] = body;
                found = true;
            });
            if (!found)
            {
                return Results.NotFound(ApiResponse.Fail("Unknown rule."));
            }
            engine.Reconcile();
            return Results.Ok(body);
        });

        app.MapDelete("/diagnostics/alerts/{id}", (string id, IConfigStore store, SensorAlertEngine engine) =>
        {
            store.Update(s => s.Diagnostics.SensorAlerts?.RemoveAll(r => r.Id == id));
            engine.Reconcile();
            return Results.NoContent();
        });

        app.MapPost("/diagnostics/alerts/{id}/acknowledge", (string id, IConfigStore store, SensorAlertEngine engine) =>
        {
            if (!(store.Load().Diagnostics.SensorAlerts?.Any(r => r.Id == id) ?? false))
            {
                return Results.NotFound(ApiResponse.Fail("Unknown rule."));
            }
            engine.Acknowledge(id);
            return Results.NoContent();
        });

        app.MapPost("/diagnostics/alerts/history/clear", (SensorAlertEngine engine) =>
        {
            engine.ClearHistory();
            return Results.NoContent();
        });
    }
}

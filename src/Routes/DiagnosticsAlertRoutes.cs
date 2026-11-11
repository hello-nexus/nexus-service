using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Routes;

/// <summary>User sensor threshold rules: CRUD, acknowledge and history. The runtime lives in <see cref="SensorAlertEngine"/>; every mutation ends in its Reconcile so the diagnostics/alerts topic fires.</summary>
public static class DiagnosticsAlertRoutes
{
    public static void MapDiagnosticsAlertEndpoints(this WebApplication app)
    {
        app.MapGet("/diagnostics/alerts", (IConfigStore store, SensorAlertEngine engine) => new SensorAlertsResponse
        {
            Rules = engine.Rules(),
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
        }).Produces<SensorAlertRule>(StatusCodes.Status201Created).Produces<ApiResponse>(StatusCodes.Status400BadRequest).Produces<ApiResponse>(StatusCodes.Status409Conflict);

        app.MapPut("/diagnostics/alerts/{id}", (string id, SensorAlertRule? body, IConfigStore store, SensorAlertEngine engine, IMetricsHistoryStore history, MetricsSampleBuffer buffer) =>
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
            var sensorChanged = false;
            store.Update(s =>
            {
                var list = s.Diagnostics.SensorAlerts;
                var index = list?.FindIndex(r => r.Id == id) ?? -1;
                if (list is null || index < 0)
                {
                    return;
                }
                sensorChanged = !SensorAlertRules.SameSensor(list[index].Sensor, body.Sensor);
                list[index] = body;
                found = true;
            });
            if (!found)
            {
                return Results.NotFound(ApiResponse.Fail("Unknown rule."));
            }
            if (sensorChanged)
            {
                DropHistory(id, history, buffer);
            }
            engine.Reconcile();
            return Results.Ok(body);
        }).Produces<SensorAlertRule>().Produces<ApiResponse>(StatusCodes.Status400BadRequest).Produces<ApiResponse>(StatusCodes.Status404NotFound);

        app.MapDelete("/diagnostics/alerts/{id}", (string id, IConfigStore store, SensorAlertEngine engine, IMetricsHistoryStore history, MetricsSampleBuffer buffer) =>
        {
            var removed = false;
            store.Update(s => removed = s.Diagnostics.SensorAlerts?.RemoveAll(r => r.Id == id) > 0);
            if (removed)
            {
                DropHistory(id, history, buffer);
            }
            engine.Reconcile();
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);

        app.MapGet("/diagnostics/alerts/history-series", (
            long? from, long? to, int? maxPoints, string? ruleId,
            IConfigStore store, IMetricsHistoryStore history, MetricsSampleBuffer buffer) =>
        {
            if (from is null || to is null || to < from)
            {
                return Results.BadRequest(ApiResponse.Fail("from and to are required and to must be >= from"));
            }

            var fromSec = from.Value / 1000;
            var toSec = to.Value / 1000;
            var clamped = Math.Clamp(maxPoints ?? MonitoringHistoryRoutes.DefaultMaxPoints, MonitoringHistoryRoutes.MinMaxPoints, MonitoringHistoryRoutes.MaxMaxPoints);
            var step = MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), clamped);
            // The raw tier only reaches back a couple of days; an older window has to read the minute rollup.
            var rawFloor = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Monitoring.History.Binary.BinaryMetricsHistoryStore.SensorSeriesRawDays * 86_400L;
            if (step < 60 && fromSec < rawFloor)
            {
                step = 60;
            }

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            store.Read(s =>
            {
                foreach (var rule in s.Diagnostics.SensorAlerts ?? new List<SensorAlertRule>())
                {
                    if (string.IsNullOrEmpty(ruleId) || rule.Id == ruleId)
                    {
                        names[rule.Id] = rule.Sensor.Name;
                    }
                }
            });

            try
            {
                // Tail first, then the store: see /monitoring/history for why the order matters.
                var tail = buffer.SnapshotRange(fromSec, toSec);
                var db = history.QuerySensorSeriesDecimated(fromSec, toSec, step);
                var series = new List<MetricSeriesWire>();
                foreach (var (id, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var dbPoints = db.Where(sl => sl.SeriesId == id).Select(sl => (sl.Slot, (double?)sl.Avg, (double?)sl.Max));
                    var tailPoints = MetricsDecimation.Decimate(
                        tail.Select(t => new MetricSamplePoint(t.TsSec, t.SensorReadings.FirstOrDefault(r => r.Id == id)?.Value)),
                        fromSec, toSec, step);
                    var merged = MonitoringHistoryRoutes.MergeDecimatedSlots(dbPoints, tailPoints);
                    if (merged.Count == 0)
                    {
                        continue;
                    }
                    series.Add(new MetricSeriesWire
                    {
                        Id = id,
                        Kind = "sensorAlert",
                        Name = name,
                        Points = MonitoringHistoryRoutes.ToWirePoints(merged, wholeNumbers: false),
                    });
                }
                return Results.Ok(new MetricsHistoryResponse
                {
                    RetentionDays = MetricsHistory.TempRetentionDays,
                    StepSeconds = step,
                    Series = series,
                });
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[sensor-alerts] history-series query failed: {ex.Message}");
                return Results.Ok(new MetricsHistoryResponse { Supported = false });
            }
        }).AllowPanel().Produces<MetricsHistoryResponse>().Produces<ApiResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/diagnostics/alerts/{id}/acknowledge", (string id, IConfigStore store, SensorAlertEngine engine) =>
        {
            if (!engine.Rules().Any(r => r.Id == id))
            {
                return Results.NotFound(ApiResponse.Fail("Unknown rule."));
            }
            engine.Acknowledge(id);
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent).Produces<ApiResponse>(StatusCodes.Status404NotFound);

        app.MapPost("/diagnostics/alerts/history/clear", (SensorAlertEngine engine) =>
        {
            engine.ClearHistory();
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);
    }

    private static void DropHistory(string ruleId, IMetricsHistoryStore history, MetricsSampleBuffer buffer)
    {
        buffer.RemoveSensorSeries(ruleId);
        history.DeleteSensorSeries(ruleId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}

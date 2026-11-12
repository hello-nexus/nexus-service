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
using Nexus.Service.Sensors;

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
            if (body.BuiltIn is not null)
            {
                return Results.BadRequest(ApiResponse.Fail("Built-in sensors cannot be created."));
            }
            SensorAlertRules.Normalize(body);
            if (SensorAlertRules.Validate(body) is { } error)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            body.Source = null;
            body.Id = Guid.NewGuid().ToString("N");
            // Read outside the store lock: it can walk hardware.
            var builtIns = engine.Rules().Where(r => r.BuiltIn is not null).ToList();
            var candidateResolves = engine.IdResolves(body.Sensor);
            SensorAlertRule? tracker = null;
            var full = false;
            store.Update(s =>
            {
                s.Diagnostics.SensorAlerts ??= new List<SensorAlertRule>();
                tracker = SensorAlertRules.FindTracker(body.Sensor, candidateResolves, builtIns.Concat(s.Diagnostics.SensorAlerts.Where(r => r.BuiltIn is null)), null);
                if (tracker is not null)
                {
                    return;
                }
                // The cap counts every listed entry: the user's rules plus the built-ins detected now.
                if (s.Diagnostics.SensorAlerts.Count(r => r.BuiltIn is null) + builtIns.Count >= SensorAlertRules.MaxRules)
                {
                    full = true;
                    return;
                }
                s.Diagnostics.SensorAlerts.Add(body);
            });
            if (tracker is not null)
            {
                return Results.Conflict(SensorAlertRules.AlreadyTracked(tracker));
            }
            if (full)
            {
                return Results.Conflict(ApiResponse.Fail($"At most {SensorAlertRules.MaxRules} Watchlist entries are allowed."));
            }
            engine.Reconcile();
            return Results.Created($"/diagnostics/alerts/{body.Id}", body);
        }).Produces<SensorAlertRule>(StatusCodes.Status201Created).Produces<ApiResponse>(StatusCodes.Status400BadRequest).Produces<ApiResponse>(StatusCodes.Status409Conflict).Produces<SensorAlreadyTrackedResponse>(StatusCodes.Status409Conflict);

        app.MapPut("/diagnostics/alerts/{id}", (string id, SensorAlertRule? body, IConfigStore store, SensorAlertEngine engine, IMetricsHistoryStore history, MetricsSampleBuffer buffer) =>
        {
            if (body is null)
            {
                return Results.BadRequest(ApiResponse.Fail("A rule is required."));
            }
            body.Id = id;
            body.Source = null;
            if (BuiltInSensorRules.IsBuiltInId(id))
            {
                // Identity and sensor come from the stored rule, never the client; only a detected device has one.
                if (engine.Rules().FirstOrDefault(r => r.Id == id && r.BuiltIn is not null) is not { } current)
                {
                    return Results.NotFound(ApiResponse.Fail("Unknown rule."));
                }
                BuiltInSensorRules.Pin(body, current);
            }
            else if (body.BuiltIn is not null)
            {
                return Results.BadRequest(ApiResponse.Fail("Built-in sensors cannot be created."));
            }
            SensorAlertRules.Normalize(body);
            if (SensorAlertRules.Validate(body) is { } error)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            // Read outside the store lock: it can walk hardware.
            var bodyIdResolves = engine.IdResolves(body.Sensor);
            var builtIns = engine.Rules().Where(r => r.BuiltIn is not null).ToList();
            SensorAlertRule? tracker = null;
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
                sensorChanged = !SensorAlertRules.SameSensor(list[index].Sensor, body.Sensor, bodyIdResolves);
                // Only a re-point is checked, so a rule that already duplicated another keeps its limits editable. A built-in's sensor is pinned.
                if (sensorChanged && body.BuiltIn is null)
                {
                    tracker = SensorAlertRules.FindTracker(body.Sensor, bodyIdResolves, builtIns.Concat(list.Where(r => r.BuiltIn is null)), id);
                    if (tracker is not null)
                    {
                        return;
                    }
                }
                list[index] = body.Clone();
                found = true;
                if (body.BuiltIn == BuiltInSensorRules.FanSpeed && engine.BuiltInDevices().FirstOrDefault(d => BuiltInSensorRules.RuleId(d.SeriesId) == id) is { } fan)
                {
                    BuiltInSensorRules.SetFanEnabled(s.Diagnostics, fan, body.Enabled);
                }
            });
            if (tracker is not null)
            {
                return Results.Conflict(SensorAlertRules.AlreadyTracked(tracker));
            }
            if (!found)
            {
                return Results.NotFound(ApiResponse.Fail("Unknown rule."));
            }
            if (sensorChanged)
            {
                DropHistory(id, history, buffer);
            }
            engine.Reconcile();
            // The stored copy has no source; the response carries the one feeding the rule now.
            body.Source = engine.Rules().FirstOrDefault(r => r.Id == id)?.Source;
            return Results.Ok(body);
        }).Produces<SensorAlertRule>().Produces<ApiResponse>(StatusCodes.Status400BadRequest).Produces<ApiResponse>(StatusCodes.Status404NotFound).Produces<SensorAlreadyTrackedResponse>(StatusCodes.Status409Conflict);

        app.MapDelete("/diagnostics/alerts/{id}", (string id, IConfigStore store, SensorAlertEngine engine, IMetricsHistoryStore history, MetricsSampleBuffer buffer) =>
        {
            if (BuiltInSensorRules.IsBuiltInId(id))
            {
                return Results.Conflict(ApiResponse.Fail("Built-in sensors can be turned off, not deleted."));
            }
            var removed = false;
            store.Update(s => removed = s.Diagnostics.SensorAlerts?.RemoveAll(r => r.Id == id) > 0);
            if (removed)
            {
                DropHistory(id, history, buffer);
            }
            engine.Reconcile();
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent).Produces<ApiResponse>(StatusCodes.Status409Conflict);

        // Writes the factory state back (the series is kept: it is the same sensor). No id resets every built-in.
        app.MapPost("/diagnostics/alerts/builtins/reset", (string? id, IConfigStore store, SensorAlertEngine engine) =>
        {
            var devices = engine.BuiltInDevices().Where(d => string.IsNullOrEmpty(id) || BuiltInSensorRules.RuleId(d.SeriesId) == id).ToList();
            if (!string.IsNullOrEmpty(id) && devices.Count == 0)
            {
                return Results.NotFound(ApiResponse.Fail("Unknown built-in sensor."));
            }
            store.Update(s =>
            {
                var list = s.Diagnostics.SensorAlerts ??= new List<SensorAlertRule>();
                foreach (var device in devices)
                {
                    var index = list.FindIndex(r => r.Id == BuiltInSensorRules.RuleId(device.SeriesId));
                    if (index >= 0)
                    {
                        list[index] = BuiltInSensorRules.Defaults(device);
                        if (device.Kind == BuiltInSensorRules.FanSpeed)
                        {
                            BuiltInSensorRules.SetFanEnabled(s.Diagnostics, device, true);
                        }
                    }
                }
            });
            engine.Reconcile();
            return Results.Ok(new SensorAlertsResponse { Rules = engine.Rules(), States = engine.States(), History = engine.History() });
        }).Produces<SensorAlertsResponse>().Produces<ApiResponse>(StatusCodes.Status404NotFound);

        app.MapGet("/diagnostics/alerts/history-series", (
            long? from, long? to, int? maxPoints, string? ruleId,
            IConfigStore store, SensorAlertEngine engine, IMetricsHistoryStore history, MetricsSampleBuffer buffer, ISensorProvider sensors) =>
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
                    if (rule.BuiltIn is null && (string.IsNullOrEmpty(ruleId) || rule.Id == ruleId))
                    {
                        names[rule.Id] = rule.Sensor.Name;
                    }
                }
            });

            try
            {
                // Tail first, then the store: see /monitoring/history for why the order matters.
                var tail = buffer.SnapshotRange(fromSec, toSec);
                var db = history.QuerySensorSeriesDecimated(fromSec, toSec, step, names.Keys.ToHashSet(StringComparer.Ordinal));
                var series = new List<MetricSeriesWire>();
                // A built-in plots the monitoring history's own series, relabeled with its rule id.
                var builtIns = engine.Rules().Where(r => r.BuiltIn is not null && (string.IsNullOrEmpty(ruleId) || r.Id == ruleId)).ToList();
                if (builtIns.Count > 0)
                {
                    var wanted = builtIns.ToDictionary(r => BuiltInSensorRules.SeriesId(r.Id), StringComparer.Ordinal);
                    var monitoring = MonitoringHistoryRoutes.QueryHistory(fromSec, toSec, step, clamped, wanted.Keys.ToHashSet(StringComparer.Ordinal), history, buffer, sensors);
                    foreach (var rule in builtIns)
                    {
                        if (monitoring.Series.FirstOrDefault(x => x.Id == BuiltInSensorRules.SeriesId(rule.Id)) is { Points.Count: > 0 } found)
                        {
                            series.Add(new MetricSeriesWire { Id = rule.Id, Kind = "sensorAlert", Name = rule.Sensor.Name, Points = found.Points });
                        }
                    }
                }
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

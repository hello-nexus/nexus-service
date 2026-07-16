using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Models;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class CoolingRoutes
{
    public static void MapCoolingEndpoints(this WebApplication app)
    {
        // Curves
        app.MapGet("/cooling/curves", (Nexus.Service.Persistence.IConfigStore store) =>
        {
            var settings = store.Load();
            var docs = settings.Cooling.Curves;
            var curves = docs.Select(d => new Curve
            {
                Id = d.Id,
                Name = d.Name,
                Type = d.Type,
                Input = new CurveInput { Id = d.Input.Id, Type = d.Input.Type, Device = d.Input.Device },
                Outputs = d.Outputs.Select(o => new CurveOutput { Id = o.Id, Type = o.Type }).ToList(),
                Flat = d.Flat is null ? null : new FlatCurve { Speed = d.Flat.Speed },
                Linear = d.Linear is null ? null : new LinearCurve
                {
                    ResponseTime = d.Linear.ResponseTime,
                    MinTemp = d.Linear.MinTemp,
                    MaxTemp = d.Linear.MaxTemp,
                    MinSpeed = d.Linear.MinSpeed,
                    MaxSpeed = d.Linear.MaxSpeed,
                },
                Graph = d.Graph is null ? null : new GraphCurve
                {
                    ResponseTime = d.Graph.ResponseTime,
                    SpeedModifier = d.Graph.SpeedModifier,
                    Points = d.Graph.Points.Select(p => new Models.Cooling.GraphPoint { Temp = p.Temp, Speed = p.Speed }).ToList(),
                },
                Mixed = d.Mixed is null ? null : new MixedCurve
                {
                    ResponseTime = d.Mixed.ResponseTime,
                    CurveIds = new List<string>(d.Mixed.CurveIds),
                    Fn = d.Mixed.Fn,
                },
                Preset = d.Preset,
                IsDefault = d.Preset is null ? null : FanProfiles.IsPresetCurveAtDefaults(d),
            }).ToList();
            return new GetCurvesResponse
            {
                GlobalSpeedModifier = settings.Cooling.GlobalSpeedModifier,
                Curves = curves,
            };
        }).AllowPanel();

        app.MapPost("/cooling/curves/set", (SetCurvesBody body, ICurveProvider c, IFanControlProvider f, IConfigStore store, MultiplexHub hub) =>
        {
            // Clamp every stored speed to [0,100] (and the global boost) so a
            // malformed curve can't persist out-of-range values; the write
            // chokepoint clamps the physical output independently.
            c.SetCurves(CoolingSafety.Sanitize(body));
            // Recompute the active preset from the saved curve outputs so the
            // profile bar stays in sync after a manual edit in the curve list.
            var derived = FanProfiles.DerivePresetFromCurves(store, f);
            store.Update(s => s.Cooling.ActivePreset = derived);
            PanelTopics.BroadcastCooling(hub);
            return ApiResponse.Ok();
        });

        // Fan control
        app.MapGet("/cooling/fans", (IFanControlProvider f, IConfigStore store) =>
        {
            var channels = new List<FanChannel>(f.GetFanChannels());
            var cooling = store.Load().Cooling;
            var names = cooling.FanNames;
            foreach (var ch in channels)
            {
                if (names.TryGetValue(ch.Id, out var custom))
                {
                    ch.Name = custom;
                }
                ch.Locked = FanProfiles.IsLocked(ch, cooling.FanLockOverrides);
            }
            return new GetFanChannelsResponse { Channels = channels };
        }).AllowPanel();

        app.MapGet("/cooling/sources", (IFanControlProvider f) =>
            new GetTemperatureSourcesResponse { Sources = new(f.GetTemperatureSources()) }).AllowPanel();

        app.MapPost("/cooling/fan/{id}/speed", (string id, SetFanSpeedBody body, IFanControlProvider f, Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            id = Uri.UnescapeDataString(id);
            // Detach from any curve first. Otherwise CurveEngine would
            // re-drive the fan on the next tick (silent overriding the user's
            // manual choice) and the active-preset derivation would still
            // count this fan as on the shared preset, leaving the chip lit.
            FanProfiles.DetachFanFromCurves(id, store);
            var actual = f.SetFanSpeed(id, body.Speed);
            store.Update(s => s.Cooling.ManualSpeeds[id] = actual);
            var derivedAfterSpeed = FanProfiles.DerivePresetFromCurves(store, f);
            store.Update(s => s.Cooling.ActivePreset = derivedAfterSpeed);
            PanelTopics.BroadcastCooling(hub);
            telemetry.Capture(Nexus.Service.Telemetry.TelemetryEvents.FanSpeedSet, ("speed", actual));
            return new SetFanSpeedResponse { ChannelId = id, Speed = actual, Mode = Nexus.Service.Models.Cooling.FanModes.Manual };
        });

        app.MapPost("/cooling/fan/{id}/auto", (string id, IFanControlProvider f, Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
        {
            id = Uri.UnescapeDataString(id);
            // Same rationale as /speed: drop any curve attachment so the BIOS
            // release actually persists past the next CurveEngine tick, and so
            // derivation sees this fan as truly off-preset.
            FanProfiles.DetachFanFromCurves(id, store);
            f.ReleaseFan(id);
            store.Update(s => s.Cooling.ManualSpeeds.Remove(id));
            var derivedAfterAuto = FanProfiles.DerivePresetFromCurves(store, f);
            store.Update(s => s.Cooling.ActivePreset = derivedAfterAuto);
            PanelTopics.BroadcastCooling(hub);
            return ApiResponse.Ok();
        });

        app.MapPost("/cooling/fan/{id}/name", (string id, SetFanNameBody body, IFanControlProvider f, IConfigStore store, MultiplexHub hub) =>
        {
            id = Uri.UnescapeDataString(id);
            var channels = f.GetFanChannels();
            if (!channels.Any(c => c.Id == id))
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = "Unknown fan channel" });
            }

            store.Update(s =>
            {
                if (string.IsNullOrWhiteSpace(body.Name))
                {
                    s.Cooling.FanNames.Remove(id);
                }
                else
                {
                    s.Cooling.FanNames[id] = body.Name.Trim();
                }
            });
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/cooling/fan/{id}/lock", (string id, SetFanLockBody body, IFanControlProvider f, IConfigStore store, MultiplexHub hub) =>
        {
            id = Uri.UnescapeDataString(id);
            var ch = f.GetFanChannels().FirstOrDefault(c => c.Id == id);
            if (ch is null)
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = "Unknown fan channel" });
            }

            FanProfiles.SetLockOverride(ch, body.Locked, store);

            // Locking/unlocking never moves a fan; only re-derive the active
            // preset label so it stays truthful for future applies.
            var derived = FanProfiles.DerivePresetFromCurves(store, f);
            store.Update(s => s.Cooling.ActivePreset = derived);
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        // Profiles
        app.MapGet("/cooling/profiles", (IConfigStore store) =>
            new GetProfilesResponse
            {
                Profiles = FanProfiles.GetBuiltInProfiles(),
                Active = store.Load().Cooling.ActivePreset,
            }).AllowPanel();

        app.MapPost("/cooling/profile/{name}", (string name, IFanControlProvider f, Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            var profile = FanProfiles.GetBuiltInProfiles()
                .Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            // "auto" is accepted as a synonym for "off" via FanProfiles.Apply itself.
            var isAutoSynonym = string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase);
            if (profile is null && !isAutoSynonym)
            {
                return Results.BadRequest(new ApplyProfileResponse { Error = true, Msg = $"Unknown profile: {name}" });
            }
            var requested = isAutoSynonym ? "off" : profile!.Name;
            var applied = FanProfiles.Apply(requested, f, store);
            PanelTopics.BroadcastCooling(hub);
            telemetry.Capture(Nexus.Service.Telemetry.TelemetryEvents.FanCurveApplied, ("preset", requested));
            return Results.Ok(new ApplyProfileResponse { Applied = applied });
        }).AllowPanel();

        // Reset a Silent / Balanced / Performance preset curve to its default
        // type + Linear parameters. Fan attachments stay intact so the active
        // preset doesn't flip to "custom" as a side effect of the reset.
        app.MapPost("/cooling/profile/{name}/reset", (string name, IFanControlProvider f, IConfigStore store, MultiplexHub hub) =>
        {
            var canonical = (name ?? "").ToLowerInvariant();
            if (canonical != "silent" && canonical != "balanced" && canonical != "turbo")
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = $"Cannot reset preset: {name}" });
            }
            FanProfiles.ResetPresetCurve(canonical, f, store);
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        // Calibration - fire-and-forget, poll status
        app.MapPost("/cooling/calibrate", (StartCalibrationBody body, IFanControlProvider f, CalibrationRunner runner) =>
        {
            var started = runner.Start(f, body.FanIds);
            return new CalibrationStartResponse { SessionId = started ? "active" : "", Error = !started, Msg = started ? "Ok" : "Calibration already running" };
        });

        // The last run's results, not every calibration ever stored: a fan whose
        // chip has since stopped enumerating is not part of "calibration
        // complete", and the panel rendered those stale keys as raw LHM ids.
        // Each live fan's stored calibration ships on its channel in /cooling/fans.
        app.MapGet("/cooling/calibration/results", (CalibrationRunner runner) =>
            new GetCalibrationsResponse { Calibrations = runner.Results.ToList() }).AllowPanel();

        // Status summary
        app.MapGet("/cooling/status", (IFanControlProvider f, CalibrationRunner runner, Nexus.Service.Persistence.IConfigStore store) =>
        {
            var channels = f.GetFanChannels();
            var curves = store.Load().Cooling.Curves;
            return new CoolingStatusResponse
            {
                Calibrating = runner.State == CalibrationState.Running,
                CalibrationState = runner.State.ToString().ToLowerInvariant(),
                ActiveCurves = curves.Count,
                FanCount = channels.Count,
                ManualFans = channels.Count(c => c.Mode == Nexus.Service.Models.Cooling.FanModes.Manual),
                ActiveCurveFanCount = channels.Count(c => c.Mode == Nexus.Service.Models.Cooling.FanModes.Curve),
            };
        }).AllowPanel();
    }
}

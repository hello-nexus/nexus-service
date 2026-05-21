using System;
using System.Collections.Generic;
using System.Linq;
using Qos.Service.Auth;
using Qos.Service.Cooling;
using Qos.Service.Models;
using Qos.Service.Models.Cooling;
using Qos.Service.Persistence;
using Qos.Service.Sockets;

namespace Qos.Service.Routes;

public static class CoolingRoutes
{
    public static void MapCoolingEndpoints(this WebApplication app)
    {
        // Main cooling
        app.MapGet("/cooling/all", (ICoolingProvider c) =>
            new GetAllCoolingResponse { CoolingComponents = new(c.GetAll()) }).AllowPanel();

        // Curves
        app.MapGet("/cooling/curves", (Qos.Service.Persistence.IConfigStore store) =>
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
            c.SetCurves(body);
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
            var names = store.Load().Cooling.FanNames;
            foreach (var ch in channels)
            {
                if (names.TryGetValue(ch.Id, out var custom))
                {
                    ch.Name = custom;
                }
            }
            return new GetFanChannelsResponse { Channels = channels };
        }).AllowPanel();

        app.MapGet("/cooling/sources", (IFanControlProvider f) =>
            new GetTemperatureSourcesResponse { Sources = new(f.GetTemperatureSources()) }).AllowPanel();

        app.MapPost("/cooling/fan/{id}/speed", (string id, SetFanSpeedBody body, IFanControlProvider f, Qos.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
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
            return new SetFanSpeedResponse { ChannelId = id, Speed = actual, Mode = Qos.Service.Models.Cooling.FanModes.Manual };
        });

        app.MapPost("/cooling/fan/{id}/auto", (string id, IFanControlProvider f, Qos.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
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

        // Profiles
        app.MapGet("/cooling/profiles", (IConfigStore store) =>
            new GetProfilesResponse
            {
                Profiles = FanProfiles.GetBuiltInProfiles(),
                Active = store.Load().Cooling.ActivePreset,
            }).AllowPanel();

        app.MapPost("/cooling/profile/{name}", (string name, IFanControlProvider f, Qos.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
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

        // Calibration — fire-and-forget, poll status
        app.MapPost("/cooling/calibrate", (StartCalibrationBody body, IFanControlProvider f, CalibrationRunner runner) =>
        {
            var started = runner.Start(f, body.FanIds);
            return new CalibrationStartResponse { SessionId = started ? "active" : "", Error = !started, Msg = started ? "Ok" : "Calibration already running" };
        });

        app.MapGet("/cooling/calibrations", (Qos.Service.Persistence.IConfigStore store) =>
            new GetCalibrationsResponse { Calibrations = store.Load().Cooling.FanCalibrations.Values.ToList() }).AllowPanel();

        // Status summary
        app.MapGet("/cooling/status", (IFanControlProvider f, CalibrationRunner runner, Qos.Service.Persistence.IConfigStore store) =>
        {
            var channels = f.GetFanChannels();
            var curves = store.Load().Cooling.Curves;
            return new CoolingStatusResponse
            {
                Calibrating = runner.State == CalibrationState.Running,
                CalibrationState = runner.State.ToString().ToLowerInvariant(),
                ActiveCurves = curves.Count,
                FanCount = channels.Count,
                ManualFans = channels.Count(c => c.Mode == Qos.Service.Models.Cooling.FanModes.Manual),
                ActiveCurveFanCount = channels.Count(c => c.Mode == Qos.Service.Models.Cooling.FanModes.Curve),
            };
        }).AllowPanel();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class CoolingRoutes
{
    // Rail block id the cooling page renames the board's own fan headers under;
    // kept in step with nexus-web's page/deviceGroupName.ts.
    private const string MotherboardBlockId = "motherboard";

    public static void MapCoolingEndpoints(this WebApplication app)
    {
        // Curves
        app.MapGet("/cooling/curves", (Nexus.Service.Persistence.IConfigStore store) =>
        {
            var settings = store.Load();
            var docs = settings.Cooling.Curves;
            var curves = docs.Select(d =>
            {
                var wire = CurveWireMapper.ToWire(d);
                wire.IsDefault = d.Preset is null ? null : FanProfiles.IsPresetCurveAtDefaults(d);
                return wire;
            }).ToList();
            return new GetCurvesResponse
            {
                GlobalSpeedModifier = settings.Cooling.GlobalSpeedModifier,
                Curves = curves,
            };
        }).AllowPanel();

        app.MapPost("/cooling/curves/set", (SetCurvesBody body, ICurveProvider c, IFanControlProvider f, IConfigStore store, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
            // A Mixed curve reading its own output (directly or through a Sync
            // hop) has no defined value and would chase itself every tick.
            var cyclic = CurveOrdering.FindCycleMembers(body.Curves.ConvertAll(CurveWireMapper.ToDocument));
            if (cyclic.Count > 0)
            {
                return Results.Ok(ApiResponse.Fail($"Curve dependency cycle: {string.Join(", ", cyclic)}"));
            }

            // Clamp every stored speed to [0,100] (and the global boost) so a
            // malformed curve can't persist out-of-range values; the write
            // chokepoint clamps the physical output independently.
            c.SetCurves(CoolingSafety.Sanitize(body));
            // Recompute the active preset from the saved curve outputs so the
            // profile bar stays in sync after a manual edit in the curve list.
            var derived = FanProfiles.DerivePresetFromCurves(store, f);
            store.Update(s => s.Cooling.ActivePreset = derived);
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
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
                    ch.OriginalName = ch.Name;
                    ch.Name = custom;
                }
                // A hub group header has no channel of its own, so its rename is
                // stored under the device id and surfaces as the group name every
                // channel on that device carries.
                if (ch.DeviceId is { } devId && names.TryGetValue(devId, out var deviceCustom))
                {
                    ch.OriginalDeviceName = ch.DeviceName;
                    ch.DeviceName = deviceCustom;
                }
                // The board's headers carry no device, so the page groups them under
                // a synthetic block it names from system specs; a rename of that
                // block is stored under the block id and has nothing to fall back to.
                else if (ch.DeviceId is null && names.TryGetValue(MotherboardBlockId, out var blockCustom))
                {
                    ch.DeviceName = blockCustom;
                }
                ch.Locked = FanProfiles.IsLocked(ch, cooling.FanLockOverrides);
                ch.Controlled = cooling.UncontrolledFanChannels.Count == 0
                    || !cooling.UncontrolledFanChannels.Contains(ch.Id);
                // An explicit user assignment wins; otherwise a GPU fan reports its
                // role from the hardware. Derived per read, never persisted, so
                // re-detection on new hardware just re-derives it.
                ch.Role = cooling.FanRoles.TryGetValue(ch.Id, out var role) ? role
                    : ch.IsGpu ? FanRoleKind.Gpu
                    : FanRoleKind.None;
                ch.Offset = cooling.FanOffsets.TryGetValue(ch.Id, out var offset) ? offset : 0;
                ch.SeriesId = Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId(ch.Id);
            }
            return new GetFanChannelsResponse { Channels = channels, Groups = cooling.FanGroups };
        }).AllowPanel();

        // Whole-list replace, mirroring /devices/lighting-devices/groups: the
        // page owns group order and membership.
        app.MapPut("/cooling/fan-groups", (
            Nexus.Service.Models.Devices.SetDeviceGroupsBody body,
            IConfigStore store,
            MultiplexHub hub) =>
        {
            var groups = Nexus.Service.Common.DeviceGroupList.Sanitize(body.Groups);
            store.Update(s => s.Cooling.FanGroups = groups);
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(new Nexus.Service.Models.Devices.SetDeviceGroupsBody { Groups = groups });
        });

        app.MapGet("/cooling/sources", (IFanControlProvider f) =>
            new GetTemperatureSourcesResponse { Sources = new(f.GetTemperatureSources()) }).AllowPanel();

        app.MapPost("/cooling/fan/{id}/speed", (string id, SetFanSpeedBody body, IFanControlProvider f, Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub, Nexus.Service.Telemetry.ITelemetry telemetry, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
            id = Uri.UnescapeDataString(id);
            // A channel the user marked not controlled takes no write, so
            // recording a Manual intent for it would be a lie the UI reads back
            // as a mode - and CurveEngine would replay that duty the moment
            // control came back. Report what is actually true instead.
            if (!FanControlledState.IsControlled(id, store.Load()))
            {
                return Results.Ok(new SetFanSpeedResponse { ChannelId = id, Speed = 0, Mode = Nexus.Service.Models.Cooling.FanModes.Auto });
            }
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
            return Results.Ok(new SetFanSpeedResponse { ChannelId = id, Speed = actual, Mode = Nexus.Service.Models.Cooling.FanModes.Manual });
        });

        app.MapPost("/cooling/fan/{id}/auto", (string id, IFanControlProvider f, Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
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
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/cooling/fan/{id}/controlled", (string id, SetFanControlledBody body, IFanControlProvider f, Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
            id = Uri.UnescapeDataString(id);
            // Same guard as /lock: nothing prunes this list, so an id that was
            // never a channel would sit in it with no UI affordance to clear it.
            if (!f.GetFanChannels().Any(c => c.Id == id))
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = "Unknown fan channel" });
            }
            // Turning control off is the BIOS release plus a persisted marker:
            // without the release the fan would hold its last duty forever, and
            // without the marker the next preset apply would reclaim it. Order
            // matters - the flag goes on last so the release itself is not
            // swallowed by the write gate it installs.
            if (!body.Controlled)
            {
                FanProfiles.DetachFanFromCurves(id, store);
                f.ReleaseFan(id);
                store.Update(s => s.Cooling.ManualSpeeds.Remove(id));
            }
            FanControlledState.SetControlled(id, body.Controlled, store);
            var derived = FanProfiles.DerivePresetFromCurves(store, f);
            store.Update(s => s.Cooling.ActivePreset = derived);
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/cooling/fan/{id}/name", (string id, SetFanNameBody body, IConfigStore store, MultiplexHub hub) =>
        {
            // No existence check: the id is either a channel the list handed the
            // UI or the device id of one of its group headers, and re-enumerating
            // the hardware to validate a string write buys nothing.
            id = Uri.UnescapeDataString(id);
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

        app.MapPost("/cooling/fan/{id}/offset", (string id, SetFanOffsetBody body, IFanControlProvider f, IConfigStore store, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
            id = Uri.UnescapeDataString(id);
            if (!f.GetFanChannels().Any(c => c.Id == id))
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = "Unknown fan channel" });
            }

            var offset = Math.Clamp(body.Offset, -CoolingSafety.MaxDuty, CoolingSafety.MaxDuty);
            store.Update(s =>
            {
                // No entry rather than a zero, so the dictionary only holds
                // real deviations.
                if (offset == 0)
                {
                    s.Cooling.FanOffsets.Remove(id);
                }
                else
                {
                    s.Cooling.FanOffsets[id] = offset;
                }
            });
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        app.MapPost("/cooling/fan/{id}/role", (string id, SetFanRoleBody body, IFanControlProvider f, IConfigStore store, MultiplexHub hub) =>
        {
            id = Uri.UnescapeDataString(id);
            var ch = f.GetFanChannels().FirstOrDefault(c => c.Id == id);
            if (ch is null)
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = "Unknown fan channel" });
            }

            var role = body.Role?.ToLowerInvariant() ?? FanRoleKind.None;
            if (!FanRoleKind.Valid.Contains(role))
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = "Invalid fan role" });
            }

            FanProfiles.SetFanRole(ch, role, store);
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

        app.MapPost("/cooling/profile/{name}", (string name, IFanControlProvider f, Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub, Nexus.Service.Telemetry.ITelemetry telemetry, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
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

        // Reset a Silent / Balanced / Turbo / Max preset curve to its default
        // type + Linear parameters. Fan attachments stay intact so the active
        // preset doesn't flip to "custom" as a side effect of the reset.
        app.MapPost("/cooling/profile/{name}/reset", (string name, IFanControlProvider f, IConfigStore store, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
            var canonical = (name ?? "").ToLowerInvariant();
            if (canonical is not ("silent" or "balanced" or "turbo" or "max"))
            {
                return Results.BadRequest(new ApiResponse { Error = true, Msg = $"Cannot reset preset: {name}" });
            }
            FanProfiles.ResetPresetCurve(canonical, f, store);
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        // User-saved presets. Distinct from the built-in modes above: a preset
        // stores fan-to-curve assignments plus which mode was active, and
        // activating one replays that through FanProfiles.Apply.
        app.MapGet("/cooling/presets", (IConfigStore store) =>
        {
            var cooling = store.Load().Cooling;
            return new CoolingPresetsResponse
            {
                Presets = cooling.Presets.ConvertAll(ToPresetDto),
                ActiveId = cooling.ActivePresetId,
            };
        }).AllowPanel();

        app.MapPost("/cooling/presets", (CreateCoolingPresetBody body, IFanControlProvider f, IConfigStore store) =>
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length == 0)
            {
                return Results.BadRequest(new CreateCoolingPresetResponse { Error = true, Msg = "Preset name required" });
            }
            if (store.Load().Cooling.Presets.Count >= CoolingPresets.Cap)
            {
                return Results.BadRequest(new CreateCoolingPresetResponse { Error = true, Msg = $"Preset cap of {CoolingPresets.Cap} reached" });
            }
            var created = CoolingPresets.Capture(Guid.NewGuid().ToString("n"), name, store, f);
            store.Update(s =>
            {
                s.Cooling.Presets.Add(created);
                s.Cooling.ActivePresetId = created.Id;
            });
            return Results.Ok(new CreateCoolingPresetResponse { Preset = ToPresetDto(created), ActiveId = created.Id });
        }).AllowPanel();

        app.MapPut("/cooling/presets/active", (SetActiveCoolingPresetBody body, IConfigStore store) =>
        {
            store.Update(s => s.Cooling.ActivePresetId = body.Id);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        app.MapPut("/cooling/presets/{id}", (string id, UpdateCoolingPresetBody body, IFanControlProvider f, IConfigStore store) =>
        {
            if (store.Load().Cooling.Presets.Find(p => p.Id == id) is null)
            {
                return Results.NotFound(new ApiResponse { Error = true, Msg = "Preset not found" });
            }
            store.Update(s =>
            {
                var preset = s.Cooling.Presets.Find(p => p.Id == id);
                if (preset is null) return;
                var name = (body.Name ?? "").Trim();
                if (name.Length > 0) preset.Name = name;
                if (body.SaveCurrent) CoolingPresets.CaptureInto(preset, store, f);
            });
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        app.MapDelete("/cooling/presets/{id}", (string id, IConfigStore store) =>
        {
            string? activeId = null;
            store.Update(s =>
            {
                s.Cooling.Presets.RemoveAll(p => p.Id == id);
                if (s.Cooling.ActivePresetId == id) s.Cooling.ActivePresetId = null;
                activeId = s.Cooling.ActivePresetId;
            });
            return Results.Ok(new DeleteCoolingPresetResponse { ActiveId = activeId });
        }).AllowPanel();

        app.MapPost("/cooling/presets/{id}/activate", (string id, IFanControlProvider f, IConfigStore store, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
            if (!CoolingPresets.Activate(id, store, f))
            {
                return Results.NotFound(new ApiResponse { Error = true, Msg = "Preset not found" });
            }
            PanelTopics.BroadcastCooling(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        // Calibration - fire-and-forget, poll status
        app.MapPost("/cooling/calibrate", (StartCalibrationBody body, IFanControlProvider f, CalibrationRunner runner, FeatureGates gates) =>
        {
            if (!gates.Cooling)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Cooling });
            }
            var started = runner.Start(f, body.FanIds);
            return Results.Ok(new CalibrationStartResponse { SessionId = started ? "active" : "", Error = !started, Msg = started ? "Ok" : "Calibration already running" });
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

    private static CoolingPresetDto ToPresetDto(Nexus.Service.Persistence.CoolingPreset p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Mode = p.Mode,
    };
}

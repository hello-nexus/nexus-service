using System;
using System.Collections.Generic;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    // Mirrors a layouts map into the live engine frames. Devices absent from the
    // map fall back to provider-computed defaults (same behavior as DELETE /layouts).
    private static void MirrorLayoutsToEngine(
        Dictionary<string, Nexus.Service.Persistence.DeviceLayout> layouts,
        ILightingDeviceProvider lightingProvider,
        Nexus.Service.Lighting.Engine.LightingEngine engine)
    {
        var fresh = lightingProvider.GetAll();
        foreach (var frame in engine.Devices)
        {
            if (layouts.TryGetValue(frame.Id, out var layout))
            {
                frame.X = layout.X;
                frame.Y = layout.Y;
                frame.W = layout.W;
                frame.H = layout.H;
                frame.Rotation = layout.Rotation;
            }
            else
            {
                foreach (var freshDev in fresh.Devices)
                {
                    if (freshDev.Id == frame.Id)
                    {
                        frame.X = freshDev.CanvasX;
                        frame.Y = freshDev.CanvasY;
                        frame.W = freshDev.CanvasW;
                        frame.H = freshDev.CanvasH;
                        frame.Rotation = freshDev.CanvasRotation;
                        break;
                    }
                }
            }
        }
    }

    // LiveEngineSync covers every mode a profile switch can restore; a preset
    // can additionally carry Off or Game Sync, which it has no arm for.
    private static void EngageLook(
        Nexus.Service.Persistence.LightingPresetLook look,
        Nexus.Service.Persistence.IConfigStore store,
        Nexus.Service.Lighting.ILightingProvider lighting)
    {
        // Runs for every mode, including the Off / Game Sync arms below that
        // never reach LiveEngineSync, so a later switch to Mirror or Media picks
        // up this preset's filter rather than the outgoing one.
        Nexus.Service.Lifecycle.LiveEngineSync.ApplyPostProcess(store, lighting);

        var sync = (look.Sync ?? "").ToLowerInvariant();
        if (sync.Length == 0)
        {
            // Matches LiveEngineSync: only "none" means off, a blank mode is inert.
            return;
        }
        if (sync == "none")
        {
            lighting.StopAll();
            return;
        }
        if (sync == "gamesync")
        {
            lighting.StartGameSync();
            return;
        }
        Nexus.Service.Lifecycle.LiveEngineSync.ApplyLighting(store, lighting);
    }

    // Mirror the live per-device power + ignore state into the active preset so
    // a toggle from any surface lands in the preset the user is sitting on.
    private static void CaptureDeviceStateIntoActive(Nexus.Service.Persistence.IConfigStore store)
    {
        store.Update(s =>
        {
            var id = s.Lighting.ActiveLayoutPresetId;
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            var preset = s.Lighting.LayoutPresets.Find(p => p.Id == id);
            if (preset is null)
            {
                return;
            }
            // Only on an actual change: undo/redo replays every device's power
            // before switching preset, which would otherwise rewrite the
            // outgoing preset's state on the way past.
            if (!SameIds(preset.DisabledDevices, s.Devices.DisabledLightingDevices))
            {
                preset.DisabledDevices = new List<string>(s.Devices.DisabledLightingDevices);
            }
            if (!SameIds(preset.UncontrolledDevices, s.Devices.UncontrolledLightingDevices))
            {
                preset.UncontrolledDevices = new List<string>(s.Devices.UncontrolledLightingDevices);
            }
            // Assignments and scales have no cheap equality, and unlike the id
            // lists above they are not replayed on undo/redo, so a straight
            // copy cannot rewrite the outgoing preset on the way past.
            preset.StaticDeviceLooks = DeepCopyStaticLooks(s.Lighting.StaticDeviceLooks);
            preset.DevicePrefs = DeepCopyDevicePrefs(s.Devices.LightingDevicePrefs);
        });
    }

    private static bool SameIds(List<string>? stored, List<string> live)
    {
        if (stored is null || stored.Count != live.Count)
        {
            return false;
        }
        for (var i = 0; i < live.Count; i++)
        {
            if (stored[i] != live[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Copies the preset list before reading it outside the store's
    /// update lock, which another thread can be inside.</summary>
    private static List<Nexus.Service.Persistence.LayoutPreset> SnapshotPresets(
        Nexus.Service.Persistence.NexusSettings settings)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new List<Nexus.Service.Persistence.LayoutPreset>(settings.Lighting.LayoutPresets);
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
            }
        }
    }

    // A pick off the running-apps list already carries the process name; a
    // Start-menu pick carries a display name the focus signal never reports.
    private static string ResolveBindingProcessName(string appId, Nexus.Service.Activity.IShortcutsProvider shortcuts)
    {
        const string RunningPrefix = "proc:";
        if (appId.StartsWith(RunningPrefix, StringComparison.Ordinal))
        {
            return Nexus.Service.Lighting.AppPresetMatching.ProcessKey(appId[RunningPrefix.Length..]);
        }
        try
        {
            return Nexus.Service.Lighting.AppPresetMatching.ProcessKey(shortcuts.ResolveProcessName(appId));
        }
        catch
        {
            return "";
        }
    }

    private static LayoutPresetDto ToDto(Nexus.Service.Persistence.LayoutPreset p) =>
        new()
        {
            Id = p.Id,
            Name = p.Name,
            Layouts = p.Layouts,
            Apps = p.Apps is null
                ? new List<PresetAppDto>()
                : p.Apps.ConvertAll(a => new PresetAppDto
                {
                    Id = a.Id,
                    Name = a.Name,
                    ProcessName = a.ProcessName,
                }),
        };

    private static Dictionary<string, Nexus.Service.Persistence.StaticDeviceLook> DeepCopyStaticLooks(
        Dictionary<string, Nexus.Service.Persistence.StaticDeviceLook> source)
    {
        var copy = new Dictionary<string, Nexus.Service.Persistence.StaticDeviceLook>(source.Count);
        foreach (var kv in source)
        {
            if (kv.Value is null)
            {
                continue;
            }
            copy[kv.Key] = new Nexus.Service.Persistence.StaticDeviceLook
            {
                Effect = kv.Value.Effect,
                Color = kv.Value.Color,
                Intensity = kv.Value.Intensity,
                Hue = kv.Value.Hue,
                Colorize = kv.Value.Colorize,
                Saturation = kv.Value.Saturation,
                Contrast = kv.Value.Contrast,
                Params = kv.Value.Params is null
                    ? new Dictionary<string, float>()
                    : new Dictionary<string, float>(kv.Value.Params),
                Slot = kv.Value.Slot,
            };
        }
        return copy;
    }

    // Every per-device slice a preset carries beyond its layouts, captured
    // together so create / save-current / the live mirror cannot drift apart.
    private static void CaptureDeviceSlices(
        Nexus.Service.Persistence.NexusSettings settings,
        Nexus.Service.Persistence.LayoutPreset preset,
        bool includeLooks = true)
    {
        preset.DisabledDevices = new List<string>(settings.Devices.DisabledLightingDevices);
        preset.UncontrolledDevices = new List<string>(settings.Devices.UncontrolledLightingDevices);
        if (!includeLooks)
        {
            return;
        }
        preset.StaticDeviceLooks = DeepCopyStaticLooks(settings.Lighting.StaticDeviceLooks);
        preset.DevicePrefs = DeepCopyDevicePrefs(settings.Devices.LightingDevicePrefs);
    }

    private static Dictionary<string, Nexus.Service.Persistence.LightingDevicePreference> DeepCopyDevicePrefs(
        Dictionary<string, Nexus.Service.Persistence.LightingDevicePreference> source)
    {
        var copy = new Dictionary<string, Nexus.Service.Persistence.LightingDevicePreference>(source.Count);
        foreach (var kv in source)
        {
            if (kv.Value is null)
            {
                continue;
            }
            copy[kv.Key] = new Nexus.Service.Persistence.LightingDevicePreference
            {
                Brightness = kv.Value.Brightness,
                Hue = kv.Value.Hue,
                Saturation = kv.Value.Saturation,
                AdjustRed = kv.Value.AdjustRed,
                AdjustGreen = kv.Value.AdjustGreen,
                AdjustBlue = kv.Value.AdjustBlue,
                AdjustTemperature = kv.Value.AdjustTemperature,
                AdjustSaturation = kv.Value.AdjustSaturation,
            };
        }
        return copy;
    }

    private static Dictionary<string, Nexus.Service.Persistence.DeviceLayout> DeepCopyLayouts(
        Dictionary<string, Nexus.Service.Persistence.DeviceLayout> source)
    {
        var copy = new Dictionary<string, Nexus.Service.Persistence.DeviceLayout>(source.Count);
        foreach (var kv in source)
        {
            copy[kv.Key] = new Nexus.Service.Persistence.DeviceLayout
            {
                X = kv.Value.X,
                Y = kv.Value.Y,
                W = kv.Value.W,
                H = kv.Value.H,
                Rotation = kv.Value.Rotation,
            };
        }
        return copy;
    }

    private static void MapLightingDevicesEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lighting-devices/all", (ILightingDeviceProvider ld) =>
            ld.GetAll()).AllowPanel();

        app.MapPost("/devices/lighting-devices/layout", (SaveDeviceLayoutBody body, Nexus.Service.Persistence.IConfigStore store, Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            store.Update(s =>
            {
                s.Lighting.DeviceLayouts[body.Id] = new Nexus.Service.Persistence.DeviceLayout
                { X = body.X, Y = body.Y, W = body.W, H = body.H, Rotation = body.Rotation };
            });
            foreach (var dev in engine.Devices)
            {
                if (dev.Id == body.Id)
                { dev.X = body.X; dev.Y = body.Y; dev.W = body.W; dev.H = body.H; dev.Rotation = body.Rotation; break; }
            }
            return ApiResponse.Ok();
        });

        // Reset every device frame's persisted layout back to provider-computed
        // defaults. Does NOT clear ActiveLayoutPresetId so the toolbar keeps
        // showing the selected preset name after a reset.
        app.MapDelete("/devices/lighting-devices/layouts", (
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub hub,
            ILightingDeviceProvider lightingProvider,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            store.Update(s =>
            {
                s.Lighting.DeviceLayouts.Clear();
            });
            MirrorLayoutsToEngine(new Dictionary<string, Nexus.Service.Persistence.DeviceLayout>(), lightingProvider, engine);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // Batch-replace DeviceLayouts and mirror to engine. Used by undo/redo and preset load.
        app.MapPost("/devices/lighting-devices/layouts", (
            BatchApplyLayoutsBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub hub,
            ILightingDeviceProvider lightingProvider,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            store.Update(s =>
            {
                s.Lighting.DeviceLayouts.Clear();
                foreach (var kv in body.Layouts)
                {
                    s.Lighting.DeviceLayouts[kv.Key] = kv.Value;
                }
            });
            MirrorLayoutsToEngine(body.Layouts, lightingProvider, engine);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        app.MapGet("/devices/lighting-devices/static-looks", (
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            Dictionary<string, Nexus.Service.Persistence.StaticDeviceLook> looks = null!;
            // Snapshot under the store lock: StaticDeviceEffectTracker mutates
            // this very dictionary in place, so enumerating it on the request
            // thread throws mid-iteration.
            store.Update(s => looks = DeepCopyStaticLooks(s.Lighting.StaticDeviceLooks));
            var dto = new StaticDeviceLooksResponse();
            foreach (var (id, look) in looks)
            {
                if (look is null || string.IsNullOrEmpty(look.Effect))
                {
                    continue;
                }
                dto.Looks[id] = new StaticDeviceLookDto
                {
                    Effect = look.Effect,
                    Color = look.Color ?? "",
                    Intensity = look.Intensity,
                    Hue = look.Hue,
                    Colorize = look.Colorize,
                    Saturation = look.Saturation,
                    Contrast = look.Contrast,
                    Slot = look.Slot,
                    Params = look.Params is null
                        ? new Dictionary<string, float>()
                        : new Dictionary<string, float>(look.Params),
                };
            }
            return Results.Json(
                dto,
                Nexus.Service.Serialization.AppJsonContext.Default.StaticDeviceLooksResponse);
        }).AllowPanel();

        app.MapGet("/devices/lighting-devices/layout-presets", (
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var s = store.Load();
            return Results.Json(
                new LayoutPresetsResponse
                {
                    Presets = s.Lighting.LayoutPresets.ConvertAll(ToDto),
                    ActiveId = s.Lighting.ActiveLayoutPresetId,
                },
                Nexus.Service.Serialization.AppJsonContext.Default.LayoutPresetsResponse);
        }).AllowPanel();

        app.MapPost("/devices/lighting-devices/layout-presets", (
            CreateLayoutPresetBody body,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            bool capped = false;
            Nexus.Service.Persistence.LayoutPreset? created = null;
            store.Update(s =>
            {
                if (s.Lighting.LayoutPresets.Count >= 10)
                {
                    capped = true;
                    return;
                }
                var id = Guid.NewGuid().ToString("n");
                created = new Nexus.Service.Persistence.LayoutPreset
                {
                    Id = id,
                    Name = body.Name,
                    Layouts = DeepCopyLayouts(s.Lighting.DeviceLayouts),
                    Look = Nexus.Service.Lighting.LightingPresetLooks.Capture(s.Lighting),
                };
                CaptureDeviceSlices(s, created);
                s.Lighting.LayoutPresets.Add(created);
                s.Lighting.ActiveLayoutPresetId = id;
            });
            if (capped)
            {
                return Results.Json(
                    ApiResponse.Fail("Layout preset cap of 10 reached"),
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 400);
            }
            return Results.Json(
                new CreateLayoutPresetResponse { Preset = ToDto(created!), ActiveId = created!.Id },
                Nexus.Service.Serialization.AppJsonContext.Default.CreateLayoutPresetResponse);
        });

        app.MapPut("/devices/lighting-devices/layout-presets/active", (
            SetActivePresetBody body,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            store.Update(s =>
            {
                s.Lighting.ActiveLayoutPresetId = body.Id;
            });
            return ApiResponse.Ok();
        });

        app.MapPut("/devices/lighting-devices/layout-presets/{id}", (
            string id,
            UpdateLayoutPresetBody body,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var s = store.Load();
            var preset = s.Lighting.LayoutPresets.Find(p => p.Id == id);
            if (preset is null)
            {
                return Results.Json(
                    ApiResponse.Fail("Layout preset not found"),
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 404);
            }

            store.Update(settings =>
            {
                var p = settings.Lighting.LayoutPresets.Find(x => x.Id == id);
                if (p is null)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(body.Name))
                {
                    p.Name = body.Name;
                }
                if (body.SaveCurrent)
                {
                    p.Layouts = DeepCopyLayouts(settings.Lighting.DeviceLayouts);
                    p.Look = Nexus.Service.Lighting.LightingPresetLooks.Capture(settings.Lighting);
                    CaptureDeviceSlices(settings, p, body.SaveDeviceLooks);
                }
            });
            return Results.Json(ApiResponse.Ok(), Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse);
        });

        // Apps that auto-activate this preset when they take focus. An app
        // drives exactly one preset, so assigning it here unbinds it elsewhere.
        app.MapPut("/devices/lighting-devices/layout-presets/{id}/apps", (
            string id,
            SetPresetAppsBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Activity.IShortcutsProvider shortcuts) =>
        {
            // Resolution is a helper round trip on Windows; run it before
            // taking the store's update lock.
            // Previously resolved names, to fall back on. Resolution can fail
            // transiently (a helper RPC timeout) and the Windows provider caches
            // an empty result, so a re-save must not downgrade a good name.
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in SnapshotPresets(store.Load()))
            {
                if (preset.Apps is null) continue;
                foreach (var b in preset.Apps)
                {
                    if (b.ProcessName.Length > 0) known[b.Id] = b.ProcessName;
                }
            }

            var resolved = new List<Nexus.Service.Persistence.PresetAppBinding>();
            foreach (var app in body.Apps)
            {
                if (string.IsNullOrWhiteSpace(app.Id)
                    || resolved.Exists(r => string.Equals(r.Id, app.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var processName = ResolveBindingProcessName(app.Id, shortcuts);
                if (processName.Length == 0 && known.TryGetValue(app.Id, out var previous))
                {
                    processName = previous;
                }
                resolved.Add(new Nexus.Service.Persistence.PresetAppBinding
                {
                    Id = app.Id,
                    Name = app.Name,
                    ProcessName = processName,
                });
            }

            var existing = SnapshotPresets(store.Load());
            if (existing.Find(p => p.Id == id) is null)
            {
                return Results.Json(
                    ApiResponse.Fail("Layout preset not found"),
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 404);
            }

            // An app triggers exactly one preset, so a request that would take
            // one off another preset is refused rather than silently moved -
            // the client turns this into a message naming the owning preset.
            // Matched on the resolved process name as well as the id: the same
            // app can be picked off the running list (proc:<name>) or the
            // installed list (a shortcut id).
            foreach (var other in existing)
            {
                if (other.Id == id || other.Apps is null)
                {
                    continue;
                }
                foreach (var taken in other.Apps)
                {
                    var clash = resolved.Find(r =>
                        string.Equals(r.Id, taken.Id, StringComparison.OrdinalIgnoreCase)
                        || (r.ProcessName.Length > 0
                            && string.Equals(r.ProcessName, taken.ProcessName, StringComparison.Ordinal)));
                    if (clash is not null)
                    {
                        return Results.Json(
                            new PresetAppConflictResponse
                            {
                                Error = true,
                                Msg = "app_already_bound",
                                AppName = clash.Name,
                                PresetName = other.Name,
                            },
                            Nexus.Service.Serialization.AppJsonContext.Default.PresetAppConflictResponse,
                            statusCode: 409);
                    }
                }
            }

            store.Update(s =>
            {
                var preset = s.Lighting.LayoutPresets.Find(p => p.Id == id);
                if (preset is null)
                {
                    return;
                }
                preset.Apps = resolved;
            });
            return Results.Json(ApiResponse.Ok(), Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse);
        });

        app.MapDelete("/devices/lighting-devices/layout-presets/{id}", (
            string id,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            string? activeId = null;
            store.Update(s =>
            {
                s.Lighting.LayoutPresets.RemoveAll(p => p.Id == id);
                if (s.Lighting.ActiveLayoutPresetId == id)
                {
                    s.Lighting.ActiveLayoutPresetId = null;
                }
                activeId = s.Lighting.ActiveLayoutPresetId;
            });
            return Results.Json(
                new DeletePresetResponse { ActiveId = activeId },
                Nexus.Service.Serialization.AppJsonContext.Default.DeletePresetResponse);
        });

        // Panel-reachable (with the list above) so a Deck widget key bound to a
        // lighting preset fires from a panel too, matching what
        // /cooling/presets/{id}/activate already allows.
        app.MapPost("/devices/lighting-devices/layout-presets/{id}/activate", (
            string id,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub hub,
            ILightingDeviceProvider lightingProvider,
            Nexus.Service.Lighting.ILightingProvider lighting,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Lighting.Smart.SmartLightProvider smart,
            Nexus.Service.Lighting.Engine.LightingEngine engine,
            FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            if (!ActivateLayoutPreset(id, store, hub, lightingProvider, lighting, bridge, smart, engine))
            {
                return Results.Json(
                    ApiResponse.Fail("Layout preset not found"),
                    Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse,
                    statusCode: 404);
            }
            return Results.Json(ApiResponse.Ok(), Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse);
        }).AllowPanel();

        app.MapPost("/devices/lighting-devices/power", (
            SetLightingDevicePowerBody body,
            ILightingDeviceProvider ld,
            Nexus.Service.Persistence.IConfigStore store,
            FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            ld.SetPower(body.Id, body.On);
            CaptureDeviceStateIntoActive(store);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        // Uncontrolled ids are pure persisted state - no provider owns a "not
        // controlled" action, so this writes the shared store directly rather
        // than dispatching through ILightingDeviceProvider. The refresh nudge
        // runs in both directions: on re-enable RgbBridge reclaims direct mode
        // immediately rather than waiting for its poll cadence, and on disable
        // its reconcile pass excludes a fully un-controlled OpenRGB device
        // from the daemon's detection so vendor software can own it. Smart
        // lights additionally re-push their static color on re-enable: while
        // no effect runs nothing else re-pushes it (effect mode recovers on
        // its own writer tick once the id drops out of the uncontrolled list).
        app.MapPost("/devices/lighting-devices/controlled", (
            SetLightingDeviceControlledBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Lighting.Smart.SmartLightProvider smart,
            FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            Nexus.Service.Lighting.LightingControlledState.SetControlled(body.Id, body.Controlled, store);
            CaptureDeviceStateIntoActive(store);
            bridge?.RequestTopologyRefresh();
            if (body.Controlled)
            {
                smart.RestoreStatic(body.Id);
            }
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        app.MapPost("/devices/lighting-devices/brightness", (SetLightingDeviceBrightness body, ILightingDeviceProvider ld, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            ld.SetBrightness(body.Id, body.Brightness);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        // Colour tuning: per-device channel / temperature / saturation trims,
        // applied by the frame writers on the way to the hardware. Read as a
        // sparse map - only cards the user actually trimmed appear, so a fresh
        // install answers with an empty object.
        app.MapGet("/devices/lighting-devices/color-adjust", (
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var dto = new LightingColorAdjustResponse();
            // Deliberately not store.Update: that marks settings dirty and
            // fires OnChanged, so a read would schedule a settings.json flush
            // and a cloud profile re-upload. The preference dictionary is
            // mutated in place, so a concurrent insert can throw mid-iteration;
            // retry once and answer with what resolved, same guard shape the
            // frame writers use.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                dto.Adjustments.Clear();
                try
                {
                    foreach (var (id, pref) in store.Load().Devices.LightingDevicePrefs)
                    {
                        if (pref is null
                            || (pref.AdjustRed == 1f && pref.AdjustGreen == 1f && pref.AdjustBlue == 1f
                                && pref.AdjustTemperature == 0f && pref.AdjustSaturation == 1f))
                        {
                            continue;
                        }
                        dto.Adjustments[id] = new LightingColorAdjustDto
                        {
                            Red = pref.AdjustRed,
                            Green = pref.AdjustGreen,
                            Blue = pref.AdjustBlue,
                            Temperature = pref.AdjustTemperature,
                            Saturation = pref.AdjustSaturation,
                        };
                    }
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Fall through to the retry; a second failure answers with
                    // whatever the partial pass collected.
                }
            }
            return Results.Json(
                dto,
                Nexus.Service.Serialization.AppJsonContext.Default.LightingColorAdjustResponse);
        }).AllowPanel();

        app.MapPost("/devices/lighting-devices/color-adjust", (
            SetLightingColorAdjustBody body,
            ILightingDeviceProvider ld,
            Nexus.Service.Persistence.IConfigStore store,
            FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            // Clamped here as well as in DeviceColorAdjust: the stored value is
            // what the UI reads back, so a client sending an out-of-range trim
            // must not come back as one.
            static float? Channel(float? v) => v is null
                ? null
                : Math.Clamp(v.Value, Nexus.Service.Lighting.DeviceColorAdjust.MinChannel, Nexus.Service.Lighting.DeviceColorAdjust.MaxChannel);
            var red = Channel(body.Red);
            var green = Channel(body.Green);
            var blue = Channel(body.Blue);
            var temperature = body.Temperature is null ? (float?)null : Math.Clamp(body.Temperature.Value, -1f, 1f);
            var saturation = body.Saturation is null
                ? (float?)null
                : Math.Clamp(body.Saturation.Value, Nexus.Service.Lighting.DeviceColorAdjust.MinSaturation, Nexus.Service.Lighting.DeviceColorAdjust.MaxSaturation);
            store.Update(s =>
            {
                foreach (var id in body.Ids)
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }
                    if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref) || pref is null)
                    {
                        pref = new Nexus.Service.Persistence.LightingDevicePreference();
                        s.Devices.LightingDevicePrefs[id] = pref;
                    }
                    // Absent means "leave alone", not "reset": one slider drag
                    // must not flatten the other four across a mixed scope.
                    if (red is not null) pref.AdjustRed = red.Value;
                    if (green is not null) pref.AdjustGreen = green.Value;
                    if (blue is not null) pref.AdjustBlue = blue.Value;
                    if (temperature is not null) pref.AdjustTemperature = temperature.Value;
                    if (saturation is not null) pref.AdjustSaturation = saturation.Value;
                }
            });
            // Brightness keeps going through the provider, which is what the
            // dedicated brightness route does; carrying it here only saves the
            // client one HTTP call per device per drag.
            if (body.Brightness is not null)
            {
                foreach (var id in body.Ids)
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        ld.SetBrightness(id, body.Brightness.Value);
                    }
                }
            }
            CaptureDeviceStateIntoActive(store);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        app.MapPost("/devices/lighting-devices/color", (
            SetLightingDeviceColor body,
            ILightingDeviceProvider ld,
            Nexus.Service.Persistence.IConfigStore store,
            // [FromServices] is load-bearing under AOT: the request-delegate
            // generator reads a concrete class parameter as a second body
            // parameter and the route 400s on binding.
            [Microsoft.AspNetCore.Mvc.FromServices] Nexus.Service.Lighting.StaticDeviceEffectTracker staticEffects,
            FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            ld.SetHue(body.Id, body.Hue);
            ld.SetSaturation(body.Id, body.Saturation);
            // The prefs above are device metadata the UI reads back. The
            // assignment below is what actually reaches the LEDs: a whole look
            // (effect + tint + params), so patterns and the tint controls apply
            // instead of being flattened to one swatch.
            if (string.IsNullOrEmpty(body.Effect))
            {
                staticEffects.Clear(body.Id);
            }
            else
            {
                staticEffects.Set(body.Id, new Nexus.Service.Lighting.StaticDeviceAssignment
                {
                    Effect = body.Effect,
                    Color = body.Color ?? "",
                    Intensity = body.Intensity <= 0 ? 1f : body.Intensity,
                    Hue = body.Hue,
                    Colorize = body.Colorize,
                    Saturation = body.Saturation,
                    Contrast = body.Contrast <= 0 ? 1f : body.Contrast,
                    Params = body.Params,
                    Slot = body.Slot,
                });
            }
            CaptureDeviceStateIntoActive(store);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        // Motherboard ARGB zone LED count - persists and applies via OpenRGB RESIZEZONE.
        // Broadcasts `lighting` so the device list (and its cards' LED counts)
        // refetches without waiting for an unrelated mutation.
        app.MapPost("/devices/lighting-devices/zone-size", (SetZoneLedCountBody body, ILightingDeviceProvider ld, FeatureGates gates, Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            ld.SetZoneLedCount(body.Id, body.Count);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        });

        // Identify a strip / zone with a unique colour pulse
        app.MapPost("/devices/lighting-devices/identify", (IdentifyLightingDeviceBody body, ILightingDeviceProvider ld, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            ld.Identify(body.Id, body.DurationMs);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        // Force-rescan: restart OpenRGB subprocess (only for plugins that scan once at boot)
        // Broadcasts a `lighting` topic frame so the SPA's useRgbStatus hook
        // refetches /lighting/status immediately and observes `scanning=true`
        // without waiting for an unrelated mutation. The hook's poll-while-
        // scanning loop then tracks the rescan to completion on its own.
        app.MapPost("/devices/lighting-devices/rescan", (Nexus.Service.Lighting.Rgb.RgbBridge? bridge, Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            bridge?.ForceRescan();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // LED map: get resolved positions (defaults -> applied mapping ->
        // custom overrides) for ANY lighting device. Partition-backed cards
        // (keeb / OpenRGB zones) resolve through the zone topology; other
        // contributor cards (NP50, hubs, smart lights) resolve via their
        // engine frame seeded with provider defaults.
        app.MapGet("/devices/lighting-devices/{id}/led-map", (string id, bool? defaults,
            Nexus.Service.Lighting.Zones.ZoneTopology topology,
            Nexus.Service.Lighting.Mappings.MappingApplyService mappings,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var settings = store.Load();
            // defaults=true previews the factory layout: keep persisted LED
            // counts and the partition (they describe the hardware as wired
            // and the card shape) but drop mapping + override + group layers.
            var effective = defaults == true
                ? new Nexus.Service.Persistence.NexusSettings
                {
                    Devices = new Nexus.Service.Persistence.DevicesSettings
                    {
                        ZoneLedCounts = settings.Devices.ZoneLedCounts,
                        ZonePartitions = settings.Devices.ZonePartitions,
                        DeviceAspectRatios = settings.Devices.DeviceAspectRatios,
                    },
                }
                : settings;

            var resolution = topology.ResolveCard(id, effective);
            if (resolution is null)
                return Results.Json(new LedMapResponse { Id = id }, Nexus.Service.Serialization.AppJsonContext.Default.LedMapResponse);
            var resolved = resolution.Layout;
            var device = resolution.Device;
            var globalOffset = resolved.GlobalOffset;

            if (defaults != true)
            {
                topology.ApplyToEngine(id, resolution);
            }

            var leds = new List<LedMapEntry>(resolved.LedCount);
            for (int i = 0; i < resolved.LedCount; i++)
            {
                var globalIdx = globalOffset + i;
                leds.Add(new LedMapEntry
                {
                    Index = i,
                    U = i < resolved.U.Length ? resolved.U[i] : 0f,
                    V = i < resolved.V.Length ? resolved.V[i] : 0f,
                    Name = device is not null && globalIdx < device.LedNames.Count
                        ? device.LedNames[globalIdx]
                        : $"LED {i}",
                    ZoneType = i < resolved.ZoneTypes.Length ? resolved.ZoneTypes[i] : "unknown",
                    IsCustom = resolved.CustomLeds.Contains(i),
                    Disabled = resolved.Disabled is { } flags && i < flags.Length && flags[i],
                });
            }

            var card = mappings.FindCard(id);
            return Results.Json(new LedMapResponse
            {
                Id = id,
                LedCount = resolved.LedCount,
                Leds = leds,
                HasCustomOverrides = resolved.HasUserOverrides,
                AspectRatio = resolved.AspectRatio,
                Groups = resolved.Groups,
                Applied = resolved.Applied is { } applied
                    ? new AppliedMappingSummary
                    {
                        MappingId = applied.MappingId,
                        Name = applied.Name,
                        Source = applied.Source,
                        ContentHash = applied.ContentHash,
                        AutoApplied = applied.AutoApplied,
                        AppliedAtMs = applied.AppliedAt.ToUnixTimeMilliseconds(),
                    }
                    : null,
                DeviceKey = card?.DeviceKey ?? "",
            }, Nexus.Service.Serialization.AppJsonContext.Default.LedMapResponse);
        }).AllowPanel();

        // LED map: save custom overrides (+ optional group replacement). The
        // body carries zone-local indices; they re-key through the card's
        // slices into the device's segment-local store, replacing only the
        // entries this zone covers.
        app.MapPost("/devices/lighting-devices/{id}/led-map", (string id, SaveLedMapBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Zones.ZoneTopology topology) =>
        {
            var ctx = topology.ContextFor(id, store.Load());
            store.Update(s =>
            {
                var next = CollectOverridesOutsideZone(s, ctx);
                foreach (var o in body.Overrides)
                {
                    if (ctx.TryMapToSegment(o.LedIndex, out var segment, out var local))
                    {
                        next.Add(new Nexus.Service.Persistence.SegmentLedOverride
                        { Segment = segment, LedIndex = local, U = o.U, V = o.V, Disabled = o.Disabled });
                    }
                }
                s.Devices.DeviceLedOverrides[ctx.DeviceId] = next;
                if (body.AspectRatio > 0)
                    s.Devices.DeviceAspectRatios[ctx.DeviceId] = body.AspectRatio;
                if (body.Groups is not null)
                    s.Devices.LedGroups[id] = body.Groups;
            });
            topology.RefreshCardFrame(id);
            return ApiResponse.Ok();
        });

        // LED map: reset user deltas (overrides, groups) for this card's
        // zone. The canvas aspect ratio is device-wide and shared by sibling
        // zone cards, so it stays; the device-map DELETE owns device-level
        // reset. An applied community mapping survives; reverting that is
        // the mapping DELETE.
        app.MapDelete("/devices/lighting-devices/{id}/led-map", (string id,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Zones.ZoneTopology topology) =>
        {
            var ctx = topology.ContextFor(id, store.Load());
            store.Update(s =>
            {
                var remaining = CollectOverridesOutsideZone(s, ctx);
                if (remaining.Count > 0)
                    s.Devices.DeviceLedOverrides[ctx.DeviceId] = remaining;
                else
                    s.Devices.DeviceLedOverrides.Remove(ctx.DeviceId);
                s.Devices.LedGroups.Remove(id);
            });
            topology.RefreshCardFrame(id);
            return ApiResponse.Ok();
        });

        // LED map editor: highlight specific LEDs (white, rest dark)
        app.MapPost("/devices/lighting-devices/{id}/led-highlight", (string id, LedHighlightBody body,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.HighlightLeds = body.Indices.Count > 0 ? new HashSet<int>(body.Indices) : null;
                    break;
                }
            }
            return ApiResponse.Ok();
        });

        // LED map editor: directional test pattern
        app.MapPost("/devices/lighting-devices/{id}/led-test-pattern", (string id, LedTestPatternBody body,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.TestPattern = body.Pattern == "animation" ? null : body.Pattern;
                    frame.TestPatternStartMs = frame.TestPattern is not null ? Environment.TickCount64 : 0;
                    break;
                }
            }
            return ApiResponse.Ok();
        });

        // LED map editor: transient preview layout (draft LED count + positions, not persisted)
        app.MapPost("/devices/lighting-devices/{id}/led-preview-layout", (string id, LedPreviewLayoutBody body,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.PreviewLedCount = body.LedCount > 0 ? body.LedCount : null;
                    if (body.Leds.Count > 0)
                    {
                        var positions = new Nexus.Service.Lighting.Engine.PreviewLedPosition[body.Leds.Count];
                        for (int i = 0; i < body.Leds.Count; i++)
                        {
                            var src = body.Leds[i];
                            positions[i] = new Nexus.Service.Lighting.Engine.PreviewLedPosition
                            {
                                Index = src.Index,
                                U = src.U,
                                V = src.V,
                                Disabled = src.Disabled,
                            };
                        }
                        frame.PreviewLayout = positions;
                    }
                    else
                    {
                        frame.PreviewLayout = null;
                    }
                    break;
                }
            }
            return ApiResponse.Ok();
        });

        // LED map editor: clear all overlays
        app.MapDelete("/devices/lighting-devices/{id}/led-editor", (string id,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
        {
            foreach (var frame in engine.Devices)
            {
                if (frame.Id == id)
                {
                    frame.HighlightLeds = null;
                    frame.TestPattern = null;
                    frame.PreviewLedCount = null;
                    frame.PreviewLayout = null;
                    break;
                }
            }
            return ApiResponse.Ok();
        });
    }
}

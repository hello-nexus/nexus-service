using Nexus.Service.Devices;
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

    private static LayoutPresetDto ToDto(Nexus.Service.Persistence.LayoutPreset p) =>
        new() { Id = p.Id, Name = p.Name, Layouts = p.Layouts };

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
            ld.GetAll());

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
        });

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
                    DisabledDevices = new List<string>(s.Devices.DisabledLightingDevices),
                };
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
                    p.DisabledDevices = new List<string>(settings.Devices.DisabledLightingDevices);
                }
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

        app.MapPost("/devices/lighting-devices/layout-presets/{id}/activate", (
            string id,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub hub,
            ILightingDeviceProvider lightingProvider,
            Nexus.Service.Lighting.Engine.LightingEngine engine) =>
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

            var layouts = new Dictionary<string, Nexus.Service.Persistence.DeviceLayout>(preset.Layouts);
            var disabled = preset.DisabledDevices is null ? null : new List<string>(preset.DisabledDevices);
            store.Update(settings =>
            {
                settings.Lighting.DeviceLayouts.Clear();
                foreach (var kv in layouts)
                {
                    settings.Lighting.DeviceLayouts[kv.Key] = kv.Value;
                }
                settings.Lighting.ActiveLayoutPresetId = id;
                if (disabled is not null)
                {
                    settings.Devices.DisabledLightingDevices = new List<string>(disabled);
                }
            });
            MirrorLayoutsToEngine(layouts, lightingProvider, engine);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return Results.Json(ApiResponse.Ok(), Nexus.Service.Serialization.AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lighting-devices/power", (SetLightingDevicePowerBody body, ILightingDeviceProvider ld) =>
        {
            ld.SetPower(body.Id, body.On);
            return ApiResponse.Ok();
        });
        // Uncontrolled ids are pure persisted state - no provider owns a "not
        // controlled" action, so this writes the shared store directly rather
        // than dispatching through ILightingDeviceProvider. On re-enable,
        // nudge RgbBridge to reclaim direct mode immediately rather than
        // waiting for its poll cadence, and re-push a smart light's static
        // color: while no effect runs nothing else re-pushes it (effect mode
        // recovers on its own writer tick once the id drops out of the
        // uncontrolled list).
        app.MapPost("/devices/lighting-devices/controlled", (
            SetLightingDeviceControlledBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Lighting.Smart.SmartLightProvider smart) =>
        {
            Nexus.Service.Lighting.LightingControlledState.SetControlled(body.Id, body.Controlled, store);
            if (body.Controlled)
            {
                bridge?.RequestTopologyRefresh();
                smart.RestoreStatic(body.Id);
            }
            return ApiResponse.Ok();
        });
        app.MapPost("/devices/lighting-devices/brightness", (SetLightingDeviceBrightness body, ILightingDeviceProvider ld) =>
        {
            ld.SetBrightness(body.Id, body.Brightness);
            return ApiResponse.Ok();
        });
        app.MapPost("/devices/lighting-devices/color", (SetLightingDeviceColor body, ILightingDeviceProvider ld) =>
        {
            ld.SetHue(body.Id, body.Hue);
            ld.SetSaturation(body.Id, body.Saturation);
            return ApiResponse.Ok();
        });

        // Motherboard ARGB zone LED count - persists and applies via OpenRGB RESIZEZONE
        app.MapPost("/devices/lighting-devices/zone-size", (SetZoneLedCountBody body, ILightingDeviceProvider ld) =>
        {
            ld.SetZoneLedCount(body.Id, body.Count);
            return ApiResponse.Ok();
        });

        // Identify a strip / zone with a unique colour pulse
        app.MapPost("/devices/lighting-devices/identify", (IdentifyLightingDeviceBody body, ILightingDeviceProvider ld) =>
        {
            ld.Identify(body.Id, body.DurationMs);
            return ApiResponse.Ok();
        });

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
        });

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

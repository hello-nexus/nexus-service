using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapStrimerEndpoints(WebApplication app)
    {
        // GET /devices/strimer/lighting - current settings and mode catalog.
        app.MapGet("/devices/strimer/lighting", (IConfigStore store) =>
        {
            // Read under the store's lock: a PUT edits the target dictionary in place.
            StrimerLightingResponse? response = null;
            store.Read(s => response = BuildStrimerLightingResponse(s));
            return Results.Json(response!, AppJsonContext.Default.StrimerLightingResponse);
        });

        // PUT /devices/strimer/lighting - patch mode/speed/direction/brightness/colors.
        app.MapPut("/devices/strimer/lighting", (
            StrimerLightingRequest body,
            IConfigStore store,
            StrimerLightingDeviceProvider lighting,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            string? error = null;
            store.Read(s => error = ValidateStrimerPatch(
                s.Devices.StrimerLighting, StrimerProtocol.NormalizeGpuZoneCount(s.Devices.StrimerGpuZones), body));
            if (error != null) return Results.BadRequest(ApiResponse.Fail(error));
            string? target = body.Target is null or StrimerEffectLooks.AllTarget ? null : StrimerEffectLooks.NormalizeTarget(body.Target);
            store.Update(s =>
            {
                var ls = s.Devices.StrimerLighting;
                if (target != null)
                {
                    if (body.ResetTarget == true)
                    {
                        ls.Targets.Remove(target);
                        return;
                    }
                    if (!ls.Targets.TryGetValue(target, out var own))
                    {
                        own = StrimerEffectLooks.Seed(ls, target);
                        ls.Targets[target] = own;
                    }
                    if (body.Mode != null) own.Mode = body.Mode;
                    var ownMode = StrimerLightingModes.Find(own.Mode)!;
                    if (body.Speed.HasValue)      own.ModeSpeeds[ownMode.Key] = Math.Clamp(body.Speed.Value, 0, 4);
                    if (body.Direction.HasValue)  own.Direction  = Math.Clamp(body.Direction.Value, 0, 1);
                    if (body.Brightness.HasValue) own.Brightness = Math.Clamp(body.Brightness.Value, 0, 4);
                    if (body.Colors != null) StoreColors(own.ModeColors, ownMode, body.Colors, StrimerEffectLooks.Own(own).Colors);
                    return;
                }
                if (body.Mode != null)
                {
                    ls.Mode = body.Mode;
                    if (body.Mode != "custom") ls.EffectMode = body.Mode;
                }
                var sharedMode = StrimerEffectLooks.Shared(ls).Mode;
                if (body.Speed.HasValue)      ls.ModeSpeeds[sharedMode.Key] = Math.Clamp(body.Speed.Value, 0, 4);
                if (body.Direction.HasValue)  ls.Direction  = Math.Clamp(body.Direction.Value, 0, 1);
                if (body.Brightness.HasValue) ls.Brightness = Math.Clamp(body.Brightness.Value, 0, 4);
                if (body.Colors != null) StoreColors(ls.ModeColors, sharedMode, body.Colors, StrimerEffectLooks.Shared(ls).Colors);
                if (body.ArgbSync.HasValue) ls.ArgbSync = body.ArgbSync.Value;
            });
            if (body.ArgbSync.HasValue) lighting.OnHubStateUpdated();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public static partial class DevicesRoutes
{
    internal static StrimerLightingResponse BuildStrimerLightingResponse(NexusSettings s)
    {
        var ls      = s.Devices.StrimerLighting;
        var catalog = new StrimerModeInfoDto[StrimerLightingModes.Catalog.Length];
        for (var i = 0; i < StrimerLightingModes.Catalog.Length; i++)
        {
            var m = StrimerLightingModes.Catalog[i];
            catalog[i] = new StrimerModeInfoDto
            {
                Key          = m.Key,
                Label        = m.Label,
                HasSpeed     = m.HasSpeed,
                HasDirection = m.HasDirection,
                HasBrightness= m.HasBrightness,
                ColorsMin    = m.ColorsMin,
                ColorsMax    = m.ColorsMax,
                DefaultColors= m.DefaultColors,
                Complete     = m.Complete,
            };
        }
        var shared = StrimerEffectLooks.Shared(ls);
        var looks  = new List<StrimerLookDto>(ls.Targets.Count);
        foreach (var (key, own) in ls.Targets)
        {
            if (StrimerEffectLooks.NormalizeTarget(key) is not { } target) continue;
            var look = StrimerEffectLooks.Own(own);
            looks.Add(new StrimerLookDto
            {
                Target = target, Mode = look.Mode.Key, Speed = look.Speed, Direction = look.Direction,
                Brightness = look.Brightness, Colors = look.Colors,
            });
        }
        return new StrimerLightingResponse
        {
            Mode      = ls.Mode,
            EffectMode= ls.Mode != "custom" ? ls.Mode : ls.EffectMode ?? "rainbow",
            Speed     = shared.Speed,
            Direction = ls.Direction,
            Brightness= ls.Brightness,
            Colors    = shared.Colors,
            Looks     = looks.ToArray(),
            GpuZones  = StrimerProtocol.NormalizeGpuZoneCount(s.Devices.StrimerGpuZones),
            Modes     = catalog,
            ArgbSync  = ls.ArgbSync,
        };
    }

    /// <summary>The reason a patch is refused, or null when it can be applied.</summary>
    internal static string? ValidateStrimerPatch(StrimerLightingSettings ls, int gpuZones, StrimerLightingRequest body)
    {
        if (body.Mode != null && StrimerLightingModes.Find(body.Mode) == null) return "unknown mode key";
        if (body.Colors != null && Array.Exists(body.Colors, c => !StrimerLightingModes.IsHexColor(c))) return "colors must be #rrggbb";
        if (body.Target is null or StrimerEffectLooks.AllTarget) return null;

        var target = StrimerEffectLooks.NormalizeTarget(body.Target);
        if (target == null) return "unknown target";
        if (body.Mode == "custom") return "a cable or port cannot play the Lighting page mode";
        var isPort = target[0] == 'p';
        if (isPort && int.Parse(target.AsSpan(1)) >= StrimerProtocol.AtxZoneCount + gpuZones) return "that strip is not on this harness";
        if (body.ResetTarget == true || !isPort) return null;

        var effective = StrimerLightingModes.Find(body.Mode ?? StrimerEffectLooks.Resolve(ls, target).Mode.Key)!;
        return effective.Complete ? "a channel-wide effect runs on a cable, not a single port; pick a per-port effect" : null;
    }

    // Slots the request omits keep what the mode shows now.
    private static void StoreColors(Dictionary<string, List<string>> perMode, StrimerModeInfo mode, string[] sent, string[] current)
    {
        if (mode.ColorsMax == 0) return;
        var merged = new List<string>(mode.ColorsMax);
        for (var i = 0; i < mode.ColorsMax; i++)
        {
            merged.Add(i < sent.Length ? sent[i] : current[i]);
        }
        perMode[mode.Key] = merged;
    }
}

public sealed class StrimerLookDto
{
    public string   Target     { get; set; } = "";
    public string   Mode       { get; set; } = "";
    public int      Speed      { get; set; }
    public int      Direction  { get; set; }
    public int      Brightness { get; set; }
    public string[] Colors     { get; set; } = Array.Empty<string>();
}

public sealed class StrimerModeInfoDto
{
    public string Key          { get; set; } = "";
    public string Label        { get; set; } = "";
    public bool   HasSpeed     { get; set; }
    public bool   HasDirection { get; set; }
    public bool   HasBrightness{ get; set; }
    public int    ColorsMin    { get; set; }
    public int    ColorsMax    { get; set; }
    public string[] DefaultColors { get; set; } = Array.Empty<string>();
    /// <summary>The animation runs across a whole cable, so only a cable (not a single port) can play it.</summary>
    public bool   Complete     { get; set; }
}

public sealed class StrimerLightingResponse
{
    public string             Mode      { get; set; } = "";
    /// <summary>The mode the device returns to when Lighting page control is turned off.</summary>
    public string             EffectMode{ get; set; } = "";
    public int                Speed     { get; set; }
    public int                Direction { get; set; }
    public int                Brightness{ get; set; }
    public string[]           Colors    { get; set; } = Array.Empty<string>();
    public bool               ArgbSync  { get; set; }
    public StrimerModeInfoDto[] Modes   { get; set; } = Array.Empty<StrimerModeInfoDto>();
    /// <summary>Cables and ports playing their own effect; the rest follow the shared one above.</summary>
    public StrimerLookDto[]  Looks    { get; set; } = Array.Empty<StrimerLookDto>();
    /// <summary>Strips on the 8-pin cable: four on the dual harness, six on the triple.</summary>
    public int                GpuZones { get; set; }
}

public sealed class StrimerLightingRequest
{
    public string?  Mode      { get; set; }
    public int?     Speed     { get; set; }
    public int?     Direction { get; set; }
    public int?     Brightness{ get; set; }
    public string[]? Colors   { get; set; }
    public bool?    ArgbSync  { get; set; }
    /// <summary>"all" (or absent) for the shared effect, "ch0" / "ch1" for a cable, "p0".."p11" for one port.</summary>
    public string?  Target    { get; set; }
    /// <summary>Drops the target's own effect so it follows the shared one again.</summary>
    public bool?    ResetTarget { get; set; }
}

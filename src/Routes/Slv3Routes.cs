using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class Slv3MacRequest
{
    public string Mac { get; set; } = "";
}

public sealed class Slv3LightingModeDto
{
    public string Key { get; set; } = "";
    public bool HasSpeed { get; set; }
    public bool HasDirection { get; set; }
    public int ColorsMin { get; set; }
    public int ColorsMax { get; set; }
    public bool Mergeable { get; set; }
}

public sealed class Slv3LaneDto
{
    public string Mode { get; set; } = "";
    public int Direction { get; set; }
    public string Color { get; set; } = "";
}

public sealed class Slv3ChainLightingDto
{
    public string Mac { get; set; } = "";
    /// <summary>"strimer" or "fans".</summary>
    public string Kind { get; set; } = "";
    public int DevType { get; set; }
    /// <summary>Strimer model name; empty for a fan chain, which the UI names from <see cref="FanType"/>.</summary>
    public string Model { get; set; } = "";
    public int FanType { get; set; }
    public int FanCount { get; set; }
    public int Lanes { get; set; }
    public int LedsPerLane { get; set; }
    /// <summary>Animations this chain can play on its own, in display order; "custom" and "perLane" are not listed.</summary>
    public Slv3LightingModeDto[] Modes { get; set; } = Array.Empty<Slv3LightingModeDto>();
    public bool SupportsPerLane { get; set; }
    public string Mode { get; set; } = "";
    /// <summary>The mode the device returns to when Lighting page control is turned off.</summary>
    public string EffectMode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public bool Merge { get; set; }
    public Slv3LaneDto[] LaneSettings { get; set; } = Array.Empty<Slv3LaneDto>();
}

public sealed class Slv3LightingResponse
{
    public string[] LaneModes { get; set; } = Array.Empty<string>();
    public Slv3ChainLightingDto[] Chains { get; set; } = Array.Empty<Slv3ChainLightingDto>();
}

/// <summary>Patch for one chain; null fields keep their value.</summary>
public sealed class Slv3ChainLightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
    public bool? Merge { get; set; }
    public Slv3LaneDto[]? LaneSettings { get; set; }
}

/// <summary>
/// First-party Lian Li L-Wireless (SLV3) dongle routes: discovery,
/// bind/unbind/identify, chain reset.
/// </summary>
public static partial class Slv3Routes
{
    public static void MapSlv3Endpoints(this WebApplication app)
    {
        // GET /devices/lianli-wireless/state - link + fan list.
        app.MapGet("/devices/lianli-wireless/state", (Slv3Hub hub) =>
            Results.Json(hub.State, AppJsonContext.Default.Slv3State));

        // POST /devices/lianli-wireless/bind - request binding a discovered fan
        // to our master, into the first free slot. The connection worker's tick
        // drives the RF state machine until the device list confirms.
        app.MapPost("/devices/lianli-wireless/bind", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.Bind(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("invalid mac or no free slot"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/lianli-wireless/unbind - request releasing a fan from our master.
        app.MapPost("/devices/lianli-wireless/unbind", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.Unbind(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("invalid mac"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/lianli-wireless/identify - one-shot RF_Select flash.
        app.MapPost("/devices/lianli-wireless/identify", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.Identify(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("fan not found"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/lianli-wireless/reset-chain - soft-reboot a chain
        // controller stuck reporting header-only records (0 fans, no RPM).
        app.MapPost("/devices/lianli-wireless/reset-chain", (Slv3MacRequest body, Slv3Hub hub) =>
        {
            if (!hub.ResetChain(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("fan not found"), AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // GET /devices/lianli-wireless/lighting - each bound chain's animation catalog and lighting mode.
        app.MapGet("/devices/lianli-wireless/lighting", (Slv3Hub hub, IConfigStore store) =>
            Results.Json(BuildLightingResponse(hub, store.Load()), AppJsonContext.Default.Slv3LightingResponse));

        // PUT /devices/lianli-wireless/lighting/{mac} - patch one bound chain's lighting mode.
        app.MapPut("/devices/lianli-wireless/lighting/{mac}", (
            string mac, Slv3ChainLightingRequest body, Slv3Hub hub, IConfigStore store, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            if (!MacHex().IsMatch(mac))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid mac"));
            }
            var chain = FindLightingChain(hub, mac);
            if (chain is null)
            {
                return Results.NotFound(ApiResponse.Fail("chain not found"));
            }
            var error = ValidateLightingRequest(body, chain.Value.Modes, chain.Value.PerLane);
            if (error is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            var key = mac.ToUpperInvariant();
            // The frame writer reads these settings without a lock, so the
            // patched cable and the map holding it are built aside and swapped
            // in by reference.
            store.Update(s =>
            {
                var current = s.Devices.LianLiWireless.Chains;
                current.TryGetValue(key, out var old);
                old ??= new LianLiWirelessChainLighting();
                var lanes = old.Lanes;
                if (body.LaneSettings is not null)
                {
                    lanes = new List<LianLiWirelessLane>(body.LaneSettings.Length);
                    foreach (var lane in body.LaneSettings)
                    {
                        lanes.Add(new LianLiWirelessLane
                        {
                            Mode = lane.Mode,
                            Direction = Math.Clamp(lane.Direction, 0, 1),
                            Color = lane.Color,
                        });
                    }
                }
                var next = new Dictionary<string, LianLiWirelessChainLighting>(current)
                {
                    [key] = new LianLiWirelessChainLighting
                    {
                        Mode = body.Mode ?? old.Mode,
                        EffectMode = body.Mode is not null && body.Mode != LianLiWirelessChainLighting.ModeCustom ? body.Mode : old.EffectMode,
                        Speed = body.Speed.HasValue ? Math.Clamp(body.Speed.Value, 0, Slv3StrimerEffects.SpeedLevels - 1) : old.Speed,
                        Direction = body.Direction.HasValue ? Math.Clamp(body.Direction.Value, 0, 1) : old.Direction,
                        Brightness = body.Brightness.HasValue ? Math.Clamp(body.Brightness.Value, 0, 4) : old.Brightness,
                        Colors = body.Colors is not null ? new List<string>(body.Colors) : old.Colors,
                        Merge = body.Merge ?? old.Merge,
                        Lanes = lanes,
                    },
                };
                s.Devices.LianLiWireless.Chains = next;
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private const string DefaultEffect = "rainbow";
    private const int MaxChainColors = 6;
    private const int MaxStrimerLanes = 6;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^[0-9A-Fa-f]{12}$")]
    private static partial Regex MacHex();

    private readonly record struct LightingChain(Slv3FanInfo Fan, IReadOnlyList<Slv3StrimerEffectInfo> Modes, bool PerLane);

    // A bound chain that can take an uploaded animation: a Strimer with a
    // known geometry, or a fan chain of a known family.
    private static LightingChain? LightingChainOf(Slv3FanInfo fan)
    {
        if (!fan.BoundToUs)
        {
            return null;
        }
        if (Slv3Protocol.IsStrimerDevType((byte)fan.DevType))
        {
            return Slv3Protocol.StrimerGeometryFor((byte)fan.DevType).Lanes > 0
                ? new LightingChain(fan, Slv3StrimerEffects.Catalog, PerLane: true)
                : null;
        }
        // CL fans pair a center with an outer ring of a different length,
        // which the uniform two-ring chain layout does not describe.
        var family = Slv3Protocol.ClassifyFanFamily((byte)fan.FanType);
        var modes = family == Slv3FanFamily.Cl ? Array.Empty<Slv3StrimerEffectInfo>() : Slv3FanEffects.CatalogFor(family);
        return fan.FanCount > 0 && modes.Count > 0 ? new LightingChain(fan, modes, PerLane: false) : null;
    }

    private static LightingChain? FindLightingChain(Slv3Hub hub, string mac)
    {
        foreach (var fan in hub.State.Fans)
        {
            if (string.Equals(fan.Mac, mac, StringComparison.OrdinalIgnoreCase))
            {
                return LightingChainOf(fan);
            }
        }
        return null;
    }

    internal static string? ValidateLightingRequest(
        Slv3ChainLightingRequest body, IReadOnlyList<Slv3StrimerEffectInfo> modes, bool perLane)
    {
        if (body.Mode is not null
            && body.Mode != LianLiWirelessChainLighting.ModeCustom
            && !(perLane && body.Mode == LianLiWirelessChainLighting.ModePerLane)
            && !modes.Any(m => m.Key == body.Mode))
        {
            return "unknown mode";
        }
        if (body.Colors is not null)
        {
            if (body.Colors.Length > MaxChainColors)
            {
                return "too many colors";
            }
            foreach (var c in body.Colors)
            {
                if (!HexColor().IsMatch(c)) return "invalid color";
            }
        }
        if (body.LaneSettings is not null)
        {
            if (!perLane)
            {
                return "lanes not supported";
            }
            if (body.LaneSettings.Length > MaxStrimerLanes)
            {
                return "too many lanes";
            }
            foreach (var lane in body.LaneSettings)
            {
                if (!Slv3StrimerEffects.LaneEffectKeys.Contains(lane.Mode)) return "unknown lane mode";
                if (!HexColor().IsMatch(lane.Color)) return "invalid color";
            }
        }
        return null;
    }

    internal static Slv3LightingResponse BuildLightingResponse(Slv3Hub hub, NexusSettings settings)
    {
        var chains = new List<Slv3ChainLightingDto>();
        foreach (var fan in hub.State.Fans)
        {
            if (LightingChainOf(fan) is not { } chain)
            {
                continue;
            }
            var isStrimer = chain.PerLane;
            var (lanes, ledsPerLane) = isStrimer ? Slv3Protocol.StrimerGeometryFor((byte)fan.DevType) : (0, 0);
            var ls = settings.Devices.LianLiWireless.Chains.TryGetValue(fan.Mac.ToUpperInvariant(), out var stored)
                ? stored
                : new LianLiWirelessChainLighting();
            var modes = new Slv3LightingModeDto[chain.Modes.Count];
            for (var i = 0; i < modes.Length; i++)
            {
                var m = chain.Modes[i];
                modes[i] = new Slv3LightingModeDto
                {
                    Key = m.Key,
                    HasSpeed = m.HasSpeed,
                    HasDirection = m.HasDirection,
                    ColorsMin = m.ColorsMin,
                    ColorsMax = m.ColorsMax,
                    Mergeable = m.Mergeable,
                };
            }
            var laneDtos = new Slv3LaneDto[ls.Lanes.Count];
            for (var i = 0; i < ls.Lanes.Count; i++)
            {
                laneDtos[i] = new Slv3LaneDto { Mode = ls.Lanes[i].Mode, Direction = ls.Lanes[i].Direction, Color = ls.Lanes[i].Color };
            }
            chains.Add(new Slv3ChainLightingDto
            {
                Mac = fan.Mac,
                Kind = isStrimer ? "strimer" : "fans",
                DevType = fan.DevType,
                Model = isStrimer ? Nexus.Service.Lighting.Slv3LightingDeviceProvider.StrimerModelNameFor((byte)fan.DevType) : "",
                FanType = fan.FanType,
                FanCount = fan.FanCount,
                Lanes = lanes,
                LedsPerLane = ledsPerLane,
                Modes = modes,
                SupportsPerLane = isStrimer,
                Mode = ls.Mode,
                EffectMode = ls.Mode != LianLiWirelessChainLighting.ModeCustom ? ls.Mode : ls.EffectMode ?? DefaultEffect,
                Speed = ls.Speed,
                Direction = ls.Direction,
                Brightness = ls.Brightness,
                Colors = ls.Colors.ToArray(),
                Merge = ls.Merge,
                LaneSettings = laneDtos,
            });
        }

        var laneModes = new string[Slv3StrimerEffects.LaneEffectKeys.Count];
        for (var i = 0; i < laneModes.Length; i++)
        {
            laneModes[i] = Slv3StrimerEffects.LaneEffectKeys[i];
        }
        return new Slv3LightingResponse { LaneModes = laneModes, Chains = chains.ToArray() };
    }
}

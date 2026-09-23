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

public sealed class Slv3StrimerModeDto
{
    public string Key { get; set; } = "";
    public bool HasSpeed { get; set; }
    public bool HasDirection { get; set; }
    public int ColorsMin { get; set; }
    public int ColorsMax { get; set; }
}

public sealed class Slv3StrimerLaneDto
{
    public string Mode { get; set; } = "";
    public int Direction { get; set; }
    public string Color { get; set; } = "";
}

public sealed class Slv3StrimerDto
{
    public string Mac { get; set; } = "";
    public int DevType { get; set; }
    public string Model { get; set; } = "";
    public int Lanes { get; set; }
    public int LedsPerLane { get; set; }
    public string Mode { get; set; } = "";
    /// <summary>The mode the device returns to when Lighting page control is turned off.</summary>
    public string EffectMode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public Slv3StrimerLaneDto[] LaneSettings { get; set; } = Array.Empty<Slv3StrimerLaneDto>();
}

public sealed class Slv3StrimersResponse
{
    /// <summary>Animations the cable can play on its own, in display order; "custom" and "perLane" are not listed.</summary>
    public Slv3StrimerModeDto[] Modes { get; set; } = Array.Empty<Slv3StrimerModeDto>();
    public string[] LaneModes { get; set; } = Array.Empty<string>();
    public Slv3StrimerDto[] Strimers { get; set; } = Array.Empty<Slv3StrimerDto>();
}

/// <summary>Patch for one cable; null fields keep their value.</summary>
public sealed class Slv3StrimerLightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
    public Slv3StrimerLaneDto[]? LaneSettings { get; set; }
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

        // GET /devices/lianli-wireless/strimers - animation catalog + each bound Strimer's lighting mode.
        app.MapGet("/devices/lianli-wireless/strimers", (Slv3Hub hub, IConfigStore store) =>
            Results.Json(BuildStrimersResponse(hub, store.Load()), AppJsonContext.Default.Slv3StrimersResponse));

        // PUT /devices/lianli-wireless/strimers/{mac} - patch one cable's lighting mode.
        app.MapPut("/devices/lianli-wireless/strimers/{mac}", (
            string mac, Slv3StrimerLightingRequest body, IConfigStore store, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var error = ValidateStrimerRequest(mac, body);
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
                var current = s.Devices.LianLiWireless.Strimers;
                current.TryGetValue(key, out var old);
                old ??= new LianLiWirelessStrimerSettings();
                var lanes = old.Lanes;
                if (body.LaneSettings is not null)
                {
                    lanes = new List<LianLiWirelessStrimerLane>(body.LaneSettings.Length);
                    foreach (var lane in body.LaneSettings)
                    {
                        lanes.Add(new LianLiWirelessStrimerLane
                        {
                            Mode = lane.Mode,
                            Direction = Math.Clamp(lane.Direction, 0, 1),
                            Color = lane.Color,
                        });
                    }
                }
                var next = new Dictionary<string, LianLiWirelessStrimerSettings>(current)
                {
                    [key] = new LianLiWirelessStrimerSettings
                    {
                        Mode = body.Mode ?? old.Mode,
                        EffectMode = body.Mode is not null && body.Mode != LianLiWirelessStrimerSettings.ModeCustom ? body.Mode : old.EffectMode,
                        Speed = body.Speed.HasValue ? Math.Clamp(body.Speed.Value, 0, Slv3StrimerEffects.SpeedLevels - 1) : old.Speed,
                        Direction = body.Direction.HasValue ? Math.Clamp(body.Direction.Value, 0, 1) : old.Direction,
                        Brightness = body.Brightness.HasValue ? Math.Clamp(body.Brightness.Value, 0, 4) : old.Brightness,
                        Colors = body.Colors is not null ? new List<string>(body.Colors) : old.Colors,
                        Lanes = lanes,
                    },
                };
                s.Devices.LianLiWireless.Strimers = next;
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private const string DefaultStrimerEffect = "rainbow";
    private const int MaxStrimerColors = 6;
    private const int MaxStrimerLanes = 6;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^[0-9A-Fa-f]{12}$")]
    private static partial Regex MacHex();

    internal static string? ValidateStrimerRequest(string mac, Slv3StrimerLightingRequest body)
    {
        if (!MacHex().IsMatch(mac))
        {
            return "invalid mac";
        }
        if (body.Mode is not null
            && body.Mode != LianLiWirelessStrimerSettings.ModeCustom
            && body.Mode != LianLiWirelessStrimerSettings.ModePerLane
            && Slv3StrimerEffects.Find(body.Mode) is null)
        {
            return "unknown mode";
        }
        if (body.Colors is not null)
        {
            if (body.Colors.Length > MaxStrimerColors)
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

    internal static Slv3StrimersResponse BuildStrimersResponse(Slv3Hub hub, NexusSettings settings)
    {
        var catalog = Slv3StrimerEffects.Catalog;
        var modes = new Slv3StrimerModeDto[catalog.Count];
        for (var i = 0; i < catalog.Count; i++)
        {
            var m = catalog[i];
            modes[i] = new Slv3StrimerModeDto
            {
                Key = m.Key,
                HasSpeed = m.HasSpeed,
                HasDirection = m.HasDirection,
                ColorsMin = m.ColorsMin,
                ColorsMax = m.ColorsMax,
            };
        }

        var strimers = new List<Slv3StrimerDto>();
        foreach (var fan in hub.State.Fans)
        {
            var (lanes, ledsPerLane) = Slv3Protocol.StrimerGeometryFor((byte)fan.DevType);
            if (!fan.BoundToUs || lanes == 0)
            {
                continue;
            }
            var ls = settings.Devices.LianLiWireless.Strimers.TryGetValue(fan.Mac.ToUpperInvariant(), out var stored)
                ? stored
                : new LianLiWirelessStrimerSettings();
            var laneDtos = new Slv3StrimerLaneDto[ls.Lanes.Count];
            for (var i = 0; i < ls.Lanes.Count; i++)
            {
                laneDtos[i] = new Slv3StrimerLaneDto { Mode = ls.Lanes[i].Mode, Direction = ls.Lanes[i].Direction, Color = ls.Lanes[i].Color };
            }
            strimers.Add(new Slv3StrimerDto
            {
                Mac = fan.Mac,
                DevType = fan.DevType,
                Model = Nexus.Service.Lighting.Slv3LightingDeviceProvider.StrimerModelNameFor((byte)fan.DevType),
                Lanes = lanes,
                LedsPerLane = ledsPerLane,
                Mode = ls.Mode,
                EffectMode = ls.Mode != LianLiWirelessStrimerSettings.ModeCustom ? ls.Mode : ls.EffectMode ?? DefaultStrimerEffect,
                Speed = ls.Speed,
                Direction = ls.Direction,
                Brightness = ls.Brightness,
                Colors = ls.Colors.ToArray(),
                LaneSettings = laneDtos,
            });
        }

        var laneModes = new string[Slv3StrimerEffects.LaneEffectKeys.Count];
        for (var i = 0; i < laneModes.Length; i++)
        {
            laneModes[i] = Slv3StrimerEffects.LaneEffectKeys[i];
        }
        return new Slv3StrimersResponse { Modes = modes, LaneModes = laneModes, Strimers = strimers.ToArray() };
    }
}

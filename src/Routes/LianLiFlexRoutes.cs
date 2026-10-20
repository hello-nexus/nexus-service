using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class LianLiFlexFanDto
{
    public string Mac { get; set; } = "";
    public string MasterMac { get; set; } = "";
    public bool BoundToUs { get; set; }
    public int Channel { get; set; }
    public int Slot { get; set; }
    public int DevType { get; set; }
    public int FanType { get; set; }
    public int FanCount { get; set; }
    public int[] Rpm { get; set; } = Array.Empty<int>();
    public int[] Pwm { get; set; } = Array.Empty<int>();
    public int FirmwareVersion { get; set; }
    public bool Stale { get; set; }
    public bool PwmCableConnected { get; set; }
    public string EffectIndex { get; set; } = "";
    public string Family { get; set; } = "";
    public string ScreenSerial { get; set; } = "";
    public string LcdFirmware { get; set; } = "";
    public bool LcdRestarting { get; set; }
    /// <summary>The identify flash is running on this unit.</summary>
    public bool Identifying { get; set; }
    public bool ReceiverBound { get; set; }
    public int SortIndex { get; set; }
}

public sealed class LianLiFlexStateResponse
{
    public bool IsConnected { get; set; }
    public string LinkStatus { get; set; } = Slv3LinkStatus.None;
    public LianLiFlexFanDto[] Fans { get; set; } = Array.Empty<LianLiFlexFanDto>();
}

public sealed class LianLiFlexModeDto
{
    public string Key { get; set; } = "";
    public bool HasSpeed { get; set; }
    public bool HasDirection { get; set; }
    public int ColorsMin { get; set; }
    public int ColorsMax { get; set; }
    public bool Mergeable { get; set; }
    /// <summary>"#RRGGBB" palette the fan renders when no colors are set; empty when the effect takes none.</summary>
    public string[] DefaultColors { get; set; } = Array.Empty<string>();
}

public sealed class LianLiFlexZoneDto
{
    public LianLiFlexModeDto[] Modes { get; set; } = Array.Empty<LianLiFlexModeDto>();
    public string Id { get; set; } = "";
    public int LedCount { get; set; }
    public string Mode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
}

public sealed class LianLiFlexZonePatch
{
    public string Id { get; set; } = "";
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
}

public sealed class LianLiFlexChainLightingDto
{
    public string Mac { get; set; } = "";
    public string Kind { get; set; } = "fans";
    public int DevType { get; set; }
    public string Model { get; set; } = "";
    public int FanType { get; set; }
    public int FanCount { get; set; }
    public int Lanes { get; set; }
    public int LedsPerLane { get; set; }
    public LianLiFlexModeDto[] Modes { get; set; } = Array.Empty<LianLiFlexModeDto>();
    public bool SupportsPerLane { get; set; }
    public string Mode { get; set; } = "";
    public string EffectMode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public bool Merge { get; set; }
    public Slv3LaneDto[] LaneSettings { get; set; } = Array.Empty<Slv3LaneDto>();
    public bool? MotherboardArgb { get; set; }
    public bool ArgbCableConnected { get; set; }
    public bool PlayingMotherboardArgb { get; set; }
    public bool SupportsZones { get; set; } = true;
    public LianLiFlexZoneDto[] Zones { get; set; } = Array.Empty<LianLiFlexZoneDto>();
    public bool? UpDownSwap { get; set; }
    public int SortIndex { get; set; }
}

public sealed class LianLiFlexLightingResponse
{
    /// <summary>The unit whose look drives the merged effect (never a custom or per-zone unit); empty when none merges.</summary>
    public string MergeLeaderMac { get; set; } = "";
    public string[] LaneModes { get; set; } = Array.Empty<string>();
    public LianLiFlexChainLightingDto[] Chains { get; set; } = Array.Empty<LianLiFlexChainLightingDto>();
}

public sealed class LianLiFlexChainLightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
    public bool? Merge { get; set; }
    public LianLiFlexZonePatch[]? Zones { get; set; }
    public bool? UpDownSwap { get; set; }
    public int? SortIndex { get; set; }
    /// <summary>Play the motherboard ARGB input (true) or return to USB (false).</summary>
    public bool? MotherboardArgb { get; set; }
}

/// <summary>Routes of the TL LCD Flex and SL-INF LCD Flex fans (wired receiver + screen).</summary>
public static partial class LianLiFlexRoutes
{
    private const string DefaultEffect = "rainbow";
    private const int MaxColors = 4;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^[0-9A-Fa-f]{12}$")]
    private static partial Regex MacHex();

    public static void MapLianLiFlexEndpoints(this WebApplication app)
    {
        MapRound2Endpoints(app);

        app.MapGet("/devices/lianli-flex/state", (FlexHub hub, FlexScreens screens, IConfigStore store, Nexus.Service.Lighting.FlexLightingFrameWriter writer) =>
            Results.Json(BuildState(hub, store.Load(), screens, writer.IsIdentifying), AppJsonContext.Default.LianLiFlexStateResponse));

        app.MapGet("/devices/lianli-flex/lighting", (FlexHub hub, IConfigStore store) =>
            Results.Json(BuildLighting(hub, store.Load()), AppJsonContext.Default.LianLiFlexLightingResponse));

        app.MapPut("/devices/lianli-flex/lighting/{mac}", (
            string mac, LianLiFlexChainLightingRequest body, FlexHub hub, IConfigStore store, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            if (!MacHex().IsMatch(mac))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid mac"));
            }
            var unit = hub.Find(mac);
            if (unit is null)
            {
                return Results.NotFound(ApiResponse.Fail("fan not found"));
            }
            if (!unit.Controllable)
            {
                return Results.Json(ApiResponse.Fail("fan is bound to a wireless controller"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            var error = ValidateLighting(body, unit.Family);
            if (error is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            var key = unit.Mac.ToUpperInvariant();
            store.Update(s =>
            {
                s.Devices.FlexUnits.TryGetValue(key, out var old);
                old ??= new LianLiFlexUnitSettings();
                var next = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits, StringComparer.Ordinal)
                {
                    [key] = Patch(old, body, unit.Family),
                };
                if (body.SortIndex is { } position)
                {
                    Nexus.Service.Lighting.FlexStoredLook.MoveSortIndex(next, key, position);
                }
                s.Devices.FlexUnits = next;
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-flex/reset-lighting", (Slv3MacRequest body, FlexHub hub, IConfigStore store, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var unit = hub.Find(body.Mac);
            if (unit is null)
            {
                return Results.NotFound(ApiResponse.Fail("fan not found"));
            }
            if (!unit.Controllable)
            {
                return Results.Json(ApiResponse.Fail("fan is bound to a wireless controller"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            var key = unit.Mac.ToUpperInvariant();
            store.Update(s =>
            {
                s.Devices.FlexUnits.TryGetValue(key, out var old);
                var next = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits, StringComparer.Ordinal)
                {
                    [key] = ResetLook(old),
                };
                s.Devices.FlexUnits = next;
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-flex/restart-lcd", (Slv3MacRequest body, FlexScreens screens, FlexHub hub) =>
        {
            if (hub.Find(body.Mac) is not { } target)
            {
                return Results.NotFound(ApiResponse.Fail("fan not found"));
            }
            if (!target.Controllable)
            {
                return Results.Json(ApiResponse.Fail("fan is bound to a wireless controller"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            if (!screens.Restart(body.Mac))
            {
                return Results.Json(ApiResponse.Fail("no screen to restart"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-flex/identify", (Slv3MacRequest body, Nexus.Service.Lighting.FlexLightingFrameWriter writer, FlexHub hub, IConfigStore store) =>
        {
            var unit = hub.Find(body.Mac);
            var refusal = IdentifyRefusal(unit, store.Load().Devices.FlexUnits);
            if (refusal is { } refused)
            {
                return refused.Status == 404
                    ? Results.NotFound(ApiResponse.Fail(refused.Error))
                    : Results.Json(ApiResponse.Fail(refused.Error), AppJsonContext.Default.ApiResponse, statusCode: refused.Status);
            }
            writer.Identify(unit!.Mac);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/devices/lianli-flex/screens", (FlexHub hub, FlexLcdHub lcd, FlexScreens screens, IConfigStore store, StreamedPanelStore panelStore) =>
        {
            var screenSettings = store.Load().Devices.FlexLcdScreens;
            var panelRecords = panelStore.Load();
            var response = new Slv3LcdScreensResponse();
            var listed = new List<Slv3LcdScreenInfo>(lcd.Hub.Screens);
            foreach (var unit in hub.Units)
            {
                if (unit.LcdRestarting && unit.ScreenSerial.Length > 0
                    && !listed.Exists(x => string.Equals(x.Serial, unit.ScreenSerial, StringComparison.OrdinalIgnoreCase)))
                {
                    listed.Add(new Slv3LcdScreenInfo { Serial = unit.ScreenSerial });
                }
            }
            foreach (var screen in Slv3LcdRoutes.OrderScreens(listed, screenSettings))
            {
                var settings = screenSettings.TryGetValue(screen.Serial, out var s) ? s : new LianLiWirelessScreenSettings();
                response.Screens.Add(new Slv3LcdScreenDto
                {
                    Serial = screen.Serial,
                    Position = -1,
                    Label = screens.PanelName(screen.Serial),
                    Order = settings.Order,
                    Brightness = settings.Brightness,
                    SleepWhenLocked = settings.SleepWhenLocked,
                    Rotation = settings.Rotation,
                    NexusPanel = FanLcdContent.IsPanel(settings),
                    ContentType = FanLcdContent.ContentOf(settings),
                    MediaId = settings.MediaId,
                    SensorSource = settings.SensorSource,
                    SensorStyle = settings.SensorStyle,
                    ClockFace = settings.ClockFace,
                    AnimationId = settings.AnimationId,
                    ColorA = settings.ColorA,
                    ColorB = settings.ColorB,
                    TempUnit = settings.TempUnit,
                    PanelDeviceId = Slv3LcdRoutes.PanelDeviceIdFor(panelRecords, screens, screen.Serial, settings),
                });
            }
            return Results.Json(response, AppJsonContext.Default.Slv3LcdScreensResponse);
        });

        app.MapPost("/devices/lianli-flex/screen/settings", (Slv3LcdSettingsRequest body, FlexLcdHub lcd, FlexScreens flexScreens, FlexHub hub, IConfigStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Serial))
            {
                return Results.Json(ApiResponse.Fail("missing serial"), AppJsonContext.Default.ApiResponse);
            }
            if (!IsKnownScreen(hub, body.Serial))
            {
                return Results.Json(ApiResponse.Fail("unknown screen"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            if (body.Brightness is > 100)
            {
                return Results.Json(ApiResponse.Fail("brightness must be 0-100"), AppJsonContext.Default.ApiResponse);
            }
            if (body.Rotation is > 3)
            {
                return Results.Json(ApiResponse.Fail("rotation must be 0-3"), AppJsonContext.Default.ApiResponse);
            }
            var current = store.Load().Devices.FlexLcdScreens.TryGetValue(body.Serial, out var cur) ? cur : new LianLiWirelessScreenSettings();
            if (body.Brightness.HasValue && !lcd.Hub.IsLockSleeping(body.Serial, () => current.SleepWhenLocked))
            {
                lcd.Hub.SetBrightness(body.Serial, body.Brightness.Value);
            }
            if (body.Rotation.HasValue)
            {
                lcd.Hub.SetRotation(body.Serial, flexScreens.HardwareRotation(body.Serial, body.Rotation.Value));
            }
            store.Update(s =>
            {
                var screen = ScreenOf(s.Devices, body.Serial);
                if (body.Brightness.HasValue)
                {
                    screen.Brightness = body.Brightness.Value;
                }
                if (body.Rotation.HasValue)
                {
                    screen.Rotation = body.Rotation.Value;
                }
                if (body.SleepWhenLocked.HasValue)
                {
                    screen.SleepWhenLocked = body.SleepWhenLocked.Value;
                }
                if (body.NexusPanel is { } nexusPanel)
                {
                    Slv3LcdRoutes.ApplyNexusPanel(screen, nexusPanel);
                }
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-flex/screens/order", (Slv3LcdOrderRequest body, FlexHub hub, IConfigStore store) =>
        {
            var serials = body.Serials ?? new List<string>();
            if (Slv3LcdRoutes.ValidateOrder(serials) is { } orderError)
            {
                return Results.Json(ApiResponse.Fail(orderError), AppJsonContext.Default.ApiResponse);
            }
            if (serials.Any(x => !IsKnownScreen(hub, x)))
            {
                return Results.Json(ApiResponse.Fail("unknown screen"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            store.Update(s =>
            {
                for (var i = 0; i < serials.Count; i++)
                {
                    ScreenOf(s.Devices, serials[i]).Order = i;
                }
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-flex/screen/content", (Slv3LcdContentRequest body, FlexHub hub, IConfigStore store) =>
        {
            if (Slv3LcdRoutes.ValidateContent(body) is { } contentError)
            {
                return Results.Json(ApiResponse.Fail(contentError), AppJsonContext.Default.ApiResponse);
            }
            if (!IsKnownScreen(hub, body.Serial))
            {
                return Results.Json(ApiResponse.Fail("unknown screen"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            store.Update(s => Slv3LcdRoutes.ApplyContent(ScreenOf(s.Devices, body.Serial), body));
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    /// <summary>Why Identify is refused for a unit (404 unknown, 409 not ours or on the board input), or null when it may flash.</summary>
    internal static (int Status, string Error)? IdentifyRefusal(FlexUnit? unit, IReadOnlyDictionary<string, LianLiFlexUnitSettings> stored)
    {
        if (unit is null)
        {
            return (404, "fan not found");
        }
        if (!unit.Controllable)
        {
            return (409, "fan is bound to a wireless controller");
        }
        if (Nexus.Service.Lighting.FlexLightingDeviceProvider.PlaysMotherboardArgb(unit, stored))
        {
            return (409, "fan follows the motherboard ARGB input");
        }
        return null;
    }

    /// <summary>The look after Reset lighting: the default effect, the list position kept, and a unit on the board input handed back to USB so the reset look shows.</summary>
    internal static LianLiFlexUnitSettings ResetLook(LianLiFlexUnitSettings? old) => new()
    {
        Mode = DefaultEffect,
        EffectMode = DefaultEffect,
        SortIndex = old?.SortIndex ?? 0,
        MotherboardArgb = false,
    };

    /// <summary>A serial of a screen that belongs to a unit the hub has seen; settings are only created for those.</summary>
    private static bool IsKnownScreen(FlexHub hub, string serial) =>
        hub.Units.Any(u => string.Equals(u.ScreenSerial, serial, StringComparison.OrdinalIgnoreCase));

    private static LianLiWirelessScreenSettings ScreenOf(DevicesSettings devices, string serial)
    {
        if (!devices.FlexLcdScreens.TryGetValue(serial, out var screen))
        {
            screen = new LianLiWirelessScreenSettings();
            devices.FlexLcdScreens[serial] = screen;
        }
        return screen;
    }

    internal static LianLiFlexStateResponse BuildState(FlexHub hub, NexusSettings settings, FlexScreens? screens = null, Func<string, bool>? identifying = null)
    {
        var units = hub.Units;
        var fans = new LianLiFlexFanDto[units.Length];
        for (var i = 0; i < units.Length; i++)
        {
            var u = units[i];
            settings.Devices.FlexUnits.TryGetValue(u.Mac, out var look);
            fans[i] = new LianLiFlexFanDto
            {
                Mac = u.Mac,
                MasterMac = u.Info.MasterMac == "000000000000" ? "" : u.Info.MasterMac,
                BoundToUs = u.Controllable,
                Channel = u.Info.Channel,
                Slot = u.Info.RxType,
                DevType = u.Info.DevType,
                FanType = u.Info.FansType,
                FanCount = u.Fans,
                Rpm = u.Info.Rpm,
                Pwm = u.Info.Pwm,
                FirmwareVersion = u.FirmwareVersion,
                Stale = u.Stale,
                PwmCableConnected = u.Info.PwmLinePresent,
                EffectIndex = u.Info.EffectIndex,
                Family = u.Family == FlexFamily.SlInfFlex ? "slInfFlex" : "tlFlex",
                ScreenSerial = u.ScreenSerial,
                LcdFirmware = screens?.VersionOf(u.ScreenSerial) ?? "",
                LcdRestarting = u.LcdRestarting,
                Identifying = identifying?.Invoke(u.Mac) ?? false,
                ReceiverBound = u.Info.RfBound,
                SortIndex = look?.SortIndex ?? 0,
            };
        }
        return new LianLiFlexStateResponse
        {
            IsConnected = units.Length > 0,
            LinkStatus = hub.LinkStatus,
            Fans = fans,
        };
    }

    internal static LianLiFlexModeDto[] ModesOf(FlexFamily family)
    {
        var keys = FlexAnimationComposer.Keys(family);
        var rf = family == FlexFamily.SlInfFlex ? Slv3FanFamily.SlInfFlex : Slv3FanFamily.TlFlex;
        var modes = new LianLiFlexModeDto[keys.Count];
        for (var i = 0; i < modes.Length; i++)
        {
            var info = Slv3FanEffects.Find(rf, keys[i]);
            var count = FlexSwatchCounts.Of(family, keys[i]);
            var defaults = FitDefaults(DefaultColorsOf(family, keys[i]), count);
            modes[i] = new LianLiFlexModeDto
            {
                Key = keys[i],
                HasSpeed = info?.HasSpeed ?? true,
                HasDirection = info?.HasDirection ?? false,
                ColorsMin = 0,
                ColorsMax = count,
                Mergeable = FlexMergeEffects.IsMergeable(family, keys[i]),
                DefaultColors = defaults,
            };
        }
        return modes;
    }

    /// <summary>Cuts the default list to the swatch count, or fills it up with the stock palette when the effect offers more swatches than defaults.</summary>
    internal static string[] FitDefaults(string[] defaults, int count)
    {
        var fit = new string[count];
        for (var i = 0; i < count; i++)
        {
            if (i < defaults.Length)
            {
                fit[i] = defaults[i];
            }
            else
            {
                var c = FlexDefaultPalettes.MergeFallback[i % FlexDefaultPalettes.MergeFallback.Count];
                fit[i] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }
        return fit;
    }

    private static string[] DefaultColorsOf(FlexFamily family, string key)
    {
        var colors = FlexDefaultPalettes.For(family, key);
        if (colors.Count == 0 && family == FlexFamily.TlFlex)
        {
            colors = FlexTlExtendedEffects.DefaultColors(key);
        }
        var hex = new string[colors.Count];
        for (var i = 0; i < hex.Length; i++)
        {
            hex[i] = $"#{colors[i].R:X2}{colors[i].G:X2}{colors[i].B:X2}";
        }
        return hex;
    }

    internal static LianLiFlexLightingResponse BuildLighting(FlexHub hub, NexusSettings settings)
    {
        var chains = new List<LianLiFlexChainLightingDto>();
        foreach (var u in hub.Units)
        {
            if (!u.Controllable)
            {
                continue;
            }
            settings.Devices.FlexUnits.TryGetValue(u.Mac, out var stored);
            var ls = stored ?? new LianLiFlexUnitSettings();
            var zoneIds = Nexus.Service.Lighting.FlexLightingDeviceProvider.ZonesOf(u.Family);
            var modes = ModesOf(u.Family);
            var zones = new LianLiFlexZoneDto[zoneIds.Count];
            for (var i = 0; i < zones.Length; i++)
            {
                var z = i < ls.Zones.Count ? ls.Zones[i] : new LianLiFlexZoneSettings();
                zones[i] = new LianLiFlexZoneDto
                {
                    Id = zoneIds[i],
                    Modes = modes,
                    LedCount = FlexProtocol.ZoneWireIndexes(u.Family, zoneIds[i]).Count,
                    Mode = z.Mode,
                    Speed = z.Speed,
                    Direction = z.Direction,
                    Brightness = z.Brightness,
                    Colors = z.Colors.ToArray(),
                };
            }
            chains.Add(new LianLiFlexChainLightingDto
            {
                Mac = u.Mac,
                DevType = u.Info.DevType,
                Model = Nexus.Service.Lighting.FlexLightingDeviceProvider.ModelName(u.Family),
                FanType = u.Info.FansType,
                FanCount = u.Fans,
                Modes = modes,
                Mode = ls.Mode,
                EffectMode = ls.Mode != LianLiWirelessChainLighting.ModeCustom ? ls.Mode : ls.EffectMode ?? DefaultEffect,
                Speed = ls.Speed,
                Direction = ls.Direction,
                Brightness = ls.Brightness,
                Colors = ls.Colors.ToArray(),
                Merge = ls.Merge,
                MotherboardArgb = ls.MotherboardArgb,
                ArgbCableConnected = u.Info.ArgbCableConnected,
                PlayingMotherboardArgb = u.Info.PlayingMotherboardArgb,
                Zones = zones,
                UpDownSwap = u.Family == FlexFamily.TlFlex ? ls.UpDownSwap : null,
                SortIndex = ls.SortIndex,
            });
        }
        var leader = Nexus.Service.Lighting.FlexStoredLook.FindMergeGroup(hub.Units, settings.Devices.FlexUnits)?.LeaderMac ?? "";
        return new LianLiFlexLightingResponse { Chains = chains.ToArray(), MergeLeaderMac = leader };
    }

    internal static string? ValidateLighting(LianLiFlexChainLightingRequest body, FlexFamily family)
    {
        var keys = FlexAnimationComposer.Keys(family);
        bool KnownMode(string m) => keys.Contains(m);
        if (body.Mode is not null
            && body.Mode != LianLiWirelessChainLighting.ModeCustom
            && body.Mode != LianLiFlexUnitSettings.ModePerZone
            && !KnownMode(body.Mode))
        {
            return "unknown mode";
        }
        if (body.Colors is not null)
        {
            var error = ValidateColors(body.Colors);
            if (error is not null)
            {
                return error;
            }
        }
        if (body.SortIndex is < 0)
        {
            return "sortIndex must not be negative";
        }
        if (body.UpDownSwap is not null && family != FlexFamily.TlFlex)
        {
            return "up/down swap is not supported";
        }
        if (body.Zones is not null)
        {
            var valid = Nexus.Service.Lighting.FlexLightingDeviceProvider.ZonesOf(family);
            foreach (var z in body.Zones)
            {
                if (!valid.Contains(z.Id))
                {
                    return "unknown zone";
                }
                if (z.Mode is not null && !KnownMode(z.Mode))
                {
                    return "unknown mode";
                }
                if (z.Colors is not null && ValidateColors(z.Colors) is { } colorError)
                {
                    return colorError;
                }
            }
        }
        return null;
    }

    private static string? ValidateColors(string[] colors)
    {
        if (colors.Length > MaxColors)
        {
            return "too many colors";
        }
        return colors.Any(c => !HexColor().IsMatch(c)) ? "invalid color" : null;
    }

    internal static LianLiFlexUnitSettings Patch(LianLiFlexUnitSettings old, LianLiFlexChainLightingRequest body, FlexFamily family)
    {
        var zoneIds = Nexus.Service.Lighting.FlexLightingDeviceProvider.ZonesOf(family);
        var zones = new List<LianLiFlexZoneSettings>(zoneIds.Count);
        for (var i = 0; i < zoneIds.Count; i++)
        {
            var o = i < old.Zones.Count ? old.Zones[i] : new LianLiFlexZoneSettings();
            var p = body.Zones?.FirstOrDefault(x => x.Id == zoneIds[i]);
            zones.Add(new LianLiFlexZoneSettings
            {
                Mode = p?.Mode ?? o.Mode,
                Speed = p?.Speed is { } sp ? Math.Clamp(sp, 0, 4) : o.Speed,
                Direction = p?.Direction is { } d ? Math.Clamp(d, 0, 1) : o.Direction,
                Brightness = p?.Brightness is { } b ? Math.Clamp(b, 0, 4) : o.Brightness,
                Colors = p?.Colors is { } c ? new List<string>(c) : o.Colors,
            });
        }
        var next = old.Clone();
        next.Mode = body.Mode ?? old.Mode;
        next.EffectMode = body.Mode is not null && body.Mode != LianLiWirelessChainLighting.ModeCustom ? body.Mode : old.EffectMode;
        next.Speed = body.Speed.HasValue ? Math.Clamp(body.Speed.Value, 0, 4) : old.Speed;
        next.Direction = body.Direction.HasValue ? Math.Clamp(body.Direction.Value, 0, 1) : old.Direction;
        next.Brightness = body.Brightness.HasValue ? Math.Clamp(body.Brightness.Value, 0, 4) : old.Brightness;
        next.Colors = body.Colors is not null ? new List<string>(body.Colors) : old.Colors;
        next.Merge = body.Merge ?? old.Merge;
        next.UpDownSwap = body.UpDownSwap ?? old.UpDownSwap;
        next.MotherboardArgb = body.MotherboardArgb ?? old.MotherboardArgb;
        next.Zones = zones;
        return next;
    }
}

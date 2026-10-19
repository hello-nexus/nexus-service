using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class LianLiFlexPointDto
{
    public int Temp { get; set; }
    public int Rpm { get; set; }
}

public sealed class LianLiFlexProfileDefDto
{
    public string Key { get; set; } = "";
    public int FanSize { get; set; }
    public LianLiFlexPointDto[] Points { get; set; } = Array.Empty<LianLiFlexPointDto>();
    public int MinRpm { get; set; }
    public int MaxRpm { get; set; }
}

public sealed class LianLiFlexProfilesResponse
{
    public LianLiFlexProfileDefDto[] Profiles { get; set; } = Array.Empty<LianLiFlexProfileDefDto>();
}

public sealed class LianLiFlexSourceAvailabilityDto
{
    public bool Cpu { get; set; }
    public bool Gpu { get; set; }
    public bool Water { get; set; }
}

public sealed class LianLiFlexFanProfileDto
{
    public string Mac { get; set; } = "";
    public string Profile { get; set; } = "";
    public bool MbSync { get; set; }
    public string Source { get; set; } = "cpu";
    public LianLiFlexSourceAvailabilityDto SourceAvailable { get; set; } = new();
    public int FanSize { get; set; } = 120;
    public bool StartStop { get; set; }
    public int FixedPercent { get; set; }
    public LianLiFlexPointDto[] Points { get; set; } = Array.Empty<LianLiFlexPointDto>();
    public string CurveId { get; set; } = "";
    public int Rpm { get; set; }
    public int DutyPercent { get; set; }
}

public sealed class LianLiFlexFanProfilePatch
{
    public string? Profile { get; set; }
    public string? Source { get; set; }
    public int? FanSize { get; set; }
    public bool? StartStop { get; set; }
    public int? FixedPercent { get; set; }
    /// <summary>Points for the profile being set when it is a custom slot.</summary>
    public LianLiFlexPointDto[]? Points { get; set; }
}

public sealed class LianLiFlexFanSyncRequest
{
    public bool Enabled { get; set; }
}

public sealed class LianLiFlexSyncMemberDto
{
    public string Mac { get; set; } = "";
    public string Family { get; set; } = "";
    public string Label { get; set; } = "";
    public int SortIndex { get; set; }
    public int SliceDirection { get; set; }
}

public sealed class LianLiFlexSyncMemberPatch
{
    public string Mac { get; set; } = "";
    public int SliceDirection { get; set; }
}

public sealed class LianLiFlexSyncDto
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public LianLiFlexModeDto[] Modes { get; set; } = Array.Empty<LianLiFlexModeDto>();
    public LianLiFlexSyncMemberDto[] Members { get; set; } = Array.Empty<LianLiFlexSyncMemberDto>();
}

public sealed class LianLiFlexSyncPatch
{
    public bool? Enabled { get; set; }
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
    public LianLiFlexSyncMemberPatch[]? Members { get; set; }
}

public sealed class LianLiFlexThermalSourceDto
{
    public bool Enabled { get; set; }
    public int Threshold { get; set; }
    public string Color { get; set; } = "";
}

public sealed class LianLiFlexThermalActiveDto
{
    public string? Source { get; set; }
    public long? Since { get; set; }
}

public sealed class LianLiFlexThermalAlertDto
{
    public bool Enabled { get; set; }
    public LianLiFlexThermalSourceDto Cpu { get; set; } = new();
    public LianLiFlexThermalSourceDto Gpu { get; set; } = new();
    public string Unit { get; set; } = "c";
    public string[] Members { get; set; } = Array.Empty<string>();
    public LianLiFlexThermalActiveDto Active { get; set; } = new();
}

public sealed class LianLiFlexThermalSourcePatch
{
    public bool? Enabled { get; set; }
    public int? Threshold { get; set; }
    public string? Color { get; set; }
}

public sealed class LianLiFlexThermalAlertPatch
{
    public bool? Enabled { get; set; }
    public LianLiFlexThermalSourcePatch? Cpu { get; set; }
    public LianLiFlexThermalSourcePatch? Gpu { get; set; }
    public string? Unit { get; set; }
    public string[]? Members { get; set; }
}

public sealed class LianLiFlexApplyAllRequest
{
    public string Mac { get; set; } = "";
    public string[] What { get; set; } = Array.Empty<string>();
    public string Scope { get; set; } = "family";
}

public sealed class LianLiFlexApplyAllResponse : ApiResponse
{
    public string[] Applied { get; set; } = Array.Empty<string>();
    public string[] Skipped { get; set; } = Array.Empty<string>();
}

public sealed class LianLiFlexScreenSyncRequest
{
    public string Serial { get; set; } = "";
}

public static partial class LianLiFlexRoutes
{
    private static void MapRound2Endpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-flex/fan-profiles", () =>
        {
            var list = FlexFanProfiles.Catalog().Select(p => new LianLiFlexProfileDefDto
            {
                Key = p.Key,
                FanSize = p.FanSize,
                Points = p.Points.Select(x => new LianLiFlexPointDto { Temp = x.Temp, Rpm = x.Rpm }).ToArray(),
                MinRpm = 350,
                MaxRpm = FlexFanProfiles.TlMaxRpm,
            }).ToArray();
            return Results.Json(new LianLiFlexProfilesResponse { Profiles = list }, AppJsonContext.Default.LianLiFlexProfilesResponse);
        });

        app.MapGet("/devices/lianli-flex/fan/{mac}", (string mac, FlexHub hub, FlexFanProfileService profiles) =>
        {
            var unit = hub.Find(mac);
            if (unit is null)
            {
                return Results.NotFound(ApiResponse.Fail("fan not found"));
            }
            return Results.Json(ProfileDto(unit, profiles), AppJsonContext.Default.LianLiFlexFanProfileDto);
        });

        app.MapPut("/devices/lianli-flex/fan/{mac}", (string mac, LianLiFlexFanProfilePatch body, FlexHub hub, FlexFanProfileService profiles) =>
        {
            var unit = hub.Find(mac);
            if (unit is null)
            {
                return Results.NotFound(ApiResponse.Fail("fan not found"));
            }
            var next = profiles.Current(unit.Mac).Clone();
            if (body.Profile is not null)
            {
                next.Profile = body.Profile;
            }
            if (body.Source is not null)
            {
                next.Source = body.Source;
            }
            if (body.FanSize is { } size)
            {
                next.FanSize = size;
            }
            if (body.StartStop is { } startStop)
            {
                next.StartStop = startStop;
            }
            if (body.FixedPercent is { } fixedPercent)
            {
                next.FixedPercent = fixedPercent;
            }
            if (body.Points is not null)
            {
                if (!FlexFanProfiles.CustomKeys.Contains(next.Profile))
                {
                    return Results.BadRequest(ApiResponse.Fail("points apply to a custom profile"));
                }
                var clean = FlexFanProfiles.Sanitize(body.Points.Select(p => (p.Temp, p.Rpm)));
                if (clean is null)
                {
                    return Results.BadRequest(ApiResponse.Fail("at least two points are needed"));
                }
                next.Custom = new Dictionary<string, List<LianLiFlexCurvePoint>>(next.Custom)
                {
                    [next.Profile] = clean.Select(p => new LianLiFlexCurvePoint { Temp = p.Temp, Rpm = p.Rpm }).ToList(),
                };
            }
            var error = profiles.Apply(unit.Mac, next);
            return error is null
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.BadRequest(ApiResponse.Fail(error));
        });

        app.MapPost("/devices/lianli-flex/fan/{mac}/default", (string mac, FlexHub hub, FlexFanProfileService profiles) =>
        {
            var unit = hub.Find(mac);
            if (unit is null)
            {
                return Results.NotFound(ApiResponse.Fail("fan not found"));
            }
            var error = profiles.Default(unit.Mac);
            return error is null
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.BadRequest(ApiResponse.Fail(error));
        });

        app.MapPut("/devices/lianli-flex/fan-sync", (LianLiFlexFanSyncRequest body, FlexFanProfileService profiles) =>
        {
            profiles.SetMbSync(body.Enabled);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/devices/lianli-flex/sync", (FlexGroupLighting group) =>
            Results.Json(SyncDto(group), AppJsonContext.Default.LianLiFlexSyncDto));

        app.MapPut("/devices/lianli-flex/sync", (
            LianLiFlexSyncPatch body, FlexGroupLighting group, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var error = group.Apply(new FlexSyncPatch
            {
                Enabled = body.Enabled,
                Mode = body.Mode,
                Speed = body.Speed,
                Direction = body.Direction,
                Brightness = body.Brightness,
                Colors = body.Colors,
                Members = body.Members?.Select(m => (m.Mac, m.SliceDirection)).ToList(),
            });
            if (error is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/devices/lianli-flex/thermal-alert", (IConfigStore store, FlexThermalAlert alert) =>
            Results.Json(ThermalDto(store.Load().Devices.FlexThermalAlert, alert.State), AppJsonContext.Default.LianLiFlexThermalAlertDto));

        app.MapPut("/devices/lianli-flex/thermal-alert", (LianLiFlexThermalAlertPatch body, IConfigStore store) =>
        {
            var error = ValidateThermal(body);
            if (error is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            store.Update(s =>
            {
                var old = s.Devices.FlexThermalAlert;
                s.Devices.FlexThermalAlert = new LianLiFlexThermalAlertSettings
                {
                    Enabled = body.Enabled ?? old.Enabled,
                    CpuEnabled = body.Cpu?.Enabled ?? old.CpuEnabled,
                    CpuThreshold = body.Cpu?.Threshold ?? old.CpuThreshold,
                    CpuColor = body.Cpu?.Color ?? old.CpuColor,
                    GpuEnabled = body.Gpu?.Enabled ?? old.GpuEnabled,
                    GpuThreshold = body.Gpu?.Threshold ?? old.GpuThreshold,
                    GpuColor = body.Gpu?.Color ?? old.GpuColor,
                    Unit = body.Unit ?? old.Unit,
                    Members = body.Members is not null ? new List<string>(body.Members) : old.Members,
                };
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-flex/apply-all", (
            LianLiFlexApplyAllRequest body, FlexApplyAll applyAll, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var what = body.What ?? Array.Empty<string>();
            if (what.Any(w => w is not ("lighting" or "fan")))
            {
                return Results.BadRequest(ApiResponse.Fail("what must be lighting and/or fan"));
            }
            var (result, error) = applyAll.Apply(body.Mac, what.Contains("lighting"), what.Contains("fan"), body.Scope ?? "family");
            if (error is not null || result is null)
            {
                return Results.BadRequest(ApiResponse.Fail(error ?? "failed"));
            }
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(
                new LianLiFlexApplyAllResponse { Applied = result.Applied.ToArray(), Skipped = result.Skipped.ToArray() },
                AppJsonContext.Default.LianLiFlexApplyAllResponse);
        });

        app.MapPost("/devices/lianli-flex/screens/sync", (LianLiFlexScreenSyncRequest body, FlexLcdHub lcd, IConfigStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Serial))
            {
                return Results.Json(ApiResponse.Fail("missing serial"), AppJsonContext.Default.ApiResponse);
            }
            var serials = lcd.Hub.Screens.Select(s => s.Serial).ToList();
            if (!serials.Contains(body.Serial, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Json(ApiResponse.Fail("screen not found"), AppJsonContext.Default.ApiResponse);
            }
            store.Update(s =>
            {
                if (!s.Devices.FlexLcdScreens.TryGetValue(body.Serial, out var from))
                {
                    from = new LianLiWirelessScreenSettings();
                }
                foreach (var serial in serials)
                {
                    if (string.Equals(serial, body.Serial, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var to = ScreenOf(s.Devices, serial);
                    to.ContentType = from.ContentType;
                    to.MediaId = from.MediaId;
                    to.SensorSource = from.SensorSource;
                    to.SensorStyle = from.SensorStyle;
                    to.ClockFace = from.ClockFace;
                    to.AnimationId = from.AnimationId;
                    to.ColorA = from.ColorA;
                    to.ColorB = from.ColorB;
                    to.TempUnit = from.TempUnit;
                    to.Overlay = from.Overlay;
                    to.ScreenSource = from.ScreenSource;
                    to.NexusPanel = false;
                }
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    internal static LianLiFlexFanProfileDto ProfileDto(FlexUnit unit, FlexFanProfileService profiles)
    {
        var p = profiles.Current(unit.Mac);
        var avail = profiles.SourcesAvailable();
        var points = FlexFanProfileService.EffectivePoints(p);
        return new LianLiFlexFanProfileDto
        {
            Mac = unit.Mac,
            Profile = p.Profile,
            MbSync = p.Profile == "mbsync",
            Source = p.Source,
            SourceAvailable = new LianLiFlexSourceAvailabilityDto { Cpu = avail.Cpu, Gpu = avail.Gpu, Water = avail.Water },
            FanSize = p.FanSize,
            StartStop = p.StartStop,
            FixedPercent = p.FixedPercent,
            Points = points.Select(x => new LianLiFlexPointDto { Temp = x.Temp, Rpm = x.Rpm }).ToArray(),
            CurveId = p.CurveId,
            Rpm = unit.Info.Rpm.Length > 0 ? unit.Info.Rpm[0] : 0,
            DutyPercent = unit.Info.Pwm.Length > 0 && unit.Info.Pwm[0] != FlexProtocol.PwmFollowMotherboard ? FlexProtocol.DecodeDuty(unit.Info.Pwm[0]) : 0,
        };
    }

    internal static LianLiFlexSyncDto SyncDto(FlexGroupLighting group)
    {
        var leader = group.Leader();
        var members = group.Members();
        var family = members.Count > 0 ? members[0].Family : FlexFamily.TlFlex;
        return new LianLiFlexSyncDto
        {
            Enabled = group.Enabled,
            Mode = leader.Mode,
            Speed = leader.Speed,
            Direction = leader.Direction,
            Brightness = leader.Brightness,
            Colors = leader.Colors.ToArray(),
            Modes = ModesOf(family).Where(m => m.Mergeable).ToArray(),
            Members = members.Select(m => new LianLiFlexSyncMemberDto
            {
                Mac = m.Mac,
                Family = m.Family == FlexFamily.SlInfFlex ? "slInfFlex" : "tlFlex",
                Label = FlexLightingDeviceProvider.ModelName(m.Family),
                SortIndex = m.SortIndex,
                SliceDirection = m.SliceDirection,
            }).ToArray(),
        };
    }

    internal static LianLiFlexThermalAlertDto ThermalDto(LianLiFlexThermalAlertSettings s, FlexAlertState state) => new()
    {
        Enabled = s.Enabled,
        Cpu = new LianLiFlexThermalSourceDto { Enabled = s.CpuEnabled, Threshold = s.CpuThreshold, Color = s.CpuColor },
        Gpu = new LianLiFlexThermalSourceDto { Enabled = s.GpuEnabled, Threshold = s.GpuThreshold, Color = s.GpuColor },
        Unit = s.Unit,
        Members = s.Members.ToArray(),
        Active = new LianLiFlexThermalActiveDto { Source = state.Source, Since = state.SinceUnixMs },
    };

    internal static string? ValidateThermal(LianLiFlexThermalAlertPatch body)
    {
        foreach (var src in new[] { body.Cpu, body.Gpu })
        {
            if (src is null)
            {
                continue;
            }
            if (src.Threshold is { } t && (t < 1 || t > 110))
            {
                return "threshold must be 1..110";
            }
            if (src.Color is not null && !HexColor().IsMatch(src.Color))
            {
                return "invalid color";
            }
        }
        if (body.Unit is not null && body.Unit is not ("c" or "f"))
        {
            return "unit must be c or f";
        }
        if (body.Members is not null && body.Members.Any(m => !MacHex().IsMatch(m)))
        {
            return "invalid mac";
        }
        return null;
    }
}

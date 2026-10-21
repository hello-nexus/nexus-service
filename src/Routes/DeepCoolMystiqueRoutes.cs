using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapDeepCoolMystiqueEndpoints(WebApplication app)
    {
        app.MapGet("/devices/deepcool-mystique/state", (IConfigStore store, MystiqueWorker worker, ISensorProvider sensors, Nexus.Service.Cooling.IFanControlProvider? fans) =>
        {
            DeepCoolMystiqueSettings copy = new();
            store.Read(s => copy = s.Devices.DeepCoolMystique.Copy());
            return Results.Json(BuildMystiqueState(copy, worker.IsConnected, sensors, MystiqueSensors.FanNames(store, fans)), AppJsonContext.Default.DeepCoolMystiqueStateResponse);
        });

        app.MapPut("/devices/deepcool-mystique/settings", (DeepCoolMystiqueSettingsRequest body, IConfigStore store, MystiqueWorker worker, ISensorProvider sensors) =>
        {
            if (body.Mode is not null
                && body.Mode is not (DeepCoolMystiqueSettings.ModeMonitor or DeepCoolMystiqueSettings.ModeMedia or DeepCoolMystiqueSettings.ModeRecording))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode"));
            }
            if (body.Readout is not null
                && body.Readout is not (DeepCoolMystiqueSettings.ReadoutTemperature or DeepCoolMystiqueSettings.ReadoutFrequency
                    or DeepCoolMystiqueSettings.ReadoutTime or DeepCoolMystiqueSettings.ReadoutPump
                    or DeepCoolMystiqueSettings.ReadoutFan or DeepCoolMystiqueSettings.ReadoutFanPump))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown readout"));
            }
            if (!ValidMystiquePick(body.CpuFan, sensors) || !ValidMystiquePick(body.Pump, sensors))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid sensor"));
            }
            store.Update(s => ApplyMystiqueRequest(s.Devices.DeepCoolMystique, body));
            worker.RequestApply();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    /// <summary>An empty pick returns the readout to the automatic choice; otherwise the sensor must exist and be a fan reading.</summary>
    private static bool ValidMystiquePick(DeepCoolMystiqueSensorDto? pick, ISensorProvider sensors)
    {
        if (pick is null)
        {
            return true;
        }
        if (pick.Category is null || pick.Sensor is null)
        {
            return false;
        }
        if (pick.Category.Length == 0)
        {
            return pick.Sensor.Length == 0;
        }
        return SensorSnapshotResolver.IsSupportedCategory(pick.Category)
            && SensorSnapshotResolver.ResolveExact(sensors, pick.Category, pick.Sensor) is { Type: "Fan" };
    }

    /// <summary>Applies a settings request to the saved state; separate from the route so it can be tested.</summary>
    internal static void ApplyMystiqueRequest(DeepCoolMystiqueSettings m, DeepCoolMystiqueSettingsRequest body)
    {
        if (body.Mode is not null) { m.Mode = body.Mode; m.ModeEdited = true; }
        if (body.Readout is not null) { m.Readout = body.Readout; m.ReadoutEdited = true; }
        if (body.RecordFrequency.HasValue) { m.RecordFrequency = body.RecordFrequency.Value; m.RecordingEdited = true; }
        if (body.RecordTemperature.HasValue) { m.RecordTemperature = body.RecordTemperature.Value; m.RecordingEdited = true; }
        if (body.Brightness.HasValue) { m.Brightness = Math.Clamp(body.Brightness.Value, 0, 100); m.DeviceEdited = true; }
        if (body.Gyroscope.HasValue) { m.Gyroscope = body.Gyroscope.Value; m.DeviceEdited = true; }
        if (body.RgbSync.HasValue) { m.RgbSync = body.RgbSync.Value; m.DeviceEdited = true; }
        if (body.CpuFan is not null) { m.CpuFan = new DeepCoolMystiqueSensorPick { Category = body.CpuFan.Category, Sensor = body.CpuFan.Sensor }; }
        if (body.Pump is not null) { m.Pump = new DeepCoolMystiqueSensorPick { Category = body.Pump.Category, Sensor = body.Pump.Sensor }; }
    }

    internal static DeepCoolMystiqueStateResponse BuildMystiqueState(
        DeepCoolMystiqueSettings m, bool connected, ISensorProvider sensors, IReadOnlyDictionary<string, string>? fanNames = null) => new()
    {
        IsConnected = connected,
        ModeChosen = m.ModeEdited,
        ReadoutChosen = m.ReadoutEdited,
        RecordingChosen = m.RecordingEdited,
        DeviceChosen = m.DeviceEdited,
        Mode = m.Mode,
        Readout = m.Readout,
        RecordFrequency = m.RecordFrequency,
        RecordTemperature = m.RecordTemperature,
        Brightness = m.Brightness,
        Gyroscope = m.Gyroscope,
        RgbSync = m.RgbSync,
        CpuFan = MystiquePickDto(sensors, m.CpuFan, pump: false, fanNames),
        Pump = MystiquePickDto(sensors, m.Pump, pump: true, fanNames),
    };

    private static DeepCoolMystiqueSensorDto MystiquePickDto(
        ISensorProvider sensors, DeepCoolMystiqueSensorPick pick, bool pump, IReadOnlyDictionary<string, string>? fanNames)
    {
        var found = MystiqueSensors.FindFan(sensors, pick, pump, fanNames);
        bool picked = pick.Sensor.Length > 0;
        return new DeepCoolMystiqueSensorDto
        {
            Category = pick.Category,
            Sensor = pick.Sensor,
            ResolvedCategory = picked ? pick.Category : "motherboard",
            ResolvedSensor = found?.Id ?? "",
        };
    }
}

public sealed class DeepCoolMystiqueSensorDto
{
    public string Category { get; set; } = "";
    public string Sensor { get; set; } = "";

    /// <summary>Response only: the category the readout reads, which for an unpicked one is the motherboard.</summary>
    public string ResolvedCategory { get; set; } = "";

    /// <summary>Response only: the sensor id the readout reads, empty when none resolves.</summary>
    public string ResolvedSensor { get; set; } = "";
}

public sealed class DeepCoolMystiqueStateResponse
{
    public bool IsConnected { get; set; }

    /// <summary>False until the user has picked a mode, when the screen's actual mode is unknown.</summary>
    public bool ModeChosen { get; set; }

    /// <summary>False until the user has picked a readout; the readout shown is then a placeholder.</summary>
    public bool ReadoutChosen { get; set; }

    /// <summary>False until the user has changed a recording graph; the toggles shown are placeholders.</summary>
    public bool RecordingChosen { get; set; }

    /// <summary>False until the user has changed brightness, rotation or RGB sync, which the screen takes in one command; the values shown are what the first change writes.</summary>
    public bool DeviceChosen { get; set; }

    /// <summary>"monitor", "media" or "recording".</summary>
    public string Mode { get; set; } = "";

    /// <summary>Monitor mode's main readout: "temperature", "frequency", "time", "pump", "fan" or "fanPump".</summary>
    public string Readout { get; set; } = "";

    public bool RecordFrequency { get; set; }
    public bool RecordTemperature { get; set; }

    /// <summary>Percent.</summary>
    public int Brightness { get; set; }

    public bool Gyroscope { get; set; }
    public bool RgbSync { get; set; }
    public DeepCoolMystiqueSensorDto CpuFan { get; set; } = new();
    public DeepCoolMystiqueSensorDto Pump { get; set; } = new();
}

public sealed class DeepCoolMystiqueSettingsRequest
{
    public string? Mode { get; set; }
    public string? Readout { get; set; }
    public bool? RecordFrequency { get; set; }
    public bool? RecordTemperature { get; set; }
    public int? Brightness { get; set; }
    public bool? Gyroscope { get; set; }
    public bool? RgbSync { get; set; }
    public DeepCoolMystiqueSensorDto? CpuFan { get; set; }
    public DeepCoolMystiqueSensorDto? Pump { get; set; }
}

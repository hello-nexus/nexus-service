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

        app.MapPut("/devices/deepcool-mystique/settings", (DeepCoolMystiqueSettingsRequest body, IConfigStore store, MystiqueWorker worker, ISensorProvider sensors, Nexus.Service.Cooling.IFanControlProvider? fans) =>
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
            DeepCoolMystiqueSettings stored = new();
            store.Read(s => stored = s.Devices.DeepCoolMystique.Copy());
            var fanNames = MystiqueSensors.FanNames(store, fans);
            if (!ValidMystiquePick(body.CpuTemperature, stored.CpuTemperature, MystiqueValue.CpuTemperature, sensors, fanNames)
                || !ValidMystiquePick(body.CpuFrequency, stored.CpuFrequency, MystiqueValue.CpuFrequency, sensors, fanNames)
                || !ValidMystiquePick(body.CpuUsage, stored.CpuUsage, MystiqueValue.CpuUsage, sensors, fanNames)
                || !ValidMystiquePick(body.RamUsage, stored.RamUsage, MystiqueValue.RamUsage, sensors, fanNames)
                || !ValidMystiquePick(body.CpuFan, stored.CpuFan, MystiqueValue.CpuFan, sensors, fanNames)
                || !ValidMystiquePick(body.Pump, stored.Pump, MystiqueValue.Pump, sensors, fanNames))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid sensor"));
            }
            store.Update(s => ApplyMystiqueRequest(s.Devices.DeepCoolMystique, body));
            worker.RequestApply();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    /// <summary>
    /// An empty pick returns the readout to the automatic choice. A changed pick must name a sensor of the
    /// readout's type that exists now; the pick already stored is never re-checked, so an absent header cannot fail an unrelated change.
    /// </summary>
    private static bool ValidMystiquePick(
        DeepCoolMystiqueSensorDto? pick, DeepCoolMystiqueSensorPick stored, MystiqueValue value, ISensorProvider sensors,
        IReadOnlyDictionary<string, string>? fanNames)
    {
        if (pick is null || (pick.Category == stored.Category && pick.Sensor == stored.Sensor))
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
            && SensorSnapshotResolver.ResolveExact(sensors, pick.Category, pick.Sensor, new SensorSnapshotSources(null, null, null, fanNames)) is { } found
            && found.Type == MystiqueSensors.RequiredType(value);
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
        if (body.CpuTemperature is not null) { m.CpuTemperature = ToPick(body.CpuTemperature); }
        if (body.CpuFrequency is not null) { m.CpuFrequency = ToPick(body.CpuFrequency); }
        if (body.CpuUsage is not null) { m.CpuUsage = ToPick(body.CpuUsage); }
        if (body.RamUsage is not null) { m.RamUsage = ToPick(body.RamUsage); }
        if (body.CpuFan is not null) { m.CpuFan = ToPick(body.CpuFan); }
        if (body.Pump is not null) { m.Pump = ToPick(body.Pump); }
    }

    private static DeepCoolMystiqueSensorPick ToPick(DeepCoolMystiqueSensorDto dto) => new() { Category = dto.Category, Sensor = dto.Sensor };

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
        CpuTemperature = MystiquePickDto(sensors, m.CpuTemperature, MystiqueValue.CpuTemperature, fanNames),
        CpuFrequency = MystiquePickDto(sensors, m.CpuFrequency, MystiqueValue.CpuFrequency, fanNames),
        CpuUsage = MystiquePickDto(sensors, m.CpuUsage, MystiqueValue.CpuUsage, fanNames),
        RamUsage = MystiquePickDto(sensors, m.RamUsage, MystiqueValue.RamUsage, fanNames),
        CpuFan = MystiquePickDto(sensors, m.CpuFan, MystiqueValue.CpuFan, fanNames),
        Pump = MystiquePickDto(sensors, m.Pump, MystiqueValue.Pump, fanNames),
    };

    private static DeepCoolMystiqueSensorDto MystiquePickDto(
        ISensorProvider sensors, DeepCoolMystiqueSensorPick pick, MystiqueValue value, IReadOnlyDictionary<string, string>? fanNames)
    {
        var found = MystiqueSensors.Find(sensors, pick, value, fanNames);
        return new DeepCoolMystiqueSensorDto
        {
            Category = pick.Category,
            Sensor = pick.Sensor,
            ResolvedCategory = found?.Category ?? "",
            ResolvedSensor = found?.Sensor.Id ?? "",
        };
    }
}

public sealed class DeepCoolMystiqueSensorDto
{
    public string Category { get; set; } = "";
    public string Sensor { get; set; } = "";

    /// <summary>Response only: the category the readout reads, which for an unpicked one is where the automatic choice lives; empty when none resolves.</summary>
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
    public DeepCoolMystiqueSensorDto CpuTemperature { get; set; } = new();
    public DeepCoolMystiqueSensorDto CpuFrequency { get; set; } = new();
    public DeepCoolMystiqueSensorDto CpuUsage { get; set; } = new();
    public DeepCoolMystiqueSensorDto RamUsage { get; set; } = new();
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
    public DeepCoolMystiqueSensorDto? CpuTemperature { get; set; }
    public DeepCoolMystiqueSensorDto? CpuFrequency { get; set; }
    public DeepCoolMystiqueSensorDto? CpuUsage { get; set; }
    public DeepCoolMystiqueSensorDto? RamUsage { get; set; }
    public DeepCoolMystiqueSensorDto? CpuFan { get; set; }
    public DeepCoolMystiqueSensorDto? Pump { get; set; }
}

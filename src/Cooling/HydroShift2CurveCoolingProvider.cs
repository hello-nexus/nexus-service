using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Cooling;

/// <summary>The HydroShift II OLED Curved pump and coolant probe; the cooler has no fan headers.</summary>
public sealed class HydroShift2CurveCoolingProvider : IFanControlProvider, ICoolingProvider
{
    public const string DeviceId = HydroShift2CurveLcdDriver.Id;
    public const string DeviceName = "Lian Li HydroShift II OLED Curved";

    private const string PumpChannelId = DeviceId + ":pump";
    private const string CoolantSensorId = DeviceId + ":coolant";

    private readonly HydroShift2CurveBoard _board;

    public HydroShift2CurveCoolingProvider(HydroShift2CurveBoard board)
    {
        _board = board;
    }

    public static bool IsHydroShift2CurveId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_board.IsAvailable || _board.PumpRpm is not { } rpm)
        {
            return Array.Empty<FanChannel>();
        }
        var duty = _board.PumpDuty;
        return new[]
        {
            new FanChannel
            {
                Id = PumpChannelId,
                Name = "HydroShift II OLED Curved Pump",
                DutyPercent = duty ?? HydroShift2CurveProtocol.PumpDutyForRpm(rpm),
                Rpm = rpm,
                Mode = duty is null ? FanModes.Auto : FanModes.Manual,
                Kind = FanKinds.Pump,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Pump",
            },
        };
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        if (ReadTemperature(CoolantSensorId) is not { } coolant)
        {
            return Array.Empty<TemperatureSource>();
        }
        return new[]
        {
            new TemperatureSource
            {
                Id = CoolantSensorId,
                Name = "Liquid",
                Category = "Cooler",
                Value = coolant,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
            },
        };
    }

    public float? ReadTemperature(string sensorId)
    {
        if (sensorId != CoolantSensorId || !_board.IsAvailable)
        {
            return null;
        }
        return _board.CoolantC is { } c && c > 0 ? c : null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        int clamped = Math.Clamp(dutyPercent, 0, 100);
        DriveFanSpeed(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId && _board.IsAvailable)
        {
            _board.SetPumpDuty(Math.Clamp(dutyPercent, 0, 100));
        }
    }

    public void ReleaseFan(string channelId)
    {
        if (channelId == PumpChannelId)
        {
            _board.SetPumpDuty(null);
        }
    }

    public void ReleaseAll() => _board.SetPumpDuty(null);

    // The pump floors at ~1630 rpm whatever the output, so a ramp would not find a stop point.
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var channels = GetFanChannels();
        if (channels.Count == 0)
        {
            return Array.Empty<CoolingComponent>();
        }
        var devices = new List<CoolingDevice>(channels.Count);
        foreach (var channel in channels)
        {
            devices.Add(new CoolingDevice
            {
                Id = channel.Id,
                Name = channel.Name,
                Type = channel.Kind,
                Rpm = channel.Rpm,
                Pwm = channel.DutyPercent,
            });
        }
        return new[]
        {
            new CoolingComponent { Id = DeviceId, Name = DeviceName, Type = "HydroShift2Curve", Devices = devices },
        };
    }
}

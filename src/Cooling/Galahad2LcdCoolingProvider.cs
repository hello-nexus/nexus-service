using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.JpegPanels;

namespace Nexus.Service.Cooling;

/// <summary>Bridges the Galahad II LCD's pump and coolant probe into the fan-control subsystem.</summary>
public sealed class Galahad2LcdCoolingProvider : IFanControlProvider, ICoolingProvider
{
    public static readonly string DeviceId = JpegPanelModel.GalahadIiLcd.HandlerId;

    private static readonly string PumpChannelId = DeviceId + ":pump";
    private static readonly string CoolantSensorId = DeviceId + ":coolant";

    private readonly Galahad2LcdAio _aio;

    public Galahad2LcdCoolingProvider(Galahad2LcdAio aio)
    {
        _aio = aio;
    }

    public static bool IsGalahad2LcdId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (_aio.Status is not { } status)
        {
            return Array.Empty<FanChannel>();
        }
        var duty = _aio.PumpDuty;
        return new[]
        {
            new FanChannel
            {
                Id = PumpChannelId,
                Name = "Galahad II LCD Pump",
                DutyPercent = duty ?? LianLiAioProtocol.GalahadPwmForRpm(status.PumpRpm),
                Rpm = status.PumpRpm,
                Mode = duty is null ? FanModes.Auto : FanModes.Manual,
                Kind = FanKinds.Pump,
                MinDuty = LianLiAioProtocol.GalahadPumpDutyFloor,
                DeviceId = DeviceId,
                DeviceName = _aio.DeviceName,
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
                DeviceName = _aio.DeviceName,
            },
        };
    }

    public float? ReadTemperature(string sensorId) =>
        sensorId == CoolantSensorId ? _aio.Status?.CoolantC : null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        int clamped = Math.Clamp(dutyPercent, LianLiAioProtocol.GalahadPumpDutyFloor, 100);
        DriveFanSpeed(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId)
        {
            _aio.SetPumpDuty(dutyPercent);
        }
    }

    /// <summary>The next tick hands the pump back the speed it ran at before Nexus drove it.</summary>
    public void ReleaseFan(string channelId)
    {
        if (channelId == PumpChannelId)
        {
            _aio.SetPumpDuty(null);
        }
    }

    public void ReleaseAll() => _aio.SetPumpDuty(null);

    // A calibration ramp would only floor the pump, so it is not calibrated.
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
        var pump = channels[0];
        return new[]
        {
            new CoolingComponent
            {
                Id = DeviceId,
                Name = _aio.DeviceName,
                Type = "Galahad2Lcd",
                Devices = new List<CoolingDevice>
                {
                    new CoolingDevice { Id = pump.Id, Name = pump.Name, Type = pump.Kind, Rpm = pump.Rpm, Pwm = pump.DutyPercent },
                },
            },
        };
    }
}

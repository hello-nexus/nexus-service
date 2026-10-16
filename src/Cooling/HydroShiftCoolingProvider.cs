using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.JpegPanels;

namespace Nexus.Service.Cooling;

/// <summary>Bridges the HydroShift LCD 360R's fans and pump into the fan-control subsystem; the 360S and 360TL expose nothing.</summary>
public sealed class HydroShiftCoolingProvider : IFanControlProvider, ICoolingProvider
{
    public static readonly string DeviceId = JpegPanelModel.HydroShiftLcd.HandlerId;

    private const string DeviceName = "HydroShift LCD 360R";

    private static readonly string FansChannelId = DeviceId + ":fans";
    private static readonly string PumpChannelId = DeviceId + ":pump";

    private readonly HydroShiftAio _aio;

    public HydroShiftCoolingProvider(HydroShiftAio aio)
    {
        _aio = aio;
    }

    public static bool IsHydroShiftLcdId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_aio.IsConnected)
        {
            return Array.Empty<FanChannel>();
        }
        var status = _aio.Status;
        var fanDuty = _aio.FanDuty;
        var pumpDuty = _aio.PumpDuty;
        return new[]
        {
            new FanChannel
            {
                Id = FansChannelId,
                Name = "HydroShift 360R Fans",
                DutyPercent = fanDuty ?? 0,
                Rpm = status?.FanRpm ?? 0,
                RpmUnavailable = status is null,
                Mode = fanDuty is null ? FanModes.Auto : FanModes.Manual,
                Kind = FanKinds.Fan,
                MinDuty = LianLiAioProtocol.HydroShiftFanDutyFloor,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Fans",
            },
            new FanChannel
            {
                Id = PumpChannelId,
                Name = "HydroShift 360R Pump",
                DutyPercent = pumpDuty ?? (status is { } s ? LianLiAioProtocol.HydroShiftPumpPwmForRpm(s.PumpRpm) : 0),
                Rpm = status?.PumpRpm ?? 0,
                RpmUnavailable = status is null,
                Mode = pumpDuty is null ? FanModes.Auto : FanModes.Manual,
                Kind = FanKinds.Pump,
                MinDuty = LianLiAioProtocol.HydroShiftPumpDutyFloor,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Pump",
            },
        };
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        int clamped = Clamp(channelId, dutyPercent);
        DriveFanSpeed(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId)
        {
            _aio.SetPumpDuty(dutyPercent);
        }
        else if (channelId == FansChannelId)
        {
            _aio.SetFanDuty(dutyPercent);
        }
    }

    public void ReleaseFan(string channelId)
    {
        if (channelId == PumpChannelId)
        {
            _aio.SetPumpDuty(null);
        }
        else if (channelId == FansChannelId)
        {
            _aio.SetFanDuty(null);
        }
    }

    public void ReleaseAll()
    {
        _aio.SetPumpDuty(null);
        _aio.SetFanDuty(null);
    }

    // A calibration ramp would floor the pump and stop the fans, so neither is calibrated.
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
            devices.Add(new CoolingDevice { Id = channel.Id, Name = channel.Name, Type = channel.Kind, Rpm = channel.Rpm, Pwm = channel.DutyPercent });
        }
        return new[]
        {
            new CoolingComponent
            {
                Id = DeviceId,
                Name = DeviceName,
                Type = "HydroShiftLcd",
                Devices = devices,
            },
        };
    }

    private static int Clamp(string channelId, int dutyPercent) =>
        channelId == PumpChannelId
            ? Math.Clamp(dutyPercent, LianLiAioProtocol.HydroShiftPumpDutyFloor, 100)
            : Math.Clamp(dutyPercent, LianLiAioProtocol.HydroShiftFanDutyFloor, 100);
}

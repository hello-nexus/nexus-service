using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the HydroShift II LCD-S or LCD-C over USB into the fan-control subsystem: the pump, each
/// fan header that has reported a speed, and the coolant probe as a curve source.
/// </summary>
public sealed class HydroShift2CoolingProvider : IFanControlProvider, ICoolingProvider
{
    public const string DeviceId = HydroShift2LcdDriver.Id;
    public const string DeviceName = "Lian Li HydroShift II";

    private const string PumpChannelId = DeviceId + ":pump";
    private const string FanChannelPrefix = DeviceId + ":fan";
    private const string CoolantSensorId = DeviceId + ":coolant";

    private readonly HydroShift2Aio _aio;

    public HydroShift2CoolingProvider(HydroShift2Aio aio)
    {
        _aio = aio;
    }

    public static bool IsHydroShift2Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var reading = _aio.IsAvailable ? _aio.Params : null;
        if (reading is null)
        {
            return Array.Empty<FanChannel>();
        }
        var pumpDuty = _aio.PumpDuty;
        var channels = new List<FanChannel>(1 + HydroShift2Protocol.FanSlots)
        {
            new FanChannel
            {
                Id = PumpChannelId,
                Name = "HydroShift II Pump",
                DutyPercent = pumpDuty ?? PumpDutyForRpm(reading.PumpRpm, _aio.Round),
                Rpm = reading.PumpRpm,
                Mode = pumpDuty is null ? FanModes.Auto : FanModes.Manual,
                Kind = FanKinds.Pump,
                MinDuty = HydroShift2Protocol.PumpDutyFloor,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Pump",
            },
        };
        for (int slot = 0; slot < HydroShift2Protocol.FanSlots; slot++)
        {
            if (!_aio.FanPresent(slot))
            {
                continue;
            }
            var duty = _aio.FanDuty(slot);
            channels.Add(new FanChannel
            {
                Id = FanChannelPrefix + (slot + 1),
                Name = $"HydroShift II Fan {slot + 1}",
                DutyPercent = duty ?? _aio.SentFanDuty(slot) ?? 0,
                Rpm = reading.FanRpm[slot],
                Mode = duty is null ? FanModes.Auto : FanModes.Manual,
                Kind = FanKinds.Fan,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = $"Fan {slot + 1}",
            });
        }
        return channels;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var coolant = ReadTemperature(CoolantSensorId);
        if (coolant is null)
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
                Value = coolant.Value,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
            },
        };
    }

    public float? ReadTemperature(string sensorId)
    {
        if (sensorId != CoolantSensorId || !_aio.IsAvailable)
        {
            return null;
        }
        return _aio.Params is { CoolantC: > 0 } reading ? reading.CoolantC : null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        int clamped = Clamp(channelId, dutyPercent);
        DriveFanSpeed(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (!_aio.IsAvailable)
        {
            return;
        }
        int clamped = Clamp(channelId, dutyPercent);
        if (channelId == PumpChannelId)
        {
            _aio.SetPumpDuty(clamped);
        }
        else if (FanSlotOf(channelId) is { } slot)
        {
            _aio.SetFanDuty(slot, clamped);
        }
    }

    public void ReleaseFan(string channelId)
    {
        if (channelId == PumpChannelId)
        {
            _aio.SetPumpDuty(null);
        }
        else if (FanSlotOf(channelId) is { } slot)
        {
            _aio.SetFanDuty(slot, null);
        }
    }

    public void ReleaseAll()
    {
        _aio.SetPumpDuty(null);
        for (int slot = 0; slot < HydroShift2Protocol.FanSlots; slot++)
        {
            _aio.SetFanDuty(slot, null);
        }
    }

    // A ramp to 0% would only floor the pump at its minimum rpm, so neither channel is calibrated.
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
            new CoolingComponent { Id = DeviceId, Name = DeviceName, Type = "HydroShift2", Devices = devices },
        };
    }

    /// <summary>Inverse of <see cref="HydroShift2Protocol.PumpRpmForDuty"/>, for showing the firmware's own speed as a duty.</summary>
    private static int PumpDutyForRpm(int rpm, bool round) =>
        Math.Clamp((rpm - HydroShift2Protocol.PumpMinRpm) * 100
            / (HydroShift2Protocol.PumpMaxRpmFor(round) - HydroShift2Protocol.PumpMinRpm), 0, 100);

    private static int Clamp(string channelId, int dutyPercent) =>
        Math.Clamp(dutyPercent, channelId == PumpChannelId ? HydroShift2Protocol.PumpDutyFloor : 0, 100);

    private static int? FanSlotOf(string channelId) =>
        channelId.StartsWith(FanChannelPrefix, StringComparison.Ordinal)
        && int.TryParse(channelId.AsSpan(FanChannelPrefix.Length), out var n)
        && n >= 1 && n <= HydroShift2Protocol.FanSlots
            ? n - 1
            : null;
}

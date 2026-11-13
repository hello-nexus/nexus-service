using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Ryujin;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the ASUS ROG Ryujin III Extreme into the fan-control subsystem: the pump, the
/// pump-head fan, and the coolant probe as a curve source.
/// </summary>
public sealed class RyujinCoolingProvider : IFanControlProvider, ICoolingProvider
{
    public const string DeviceId = "asus-ryujin-lcd";
    public const string DeviceName = "ASUS ROG Ryujin III Extreme";

    private const string PumpChannelId = DeviceId + ":pump";
    private const string FanChannelId = DeviceId + ":fan";
    private const string LiquidSensorId = DeviceId + ":liquid";

    private readonly RyujinCoolingHub _hub;

    private readonly object _ctrlLock = new();
    // Commanded duties live apart from the snapshot, which carries what the cooler reports.
    private bool _pumpSw;
    private bool _fanSw;
    private int _pumpCommanded = RyujinCoolingProtocol.PumpDutyFloor;
    private int _fanCommanded;

    public RyujinCoolingProvider(RyujinCoolingHub hub)
    {
        _hub = hub;
    }

    public static bool IsRyujinId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<FanChannel>();
        }
        var snap = _hub.Snapshot;
        bool fresh = _hub.StatusFresh;
        bool pumpSw;
        bool fanSw;
        int pumpWant;
        int fanWant;
        lock (_ctrlLock)
        {
            pumpSw = _pumpSw;
            fanSw = _fanSw;
            pumpWant = _pumpCommanded;
            fanWant = _fanCommanded;
        }
        return new[]
        {
            new FanChannel
            {
                Id = PumpChannelId,
                Name = "Ryujin III Pump",
                DutyPercent = snap.PumpDuty >= 0 ? snap.PumpDuty : pumpSw ? pumpWant : RyujinCoolingProtocol.SafeDuty,
                Rpm = fresh ? snap.PumpRpm : 0,
                RpmUnavailable = !fresh,
                Mode = pumpSw ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Pump,
                MinDuty = RyujinCoolingProtocol.PumpDutyFloor,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Pump",
            },
            new FanChannel
            {
                Id = FanChannelId,
                Name = "Ryujin III Fan",
                DutyPercent = snap.FanDuty >= 0 ? snap.FanDuty : fanSw ? fanWant : RyujinCoolingProtocol.SafeDuty,
                Rpm = fresh ? snap.FanRpm : 0,
                RpmUnavailable = !fresh,
                Mode = fanSw ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Fan,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Pump fan",
            },
        };
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var liquid = ReadTemperature(LiquidSensorId);
        if (liquid is null)
        {
            return Array.Empty<TemperatureSource>();
        }
        return new[]
        {
            new TemperatureSource
            {
                Id = LiquidSensorId,
                Name = "Liquid",
                Category = "Cooler",
                Value = liquid.Value,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
            },
        };
    }

    public float? ReadTemperature(string sensorId)
    {
        if (sensorId != LiquidSensorId || !_hub.IsConnected)
        {
            return null;
        }
        var snap = _hub.Snapshot;
        return _hub.StatusFresh && snap.LiquidTempC > 0 ? (float)snap.LiquidTempC : null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId)
        {
            int clamped = RyujinCoolingProtocol.ClampPump(dutyPercent);
            Apply(pump: clamped, fan: null);
            return clamped;
        }
        if (channelId == FanChannelId)
        {
            int clamped = RyujinCoolingProtocol.ClampFan(dutyPercent);
            Apply(pump: null, fan: clamped);
            return clamped;
        }
        return dutyPercent;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) => SetFanSpeed(channelId, dutyPercent);

    /// <summary>
    /// The cooler has no firmware curve to hand back to. A released pump returns to Armoury Crate's
    /// default duty so it is never left slow; a released fan keeps its duty, as the Kraken and
    /// Galahad do, so the Off preset never makes it louder.
    /// </summary>
    public void ReleaseFan(string channelId)
    {
        if (!IsRyujinId(channelId))
        {
            return;
        }
        Release(pump: channelId == PumpChannelId, fan: channelId == FanChannelId);
    }

    public void ReleaseAll() => Release(pump: true, fan: true);

    // A ramp to 0% would stall the pump, so neither channel is calibrated.
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
            new CoolingComponent { Id = DeviceId, Name = DeviceName, Type = "Ryujin", Devices = devices },
        };
    }

    /// <summary>Resends the commanded duties; the worker calls it once per keepalive cycle.</summary>
    public void ReassertControl()
    {
        if (!_hub.IsConnected)
        {
            return;
        }
        bool pumpSw;
        bool fanSw;
        lock (_ctrlLock)
        {
            pumpSw = _pumpSw;
            fanSw = _fanSw;
        }
        if (!pumpSw && !fanSw)
        {
            return;
        }
        if (!WriteDuties())
        {
            ServiceLog.Warn("[asus-ryujin-cooling] keepalive duty write returned false");
        }
    }

    private void Apply(int? pump, int? fan)
    {
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn("[asus-ryujin-cooling] duty write dropped: cooler not connected");
            return;
        }
        lock (_ctrlLock)
        {
            if (pump is { } p)
            {
                _pumpSw = true;
                _pumpCommanded = p;
            }
            if (fan is { } f)
            {
                _fanSw = true;
                _fanCommanded = f;
            }
        }
        if (!WriteDuties())
        {
            ServiceLog.Warn("[asus-ryujin-cooling] duty write returned false");
        }
    }

    private void Release(bool pump, bool fan)
    {
        bool releasePump;
        bool releaseFan;
        lock (_ctrlLock)
        {
            releasePump = pump && _pumpSw;
            releaseFan = fan && _fanSw;
            if (releasePump)
            {
                _pumpSw = false;
                _pumpCommanded = RyujinCoolingProtocol.SafeDuty;
            }
            if (releaseFan)
            {
                _fanSw = false;
            }
        }
        if (!releasePump || !_hub.IsConnected)
        {
            return;
        }
        if (!WriteDuties(releasePump))
        {
            ServiceLog.Warn("[asus-ryujin-cooling] safe duty write on release returned false");
        }
    }

    /// <summary>
    /// The report carries both channels, so the one Nexus is not driving rides at what the cooler
    /// last reported, or the safe duty before it has reported anything.
    /// </summary>
    private bool WriteDuties(bool releasePump = false)
    {
        var snap = _hub.Snapshot;
        int pump;
        int fan;
        lock (_ctrlLock)
        {
            pump = releasePump ? RyujinCoolingProtocol.SafeDuty
                : _pumpSw ? _pumpCommanded
                : snap.PumpDuty >= 0 ? snap.PumpDuty : RyujinCoolingProtocol.SafeDuty;
            fan = _fanSw ? _fanCommanded
                : snap.FanDuty >= 0 ? snap.FanDuty : RyujinCoolingProtocol.SafeDuty;
        }
        return _hub.SetDuty(pump, fan);
    }
}

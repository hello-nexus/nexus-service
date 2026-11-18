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
    public const string DeviceId = RyujinCoolingProtocol.HandlerId;
    public const string DeviceName = "ASUS ROG Ryujin III Extreme";

    private const string PumpChannelId = RyujinCoolingProtocol.ChannelPrefix + ":pump";
    private const string FanChannelId = RyujinCoolingProtocol.ChannelPrefix + ":fan";
    private const string LiquidSensorId = RyujinCoolingProtocol.ChannelPrefix + ":liquid";

    private readonly RyujinCoolingHub _hub;

    private readonly object _ctrlLock = new();
    // Commanded duties live apart from the snapshot, which carries what the cooler reports.
    private bool _pumpSw;
    private bool _pumpWritten;
    private bool _fanSw;
    private int _pumpCommanded = RyujinCoolingProtocol.PumpDutyFloor;
    private int _fanCommanded;

    public RyujinCoolingProvider(RyujinCoolingHub hub)
    {
        _hub = hub;
    }

    public static bool IsRyujinId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(RyujinCoolingProtocol.ChannelPrefix + ":", StringComparison.Ordinal);

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
                DutyPercent = snap.PumpDuty >= 0 ? snap.PumpDuty : pumpSw ? pumpWant : 0,
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
                DutyPercent = snap.FanDuty >= 0 ? snap.FanDuty : fanSw ? fanWant : 0,
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

    public int SetFanSpeed(string channelId, int dutyPercent) => Write(channelId, dutyPercent, "route");

    public void DriveFanSpeed(string channelId, int dutyPercent) => Write(channelId, dutyPercent, "engine");

    private int Write(string channelId, int dutyPercent, string reason)
    {
        if (channelId == PumpChannelId)
        {
            int clamped = RyujinCoolingProtocol.ClampPump(dutyPercent);
            Apply(pump: clamped, fan: null, reason);
            return clamped;
        }
        if (channelId == FanChannelId)
        {
            int clamped = RyujinCoolingProtocol.ClampFan(dutyPercent);
            Apply(pump: null, fan: clamped, reason);
            return clamped;
        }
        return dutyPercent;
    }

    /// <summary>
    /// A mode, preset or profile change releases and then re-drives channels; writing on release
    /// would put a burst on the pump in between. The cooler has no firmware curve to hand back
    /// to, so a release only stops Nexus resending and the last duty stays, as with the Kraken
    /// and Galahad. The pump's safe duty is written by <see cref="ReleaseForDisconnect"/> alone.
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

    /// <summary>
    /// Called when Nexus Control goes off, the cooler goes away or the service stops: a pump Nexus
    /// wrote to is left at the safe duty rather than at whatever a low setting last put on it.
    /// </summary>
    public void ReleaseForDisconnect()
    {
        bool pumpWritten;
        lock (_ctrlLock)
        {
            pumpWritten = _pumpWritten || _hub.PumpMoved;
            _pumpWritten = false;
            _pumpSw = false;
            _fanSw = false;
        }
        if (!pumpWritten || !_hub.IsConnected)
        {
            return;
        }
        _hub.Request(RyujinCoolingProtocol.SafeDuty, null, "disconnect");
    }

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
        int? pump;
        int? fan;
        lock (_ctrlLock)
        {
            pump = _pumpSw ? _pumpCommanded : null;
            fan = _fanSw ? _fanCommanded : null;
        }
        if (pump is null && fan is null)
        {
            return;
        }
        _hub.Request(pump, fan, RyujinCoolingHub.KeepaliveReason);
    }

    private void Apply(int? pump, int? fan, string reason)
    {
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn("[asus-ryujin-cooling] duty write dropped: cooler not connected");
            return;
        }
        int? pumpOut;
        int? fanOut;
        lock (_ctrlLock)
        {
            if (pump is { } p)
            {
                _pumpSw = true;
                _pumpWritten = true;
                _pumpCommanded = p;
            }
            if (fan is { } f)
            {
                _fanSw = true;
                _fanCommanded = f;
            }
            pumpOut = _pumpSw ? _pumpCommanded : null;
            fanOut = _fanSw ? _fanCommanded : null;
        }
        _hub.Request(pumpOut, fanOut, reason);
    }

    private void Release(bool pump, bool fan)
    {
        bool releasedPump;
        bool releasedFan;
        lock (_ctrlLock)
        {
            releasedPump = pump && _pumpSw;
            releasedFan = fan && _fanSw;
            if (releasedPump)
            {
                _pumpSw = false;
            }
            if (releasedFan)
            {
                _fanSw = false;
            }
        }
        if (releasedPump || releasedFan)
        {
            ServiceLog.Info($"[asus-ryujin-cooling] release {(releasedPump ? "pump " : "")}{(releasedFan ? "fan" : "")}".TrimEnd() + ": no write, duty stays");
        }
    }
}

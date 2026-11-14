using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the NZXT Kraken into the fan-control subsystem: a pump channel, a fan channel,
/// and the coolant probe as a curve source.
/// </summary>
public sealed class KrakenCoolingProvider : IFanControlProvider, ICoolingProvider
{
    public const string DeviceId = "nzxt-kraken";
    public const string DeviceName = "NZXT Kraken";

    private const string FanChannelId = "nzxt-kraken:fan";
    private const string PumpChannelId = "nzxt-kraken:pump";
    private const string LiquidSensorId = "nzxt-kraken:liquid";

    private readonly KrakenHub _hub;

    private readonly object _ctrlLock = new();
    // Commanded duties, kept separately from the snapshot: the snapshot carries what the
    // cooler REPORTS, and re-asserting that would latch whatever it currently reads back
    // rather than what the user asked for.
    private bool _fanSw;
    private bool _pumpSw;
    private int _fanCommanded;
    private int _pumpCommanded = KrakenProtocol.PumpDutyFloor;

    public KrakenCoolingProvider(KrakenHub hub)
    {
        _hub = hub;
    }

    public static bool IsKrakenId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("nzxt-kraken:", StringComparison.Ordinal);

    // IFanControlProvider

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<FanChannel>();
        }
        var snap = _hub.Snapshot;
        bool fanSw;
        bool pumpSw;
        lock (_ctrlLock)
        {
            fanSw = _fanSw;
            pumpSw = _pumpSw;
        }
        return new[]
        {
            new FanChannel
            {
                Id = FanChannelId,
                Name = "Kraken Fans",
                // The cooler reports its live duty whether or not we are driving it.
                DutyPercent = snap.FanDuty,
                Rpm = snap.FanRpm,
                Mode = fanSw ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Fan,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Fans",
            },
            new FanChannel
            {
                Id = PumpChannelId,
                Name = "Kraken Pump",
                DutyPercent = snap.PumpDuty,
                Rpm = snap.PumpRpm,
                Mode = pumpSw ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Pump,
                MinDuty = KrakenProtocol.PumpDutyFloor,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Pump",
            },
        };
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<TemperatureSource>();
        }
        var snap = _hub.Snapshot;
        if (snap.LiquidTempC <= 0 || !_hub.StatusFresh)
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
                Value = (float)snap.LiquidTempC,
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
        return snap.LiquidTempC > 0 && _hub.StatusFresh ? (float)snap.LiquidTempC : null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId)
        {
            int clamped = Math.Clamp(dutyPercent, KrakenProtocol.PumpDutyFloor, 100);
            ApplyPumpWrite(clamped);
            return clamped;
        }
        if (channelId == FanChannelId)
        {
            int clamped = Math.Clamp(dutyPercent, 0, 100);
            ApplyFanWrite(clamped);
            return clamped;
        }
        return dutyPercent;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId)
        {
            ApplyPumpWrite(Math.Clamp(dutyPercent, KrakenProtocol.PumpDutyFloor, 100));
        }
        else if (channelId == FanChannelId)
        {
            ApplyFanWrite(Math.Clamp(dutyPercent, 0, 100));
        }
    }

    public void ReleaseFan(string channelId)
    {
        if (!IsKrakenId(channelId))
        {
            return;
        }
        lock (_ctrlLock)
        {
            if (channelId == FanChannelId)
            {
                _fanSw = false;
            }
            else if (channelId == PumpChannelId)
            {
                _pumpSw = false;
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _fanSw = false;
            _pumpSw = false;
        }
    }

    // Calibration ramps duty to 0, which would stall the pump; neither channel is calibrated.
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ICoolingProvider

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<CoolingComponent>();
        }
        var snap = _hub.Snapshot;
        return new[]
        {
            new CoolingComponent
            {
                Id = DeviceId,
                Name = DeviceName,
                Type = "Kraken",
                Devices = new List<CoolingDevice>
                {
                    new CoolingDevice
                    {
                        Id = FanChannelId,
                        Name = "Kraken Fans",
                        Type = "Fan",
                        Rpm = snap.FanRpm,
                        Pwm = snap.FanDuty,
                    },
                    new CoolingDevice
                    {
                        Id = PumpChannelId,
                        Name = "Kraken Pump",
                        Type = "Pump",
                        Rpm = snap.PumpRpm,
                        Pwm = snap.PumpDuty,
                    },
                },
            },
        };
    }

    /// <summary>
    /// Re-applies a commanded duty only when the cooler has drifted off it. The firmware
    /// holds a curve on its own, so re-sending every poll would put two 512-byte writes a
    /// second on the pipe the lighting writer and any LCD upload share.
    /// </summary>
    public void ReassertControl()
    {
        if (!_hub.IsConnected)
        {
            return;
        }
        bool fanSw;
        bool pumpSw;
        int fanWant;
        int pumpWant;
        lock (_ctrlLock)
        {
            fanSw = _fanSw;
            pumpSw = _pumpSw;
            fanWant = _fanCommanded;
            pumpWant = _pumpCommanded;
        }
        var snap = _hub.Snapshot;
        if (fanSw && snap.FanDuty != fanWant && !_hub.SetFanDuty(fanWant))
        {
            ServiceLog.Warn($"[nzxt-kraken-cooling] reassert fan duty {fanWant} returned false");
        }
        if (pumpSw && snap.PumpDuty != pumpWant && !_hub.SetPumpDuty(pumpWant))
        {
            ServiceLog.Warn($"[nzxt-kraken-cooling] reassert pump duty {pumpWant} returned false");
        }
    }

    private void ApplyFanWrite(int duty)
    {
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn("[nzxt-kraken-cooling] fan write dropped: hub not connected");
            return;
        }
        lock (_ctrlLock)
        {
            _fanSw = true;
            _fanCommanded = duty;
        }
        if (!_hub.SetFanDuty(duty))
        {
            ServiceLog.Warn($"[nzxt-kraken-cooling] SetFanDuty {duty} returned false");
        }
    }

    private void ApplyPumpWrite(int duty)
    {
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn("[nzxt-kraken-cooling] pump write dropped: hub not connected");
            return;
        }
        lock (_ctrlLock)
        {
            _pumpSw = true;
            _pumpCommanded = duty;
        }
        if (!_hub.SetPumpDuty(duty))
        {
            ServiceLog.Warn($"[nzxt-kraken-cooling] SetPumpDuty {duty} returned false");
        }
    }
}

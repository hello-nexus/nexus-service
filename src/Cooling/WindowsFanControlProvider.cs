using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Cooling;
using Qos.Service.Persistence;
using Qos.Service.Sensors;
using LibreHardwareMonitor.Hardware;

namespace Qos.Service.Cooling;

/// <summary>
/// Windows fan control provider backed by LibreHardwareMonitor. Discovers
/// controllable fan channels from motherboard SuperIO chips and GPU hardware,
/// reads RPM and duty %, and writes fan duty cycles.
///
/// Fan channels are discovered by pairing SensorType.Fan (RPM reader) with
/// SensorType.Control (duty % reader/writer) sensors from the same SubHardware.
///
/// Also implements ICoolingProvider so GetAll() returns real hardware data.
/// </summary>
public sealed class WindowsFanControlProvider : IFanControlProvider, ICoolingProvider
{
    private readonly LhmComputer _lhm;
    private readonly IConfigStore _config;
    private List<ChannelMapping>? _channels;
    private readonly object _discoveryLock = new();
    private bool _manualSpeedsRestored;
    private bool _lhmWarmedUp;
    // Total warmup time budget across the whole process lifetime. Per-call
    // warmup stops at 1.5s; if motherboard SubHardware still hasn't shown up,
    // the next discovery call picks up where this one left off -- up to this
    // cap, after which we stop probing and accept whatever LHM surfaces.
    private int _warmupBudgetRemainingMs = 15_000;

    // Track which channels are under software control
    private readonly HashSet<string> _softwareControlled = new();

    public WindowsFanControlProvider(LhmComputer lhm, IConfigStore config)
    {
        _lhm = lhm;
        _config = config;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        var mappings = EnsureDiscovered();
        var settings = _config.Load();
        var calibrations = settings.Cooling.FanCalibrations;
        // Fans bound to any curve output are reported as "Curve" so the sidebar
        // dot + Cooling view restoration can tell curve-driven fans apart from
        // user-set bias speeds. _softwareControlled alone cannot distinguish
        // them because the engine's DriveFanSpeed path adds to that set too.
        var curveOutputs = new HashSet<string>();
        foreach (var c in settings.Cooling.Curves)
        {
            foreach (var o in c.Outputs)
            {
                curveOutputs.Add(o.Id);
            }
        }
        var result = new List<FanChannel>(mappings.Count);

        foreach (var m in mappings)
        {
            string mode;
            if (curveOutputs.Contains(m.Id))
            {
                mode = FanModes.Curve;
            }
            else if (_softwareControlled.Contains(m.Id))
            {
                mode = FanModes.Manual;
            }
            else
            {
                mode = FanModes.Auto;
            }
            var ch = new FanChannel
            {
                Id = m.Id,
                Name = m.Name,
                DutyPercent = (int)(m.ControlSensor.Value ?? 0f),
                Rpm = (int)(m.FanSensor.Value ?? 0f),
                Mode = mode,
            };
            if (calibrations.TryGetValue(m.Id, out var cal))
            {
                ch.MinRpm = cal.MinRpm;
                ch.MaxRpm = cal.MaxRpm;
                ch.MinDuty = cal.MinDuty;
                ch.Classification = cal.Classification;
            }
            result.Add(ch);
        }

        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        var sources = new List<TemperatureSource>();

        foreach (var hw in _lhm.Instance.Hardware)
        {
            var category = MapCategory(hw.HardwareType);
            AddTempSensors(sources, hw, category);

            foreach (var sub in hw.SubHardware)
                AddTempSensors(sources, sub, category);
        }

        return sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));

        foreach (var hw in _lhm.Instance.Hardware)
        {
            var sensor = FindSensorById(hw, sensorId);
            if (sensor is not null) return sensor.Value;
        }

        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        var mappings = EnsureDiscovered();
        var mapping = mappings.FirstOrDefault(m => m.Id == channelId);
        if (mapping is null) return clamped;

        mapping.ControlSensor.Control.SetSoftware(clamped);
        _softwareControlled.Add(channelId);
        _config.Update(s => s.Cooling.ManualSpeeds[channelId] = clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        var mappings = EnsureDiscovered();
        var mapping = mappings.FirstOrDefault(m => m.Id == channelId);
        if (mapping is null) return;
        mapping.ControlSensor.Control.SetSoftware(clamped);
        _softwareControlled.Add(channelId);
        // No ManualSpeeds write — see interface doc.
    }

    public void ReleaseFan(string channelId)
    {
        var mappings = EnsureDiscovered();
        var mapping = mappings.FirstOrDefault(m => m.Id == channelId);
        if (mapping is null) return;

        mapping.ControlSensor.Control.SetDefault();
        _softwareControlled.Remove(channelId);
        _config.Update(s => s.Cooling.ManualSpeeds.Remove(channelId));
    }

    public void ReleaseAll()
    {
        var mappings = EnsureDiscovered();
        foreach (var m in mappings)
        {
            try { m.ControlSensor.Control.SetDefault(); }
            catch { /* swallow */ }
        }
        _softwareControlled.Clear();
        _config.Update(s => s.Cooling.ManualSpeeds.Clear());
    }

    // ── Calibration ──

    public async Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        var mappings = EnsureDiscovered();
        var toCalibrate = fanIds.Count == 0
            ? mappings
            : mappings.Where(m => fanIds.Contains(m.Id)).ToList();

        var calibrator = new FanCalibrator(_lhm);
        var tasks = toCalibrate.Select(m =>
            calibrator.CalibrateOneAsync(m.Id, m.ControlSensor, m.FanSensor, progress, ct));

        var results = await Task.WhenAll(tasks);

        _config.Update(s =>
        {
            foreach (var r in results)
                s.Cooling.FanCalibrations[r.FanId] = r;
        });

        return results;
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        var channels = GetFanChannels();
        if (channels.Count == 0) return Array.Empty<CoolingComponent>();

        var component = new CoolingComponent
        {
            Id = "motherboard-fans",
            Name = "Motherboard Fans",
            Type = "Motherboard",
            Devices = channels.Select(ch => new CoolingDevice
            {
                Id = ch.Id,
                Name = ch.Name,
                Type = "Fan",
                Speed = ch.DutyPercent,
                Rpm = ch.Rpm,
                Pwm = ch.DutyPercent,
            }).ToList(),
        };

        return new[] { component };
    }

    // ── Discovery ──

    private List<ChannelMapping> EnsureDiscovered()
    {
        // LHM surfaces motherboard SubHardware (SuperIO chips housing fan
        // controls) lazily -- the first Update() after process start often
        // yields GPU-only. Re-discovering on every call self-heals that,
        // because once LHM has enumerated the motherboard, subsequent calls
        // see the full topology. The cost is one LHM.Update plus a couple of
        // Linq passes per call; both are already in GetFanChannels' budget.
        //
        // RestoreSavedManualSpeeds is gated so it runs exactly once, the
        // first time discovery returns at least one channel. Otherwise every
        // call would re-apply persisted speeds and fight concurrent edits.
        lock (_discoveryLock)
        {
            var fresh = DiscoverChannels();
            if (!_manualSpeedsRestored && fresh.Count > 0)
            {
                RestoreSavedManualSpeeds(fresh);
                _manualSpeedsRestored = true;
            }
            _channels = fresh;
            return _channels;
        }
    }

    private void RestoreSavedManualSpeeds(List<ChannelMapping> mappings)
    {
        var saved = _config.Load().Cooling.ManualSpeeds;
        foreach (var (channelId, speed) in saved)
        {
            var mapping = mappings.FirstOrDefault(m => m.Id == channelId);
            if (mapping is null) continue;
            mapping.ControlSensor.Control.SetSoftware(speed);
            _softwareControlled.Add(channelId);
        }
        if (saved.Count > 0)
            Console.Error.WriteLine($"[fan-control] restored {saved.Count} manual fan speed(s) from config");
    }

    private List<ChannelMapping> DiscoverChannels()
    {
        // LHM enumerates motherboard SubHardware (SuperIO chips that host fan
        // controls) lazily: the first Update() after process start can return
        // a motherboard node with an empty SubHardware array. Without a
        // warmup, we'd snapshot and cache a GPU-only topology. Poll briefly
        // each discovery call until motherboard SubHardware appears -- and
        // critically, do NOT mark warmup "done" if we time out. A later call
        // picks up where this one left off (drawing from a shared budget),
        // so slow-starting LHM environments self-heal as the user interacts.
        if (!_lhmWarmedUp && _warmupBudgetRemainingMs > 0)
        {
            var perCallCap = Math.Min(1500, _warmupBudgetRemainingMs);
            var start = Environment.TickCount;
            while (Environment.TickCount - start < perCallCap)
            {
                _lhm.Update();
                var mobo = _lhm.Instance.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
                if (mobo is not null && mobo.SubHardware.Length > 0)
                {
                    _lhmWarmedUp = true;
                    break;
                }
                Thread.Sleep(150);
            }
            _warmupBudgetRemainingMs -= Environment.TickCount - start;
        }

        _lhm.Update(TimeSpan.FromMilliseconds(100));
        var result = new List<ChannelMapping>();

        foreach (var hw in _lhm.Instance.Hardware)
        {
            // Motherboard SubHardware (SuperIO chips) have the fan controls
            if (hw.HardwareType == HardwareType.Motherboard)
            {
                foreach (var sub in hw.SubHardware)
                    DiscoverFromHardware(result, sub, "Motherboard");
            }

            // GPU fans
            if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                DiscoverFromHardware(result, hw, "GPU");
        }

        Console.Error.WriteLine($"[fan-control] discovered {result.Count} controllable fan channel(s)");
        foreach (var ch in result)
            Console.Error.WriteLine($"[fan-control]   {ch.Name} ({ch.Id})");

        return result;
    }

    private static void DiscoverFromHardware(List<ChannelMapping> result, IHardware hw, string prefix)
    {
        var fans = hw.Sensors
            .Where(s => s.SensorType == SensorType.Fan)
            .OrderBy(s => s.Index)
            .ToList();

        var controls = hw.Sensors
            .Where(s => s.SensorType == SensorType.Control)
            .OrderBy(s => s.Index)
            .ToList();

        var count = Math.Min(fans.Count, controls.Count);
        for (int i = 0; i < count; i++)
        {
            // Only include channels that have a Control interface
            if (controls[i].Control is null) continue;

            var name = fans[i].Name;
            if (!name.Contains(prefix, StringComparison.OrdinalIgnoreCase) && prefix != "Motherboard")
                name = $"{prefix} {name}";

            result.Add(new ChannelMapping
            {
                Id = controls[i].Identifier.ToString(),
                Name = name,
                FanSensor = fans[i],
                ControlSensor = controls[i],
            });
        }
    }

    // ── Helpers ──

    private static void AddTempSensors(List<TemperatureSource> list, IHardware hw, string category)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature) continue;
            list.Add(new TemperatureSource
            {
                Id = sensor.Identifier.ToString(),
                Name = sensor.Name,
                Category = category,
                Value = sensor.Value ?? 0f,
            });
        }
    }

    private static ISensor? FindSensorById(IHardware hw, string sensorId)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Identifier.ToString() == sensorId)
                return sensor;
        }
        foreach (var sub in hw.SubHardware)
        {
            var found = FindSensorById(sub, sensorId);
            if (found is not null) return found;
        }
        return null;
    }

    private static string MapCategory(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Motherboard => "Motherboard",
        HardwareType.Storage => "Storage",
        _ => "Other",
    };

    private sealed class ChannelMapping
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required ISensor FanSensor { get; init; }
        public required ISensor ControlSensor { get; init; }
    }
}

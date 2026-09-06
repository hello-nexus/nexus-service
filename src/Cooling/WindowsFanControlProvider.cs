using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using LibreHardwareMonitor.Hardware;

namespace Nexus.Service.Cooling;

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
    private bool _lhmWarmedUp;
    // Total warmup time budget across the whole process lifetime. Per-call
    // warmup stops at 1.5s; if motherboard SubHardware still hasn't shown up,
    // the next discovery call picks up where this one left off -- up to this
    // cap, after which we stop probing and accept whatever LHM surfaces.
    private int _warmupBudgetRemainingMs = 15_000;

    // Track which channels are under software control
    private readonly HashSet<string> _softwareControlled = new();
    // Track which channel id-set we last logged so re-discovery on every
    // GetFanChannels call doesn't print the same 6-line block at the per-tick
    // poll rate. Only emit when the discovered set actually changes (channel
    // added / removed / renamed). Accessed only from DiscoverChannels(),
    // which itself only runs under _discoveryLock via EnsureDiscovered().
    private HashSet<string> _lastLoggedChannelIds = new(StringComparer.Ordinal);

    // Fans leased by an in-flight calibration, with the control state each held
    // when the lease was taken. A leased fan ignores every other duty write, and
    // the calibrator only writes while it still holds the lease - so the ramp
    // measures the duty it commanded and nothing else, and revoking the lease
    // (ReleaseAll, on shutdown or a profile switch) aborts the run. Both
    // directions are enforced here, at the write path, so no caller can opt out.
    private readonly Dictionary<string, (ChannelMapping Mapping, ControlMode Mode, float Value)> _calibrationLease = new(StringComparer.Ordinal);
    private readonly object _calibrationLock = new();

    public WindowsFanControlProvider(LhmComputer lhm, IConfigStore config)
    {
        _lhm = lhm;
        _config = config;
    }

    private bool IsLeasedForCalibration(string channelId)
    {
        lock (_calibrationLock) return _calibrationLease.ContainsKey(channelId);
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        _lhm.Update();
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
                RpmSensorId = m.FanSensorId,
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
        _lhm.Update();
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
        _lhm.Update();

        foreach (var hw in _lhm.Instance.Hardware)
        {
            var sensor = FindSensorById(hw, sensorId);
            if (sensor is null) continue;

            return sensor.Value;
        }

        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        // Dropped whole, not just the hardware write: recording ManualSpeeds
        // here would outlive the lease and contradict the state the calibration
        // restores.
        if (IsLeasedForCalibration(channelId)) return clamped;
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
        if (IsLeasedForCalibration(channelId)) return;
        var mappings = EnsureDiscovered();
        var mapping = mappings.FirstOrDefault(m => m.Id == channelId);
        if (mapping is null) return;
        mapping.ControlSensor.Control.SetSoftware(clamped);
        _softwareControlled.Add(channelId);
        // No ManualSpeeds write - see interface doc.
    }

    public void ReleaseFan(string channelId)
    {
        if (IsLeasedForCalibration(channelId)) return;
        var mappings = EnsureDiscovered();
        var mapping = mappings.FirstOrDefault(m => m.Id == channelId);
        if (mapping is null) return;

        mapping.ControlSensor.Control.SetDefault();
        _softwareControlled.Remove(channelId);
        _config.Update(s => s.Cooling.ManualSpeeds.Remove(channelId));
    }

    public void ReleaseAll()
    {
        // Revoke without restoring, before the loop below: this runs on shutdown
        // and profile switch, where every fan must end on BIOS control. Revoking
        // also stops an in-flight ramp from re-driving a fan after it is released.
        EndCalibrationLease(restorePriorState: false);
        var mappings = EnsureDiscovered();
        foreach (var m in mappings)
        {
            try { m.ControlSensor.Control.SetDefault(); }
            catch { /* swallow */ }
        }
        _softwareControlled.Clear();
        // Cooling.ManualSpeeds is preserved: ReleaseAll runs on shutdown and
        // profile switch, where the persisted intent must survive so
        // CurveEngine's replay can re-apply it. The explicit per-fan BIOS
        // choice goes through ReleaseFan, which removes its entry.
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

        FanCalibration[] results;
        BeginCalibrationLease(toCalibrate);
        try
        {
            var tasks = toCalibrate.Select(m => FanCalibrator.CalibrateOneAsync(
                m.Id,
                duty => WriteCalibrationDuty(m, duty),
                () => ReadCalibrationRpm(m),
                progress,
                ct));

            results = await Task.WhenAll(tasks);
        }
        finally
        {
            EndCalibrationLease(restorePriorState: true);
        }

        // Entries for fans that no longer enumerate are left alone: they are
        // inert, since the only reader looks each one up by a live channel's id.
        // Pruning them is not safe - LHM activates a fan sensor only once its
        // tach reports, so a fan that is merely stopped is indistinguishable
        // from one that is gone.
        _config.Update(s =>
        {
            foreach (var r in results)
                s.Cooling.FanCalibrations[r.FanId] = r;
        });

        return results;
    }

    /// <summary>Takes the fans, recording the control state each held so the run
    /// can hand it back - which is what keeps _softwareControlled truthful across
    /// a calibration without any bookkeeping of its own. Single-flight:
    /// CalibrationRunner.Start admits one run at a time.</summary>
    private void BeginCalibrationLease(IReadOnlyList<ChannelMapping> toCalibrate)
    {
        // Snapshot before taking the lock so a throw mid-read cannot leave fans
        // half-leased, which would silently swallow every later write to them.
        var snapshot = new Dictionary<string, (ChannelMapping, ControlMode, float)>(toCalibrate.Count, StringComparer.Ordinal);
        foreach (var m in toCalibrate)
        {
            var control = m.ControlSensor.Control;
            snapshot[m.Id] = (m, control.ControlMode, control.SoftwareValue);
        }

        lock (_calibrationLock)
        {
            _calibrationLease.Clear();
            foreach (var (id, entry) in snapshot) _calibrationLease[id] = entry;
        }
    }

    /// <summary>Hands the fans back. Idempotent: ReleaseAll revokes a live lease
    /// before CalibrateAsync's finally reaches it, and the second call no-ops.</summary>
    private void EndCalibrationLease(bool restorePriorState)
    {
        (ChannelMapping Mapping, ControlMode Mode, float Value)[] leased;
        lock (_calibrationLock)
        {
            if (_calibrationLease.Count == 0) return;
            leased = _calibrationLease.Values.ToArray();
            _calibrationLease.Clear();
        }

        if (!restorePriorState) return;

        foreach (var (mapping, mode, value) in leased)
        {
            try
            {
                if (mode == ControlMode.Software) mapping.ControlSensor.Control.SetSoftware(value);
                else mapping.ControlSensor.Control.SetDefault();
            }
            catch { /* a header that vanished mid-ramp must not strand the others */ }
        }
    }

    private void WriteCalibrationDuty(ChannelMapping mapping, int duty)
    {
        lock (_calibrationLock)
        {
            // Revoked mid-ramp by ReleaseAll (shutdown or profile switch).
            // Aborting rather than skipping the write is what keeps the run from
            // completing: the remaining steps would sample whatever now drives
            // the fan and persist that over a valid calibration.
            if (!_calibrationLease.ContainsKey(mapping.Id))
                throw new OperationCanceledException($"calibration lease revoked for {mapping.Id}");
            mapping.ControlSensor.Control.SetSoftware(Math.Clamp(duty, 0, 100));
        }
    }

    private int ReadCalibrationRpm(ChannelMapping mapping)
    {
        _lhm.Update(SensorRefresh.Fast);
        return (int)(mapping.FanSensor.Value ?? 0f);
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        _lhm.Update();
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
        // Persisted manual duties are replayed by CurveEngine as channels
        // appear (presence-gated, per-channel), not here: a one-shot restore
        // pass keyed to the first non-empty discovery missed every channel
        // the partial first enumeration didn't include.
        lock (_discoveryLock)
        {
            _channels = DiscoverChannels();
            return _channels;
        }
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
                _lhm.Update(SensorRefresh.Force);
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

        _lhm.Update();
        var result = new List<ChannelMapping>();
        // Per-chip raw tach/control layout, logged only when the channel set
        // changes (below). This is the diagnostic for a tach/PWM index
        // mismatch - the class of bug the by-index pairing fixes.
        var layouts = new List<string>();

        foreach (var hw in _lhm.Instance.Hardware)
        {
            // Motherboard SubHardware (SuperIO chips) have the fan controls
            if (hw.HardwareType == HardwareType.Motherboard)
            {
                foreach (var sub in hw.SubHardware)
                    DiscoverFromHardware(result, sub, "Motherboard", layouts);
            }

            // GPU fans
            if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                DiscoverFromHardware(result, hw, "GPU", layouts);
        }

        var currentIds = new HashSet<string>(result.Select(c => $"{c.Id}|{c.Name}"), StringComparer.Ordinal);
        if (!currentIds.SetEquals(_lastLoggedChannelIds))
        {
            ServiceLog.Info($"[fan-control] discovered {result.Count} controllable fan channel(s)");
            foreach (var ch in result)
                ServiceLog.Info($"[fan-control]   {ch.Name} ({ch.Id})");
            foreach (var layout in layouts)
                ServiceLog.Info(layout);
            _lastLoggedChannelIds = currentIds;
        }

        return result;
    }

    private static void DiscoverFromHardware(List<ChannelMapping> result, IHardware hw, string prefix, List<string> layouts)
    {
        var fans = hw.Sensors
            .Where(s => s.SensorType == SensorType.Fan)
            .OrderBy(s => s.Index)
            .ToList();

        var controls = hw.Sensors
            .Where(s => s.SensorType == SensorType.Control)
            .OrderBy(s => s.Index)
            .ToList();

        layouts.Add($"[fan-discovery] {prefix}/{hw.Identifier}: fans=[{string.Join(",", fans.Select(f => f.Index + ":" + f.Name))}] controls=[{string.Join(",", controls.Select(c => c.Index + ":" + c.Name + (c.Control is null ? "(ro)" : "")))}]");

        // Pair each tach (Fan) with the Control sharing its chip index.
        // Iterating fans - not controls - keeps phantom PWM outputs with no
        // fan attached out of the list; the by-index lookup fixes the old
        // Math.Min positional zip, which cross-wired a fan's RPM onto the
        // wrong control when tach and PWM indices are not contiguous
        // (IT8696E here: tachs at 0,1,2,4; PWM outputs 0-5, so position 3
        // paired tach index 4 with the dead control index 3).
        var controlsByIndex = new Dictionary<int, ISensor>();
        foreach (var c in controls) controlsByIndex[c.Index] = c;

        foreach (var fan in fans)
        {
            if (!controlsByIndex.TryGetValue(fan.Index, out var control) || control.Control is null)
                continue;

            var name = fan.Name;
            if (!name.Contains(prefix, StringComparison.OrdinalIgnoreCase) && prefix != "Motherboard")
                name = $"{prefix} {name}";

            result.Add(new ChannelMapping
            {
                Id = control.Identifier.ToString(),
                Name = name,
                FanSensor = fan,
                ControlSensor = control,
            });
        }
    }

    // ── Helpers ──

    private static void AddTempSensors(List<TemperatureSource> list, IHardware hw, string category)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature) continue;
            var value = sensor.Value ?? 0f;
            // Skip disconnected/disabled channels (e.g. unpopulated DIMM SPD
            // temps read 0 / ~0.25°C) so they never become curve inputs -
            // same gate every platform's provider applies. See TemperatureSourceFilter.
            if (!TemperatureSourceFilter.IsPlausible(value)) continue;
            list.Add(new TemperatureSource
            {
                Id = sensor.Identifier.ToString(),
                Name = sensor.Name,
                Category = category,
                Value = value,
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

        /// <summary>The join key monitoring sensors carry; <see cref="Id"/> is the control sensor.</summary>
        public string FanSensorId => FanSensor.Identifier.ToString();
    }
}

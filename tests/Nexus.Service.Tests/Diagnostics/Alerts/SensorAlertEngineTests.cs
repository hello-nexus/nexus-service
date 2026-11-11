using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Alerts;

public class SensorAlertEngineTests
{
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _wall = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public void Advance(TimeSpan by)
        {
            _wall += by;
            _ticks += by.Ticks;
        }
        // Moves the wall clock alone, like a sleep the monotonic clock does not count.
        public void JumpWall(TimeSpan by) => _wall += by;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _wall;
    }

    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> Cpu { get; set; } = Array.Empty<HardwareSensor>();
        public List<HardwareComponent> Psus { get; } = new();
        public int CpuReads { get; private set; }
        public int ExtrasReads { get; private set; }
        public int SmartReads { get; private set; }
        public int MoboFullReads { get; private set; }
        public int MoboCachedReads { get; private set; }
        public Action? OnCpuRead { get; set; }

        public void BeforeSmartRead() => SmartReads++;
        public IReadOnlyList<HardwareSensor> GetMotherboardSensorsCached()
        {
            MoboCachedReads++;
            return Array.Empty<HardwareSensor>();
        }

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors()
        {
            CpuReads++;
            OnCpuRead?.Invoke();
            return Cpu;
        }
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public Dictionary<string, StorageComponent> Storage { get; } = new();
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => Storage;
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors()
        {
            MoboFullReads++;
            return Array.Empty<HardwareSensor>();
        }
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras()
        {
            ExtrasReads++;
            return new SensorExtras { Psus = Psus };
        }
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Rig
    {
        public FakeTime Time { get; } = new();
        public InMemoryConfigStore Store { get; } = new();
        public StubSensors Sensors { get; } = new();
        public SensorAlertEngine Engine { get; }
        public List<(DiagnosticsAlertNotice Notice, bool IgnoreMaster)> Notices { get; } = new();
        public int ChangedCount { get; private set; }

        public Rig()
        {
            Engine = new SensorAlertEngine(Store, Sensors, Time);
            Engine.NoticeSink = (n, ignore) => Notices.Add((n, ignore));
            Engine.Changed += () => ChangedCount++;
        }

        public void SetTemp(float value, string id = "cpu/temp", string name = "CPU Package") =>
            Sensors.Cpu = new[] { Temp(id, name, value) };

        public void AddRule(SensorAlertRule rule) => Store.Update(s => s.Diagnostics.SensorAlerts.Add(rule));

        public void Seconds(int n)
        {
            for (var i = 0; i < n; i++)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                Engine.Tick();
            }
        }

        public string State(string ruleId = "r1") => Engine.States().Single(s => s.RuleId == ruleId).State;
    }

    private static HardwareSensor Temp(string id, string name, float value, bool stale = false) => new()
    {
        Id = id,
        Name = name,
        Type = "Temperature",
        Value = value,
        Units = "°C",
        Stale = stale,
        Parent = new SensorParent { Id = "cpu", Name = "CPU" },
    };

    private static SensorAlertRule TempRule(double? low = null, double? high = 80, int raise = 5, int clear = 5, string id = "r1") => new()
    {
        Id = id,
        Sensor = new SensorRef { Category = "cpu", Id = "cpu/temp", Name = "CPU Package", Type = "Temperature", ParentName = "CPU" },
        Low = low,
        High = high,
        RaiseAfterSeconds = raise,
        ClearAfterSeconds = clear,
    };

    private static SensorAlertRuntime Fresh() => new();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static SensorSample Ok(double v) => new(true, v, "°C");
    private static readonly SensorSample Bad = new(false, 0, "");

    // ---- Step ----

    [Fact]
    public void Step_raises_only_after_the_delay()
    {
        var rule = TempRule(raise: 5);
        var st = Fresh();

        Assert.Equal(SensorAlertEffect.Changed, SensorAlertEngine.Step(rule, Ok(90), T0, st));
        Assert.Equal(SensorAlertStates.Pending, st.Phase);
        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(91), T0.AddSeconds(4), st));
        var effect = SensorAlertEngine.Step(rule, Ok(95), T0.AddSeconds(5), st);

        Assert.True(effect.HasFlag(SensorAlertEffect.Raised));
        Assert.Equal(SensorAlertStates.Active, st.Phase);
        Assert.Equal(95, st.Extreme);
        Assert.Equal("above", st.Direction);
    }

    [Fact]
    public void Step_zero_delay_raises_on_the_first_breach()
    {
        var st = Fresh();

        var effect = SensorAlertEngine.Step(TempRule(raise: 0), Ok(90), T0, st);

        Assert.True(effect.HasFlag(SensorAlertEffect.Raised));
    }

    [Fact]
    public void Step_pending_back_in_band_returns_to_ok_silently()
    {
        var rule = TempRule(raise: 5);
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);

        var effect = SensorAlertEngine.Step(rule, Ok(70), T0.AddSeconds(3), st);

        Assert.Equal(SensorAlertEffect.Changed, effect);
        Assert.Equal(SensorAlertStates.Ok, st.Phase);
        // A fresh breach restarts the delay from zero.
        SensorAlertEngine.Step(rule, Ok(90), T0.AddSeconds(4), st);
        Assert.False(SensorAlertEngine.Step(rule, Ok(90), T0.AddSeconds(8), st).HasFlag(SensorAlertEffect.Raised));
    }

    [Fact]
    public void Step_clears_only_after_hysteresis_and_the_clear_delay()
    {
        var rule = TempRule(raise: 0, clear: 5);
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);

        // 79 is back under the limit but inside the 2 degree hysteresis, so it does not count.
        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(79), T0.AddSeconds(1), st));
        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(78), T0.AddSeconds(2), st));
        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(78), T0.AddSeconds(6), st));
        var effect = SensorAlertEngine.Step(rule, Ok(78), T0.AddSeconds(7), st);

        Assert.True(effect.HasFlag(SensorAlertEffect.Cleared));
        Assert.Equal(SensorAlertStates.Ok, st.Phase);
    }

    [Fact]
    public void Step_a_re_breach_restarts_the_clear_timer()
    {
        var rule = TempRule(raise: 0, clear: 5);
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);
        SensorAlertEngine.Step(rule, Ok(70), T0.AddSeconds(1), st);
        SensorAlertEngine.Step(rule, Ok(85), T0.AddSeconds(4), st);

        var effect = SensorAlertEngine.Step(rule, Ok(70), T0.AddSeconds(7), st);

        Assert.False(effect.HasFlag(SensorAlertEffect.Cleared));
        Assert.Equal(SensorAlertStates.Active, st.Phase);
    }

    [Fact]
    public void Step_low_only_rule_breaches_below_and_ignores_the_open_side()
    {
        var rule = TempRule(low: 20, high: null, raise: 0);
        var st = Fresh();

        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(500), T0, st));
        Assert.True(SensorAlertEngine.Step(rule, Ok(10), T0.AddSeconds(1), st).HasFlag(SensorAlertEffect.Raised));
        Assert.Equal("below", st.Direction);
        Assert.Equal(10, st.Extreme);
        SensorAlertEngine.Step(rule, Ok(5), T0.AddSeconds(2), st);
        Assert.Equal(5, st.Extreme);
        // Back above 20 but inside the hysteresis: still active.
        SensorAlertEngine.Step(rule, Ok(21), T0.AddSeconds(3), st);
        Assert.Equal(SensorAlertStates.Active, st.Phase);
    }

    [Fact]
    public void Step_invalid_sample_never_raises_and_never_clears()
    {
        var rule = TempRule(raise: 0, clear: 0);
        var ok = Fresh();
        SensorAlertEngine.Step(rule, Bad, T0, ok);
        Assert.Equal(SensorAlertStates.Ok, ok.Phase);

        var active = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, active);
        for (var i = 1; i <= 30; i++)
        {
            SensorAlertEngine.Step(rule, Bad, T0.AddSeconds(i), active);
        }
        Assert.Equal(SensorAlertStates.Active, active.Phase);
    }

    [Fact]
    public void Step_invalid_sample_breaks_a_pending_breach()
    {
        var rule = TempRule(raise: 5);
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);
        SensorAlertEngine.Step(rule, Bad, T0.AddSeconds(2), st);

        SensorAlertEngine.Step(rule, Ok(90), T0.AddSeconds(3), st);
        var effect = SensorAlertEngine.Step(rule, Ok(90), T0.AddSeconds(6), st);

        Assert.False(effect.HasFlag(SensorAlertEffect.Raised));
    }

    [Fact]
    public void Step_repeat_renotifies_on_the_interval_only_while_breached()
    {
        var rule = TempRule(raise: 0);
        rule.RepeatMinutes = 5;
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);

        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(90), T0.AddMinutes(4), st));
        Assert.True(SensorAlertEngine.Step(rule, Ok(90), T0.AddMinutes(5), st).HasFlag(SensorAlertEffect.Repeated));
        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(90), T0.AddMinutes(6), st));
    }

    [Fact]
    public void Step_no_repeat_means_once_per_episode()
    {
        var rule = TempRule(raise: 0);
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);

        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(90), T0.AddHours(3), st));
    }

    [Theory]
    [InlineData("Temperature", 2.0)]
    [InlineData("Current", 0.5)]
    [InlineData("Power", 5.0)]
    [InlineData("Voltage", 0.05)]
    [InlineData("Fan", 100.0)]
    [InlineData("Load", 2.0)]
    [InlineData("Control", 2.0)]
    [InlineData("Level", 2.0)]
    public void Hysteresis_follows_the_sensor_type(string type, double expected)
    {
        var rule = new SensorAlertRule { High = 100000, Low = null, Sensor = new SensorRef { Type = type } };

        Assert.Equal(expected, SensorAlertRules.Hysteresis(rule));
    }

    [Fact]
    public void Hysteresis_other_types_use_one_percent_of_the_band()
    {
        var rule = new SensorAlertRule { Low = 1000, High = 3000, Sensor = new SensorRef { Type = "Clock" } };

        Assert.Equal(20, SensorAlertRules.Hysteresis(rule), 6);
    }

    // ---- engine ----

    [Fact]
    public void Engine_raises_after_the_delay_and_notifies_in_the_users_unit()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 3));
        rig.SetTemp(90);

        rig.Seconds(1);
        Assert.Equal(SensorAlertStates.Pending, rig.State());
        Assert.Empty(rig.Notices);
        rig.Seconds(3);

        Assert.Equal(SensorAlertStates.Active, rig.State());
        var (notice, ignoreMaster) = Assert.Single(rig.Notices);
        Assert.True(ignoreMaster);
        Assert.Equal("sensorAlert", notice.Kind);
        Assert.Equal("CPU Package Temperature above 80 °C for 3 s (now 90 °C)", notice.Text);
        Assert.False(notice.Urgent);
    }

    [Fact]
    public void Engine_formats_the_toast_in_fahrenheit_and_stores_celsius()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Units.MonitoringTempUnit = "f");
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);

        rig.Seconds(1);

        Assert.Equal("CPU Package Temperature above 176 °F (now 194 °F)", Assert.Single(rig.Notices).Notice.Text);
        Assert.Equal(80, rig.Store.Load().Diagnostics.SensorAlerts[0].High);
    }

    [Fact]
    public void Engine_act_severity_is_urgent_and_notifyDesktop_off_stays_silent()
    {
        var rig = new Rig();
        var loud = TempRule(raise: 0, id: "r1");
        loud.Severity = "act";
        var quiet = TempRule(raise: 0, id: "r2");
        quiet.NotifyDesktop = false;
        rig.AddRule(loud);
        rig.AddRule(quiet);
        rig.SetTemp(90);

        rig.Seconds(1);

        var (notice, _) = Assert.Single(rig.Notices);
        Assert.True(notice.Urgent);
        Assert.Equal(SensorAlertStates.Active, rig.State("r2"));
    }

    [Fact]
    public void Engine_writes_history_and_clears_with_hysteresis()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0, clear: 2));
        rig.SetTemp(90);
        rig.Seconds(1);
        rig.SetTemp(95);
        rig.Seconds(1);
        rig.SetTemp(78);
        rig.Seconds(3);

        Assert.Equal(SensorAlertStates.Ok, rig.State());
        var entry = Assert.Single(rig.Engine.History());
        Assert.Equal("above", entry.Direction);
        Assert.Equal(95, entry.Extreme);
        Assert.Equal(80, entry.Threshold);
        Assert.NotNull(entry.EndedUtc);
    }

    [Fact]
    public void Engine_unavailable_after_ten_seconds_and_active_stays_active()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0, id: "r1"));
        rig.AddRule(TempRule(high: 70, raise: 99, id: "r2"));
        rig.SetTemp(90);
        rig.Seconds(1);

        rig.Sensors.Cpu = Array.Empty<HardwareSensor>();
        rig.Seconds(9);
        Assert.Equal(SensorAlertStates.Ok, rig.State("r2"));
        rig.Seconds(2);

        Assert.Equal(SensorAlertStates.Unavailable, rig.State("r2"));
        Assert.Equal(SensorAlertStates.Active, rig.State("r1"));
    }

    [Fact]
    public void Engine_ignores_a_stale_sensor()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.Sensors.Cpu = new[] { Temp("cpu/temp", "CPU Package", 90, stale: true) };

        rig.Seconds(15);

        Assert.Empty(rig.Notices);
        Assert.Equal(SensorAlertStates.Unavailable, rig.State());
    }

    [Theory]
    [InlineData(-100f)]
    [InlineData(251f)]
    public void Engine_implausible_temperatures_are_invalid(float value)
    {
        var rig = new Rig();
        rig.AddRule(TempRule(low: 10, high: 80, raise: 0));
        rig.SetTemp(value);

        rig.Seconds(2);

        Assert.Empty(rig.Notices);
        Assert.NotEqual(SensorAlertStates.Active, rig.State());
    }

    [Fact]
    public void Engine_editing_a_rule_resets_its_state_and_ends_the_episode()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);
        rig.Seconds(1);
        Assert.Equal(SensorAlertStates.Active, rig.State());

        rig.Store.Update(s => s.Diagnostics.SensorAlerts[0] = TempRule(high: 85, raise: 600));
        rig.Engine.Reconcile();

        Assert.Equal(SensorAlertStates.Ok, rig.State());
        Assert.NotNull(Assert.Single(rig.Engine.History()).EndedUtc);
    }

    [Fact]
    public void Engine_deleting_a_rule_drops_its_state()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.Seconds(1);

        rig.Store.Update(s => s.Diagnostics.SensorAlerts.Clear());
        rig.Engine.Reconcile();

        Assert.Empty(rig.Engine.States());
    }

    [Fact]
    public void Engine_disabling_a_rule_resets_it()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);
        rig.Seconds(1);

        rig.Store.Update(s => s.Diagnostics.SensorAlerts[0].Enabled = false);
        rig.Seconds(1);
        Assert.Empty(rig.Engine.States());

        rig.Store.Update(s => s.Diagnostics.SensorAlerts[0].Enabled = true);
        rig.Sensors.Cpu = Array.Empty<HardwareSensor>();
        rig.Seconds(1);

        Assert.Equal(SensorAlertStates.Ok, rig.State());
    }

    [Fact]
    public void Engine_acknowledge_removes_the_health_component_until_the_next_episode()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0, clear: 0));
        rig.SetTemp(90);
        rig.Seconds(1);

        var component = Assert.Single(rig.Engine.HealthComponents());
        Assert.Equal("alert:r1", component.Id);
        Assert.Equal("alerts", component.Kind);
        Assert.Equal(HealthStatuses.Watch, component.Status);

        Assert.True(rig.Engine.Acknowledge("r1"));
        Assert.Empty(rig.Engine.HealthComponents());
        Assert.True(rig.Engine.States().Single().Acknowledged);

        rig.SetTemp(60);
        rig.Seconds(1);
        rig.SetTemp(90);
        rig.Seconds(1);
        Assert.Single(rig.Engine.HealthComponents());
    }

    [Fact]
    public void Engine_showInHealth_off_and_act_severity_map_to_components()
    {
        var rig = new Rig();
        var hidden = TempRule(raise: 0, id: "r1");
        hidden.ShowInHealth = false;
        var act = TempRule(raise: 0, id: "r2");
        act.Severity = "act";
        rig.AddRule(hidden);
        rig.AddRule(act);
        rig.SetTemp(90);

        rig.Seconds(1);

        var component = Assert.Single(rig.Engine.HealthComponents());
        Assert.Equal("alert:r2", component.Id);
        Assert.Equal(HealthStatuses.Act, component.Status);
    }

    [Fact]
    public void Engine_self_heals_a_changed_sensor_id_by_name_without_resetting()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90, id: "cpu/temp");
        rig.Seconds(1);

        rig.SetTemp(91, id: "cpu/temp-renamed");
        rig.Seconds(1);

        Assert.Equal("cpu/temp-renamed", rig.Store.Load().Diagnostics.SensorAlerts[0].Sensor.Id);
        Assert.Equal(SensorAlertStates.Active, rig.State());
        Assert.Single(rig.Notices);
    }

    [Fact]
    public void Engine_id_match_requires_the_same_type()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.Sensors.Cpu = new[]
        {
            new HardwareSensor { Id = "cpu/temp", Name = "CPU Package", Type = "Load", Value = 99, Parent = new SensorParent { Name = "CPU" } },
        };

        rig.Seconds(2);

        Assert.Empty(rig.Notices);
    }

    [Fact]
    public void Engine_name_match_prefers_the_same_parent()
    {
        var other = Temp("a", "CPU Package", 10);
        other.Parent = new SensorParent { Id = "x", Name = "Other" };
        var mine = Temp("b", "CPU Package", 90);
        var rule = TempRule();
        rule.Sensor.Id = "gone";

        var sample = SensorAlertEngine.Resolve(new[] { other, mine }, rule.Sensor, out var healed);

        Assert.Equal(90, sample.Value);
        Assert.Equal("b", healed);
    }

    [Fact]
    public void Engine_without_enabled_rules_reads_no_sensors()
    {
        var rig = new Rig();
        var off = TempRule();
        off.Enabled = false;
        rig.AddRule(off);

        rig.Seconds(3);

        Assert.Equal(0, rig.Sensors.CpuReads);
        Assert.Equal(0, rig.Sensors.ExtrasReads);
    }

    [Fact]
    public void Engine_reads_each_category_once_per_tick_and_extras_only_when_referenced()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(id: "r1"));
        rig.AddRule(TempRule(id: "r2"));
        rig.SetTemp(50);

        rig.Seconds(1);
        Assert.Equal(1, rig.Sensors.CpuReads);
        Assert.Equal(0, rig.Sensors.ExtrasReads);

        var psu = TempRule(id: "r3");
        psu.Sensor.Category = "psu";
        rig.AddRule(psu);
        rig.Seconds(1);

        Assert.Equal(2, rig.Sensors.CpuReads);
        Assert.Equal(1, rig.Sensors.ExtrasReads);
    }

    [Fact]
    public void Engine_broadcasts_on_transitions_not_on_every_tick()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);
        rig.Seconds(1);
        var afterRaise = rig.ChangedCount;

        rig.Seconds(20);

        Assert.True(afterRaise > 0);
        Assert.Equal(afterRaise, rig.ChangedCount);
    }

    [Fact]
    public void Engine_history_is_capped_at_100_and_clear_keeps_open_episodes()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0, clear: 0));
        for (var i = 0; i < 105; i++)
        {
            rig.SetTemp(90);
            rig.Seconds(1);
            rig.SetTemp(60);
            rig.Seconds(1);
        }
        Assert.Equal(100, rig.Engine.History().Count);

        rig.SetTemp(90);
        rig.Seconds(1);
        rig.Engine.ClearHistory();

        var left = Assert.Single(rig.Engine.History());
        Assert.Null(left.EndedUtc);
    }

    [Fact]
    public void Engine_repeat_renotifies_while_active()
    {
        var rig = new Rig();
        var rule = TempRule(raise: 0);
        rule.RepeatMinutes = 5;
        rig.AddRule(rule);
        rig.SetTemp(90);

        rig.Seconds(1);
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        rig.Engine.Tick();

        Assert.Equal(2, rig.Notices.Count);
    }

    // ---- validation and persistence ----

    [Fact]
    public void Validate_orders_two_bounds_and_accepts_one_or_none()
    {
        Assert.Null(SensorAlertRules.Validate(TempRule(low: 10, high: 80)));
        Assert.Null(SensorAlertRules.Validate(TempRule(low: null, high: null)));
        Assert.NotNull(SensorAlertRules.Validate(TempRule(low: 80, high: 80)));
        Assert.NotNull(SensorAlertRules.Validate(TempRule(low: 90, high: 80)));
        Assert.Null(SensorAlertRules.Validate(TempRule(low: 10, high: null)));
    }

    [Theory]
    [InlineData("loud", 0, false)]
    [InlineData("watch", 7, false)]
    [InlineData("act", 15, true)]
    [InlineData("watch", 0, true)]
    [InlineData("watch", 60, true)]
    public void Validate_severity_and_repeat(string severity, int repeat, bool valid)
    {
        var rule = TempRule();
        rule.Severity = severity;
        rule.RepeatMinutes = repeat;

        Assert.Equal(valid, SensorAlertRules.Validate(rule) is null);
    }

    [Fact]
    public void Validate_rejects_unknown_category_and_missing_sensor()
    {
        var rule = TempRule();
        rule.Sensor.Category = "nope";
        Assert.NotNull(SensorAlertRules.Validate(rule));

        var empty = TempRule();
        empty.Sensor.Id = "";
        empty.Sensor.Name = "";
        Assert.NotNull(SensorAlertRules.Validate(empty));
    }

    [Fact]
    public void Normalize_clamps_the_delays()
    {
        var rule = TempRule(raise: -3, clear: 9000);

        SensorAlertRules.Normalize(rule);

        Assert.Equal(0, rule.RaiseAfterSeconds);
        Assert.Equal(600, rule.ClearAfterSeconds);
    }

    [Fact]
    public void Rules_round_trip_through_the_persistence_context()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-sensor-alerts-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var store = new JsonConfigStore(path);
            var rule = TempRule(low: 10, high: null, raise: 7, clear: 9);
            rule.Severity = "act";
            rule.RepeatMinutes = 15;
            rule.ShowInHealth = false;
            store.Update(s => s.Diagnostics.SensorAlerts.Add(rule));
            store.FlushNow();

            var loaded = new JsonConfigStore(path).Load().Diagnostics.SensorAlerts;

            var back = Assert.Single(loaded);
            Assert.Equal("r1", back.Id);
            Assert.Equal(10, back.Low);
            Assert.Null(back.High);
            Assert.Equal(7, back.RaiseAfterSeconds);
            Assert.Equal(9, back.ClearAfterSeconds);
            Assert.Equal("act", back.Severity);
            Assert.Equal(15, back.RepeatMinutes);
            Assert.False(back.ShowInHealth);
            Assert.Equal("cpu/temp", back.Sensor.Id);
            Assert.Equal("CPU", back.Sensor.ParentName);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Wire_shape_keeps_null_bounds_and_omits_nothing_else_the_web_needs()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(TempRule(low: null, high: 80), Nexus.Service.Serialization.AppJsonContext.Default.SensorAlertRule);

        Assert.Contains("\"low\":null", json);
        Assert.Contains("\"high\":80", json);
        Assert.Contains("\"raiseAfterSeconds\":5", json);
        Assert.Contains("\"parentName\":\"CPU\"", json);
    }

    [Fact]
    public void HardwareSensor_stale_flag_stays_off_the_wire()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Temp("a", "b", 1, stale: true), Nexus.Service.Serialization.AppJsonContext.Default.HardwareSensor);

        Assert.DoesNotContain("stale", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Health_model_merges_alert_components_and_honors_ignored_components()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);
        rig.Seconds(1);
        var model = new DiagnosticsHealthModel(
            new UnsupportedSmartHealthSource(),
            new Nexus.Service.Diagnostics.Cooling.CoolingStallDetector(),
            new Nexus.Service.Diagnostics.Gpu.GpuHealthMonitor(),
            new Nexus.Service.Diagnostics.EventLog.EventLogMonitor(),
            new Nexus.Service.Diagnostics.Memory.MemoryDiagnosticOrchestrator(),
            new Nexus.Service.Diagnostics.SystemInfo.PnpProblemScanner(),
            rig.Sensors,
            new Nexus.Service.Monitoring.History.InMemoryMetricsHistoryStore(),
            rig.Store,
            rig.Engine);

        var health = model.BuildRealHealth();
        Assert.Contains(health.Components, c => c.Id == "alert:r1");
        Assert.Equal(HealthStatuses.Watch, health.Overall);

        rig.Store.Update(s => s.Diagnostics.IgnoredComponents.Add("alert:r1"));
        Assert.DoesNotContain(model.BuildRealHealth().Components, c => c.Id == "alert:r1");
    }

    [Fact]
    public void Engine_a_rule_edit_during_the_sensor_read_is_not_stepped_with_the_old_rule()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(high: 80, raise: 0));
        rig.SetTemp(90);
        rig.Engine.Reconcile();
        rig.Sensors.OnCpuRead = () =>
        {
            rig.Sensors.OnCpuRead = null;
            rig.Store.Update(s => s.Diagnostics.SensorAlerts[0] = TempRule(high: 95, raise: 0));
            rig.Engine.Reconcile();
        };

        rig.Seconds(1);

        Assert.Empty(rig.Notices);
        Assert.Equal(SensorAlertStates.Ok, rig.State());
    }

    [Fact]
    public void Engine_a_rule_disabled_during_the_sensor_read_stays_quiet()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);
        rig.Sensors.OnCpuRead = () =>
        {
            rig.Sensors.OnCpuRead = null;
            rig.Store.Update(s => s.Diagnostics.SensorAlerts[0].Enabled = false);
            rig.Engine.Reconcile();
        };

        rig.Seconds(3);

        Assert.Empty(rig.Notices);
        Assert.Empty(rig.Engine.States());
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void Engine_disabled_rules_have_no_state_or_health_component()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);
        rig.Seconds(1);

        rig.Store.Update(s => s.Diagnostics.SensorAlerts[0].Enabled = false);

        Assert.Empty(rig.Engine.States());
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void Normalize_coalesces_null_sensor_fields_so_naming_never_throws()
    {
        var rule = TempRule();
        rule.Sensor.Name = null!;
        rule.Sensor.Id = null!;
        rule.Sensor.ParentName = null!;

        SensorAlertRules.Normalize(rule);

        Assert.Equal("", rule.Sensor.Name);
        Assert.Equal("Temperature", SensorAlertText.DisplayName(rule.Sensor).Trim());
    }

    [Fact]
    public void Engine_display_timestamps_follow_the_wall_clock_not_the_monotonic_one()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0, clear: 0));
        rig.SetTemp(90);
        rig.Seconds(1);
        var raisedAt = rig.Time.GetUtcNow();
        var since = rig.Engine.States().Single().Since;
        Assert.Equal(raisedAt.UtcDateTime.ToString("O"), since);

        rig.Time.JumpWall(TimeSpan.FromHours(3));
        rig.Engine.Tick();
        Assert.Equal(since, rig.Engine.States().Single().Since);

        rig.SetTemp(60);
        rig.Seconds(1);
        var entry = Assert.Single(rig.Engine.History());
        Assert.Equal(since, entry.StartedUtc);
        Assert.Equal(rig.Time.GetUtcNow().UtcDateTime.ToString("O"), entry.EndedUtc);
    }

    [Fact]
    public void Engine_reads_smart_at_most_once_a_minute()
    {
        var rig = new Rig();
        var rule = TempRule();
        rule.Sensor.Category = "smart";
        rig.AddRule(rule);
        rig.Sensors.Storage["smart/nvme/0"] = new StorageComponent { Id = "smart/nvme/0", Sensors = new List<HardwareSensor> { Temp("cpu/temp", "CPU Package", 50) } };

        rig.Seconds(30);
        Assert.Equal(1, rig.Sensors.SmartReads);
        rig.Seconds(31);

        Assert.Equal(2, rig.Sensors.SmartReads);
    }

    [Fact]
    public void Engine_retries_an_empty_smart_read_after_ten_seconds()
    {
        var rig = new Rig();
        var rule = TempRule();
        rule.Sensor.Category = "smart";
        rig.AddRule(rule);

        rig.Seconds(9);
        Assert.Equal(1, rig.Sensors.SmartReads);
        rig.Sensors.Storage["smart/nvme/0"] = new StorageComponent { Id = "smart/nvme/0", Sensors = new List<HardwareSensor> { Temp("cpu/temp", "CPU Package", 50) } };
        rig.Seconds(2);
        Assert.Equal(2, rig.Sensors.SmartReads);
        Assert.Equal("ok", rig.Engine.States().Single().State);

        rig.Seconds(30);
        Assert.Equal(2, rig.Sensors.SmartReads);
    }

    [Fact]
    public void Engine_motherboard_rules_use_the_cached_read()
    {
        var rig = new Rig();
        var rule = TempRule();
        rule.Sensor.Category = "motherboard";
        rig.AddRule(rule);

        rig.Seconds(3);

        Assert.Equal(3, rig.Sensors.MoboCachedReads);
        Assert.Equal(0, rig.Sensors.MoboFullReads);
    }

    [Fact]
    public void Validate_rejects_fps_and_accepts_every_picker_category()
    {
        foreach (var category in new[] { "cpu", "gpu", "memory", "motherboard", "storage", "psu", "cooler" })
        {
            var rule = TempRule();
            rule.Sensor.Category = category;
            Assert.Null(SensorAlertRules.Validate(rule));
        }
        var fps = TempRule();
        fps.Sensor.Category = "fps";
        Assert.NotNull(SensorAlertRules.Validate(fps));
    }

    [Fact]
    public void Step_repeat_stops_after_acknowledge()
    {
        var rule = TempRule(raise: 0);
        rule.RepeatMinutes = 5;
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);
        st.Acknowledged = true;

        Assert.Equal(SensorAlertEffect.None, SensorAlertEngine.Step(rule, Ok(90), T0.AddMinutes(6), st));
    }

    [Fact]
    public void Step_leaving_a_band_through_the_other_side_flips_direction_and_extreme()
    {
        var rule = TempRule(low: 20, high: 80, raise: 0);
        var st = Fresh();
        SensorAlertEngine.Step(rule, Ok(90), T0, st);
        Assert.Equal("above", st.Direction);

        SensorAlertEngine.Step(rule, Ok(10), T0.AddSeconds(1), st);

        Assert.Equal("below", st.Direction);
        Assert.Equal(10, st.Extreme);
        Assert.Equal(SensorAlertStates.Active, st.Phase);
    }

    [Fact]
    public void Sensor_type_is_matched_case_insensitively_for_hysteresis_and_unit()
    {
        var rule = new SensorAlertRule { High = 100, Sensor = new SensorRef { Type = "temperature" } };

        Assert.Equal(2, SensorAlertRules.Hysteresis(rule));
        Assert.Equal("176 °F", SensorAlertText.Format(80, "temperature", "°C", fahrenheit: true));
    }

    [Fact]
    public void LogThrottle_allows_one_line_per_key_per_interval()
    {
        var throttle = new LogThrottle(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldLog("a", T0));
        Assert.False(throttle.ShouldLog("a", T0.AddSeconds(30)));
        Assert.True(throttle.ShouldLog("b", T0.AddSeconds(30)));
        Assert.True(throttle.ShouldLog("a", T0.AddSeconds(61)));
    }

    [Fact]
    public void Evaluator_registers_a_snapshot_with_the_current_states()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(raise: 0));
        rig.SetTemp(90);
        rig.Seconds(1);
        var hub = new Nexus.Service.Sockets.MultiplexHub();
        _ = new SensorAlertEvaluator(rig.Engine, Nexus.Service.Lifecycle.FeatureGates.AllEnabled, hub, rig.Time);

        Assert.True(hub.TryGetTopicSnapshot(Nexus.Service.Sockets.PanelTopics.SensorAlerts, out var envelope));
        var text = System.Text.Encoding.UTF8.GetString(envelope.Span);
        Assert.Contains("diagnostics/alerts", text);
        Assert.Contains("\"state\":\"active\"", text);
    }

    // ---- Tracked sensors ----

    [Fact]
    public void A_track_only_rule_never_raises_and_stays_ok()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(low: null, high: null, raise: 0));
        rig.SetTemp(95);
        rig.Seconds(30);

        Assert.Equal("ok", rig.State());
        Assert.Empty(rig.Notices);
    }

    [Fact]
    public void A_track_only_rule_turns_unavailable_when_the_sensor_is_gone()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(low: null, high: null));
        rig.SetTemp(50);
        rig.Seconds(2);
        rig.Sensors.Cpu = Array.Empty<HardwareSensor>();
        rig.Seconds(12);

        Assert.Equal("unavailable", rig.State());
    }

    [Fact]
    public void Every_enabled_rule_contributes_its_reading_with_or_without_bounds()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(low: null, high: null, id: "tracked"));
        rig.AddRule(TempRule(high: 80, id: "bounded"));
        rig.SetTemp(61.5f);
        rig.Seconds(1);

        var readings = rig.Engine.CurrentReadings(0);

        Assert.Equal(new[] { "bounded", "tracked" }, readings.Select(r => r.Id).OrderBy(i => i).ToArray());
        Assert.All(readings, r => Assert.Equal(61.5, r.Value));
    }

    [Fact]
    public void A_disabled_rule_is_not_recorded()
    {
        var rig = new Rig();
        var off = TempRule(low: null, high: null, id: "off");
        off.Enabled = false;
        rig.AddRule(off);
        rig.AddRule(TempRule(id: "on"));
        rig.SetTemp(50);
        rig.Seconds(1);

        Assert.Equal(new[] { "on" }, rig.Engine.CurrentReadings(0).Select(r => r.Id).ToArray());
    }

    [Fact]
    public void An_unavailable_sensor_is_not_recorded()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(low: null, high: null));
        rig.Sensors.Cpu = new[] { Temp("cpu/temp", "CPU Package", 50, stale: true) };
        rig.Seconds(1);
        Assert.Empty(rig.Engine.CurrentReadings(0));

        rig.Sensors.Cpu = new[] { Temp("cpu/temp", "CPU Package", float.NaN) };
        rig.Seconds(1);
        Assert.Empty(rig.Engine.CurrentReadings(0));
    }

    [Fact]
    public void Readings_go_stale_when_evaluation_stops()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(low: null, high: null));
        rig.SetTemp(50);
        rig.Seconds(1);
        Assert.Single(rig.Engine.CurrentReadings(0));

        rig.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Engine.CurrentReadings(0));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 80.0, true)]
    [InlineData(10.0, null, true)]
    [InlineData(80.0, 10.0, false)]
    public void Validate_accepts_no_bound_at_all_but_still_orders_two(double? low, double? high, bool valid)
    {
        var rule = TempRule(low, high);

        Assert.Equal(valid, SensorAlertRules.Validate(rule) is null);
    }

    [Fact]
    public void The_health_reason_carries_invariant_params_in_native_units()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(high: 80, raise: 0));
        rig.SetTemp(91.5f);
        rig.Seconds(2);

        var reason = Assert.Single(Assert.Single(rig.Engine.HealthComponents()).Reasons);

        Assert.Equal("alerts.sensorThreshold", reason.Code);
        var p = reason.Params!;
        Assert.Equal("r1", p["ruleId"]);
        Assert.Equal("cpu", p["category"]);
        Assert.Equal("above", p["direction"]);
        Assert.Equal("Temperature", p["type"]);
        Assert.Equal("CPU Package", p["sensorName"]);
        Assert.Equal("91.5", p["value"]);
        Assert.Equal("80", p["threshold"]);
    }
}

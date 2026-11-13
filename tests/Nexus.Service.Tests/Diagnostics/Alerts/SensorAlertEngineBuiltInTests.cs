using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Alerts;

public partial class SensorAlertEngineTests
{
    private const string CpuId = "builtin:cpu-temp";

    private static string GpuId(string name) => BuiltInSensorRules.RuleId(BuiltInSensorRules.GpuSeriesPrefix + Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId(name));

    private static string DriveId(string id) => BuiltInSensorRules.RuleId(BuiltInSensorRules.DriveSeriesPrefix + Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId(id));

    /// <summary>A rig whose built-ins are seeded from the sensors set so far, then armed with a short hold.</summary>
    private static Rig BuiltInRig(Action<Rig>? sensors = null, Action<NexusSettings>? tweak = null)
    {
        var rig = new Rig();
        rig.Store.Update(s => BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: true));
        sensors?.Invoke(rig);
        rig.Engine.Rules();
        rig.Store.Update(s =>
        {
            foreach (var rule in s.Diagnostics.SensorAlerts)
            {
                rule.RaiseAfterSeconds = 5;
            }
            tweak?.Invoke(s);
        });
        return rig;
    }

    private static GpuReadout Gpu(string name, bool integrated, float temp) => new()
    {
        Id = name,
        Name = name,
        Integrated = integrated,
        Sensors = new List<HardwareSensor> { Temp(name + "/t", "GPU Core", temp) },
    };

    private static SmartDriveInfo Drive(string id, string name, double c) => new() { Id = id, Name = name, TemperatureC = c };

    private static SensorAlertState StateOf(Rig rig, string id) => rig.Engine.States().Single(s => s.RuleId == id);

    private static void CpuHot(Rig rig, float value) => rig.Sensors.Cpu = new[] { Temp("cpu/pkg", "CPU Package", value) };

    private static void EditStored(Rig rig, string id, Action<SensorAlertRule> edit) =>
        rig.Store.Update(s => edit(s.Diagnostics.SensorAlerts.Single(r => r.Id == id)));

    // ---- underlying sensor identity ----

    [Fact]
    public void A_cpu_entry_reports_the_package_sensor_in_the_picker_identity()
    {
        var rig = BuiltInRig(r => r.Sensors.Cpu = new[] { Temp("cpu/core", "CPU Core #1", 50), Temp("cpu/pkg", "CPU Package", 60) });

        var source = rig.Engine.Rules().Single().Source!;

        Assert.Equal("sensor", source.Kind);
        Assert.Equal(new SensorRef { Category = "cpu", Id = "cpu/pkg", Name = "CPU Package", Type = "Temperature", ParentName = "CPU" }, source.Sensor, SensorRefComparer.Instance);
        Assert.Equal("TestCPU", source.HardwareName);
    }

    [Fact]
    public void The_reported_cpu_sensor_follows_the_fallback_when_there_is_no_package_sensor()
    {
        var rig = BuiltInRig(r => r.Sensors.Cpu = new[] { Temp("cpu/core", "CPU Core #1", 50) });

        Assert.Equal("cpu/core", rig.Engine.Rules().Single().Source!.Sensor!.Id);

        CpuHot(rig, 55);
        rig.Engine.Tick();

        Assert.Equal("cpu/pkg", rig.Engine.Rules().Single().Source!.Sensor!.Id);
    }

    [Fact]
    public void A_gpu_entry_reports_its_core_temperature_sensor()
    {
        var rig = BuiltInRig(r => r.Sensors.Gpus.Add(Gpu("RTX 5080", integrated: false, 40)));

        var source = rig.Engine.Rules().Single(x => x.BuiltIn == "gpuTemp").Source!;

        Assert.Equal("gpu", source.Sensor!.Category);
        Assert.Equal("RTX 5080/t", source.Sensor.Id);
        Assert.Equal("RTX 5080", source.HardwareName);
    }

    [Fact]
    public void Memory_reports_the_contributing_sensors_not_a_single_id()
    {
        var rig = BuiltInRig(r => r.Sensors.Memory = new[] { Temp("m0", "DIMM #1 Temperature", 40), Temp("m1", "DIMM #2 Temperature", 80), Temp("m2", "Chipset Temperature", 99) });

        var source = rig.Engine.Rules().Single(x => x.BuiltIn == "ramTemp").Source!;

        Assert.Equal("average", source.Kind);
        Assert.Null(source.Sensor);
        Assert.Equal(new[] { "m0", "m1" }, source.Sensors.Select(x => x.Id));
        Assert.All(source.Sensors, x => Assert.Equal("memory", x.Category));
    }

    [Fact]
    public void A_single_memory_sensor_is_reported_as_that_sensor()
    {
        var rig = BuiltInRig(r => r.Sensors.Memory = new[] { Temp("m0", "DIMM #1 Temperature", 40) });

        var source = rig.Engine.Rules().Single(x => x.BuiltIn == "ramTemp").Source!;

        Assert.Equal("sensor", source.Kind);
        Assert.Equal("m0", source.Sensor!.Id);
    }

    [Fact]
    public void A_drive_reports_the_smart_reading_with_no_picker_id()
    {
        var rig = BuiltInRig(r => r.Smart.Drives.Add(Drive("storage:a", "WDS200", 35)));

        var source = rig.Engine.Rules().Single(x => x.BuiltIn == "storageTemp").Source!;

        Assert.Equal("drive", source.Kind);
        Assert.Null(source.Sensor);
        Assert.Equal("WDS200", source.HardwareName);
    }

    [Fact]
    public void The_source_is_never_stored()
    {
        var rig = BuiltInRig(r => CpuHot(r, 50));
        rig.Engine.Rules();

        Assert.All(rig.Store.Load().Diagnostics.SensorAlerts, x => Assert.Null(x.Source));
    }

    [Fact]
    public void A_built_in_and_a_custom_rule_on_the_same_sensor_read_the_same_value_and_state()
    {
        var rig = BuiltInRig(r => CpuHot(r, 50));
        rig.AddRule(new SensorAlertRule
        {
            Id = "custom",
            Sensor = new SensorRef { Category = "cpu", Id = "cpu/pkg", Name = "CPU Package", Type = "Temperature", ParentName = "CPU" },
            High = 90,
            RaiseAfterSeconds = 5,
        });
        EditStored(rig, CpuId, x => x.High = 90);

        CpuHot(rig, 95);
        rig.Seconds(6);

        Assert.Equal(StateOf(rig, "custom").Value, StateOf(rig, CpuId).Value);
        Assert.Equal(StateOf(rig, "custom").State, StateOf(rig, CpuId).State);
        Assert.Equal(SensorAlertStates.Active, StateOf(rig, CpuId).State);
    }

    private sealed class SensorRefComparer : IEqualityComparer<SensorRef?>
    {
        public static readonly SensorRefComparer Instance = new();

        public bool Equals(SensorRef? a, SensorRef? b) =>
            a is not null && b is not null && a.Category == b.Category && a.Id == b.Id && a.Name == b.Name && a.Type == b.Type && a.ParentName == b.ParentName;

        public int GetHashCode(SensorRef? r) => r?.Id.GetHashCode() ?? 0;
    }

    // ---- per-device seeding ----

    [Fact]
    public void Every_detected_device_gets_one_entry_named_with_the_device_in_chart_order()
    {
        var rig = BuiltInRig(r =>
        {
            CpuHot(r, 50);
            r.Sensors.Gpus.Add(Gpu("RTX 5080", integrated: false, 40));
            r.Sensors.Gpus.Add(Gpu("Radeon Graphics", integrated: true, 45));
            r.Smart.Drives.Add(Drive("storage:a", "WDS200", 35));
        });

        var rules = rig.Engine.Rules();

        Assert.Equal(new[] { CpuId, GpuId("Radeon Graphics"), GpuId("RTX 5080"), DriveId("storage:a") }, rules.Select(x => x.Id));
        Assert.Equal(new[] { "TestCPU", "Radeon Graphics", "RTX 5080", "WDS200" }, rules.Select(x => x.Sensor.Name));
        Assert.Equal(new[] { "cpuTemp", "gpuTemp", "gpuTemp", "storageTemp" }, rules.Select(x => x.BuiltIn));
        Assert.All(rules, x => Assert.Equal("builtin", x.Sensor.Category));
    }

    [Fact]
    public void A_machine_without_a_memory_temperature_gets_no_memory_entry()
    {
        var rig = BuiltInRig(r => CpuHot(r, 50));

        Assert.DoesNotContain(rig.Engine.Rules(), x => x.BuiltIn == "ramTemp");
    }

    [Fact]
    public void Memory_is_one_entry_averaging_the_module_sensors()
    {
        var rig = BuiltInRig(r => r.Sensors.Memory = new[] { Temp("m0", "DIMM #1 Temperature", 40), Temp("m1", "DIMM #2 Temperature", 80), Temp("m2", "Chipset Temperature", 99) });

        rig.Seconds(1);

        var rule = Assert.Single(rig.Engine.Rules(), x => x.BuiltIn == "ramTemp");
        Assert.Equal("builtin:mem-temp", rule.Id);
        Assert.Equal("Memory Temperature", rule.Sensor.Name);
        Assert.Equal(60, StateOf(rig, rule.Id).Value);
    }

    [Fact]
    public void Defaults_per_kind_and_an_integrated_gpu_follows_the_cpu_default()
    {
        var rig = new Rig();
        rig.Store.Update(s => BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: true));
        CpuHot(rig, 50);
        rig.Sensors.Gpus.Add(Gpu("RTX", integrated: false, 40));
        rig.Sensors.Gpus.Add(Gpu("Radeon", integrated: true, 40));
        rig.Smart.Drives.Add(Drive("storage:a", "Drive", 35));
        rig.Sensors.Memory = new[] { Temp("m0", "DIMM #1 Temperature", 40) };

        var byKind = rig.Engine.Rules().ToDictionary(x => x.Sensor.Name, x => x);

        Assert.Equal(100, byKind["TestCPU"].High);
        Assert.Equal(90, byKind["RTX"].High);
        Assert.Equal(100, byKind["Radeon"].High);
        Assert.Equal(75, byKind["Drive"].High);
        Assert.Equal(75, byKind["Memory Temperature"].High);
        Assert.All(byKind.Values, x =>
        {
            Assert.Equal(60, x.RaiseAfterSeconds);
            Assert.Equal("act", x.Severity);
            Assert.True(x.NotifyDesktop);
            Assert.True(x.Enabled);
        });
    }

    [Fact]
    public void A_device_missing_from_a_pass_keeps_its_entry_and_returns_with_its_settings()
    {
        var rig = BuiltInRig(r =>
        {
            CpuHot(r, 50);
            r.Smart.Drives.Add(Drive("storage:a", "Drive", 35));
        });
        var id = DriveId("storage:a");
        EditStored(rig, id, x => x.High = 66);
        rig.Seconds(1);

        rig.Smart.Drives.Clear();
        rig.Seconds(1);

        Assert.Contains(rig.Engine.Rules(), x => x.Id == id);
        Assert.Contains(rig.Engine.BuiltInDevices(), d => BuiltInSensorRules.RuleId(d.SeriesId) == id);
        Assert.Contains(rig.Store.Load().Diagnostics.SensorAlerts, x => x.Id == id);

        rig.Smart.Drives.Add(Drive("storage:a", "Drive", 36));
        rig.Seconds(1);

        Assert.Equal(66, rig.Engine.Rules().Single(x => x.Id == id).High);
    }

    [Fact]
    public void A_device_that_stays_missing_reads_unavailable_with_no_value()
    {
        var rig = BuiltInRig(r => r.Smart.Drives.Add(Drive("storage:a", "Drive", 35)));
        var id = DriveId("storage:a");
        rig.Seconds(2);
        Assert.Equal(35, StateOf(rig, id).Value);

        rig.Smart.Drives.Clear();
        rig.Seconds(12);

        var state = StateOf(rig, id);
        Assert.Equal(SensorAlertStates.Unavailable, state.State);
        Assert.Null(state.Value);
    }

    /// <summary>Raises a drive alert, acknowledges it, then runs <paramref name="drop"/> for a few ticks and <paramref name="restore"/>; the episode must be the same one throughout.</summary>
    private static void AssertNoFlapReopen(Rig rig, string id, Action drop, Action restore)
    {
        rig.Seconds(6);
        Assert.Equal(SensorAlertStates.Active, rig.State(id));
        Assert.True(rig.Engine.Acknowledge(id));
        var notices = rig.Notices.Count;
        var history = rig.Engine.History().Count;

        drop();
        rig.Seconds(3);

        Assert.Contains(rig.Engine.Rules(), x => x.Id == id);
        var held = StateOf(rig, id);
        Assert.Equal(SensorAlertStates.Active, held.State);
        Assert.True(held.Acknowledged);

        restore();
        rig.Seconds(8);

        Assert.True(StateOf(rig, id).Acknowledged);
        Assert.Equal(notices, rig.Notices.Count);
        Assert.Equal(history, rig.Engine.History().Count);
        Assert.All(rig.Engine.History(), h => Assert.Null(h.EndedUtc));
    }

    [Fact]
    public void A_drive_temperature_dropping_out_for_a_pass_does_not_close_or_reopen_its_episode()
    {
        var rig = BuiltInRig(r => r.Smart.Drives.Add(Drive("storage:a", "Drive", 80)));
        var id = DriveId("storage:a");

        AssertNoFlapReopen(rig, id, () => rig.Smart.Drives[0] = new SmartDriveInfo { Id = "storage:a", Name = "Drive" }, () => rig.Smart.Drives[0] = Drive("storage:a", "Drive", 80));
    }

    [Fact]
    public void A_gpu_losing_its_core_sensor_does_not_close_or_reopen_its_episode()
    {
        var rig = BuiltInRig(r => r.Sensors.Gpus.Add(Gpu("RTX", false, 95)));
        var id = GpuId("RTX");
        var sensors = rig.Sensors.Gpus[0].Sensors;

        AssertNoFlapReopen(rig, id, () => rig.Sensors.Gpus[0].Sensors = new List<HardwareSensor>(), () => rig.Sensors.Gpus[0].Sensors = sensors);
    }

    [Fact]
    public void A_failing_source_does_not_close_or_reopen_its_episode()
    {
        var rig = BuiltInRig(r => CpuHot(r, 104));

        AssertNoFlapReopen(rig, CpuId, () => rig.Sensors.OnCpuRead = () => throw new InvalidOperationException("cpu source down"), () => rig.Sensors.OnCpuRead = null);
    }

    [Fact]
    public void A_failing_discovery_keeps_every_entry_of_that_kind()
    {
        var rig = BuiltInRig(r =>
        {
            r.Sensors.Gpus.Add(Gpu("A", false, 50));
            r.Sensors.Gpus.Add(Gpu("B", false, 50));
        });
        rig.Seconds(1);
        var before = rig.Engine.Rules().Where(x => x.BuiltIn == BuiltInSensorRules.GpuTemp).Select(x => x.Id).OrderBy(x => x).ToList();
        Assert.Equal(2, before.Count);

        rig.Sensors.Gpus.Clear();
        rig.Seconds(2);

        Assert.Equal(before, rig.Engine.Rules().Where(x => x.BuiltIn == BuiltInSensorRules.GpuTemp).Select(x => x.Id).OrderBy(x => x).ToList());
    }

    // ---- retired health toggles applied to late devices ----

    [Fact]
    public void A_fan_enumerated_after_the_first_pass_still_gets_the_old_off_choice()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(Fan("early", "Early")));
        rig.Store.Update(s => s.Diagnostics.PendingFansOff = true);
        rig.Seconds(1);
        Assert.Contains("cooling:early", rig.Store.Load().Diagnostics.IgnoredComponents);
        Assert.True(rig.Store.Load().Diagnostics.PendingFansOff);

        rig.Seconds(30);
        rig.Fans.Channels.Add(Fan("late", "Late Hub"));
        rig.Seconds(1);

        var diagnostics = rig.Store.Load().Diagnostics;
        Assert.Contains("cooling:late", diagnostics.IgnoredComponents);
        Assert.True(diagnostics.PendingFansOff);

        rig.Time.Advance(TimeSpan.FromMinutes(10));
        rig.Engine.Tick();

        Assert.False(rig.Store.Load().Diagnostics.PendingFansOff);
    }

    [Fact]
    public void A_fan_the_user_turned_back_on_within_the_window_is_not_ignored_again()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(Fan("early", "Early")));
        rig.Store.Update(s => s.Diagnostics.PendingFansOff = true);
        rig.Seconds(1);
        rig.Store.Update(s => s.Diagnostics.IgnoredComponents.Remove("cooling:early"));

        rig.Seconds(5);

        Assert.DoesNotContain("cooling:early", rig.Store.Load().Diagnostics.IgnoredComponents);
    }

    [Fact]
    public void A_drive_that_only_reports_later_still_gets_the_old_off_choice()
    {
        var rig = BuiltInRig(r => r.Smart.Drives.Add(Drive("storage:a", "A", 40)));
        rig.Store.Update(s => s.Diagnostics.PendingDrivesOff = true);
        rig.Seconds(1);

        rig.Smart.Drives.Add(Drive("storage:slow", "Slow", 40));
        rig.Seconds(2);

        var ignored = rig.Store.Load().Diagnostics.IgnoredComponents;
        Assert.Contains("storage:a", ignored);
        Assert.Contains("storage:slow", ignored);
    }

    [Fact]
    public void Built_ins_do_not_count_as_user_rules_and_are_not_recorded_in_the_series_store()
    {
        var rig = BuiltInRig(r => CpuHot(r, 50));

        rig.Seconds(3);

        Assert.Empty(rig.Engine.CurrentReadings(0));
    }

    [Fact]
    public void A_drive_on_the_ignore_list_is_seeded_disabled()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: true);
            s.Diagnostics.IgnoredComponents.Add("storage:b");
        });
        rig.Smart.Drives.Add(Drive("storage:a", "A", 40));
        rig.Smart.Drives.Add(Drive("storage:b", "B", 40));

        var rules = rig.Engine.Rules();

        Assert.True(rules.Single(x => x.Sensor.Name == "A").Enabled);
        Assert.False(rules.Single(x => x.Sensor.Name == "B").Enabled);
    }

    [Fact]
    public void A_legacy_template_gives_a_first_seen_device_the_users_old_limits()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            s.Diagnostics.Thresholds.StorageC = 62;
            s.Diagnostics.Components.Gpu = false;
            BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: false);
        });
        rig.Smart.Drives.Add(Drive("storage:a", "A", 40));
        rig.Sensors.Gpus.Add(Gpu("RTX", integrated: false, 40));

        var rules = rig.Engine.Rules();

        Assert.Equal(62, rules.Single(x => x.Sensor.Name == "A").High);
        Assert.False(rules.Single(x => x.Sensor.Name == "RTX").Enabled);
        Assert.DoesNotContain(rules, x => x.Id.StartsWith("builtin-", StringComparison.Ordinal));
    }

    // ---- upgrade: real prior files through JsonConfigStore into the engine ----

    private static void WithJsonStore(Action<string> body)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-builtin-eng-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            body(dir);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string CopyFixture(string dir, string capture)
    {
        foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "upgrade", capture), "*.json"))
        {
            System.IO.File.Copy(file, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(file)));
        }
        return System.IO.Path.Combine(dir, "settings.json");
    }

    private static void T1Machine(Rig rig)
    {
        rig.Sensors.Cpu = new[] { Temp("cpu/pkg", "CPU Package", 50) };
        rig.Sensors.Gpus.Add(Gpu("AMD Radeon(TM) Graphics", integrated: true, 45));
        rig.Sensors.Gpus.Add(Gpu("NVIDIA GeForce RTX 5080", integrated: false, 40));
        rig.Smart.Drives.Add(Drive("storage:wds", "WDS200T1X0E-00AFY0", 35));
    }

    private static List<SensorAlertRule> EngineRulesOver(string settingsPath)
    {
        var rig = new Rig();
        T1Machine(rig);
        using var store = new JsonConfigStore(settingsPath);
        var engine = new SensorAlertEngine(store, rig.Sensors, rig.Time, null, rig.Smart);
        engine.Tick();
        return engine.Rules().Where(x => x.BuiltIn is not null).ToList();
    }

    [Fact]
    public void An_untouched_t1_file_yields_four_entries_at_the_defaults()
    {
        WithJsonStore(dir =>
        {
            var rules = EngineRulesOver(CopyFixture(dir, "v32-main-t1"));

            Assert.Equal(new[] { "TestCPU", "AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 5080", "WDS200T1X0E-00AFY0" }, rules.Select(x => x.Sensor.Name));
            Assert.Equal(new double?[] { 100, 100, 90, 75 }, rules.Select(x => x.High));
            Assert.All(rules, x => Assert.True(x.Enabled));
        });
    }

    [Fact]
    public void An_edited_t1_file_carries_the_cpu_toggle_and_limit_onto_its_entry()
    {
        WithJsonStore(dir =>
        {
            var rules = EngineRulesOver(CopyFixture(dir, "v32-main-t1-edited"));

            Assert.Equal(4, rules.Count);
            Assert.False(rules[0].Enabled);
            Assert.Equal(95, rules[0].High);
            Assert.All(rules.Skip(1), x => Assert.True(x.Enabled));
            Assert.Equal(new double?[] { 100, 90, 75 }, rules.Skip(1).Select(x => x.High));
            Assert.All(rules, x => Assert.False(x.NotifyDesktop));
        });
    }

    [Fact]
    public void Per_kind_rows_from_an_unreleased_build_map_onto_each_device_of_the_kind_and_are_hidden()
    {
        WithJsonStore(dir =>
        {
            var path = System.IO.Path.Combine(dir, "settings.json");
            System.IO.File.WriteAllText(path, """
                { "schemaVersion": 32, "diagnostics": { "sensorAlerts": [
                  { "id": "builtin-cpu", "enabled": false, "builtIn": "cpuTemp", "sensor": { "category": "builtin", "id": "cpu", "name": "CPU", "type": "Temperature", "parentName": "" }, "high": 93, "raiseAfterSeconds": 30, "severity": "act", "notifyDesktop": false },
                  { "id": "builtin-gpu", "enabled": true, "builtIn": "gpuTemp", "sensor": { "category": "builtin", "id": "gpu", "name": "GPU", "type": "Temperature", "parentName": "" }, "high": 85, "low": 62, "raiseAfterSeconds": 45, "severity": "watch", "notifyDesktop": true },
                  { "id": "builtin-storage", "enabled": true, "builtIn": "storageTemp", "sensor": { "category": "builtin", "id": "storage", "name": "Storage", "type": "Temperature", "parentName": "" }, "high": 66, "raiseAfterSeconds": 60, "severity": "watch", "notifyDesktop": true },
                  { "id": "builtin-ram", "enabled": true, "builtIn": "ramTemp", "sensor": { "category": "builtin", "id": "ram", "name": "Memory", "type": "Temperature", "parentName": "" }, "high": 70, "raiseAfterSeconds": 60, "severity": "watch", "notifyDesktop": true }
                ] } }
                """);

            var rules = EngineRulesOver(path);

            Assert.Equal(4, rules.Count);
            Assert.DoesNotContain(rules, x => x.Id.StartsWith("builtin-", StringComparison.Ordinal));
            var cpu = rules[0];
            Assert.False(cpu.Enabled);
            Assert.Equal(93, cpu.High);
            Assert.Equal(30, cpu.RaiseAfterSeconds);
            Assert.Equal("act", cpu.Severity);
            Assert.False(cpu.NotifyDesktop);
            Assert.All(rules.Skip(1).Take(2), x =>
            {
                Assert.Equal(85, x.High);
                Assert.Equal(62, x.Low);
                Assert.Equal(45, x.RaiseAfterSeconds);
            });
            Assert.Equal(66, rules[3].High);
        });
    }

    // ---- built-in resolution ----

    [Fact]
    public void Cpu_builtin_reads_the_package_sensor_and_raises_with_the_device_name()
    {
        var rig = BuiltInRig(r => r.Sensors.Cpu = new[] { Temp("cpu/core", "CPU Core #1", 50), Temp("cpu/pkg", "CPU Package", 101) });

        rig.Seconds(6);

        Assert.Equal(SensorAlertStates.Active, rig.State(CpuId));
        var notice = Assert.Single(rig.Notices).Notice;
        Assert.StartsWith("TestCPU above 100 °C", notice.Text);
    }

    [Fact]
    public void A_built_in_with_a_low_limit_raises_below_it()
    {
        var rig = BuiltInRig(r => CpuHot(r, 65), s => s.Diagnostics.SensorAlerts.Single(r => r.Id == CpuId).Low = 70);

        rig.Seconds(6);

        Assert.Equal(SensorAlertStates.Active, rig.State(CpuId));
        Assert.Equal("below", StateOf(rig, CpuId).Direction);
    }

    [Fact]
    public void A_built_in_with_no_limits_only_tracks_and_never_raises()
    {
        var rig = BuiltInRig(r => CpuHot(r, 110), s =>
        {
            var cpu = s.Diagnostics.SensorAlerts.Single(r => r.Id == CpuId);
            cpu.High = null;
            cpu.Low = null;
        });

        rig.Seconds(10);

        Assert.Equal(SensorAlertStates.Ok, rig.State(CpuId));
        Assert.Empty(rig.Notices);
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void Each_gpu_is_its_own_entry_including_an_integrated_one()
    {
        var rig = BuiltInRig(r =>
        {
            r.Sensors.Gpus.Add(Gpu("Radeon Graphics", integrated: true, 99));
            r.Sensors.Gpus.Add(Gpu("RTX A", integrated: false, 60));
            r.Sensors.Gpus.Add(Gpu("RTX B", integrated: false, 92));
        });

        rig.Seconds(6);

        Assert.Equal(SensorAlertStates.Active, rig.State(GpuId("RTX B")));
        Assert.Equal(92, StateOf(rig, GpuId("RTX B")).Value);
        Assert.Equal(SensorAlertStates.Ok, rig.State(GpuId("RTX A")));
        Assert.Equal(SensorAlertStates.Ok, rig.State(GpuId("Radeon Graphics")));
        Assert.StartsWith("RTX B above 90 °C", rig.Notices.Single().Notice.Text);
    }

    [Fact]
    public void One_failing_source_leaves_the_other_built_ins_live()
    {
        var rig = BuiltInRig(r => CpuHot(r, 104));
        rig.Sensors.MemoryRead = () => throw new InvalidOperationException("memory source down");

        rig.Seconds(6);

        Assert.Equal(SensorAlertStates.Active, rig.State(CpuId));
    }

    [Fact]
    public void A_pending_built_in_survives_another_sources_failure()
    {
        var rig = BuiltInRig(r => CpuHot(r, 104));
        rig.Sensors.MemoryRead = () => throw new InvalidOperationException("memory source down");

        rig.Seconds(3);

        Assert.Equal(SensorAlertStates.Pending, rig.State(CpuId));
    }

    [Fact]
    public void Each_drive_is_its_own_entry_and_an_ignored_one_is_disabled()
    {
        var rig = BuiltInRig(r =>
        {
            r.Store.Update(s => s.Diagnostics.IgnoredComponents.Add("storage:hot-ignored"));
            r.Smart.Drives.Add(Drive("storage:hot-ignored", "Ignored", 88));
            r.Smart.Drives.Add(Drive("storage:a", "Drive A", 50));
            r.Smart.Drives.Add(Drive("storage:b", "Drive B", 80));
        });

        rig.Seconds(6);

        Assert.Equal(SensorAlertStates.Active, rig.State(DriveId("storage:b")));
        Assert.Equal(80, StateOf(rig, DriveId("storage:b")).Value);
        Assert.Equal(SensorAlertStates.Ok, rig.State(DriveId("storage:a")));
        var ignored = StateOf(rig, DriveId("storage:hot-ignored"));
        Assert.Equal(SensorAlertStates.Off, ignored.State);
        Assert.Equal(88, ignored.Value);
        Assert.StartsWith("Drive B above 75 °C", rig.Notices.Single().Notice.Text);
    }

    [Fact]
    public void A_built_in_whose_reading_goes_stale_goes_unavailable_and_never_raises()
    {
        var rig = BuiltInRig(r => CpuHot(r, 50));
        rig.Seconds(2);
        rig.Sensors.Cpu = new[] { Temp("cpu/pkg", "CPU Package", 200, stale: true) };

        rig.Seconds(12);

        Assert.Equal(SensorAlertStates.Unavailable, rig.State(CpuId));
        Assert.Empty(rig.Notices);
    }

    [Fact]
    public void The_history_series_id_and_the_rule_sensor_match_the_monitoring_series()
    {
        var rig = BuiltInRig(r => r.Sensors.Gpus.Add(Gpu("RTX 5080", integrated: false, 40)));

        var device = Assert.Single(rig.Engine.BuiltInDevices());

        Assert.Equal("gpu-temp:RTX 5080", device.SeriesId);
        Assert.Equal("builtin:gpu-temp:RTX 5080", Assert.Single(rig.Engine.Rules()).Id);
    }

    // ---- persist until dismissed ----

    private static Rig RaisedCpuRig(out Action cool)
    {
        var rig = BuiltInRig(r => CpuHot(r, 104));
        rig.Seconds(6);
        Assert.Equal(SensorAlertStates.Active, rig.State(CpuId));
        var r = rig;
        cool = () =>
        {
            r.SetTemp(60, "cpu/pkg", "CPU Package");
            r.Seconds(7);
        };
        return rig;
    }

    [Fact]
    public void A_cleared_alert_stays_until_dismissed_and_reports_its_peak_and_threshold()
    {
        var rig = RaisedCpuRig(out var cool);
        rig.SetTemp(107, "cpu/pkg", "CPU Package");
        rig.Seconds(1);

        cool();

        var state = StateOf(rig, CpuId);
        Assert.Equal(SensorAlertStates.Ok, state.State);
        Assert.NotNull(state.EndedAt);
        Assert.Equal(107, state.EndedPeak);
        Assert.Equal(100, state.EndedThreshold);
        Assert.Equal("above", state.EndedDirection);
        var component = Assert.Single(rig.Engine.HealthComponents());
        Assert.Equal(HealthStatuses.Watch, component.Status);
        var reason = Assert.Single(component.Reasons);
        Assert.Equal("alerts.sensorThresholdEnded", reason.Code);
        Assert.Equal("false", reason.Params!["active"]);
        Assert.Equal("107", reason.Params["peak"]);
        Assert.Equal("100", reason.Params["threshold"]);
        Assert.True(long.Parse(reason.Params["ended"]) > 0);
        Assert.Equal("cpuTemp", reason.Params["builtIn"]);
    }

    [Fact]
    public void Dismissing_a_finished_alert_removes_the_banner_and_the_health_entry()
    {
        var rig = RaisedCpuRig(out var cool);
        cool();

        Assert.True(rig.Engine.Acknowledge(CpuId));

        Assert.Null(StateOf(rig, CpuId).EndedAt);
        Assert.Empty(rig.Engine.HealthComponents());
        Assert.False(rig.Engine.Acknowledge(CpuId));
    }

    [Fact]
    public void A_finished_alert_expires_a_day_after_it_ended()
    {
        var rig = RaisedCpuRig(out var cool);
        cool();

        rig.Time.Advance(TimeSpan.FromHours(23));
        rig.Engine.Tick();
        Assert.NotNull(StateOf(rig, CpuId).EndedAt);

        rig.Time.Advance(TimeSpan.FromHours(1.1));
        rig.Engine.Tick();
        Assert.Null(StateOf(rig, CpuId).EndedAt);
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void An_alert_dismissed_while_active_leaves_nothing_behind_when_it_clears()
    {
        var rig = RaisedCpuRig(out var cool);
        rig.Engine.Acknowledge(CpuId);

        cool();

        Assert.Null(StateOf(rig, CpuId).EndedAt);
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void Active_uses_the_rule_severity_and_a_finished_alert_is_watch()
    {
        var rig = BuiltInRig(r => CpuHot(r, 104), s => s.Diagnostics.SensorAlerts.Single(r => r.Id == CpuId).Severity = "act");
        rig.Seconds(6);
        var active = Assert.Single(rig.Engine.HealthComponents());
        Assert.Equal(HealthStatuses.Act, active.Status);
        var activeParams = active.Reasons[0].Params!;
        Assert.Equal("true", activeParams["active"]);
        Assert.Equal("100", activeParams["threshold"]);

        rig.SetTemp(60, "cpu/pkg", "CPU Package");
        rig.Seconds(7);

        Assert.Equal(HealthStatuses.Watch, Assert.Single(rig.Engine.HealthComponents()).Status);
    }

    [Fact]
    public void A_new_raise_replaces_the_finished_alert()
    {
        var rig = RaisedCpuRig(out var cool);
        cool();

        rig.SetTemp(105, "cpu/pkg", "CPU Package");
        rig.Seconds(6);

        var state = StateOf(rig, CpuId);
        Assert.Equal(SensorAlertStates.Active, state.State);
        Assert.Null(state.EndedAt);
        Assert.Equal("alerts.sensorThreshold", Assert.Single(rig.Engine.HealthComponents()).Reasons[0].Code);
    }

    [Fact]
    public void A_user_rule_lingers_the_same_way()
    {
        var rig = new Rig();
        rig.AddRule(TempRule(high: 80, raise: 0, clear: 0));
        rig.SetTemp(90);
        rig.Seconds(2);
        rig.SetTemp(60);
        rig.Seconds(7);

        Assert.Equal(SensorAlertStates.Ok, rig.State());
        Assert.NotNull(rig.Engine.States().Single().EndedAt);
        Assert.Single(rig.Engine.HealthComponents());
    }

    // ---- notification routing ----

    [Fact]
    public void Each_episode_emits_a_notice_keyed_by_its_rule_and_a_rule_with_notifyDesktop_off_emits_none()
    {
        var rig = BuiltInRig(r =>
        {
            r.Sensors.Gpus.Add(Gpu("RTX", integrated: false, 95));
            CpuHot(r, 104);
        }, s => s.Diagnostics.SensorAlerts.Single(r => r.Id == GpuId("RTX")).NotifyDesktop = false);
        rig.Seconds(6);
        rig.SetTemp(60, "cpu/pkg", "CPU Package");
        rig.Seconds(7);
        rig.SetTemp(104, "cpu/pkg", "CPU Package");
        rig.Seconds(6);

        Assert.Equal(2, rig.Notices.Count);
        Assert.All(rig.Notices, n => Assert.Equal("alert:" + CpuId, n.Key));
    }

    [Fact]
    public void A_rule_edit_drops_the_finished_alert()
    {
        var rig = RaisedCpuRig(out var cool);
        cool();

        rig.Store.Update(s => s.Diagnostics.SensorAlerts.Single(r => r.Id == CpuId).High = 95);
        rig.Engine.Reconcile();

        Assert.Null(StateOf(rig, CpuId).EndedAt);
    }

    [Fact]
    public void Built_in_health_component_is_the_only_one_and_a_second_alert_kind_never_double_notifies()
    {
        var rig = RaisedCpuRig(out _);

        var component = Assert.Single(rig.Engine.HealthComponents());

        Assert.Equal("alert:" + CpuId, component.Id);
        Assert.Equal("TestCPU", component.Name);
        var notices = DiagnosticsAlertService.EvaluateNotifications(
            new[] { component }, new DiagnosticsNotifications { Enabled = true }, new Dictionary<string, DateTime>(), DateTime.UtcNow);
        Assert.Empty(notices);
        Assert.Single(rig.Notices);
    }

    [Fact]
    public void The_finished_alert_text_carries_units()
    {
        var rig = RaisedCpuRig(out var cool);
        cool();

        var text = Assert.Single(rig.Engine.HealthComponents()).Reasons[0].Summary;

        Assert.Contains("100 °C", text);
    }

    // ---- fan entries ----

    private static Nexus.Service.Models.Cooling.FanChannel Fan(string id, string name, int rpm = 900, string? rpmSensorId = null, bool unavailable = false) =>
        new() { Id = id, Name = name, Rpm = rpm, RpmSensorId = rpmSensorId, RpmUnavailable = unavailable };

    private static string FanRule(string channelId) => BuiltInSensorRules.RuleId(BuiltInSensorRules.FanSeriesPrefix + Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId(channelId));

    [Fact]
    public void Each_channel_is_one_fan_entry_keyed_like_the_history_series_with_no_limits()
    {
        var rig = BuiltInRig(r =>
        {
            r.Fans.Channels.Add(Fan("/lpc/nct/control/1", "CPU Fan", 1200));
            r.Fans.Channels.Add(Fan("np50:1/port1", "Port 1", 700));
        });

        var fans = rig.Engine.Rules().Where(x => x.BuiltIn == BuiltInSensorRules.FanSpeed).ToList();

        Assert.Equal(new[] { "builtin:fan:lpc-nct-control-1", "builtin:fan:np50:1-port1" }, fans.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
        var cpu = fans.Single(x => x.Id == FanRule("/lpc/nct/control/1"));
        Assert.Equal("CPU Fan", cpu.Sensor.Name);
        Assert.Equal("Fan", cpu.Sensor.Type);
        Assert.Null(cpu.High);
        Assert.Null(cpu.Low);
        Assert.True(cpu.Enabled);
        Assert.Equal("channel", cpu.Source!.Kind);
    }

    [Fact]
    public void A_fan_entry_follows_the_stall_toggle_in_the_ignore_list_and_a_pre_ignored_fan_starts_off()
    {
        var rig = BuiltInRig(
            r => r.Fans.Channels.Add(Fan("fan1", "Rear")),
            tweak: s => s.Diagnostics.IgnoredComponents.Add("cooling:fan1"));
        // Seeded before the ignore entry existed in the rig order, so force a fresh seed.
        Assert.False(rig.Engine.Rules().Single(x => x.BuiltIn == BuiltInSensorRules.FanSpeed).Enabled);

        rig.Store.Update(s => s.Diagnostics.IgnoredComponents.Clear());

        Assert.True(rig.Engine.Rules().Single(x => x.BuiltIn == BuiltInSensorRules.FanSpeed).Enabled);
    }

    [Fact]
    public void A_fan_entry_that_is_off_still_reports_its_rpm_and_raises_nothing()
    {
        var rig = BuiltInRig(
            r => r.Fans.Channels.Add(Fan("fan1", "Rear", 100)),
            tweak: s => s.Diagnostics.IgnoredComponents.Add("cooling:fan1"));
        EditStored(rig, FanRule("fan1"), x => x.Low = 800);

        rig.Seconds(10);

        var state = StateOf(rig, FanRule("fan1"));
        Assert.Equal(SensorAlertStates.Off, state.State);
        Assert.Equal(100, state.Value);
        Assert.Empty(rig.Notices);
        Assert.DoesNotContain(rig.Engine.HealthComponents(), x => x.Id == "alert:" + FanRule("fan1"));
    }

    [Fact]
    public void A_built_in_temperature_that_is_off_keeps_its_reading_and_is_never_a_problem()
    {
        var rig = BuiltInRig(r => CpuHot(r, 99));
        EditStored(rig, CpuId, x =>
        {
            x.Enabled = false;
            x.High = 80;
            x.RaiseAfterSeconds = 0;
        });

        rig.Seconds(5);

        var state = StateOf(rig, CpuId);
        Assert.Equal(SensorAlertStates.Off, state.State);
        Assert.Equal(99, state.Value);
        Assert.Empty(rig.Notices);
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void A_fan_that_disappears_keeps_its_entry_and_settings()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(Fan("fan1", "Rear")));
        EditStored(rig, FanRule("fan1"), x => x.High = 2000);

        rig.Fans.Channels.Clear();
        rig.Engine.Tick();
        Assert.Equal(2000, rig.Engine.Rules().Single(x => x.BuiltIn == BuiltInSensorRules.FanSpeed).High);

        rig.Fans.Channels.Add(Fan("fan1", "Rear"));
        rig.Engine.Tick();
        Assert.Equal(2000, rig.Engine.Rules().Single(x => x.BuiltIn == BuiltInSensorRules.FanSpeed).High);
    }

    [Fact]
    public void A_fan_entry_with_a_limit_raises_in_rpm_and_one_without_never_does()
    {
        var rig = BuiltInRig(r =>
        {
            r.Fans.Channels.Add(Fan("fan1", "Rear", 500));
            r.Fans.Channels.Add(Fan("fan2", "Front", 500));
        });
        EditStored(rig, FanRule("fan1"), x => x.Low = 800);
        rig.Seconds(10);

        Assert.Equal("active", StateOf(rig, FanRule("fan1")).State);
        Assert.Equal("ok", StateOf(rig, FanRule("fan2")).State);
    }

    [Fact]
    public void A_fan_entry_with_a_tach_sensor_reports_it_as_its_source_and_blocks_a_duplicate_custom_rule()
    {
        var rig = BuiltInRig(r =>
        {
            r.Fans.Channels.Add(Fan("pwm1", "Chassis", 800, rpmSensorId: "tach1"));
            r.Sensors.Mobo = new[] { new HardwareSensor { Id = "tach1", Name = "Fan #1", Type = "Fan", Value = 800, Units = "RPM", Parent = new SensorParent { Id = "m", Name = "Board" } } };
        });

        var rule = rig.Engine.Rules().Single(x => x.BuiltIn == BuiltInSensorRules.FanSpeed);

        Assert.Equal("sensor", rule.Source!.Kind);
        Assert.Equal("tach1", rule.Source.Sensor!.Id);
        var tracker = SensorAlertRules.FindTracker(new SensorRef { Category = "motherboard", Id = "tach1", Type = "Fan" }, true, rig.Engine.Rules(), null);
        Assert.Equal(rule.Id, tracker?.Id);
    }

    private static SensorAlertRule UserRule(string id, string sensorId, string name, string parent) => new()
    {
        Id = id,
        Sensor = new SensorRef { Category = "motherboard", Id = sensorId, Name = name, Type = "Fan", ParentName = parent },
    };

    [Fact]
    public void A_stale_id_matches_a_tracker_by_name_only_when_exactly_one_rule_has_that_name()
    {
        var candidate = new SensorRef { Category = "motherboard", Id = "old", Name = "Fan #1", Type = "Fan", ParentName = "Board" };
        var one = new[] { UserRule("a", "new", "Fan #1", "Board") };
        var twins = new[] { UserRule("a", "new1", "Fan #1", "Board"), UserRule("b", "new2", "Fan #1", "Board") };

        Assert.Equal("a", SensorAlertRules.FindTracker(candidate, false, one, null)?.Id);
        Assert.Null(SensorAlertRules.FindTracker(candidate, false, twins, null));
        Assert.Null(SensorAlertRules.FindTracker(candidate, true, one, null));
    }

    [Fact]
    public void An_id_match_wins_over_twin_names()
    {
        var candidate = new SensorRef { Category = "motherboard", Id = "new2", Name = "Fan #1", Type = "Fan", ParentName = "Board" };
        var twins = new[] { UserRule("a", "new1", "Fan #1", "Board"), UserRule("b", "new2", "Fan #1", "Board") };

        Assert.Equal("b", SensorAlertRules.FindTracker(candidate, true, twins, null)?.Id);
    }

    [Fact]
    public void An_unreadable_tach_channel_is_listed_with_no_value()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(Fan("hub1", "Hub", 0, unavailable: true)));

        Assert.Single(rig.Engine.Rules(), x => x.BuiltIn == BuiltInSensorRules.FanSpeed);
        rig.Seconds(3);
        Assert.Equal("ok", StateOf(rig, FanRule("hub1")).State);
    }
}

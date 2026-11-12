using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Alerts;

public sealed class BuiltInSensorRulesTests : IDisposable
{
    private readonly string _dir;

    public BuiltInSensorRulesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-builtin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly string[] TemplateIds = { "builtin-cpu", "builtin-gpu", "builtin-storage", "builtin-ram" };

    private static BuiltInDevice Device(string kind, string seriesId, string name, bool integrated = false, string componentId = "") => new(kind, seriesId, name, integrated, componentId);

    private static SensorAlertRule Rule(DiagnosticsSettings d, string id) => d.SensorAlerts.Single(r => r.Id == id);

    private string CopyFixture(string capture)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "upgrade", capture);
        foreach (var file in Directory.GetFiles(source, "*.json"))
        {
            File.Copy(file, Path.Combine(_dir, Path.GetFileName(file)));
        }
        return Path.Combine(_dir, "settings.json");
    }

    private static string Rules(DiagnosticsSettings d) =>
        string.Join("\n", d.SensorAlerts.Select(r => JsonSerializer.Serialize(r, AppJsonContext.Default.SensorAlertRule)));

    private static string Serialize(NexusSettings s) => JsonSerializer.Serialize(s, PersistenceJsonContext.Default.NexusSettings);

    // ---- Ensure ----

    [Fact]
    public void A_new_install_has_no_templates_and_marks_the_per_device_generation()
    {
        var d = new DiagnosticsSettings();

        Assert.True(BuiltInSensorRules.Ensure(d, newInstall: true));

        Assert.Empty(d.SensorAlerts);
        Assert.Equal(BuiltInSensorRules.PerDeviceVersion, d.BuiltInSensorsVersion);
    }

    [Theory]
    [InlineData("cpuTemp", "cpu-temp", false, 100)]
    [InlineData("gpuTemp", "gpu-temp:g0", false, 90)]
    [InlineData("gpuTemp", "gpu-temp:g1", true, 100)]
    [InlineData("storageTemp", "drive-temp:d0", false, 75)]
    [InlineData("ramTemp", "mem-temp", false, 75)]
    public void A_new_devices_defaults_follow_its_kind_and_an_integrated_gpu_follows_the_cpu(string kind, string seriesId, bool integrated, double high)
    {
        var rule = BuiltInSensorRules.Defaults(Device(kind, seriesId, "Name", integrated));

        Assert.Equal("builtin:" + seriesId, rule.Id);
        Assert.Equal(kind, rule.BuiltIn);
        Assert.Equal(high, rule.High);
        Assert.Null(rule.Low);
        Assert.True(rule.Enabled);
        Assert.True(rule.NotifyDesktop);
        Assert.True(rule.ShowInHealth);
        Assert.Equal(60, rule.RaiseAfterSeconds);
        Assert.Equal(5, rule.ClearAfterSeconds);
        Assert.Equal("watch", rule.Severity);
        Assert.Equal(new SensorRef { Category = "builtin", Id = seriesId, Name = "Name", Type = "Temperature", ParentName = "" }.Id, rule.Sensor.Id);
        Assert.Equal("builtin", rule.Sensor.Category);
    }

    [Fact]
    public void Seed_maps_a_templates_limits_toggle_and_notify_onto_each_device_but_an_untouched_integrated_gpu_keeps_its_default()
    {
        var d = new DiagnosticsSettings();
        d.Thresholds.GpuC = 82;
        d.Components.Gpu = false;
        BuiltInSensorRules.Ensure(d, newInstall: false);

        var discrete = BuiltInSensorRules.Seed(Device("gpuTemp", "gpu-temp:g0", "RTX"), d);
        Assert.Equal(82, discrete.High);
        Assert.False(discrete.Enabled);
        Assert.Equal(82, BuiltInSensorRules.Seed(Device("gpuTemp", "gpu-temp:g1", "iGPU", integrated: true), d).High);

        Rule(d, "builtin-gpu").High = 90;
        Assert.Equal(100, BuiltInSensorRules.Seed(Device("gpuTemp", "gpu-temp:g1", "iGPU", integrated: true), d).High);
    }

    [Fact]
    public void Seed_disables_an_ignored_drive_only()
    {
        var d = new DiagnosticsSettings();
        d.IgnoredComponents.Add("storage:b");
        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.False(BuiltInSensorRules.Seed(Device("storageTemp", "drive-temp:storage-b", "B", componentId: "storage:b"), d).Enabled);
        Assert.True(BuiltInSensorRules.Seed(Device("storageTemp", "drive-temp:storage-a", "A", componentId: "storage:a"), d).Enabled);
    }

    [Fact]
    public void An_upgrade_seeds_each_built_in_from_the_legacy_fields()
    {
        var d = new DiagnosticsSettings();
        d.Thresholds.CpuC = 95;
        d.Thresholds.RamC = 55;
        d.Components.Cpu = false;
        d.Components.Storage = false;
        d.Notifications.Enabled = true;
        d.Notifications.HighTemp = true;
        d.WarningLingerMinutes = 60;

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.False(Rule(d, "builtin-cpu").Enabled);
        Assert.True(Rule(d, "builtin-gpu").Enabled);
        Assert.False(Rule(d, "builtin-storage").Enabled);
        // CPU and memory were edited and stay; GPU and storage were untouched and take the new defaults.
        Assert.Equal(new double?[] { 95, 90, 75, 55 }, d.SensorAlerts.Select(r => r.High).ToArray());
        Assert.All(d.SensorAlerts, r =>
        {
            Assert.True(r.NotifyDesktop);
            Assert.Equal(60, r.RaiseAfterSeconds);
            // The linger no longer maps to a clear delay.
            Assert.Equal(5, r.ClearAfterSeconds);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void An_upgrade_notifies_only_when_both_old_switches_were_on(bool master, bool highTemp)
    {
        var d = new DiagnosticsSettings();
        d.Notifications.Enabled = master;
        d.Notifications.HighTemp = highTemp;

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.All(d.SensorAlerts, r => Assert.False(r.NotifyDesktop));
    }

    [Fact]
    public void An_upgrade_that_ignored_the_cooling_aggregate_seeds_every_built_in_disabled()
    {
        var d = new DiagnosticsSettings();
        d.Notifications.Enabled = true;
        d.IgnoredComponents.Add("cooling");

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.All(d.SensorAlerts, r => Assert.False(r.Enabled));
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(500, 100)]
    [InlineData(-4, 20)]
    public void A_bad_legacy_limit_falls_back_or_clamps_to_the_old_slider_range(double legacy, double expected)
    {
        var d = new DiagnosticsSettings();
        d.Thresholds.CpuC = legacy;

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.Equal(expected, Rule(d, "builtin-cpu").High);
    }

    [Fact]
    public void Ensure_is_idempotent_and_leaves_user_edits_alone()
    {
        var d = new DiagnosticsSettings();
        BuiltInSensorRules.Ensure(d, newInstall: false);
        Rule(d, "builtin-gpu").High = 80;
        Rule(d, "builtin-gpu").NotifyDesktop = false;
        Rule(d, "builtin-ram").Enabled = false;
        d.SensorAlerts.Add(BuiltInSensorRules.Defaults(Device("cpuTemp", "cpu-temp", "CPU")));
        var before = Rules(d);

        Assert.False(BuiltInSensorRules.Ensure(d, newInstall: false));

        Assert.Equal(before, Rules(d));
    }

    [Fact]
    public void Templates_are_seeded_once_per_file_and_user_rules_stay()
    {
        var d = new DiagnosticsSettings();
        d.SensorAlerts.Add(new SensorAlertRule { Id = "u1", High = 50, Sensor = new SensorRef { Category = "cpu", Id = "x", Type = "Temperature" } });
        d.SensorAlerts.Add(new SensorAlertRule { Id = "builtin-gpu", High = 77, BuiltIn = "gpuTemp" });

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.Equal(new[] { "u1", "builtin-gpu", "builtin-cpu", "builtin-storage", "builtin-ram" }, d.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.Equal(77, Rule(d, "builtin-gpu").High);
        Assert.All(d.SensorAlerts.Where(r => r.BuiltIn is not null), r => Assert.True(BuiltInSensorRules.IsTemplate(r)));

        d.SensorAlerts.RemoveAll(r => r.Id == "builtin-ram");
        Assert.False(BuiltInSensorRules.Ensure(d, newInstall: false));
        Assert.DoesNotContain(d.SensorAlerts, r => r.Id == "builtin-ram");
    }

    [Fact]
    public void A_built_in_id_without_a_kind_is_dropped()
    {
        var d = new DiagnosticsSettings();
        d.SensorAlerts.Add(new SensorAlertRule { Id = "builtin:cpu-temp" });

        BuiltInSensorRules.Ensure(d, newInstall: true);

        Assert.Empty(d.SensorAlerts);
    }

    [Fact]
    public void Ensure_re_pins_a_tampered_built_in_sensor_and_keeps_its_limits_in_range()
    {
        var d = new DiagnosticsSettings();
        var cpu = BuiltInSensorRules.Defaults(Device("cpuTemp", "cpu-temp", "CPU"));
        d.SensorAlerts.Add(cpu);
        cpu.Sensor = new SensorRef { Category = "gpu", Id = "cpu-temp", Name = "x", Type = "Fan" };
        cpu.Low = 10;
        cpu.High = 300;

        Assert.True(BuiltInSensorRules.Ensure(d, newInstall: true));

        Assert.Equal("builtin", cpu.Sensor.Category);
        Assert.Equal("Temperature", cpu.Sensor.Type);
        Assert.Equal(20, cpu.Low);
        Assert.Equal(100, cpu.High);
    }

    [Fact]
    public void Ensure_keeps_a_built_in_with_no_limits_and_drops_a_crossed_low()
    {
        var d = new DiagnosticsSettings();
        var cpu = BuiltInSensorRules.Defaults(Device("cpuTemp", "cpu-temp", "CPU"));
        var gpu = BuiltInSensorRules.Defaults(Device("gpuTemp", "gpu-temp:g0", "GPU"));
        d.SensorAlerts.AddRange(new[] { cpu, gpu });
        cpu.High = null;
        gpu.Low = 100;
        gpu.High = 80;

        BuiltInSensorRules.Ensure(d, newInstall: true);

        Assert.Null(cpu.High);
        Assert.Null(cpu.Low);
        Assert.Null(gpu.Low);
        Assert.Equal(80, gpu.High);
    }

    // ---- real prior settings.json shapes through JsonConfigStore ----

    [Fact]
    public void A_real_t1_settings_file_with_untouched_limits_loads_templates_at_the_new_defaults_and_nothing_notifies()
    {
        var path = CopyFixture("v32-main-t1");
        using var store = new JsonConfigStore(path);

        var loaded = store.Load();

        Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(TemplateIds, loaded.Diagnostics.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.Equal(new double?[] { 100, 90, 75, 75 }, loaded.Diagnostics.SensorAlerts.Select(r => r.High).ToArray());
        Assert.All(loaded.Diagnostics.SensorAlerts, r =>
        {
            Assert.True(r.Enabled);
            Assert.False(r.NotifyDesktop);
        });
        Assert.True(loaded.Diagnostics.Notifications.SensorAlerts);
    }

    [Fact]
    public void An_edited_file_with_ten_user_rules_keeps_them_in_order_and_adds_the_hidden_templates()
    {
        var path = CopyFixture("v32-main-t1-edited");
        var rawRules = JsonNode.Parse(File.ReadAllText(path))!["diagnostics"]!["sensorAlerts"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToArray();
        using var store = new JsonConfigStore(path);

        var d = store.Load().Diagnostics;

        Assert.Equal(14, d.SensorAlerts.Count);
        Assert.Equal(rawRules.Concat(TemplateIds).ToArray(), d.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.False(Rule(d, "builtin-cpu").Enabled);
        Assert.Equal(95, Rule(d, "builtin-cpu").High);
        // The other three limits were never edited in this file, so they take the new defaults.
        Assert.Equal(new double?[] { 90, 75, 75 }, new[] { "builtin-gpu", "builtin-storage", "builtin-ram" }.Select(id => Rule(d, id).High).ToArray());
        Assert.True(Rule(d, "builtin-gpu").Enabled);
        // The master was on but the high-temperature category off: no toast before, none now.
        Assert.All(d.SensorAlerts.Where(r => r.BuiltIn is not null), r => Assert.False(r.NotifyDesktop));
        // The user rules came through untouched.
        Assert.Equal(50, d.SensorAlerts[0].High);
        Assert.Null(d.SensorAlerts[0].BuiltIn);
        // The legacy fields are frozen as the user left them.
        Assert.Equal(95, d.Thresholds.CpuC);
        Assert.Equal(60, d.WarningLingerMinutes);
    }

    [Fact]
    public void The_old_toast_choice_carries_over_when_both_switches_were_on()
    {
        var path = CopyFixture("v32-main-t1");
        var raw = JsonNode.Parse(File.ReadAllText(path))!;
        raw["diagnostics"]!["notifications"]!["enabled"] = true;
        File.WriteAllText(path, raw.ToJsonString());
        using var store = new JsonConfigStore(path);

        Assert.All(store.Load().Diagnostics.SensorAlerts, r => Assert.True(r.NotifyDesktop));
    }

    [Theory]
    [InlineData("v32-main-t1")]
    [InlineData("v32-main-t1-edited")]
    public void Loading_saving_and_loading_again_gives_identical_json(string capture)
    {
        var path = CopyFixture(capture);
        string first;
        using (var store = new JsonConfigStore(path))
        {
            first = Serialize(store.Load());
            store.FlushNow();
        }
        string second;
        using (var store = new JsonConfigStore(path))
        {
            second = Serialize(store.Load());
            store.FlushNow();
        }

        Assert.Equal(first, second);
        Assert.Equal(first, Serialize(JsonSerializer.Deserialize(File.ReadAllText(path), PersistenceJsonContext.Default.NexusSettings)!));
    }

    [Fact]
    public void A_build_without_sensor_alerts_that_drops_the_list_is_reseeded_from_the_frozen_legacy_fields()
    {
        var path = CopyFixture("v32-main-t1-edited");
        using (var store = new JsonConfigStore(path))
        {
            store.Load();
            store.FlushNow();
        }
        // The downgraded build rewrites the file without the list and keeps the schema version.
        var downgraded = JsonNode.Parse(File.ReadAllText(path))!;
        downgraded["diagnostics"]!.AsObject().Remove("sensorAlerts");
        downgraded["diagnostics"]!.AsObject().Remove("builtInSensorsVersion");
        File.WriteAllText(path, downgraded.ToJsonString());

        using var reopened = new JsonConfigStore(path);
        var d = reopened.Load().Diagnostics;

        Assert.Equal(TemplateIds, d.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.False(Rule(d, "builtin-cpu").Enabled);
        Assert.Equal(95, Rule(d, "builtin-cpu").High);
    }

    [Fact]
    public void A_missing_settings_file_is_a_new_install_without_templates_that_persists()
    {
        var path = Path.Combine(_dir, "settings.json");
        string first;
        using (var store = new JsonConfigStore(path))
        {
            var d = store.Load().Diagnostics;
            Assert.Empty(d.SensorAlerts);
            Assert.Equal(BuiltInSensorRules.PerDeviceVersion, d.BuiltInSensorsVersion);
            first = Serialize(store.Load());
            store.FlushNow();
        }

        using var reopened = new JsonConfigStore(path);
        Assert.Equal(first, Serialize(reopened.Load()));
    }

    [Fact]
    public void Null_and_malformed_entries_in_a_hand_edited_list_are_dropped_instead_of_wiping_the_settings()
    {
        var path = CopyFixture("v32-main-t1-edited");
        var raw = JsonNode.Parse(File.ReadAllText(path))!;
        var rules = raw["diagnostics"]!["sensorAlerts"]!.AsArray();
        rules.Insert(0, null);
        rules.Add(new JsonObject { ["enabled"] = true });
        rules.Add(new JsonObject { ["id"] = "no-sensor", ["high"] = 70 });
        raw["ui"]!["sidebarCollapsed"] = true;
        File.WriteAllText(path, raw.ToJsonString());
        using var store = new JsonConfigStore(path);

        var loaded = store.Load();

        Assert.True(loaded.Ui.SidebarCollapsed);
        Assert.Equal(TemplateIds, loaded.Diagnostics.SensorAlerts.TakeLast(4).Select(r => r.Id).ToArray());
        Assert.Equal(15, loaded.Diagnostics.SensorAlerts.Count);
        Assert.All(loaded.Diagnostics.SensorAlerts, r => Assert.NotNull(r.Sensor));
        Assert.Equal(95, Rule(loaded.Diagnostics, "builtin-cpu").High);
    }

    [Fact]
    public void An_unreadable_file_falls_back_to_defaults_seeded_as_an_upgrade_not_a_new_install()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        using var store = new JsonConfigStore(path);

        var d = store.Load().Diagnostics;

        Assert.Equal(new double?[] { 100, 90, 75, 75 }, d.SensorAlerts.Select(r => r.High).ToArray());
        Assert.All(d.SensorAlerts, r => Assert.False(r.NotifyDesktop));
    }

    [Theory]
    [InlineData(90, 100)]
    [InlineData(91, 91)]
    [InlineData(95, 95)]
    public void A_cpu_limit_equal_to_the_old_default_takes_the_new_one_and_any_other_value_is_kept(double legacy, double expected)
    {
        var d = new DiagnosticsSettings();
        d.Thresholds.CpuC = legacy;

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.Equal(expected, Rule(d, "builtin-cpu").High);
    }

    [Fact]
    public void Each_component_compares_against_its_own_old_default()
    {
        var d = new DiagnosticsSettings();
        d.Thresholds.GpuC = 85;
        d.Thresholds.StorageC = 70;
        d.Thresholds.RamC = 60;
        d.Thresholds.CpuC = 85;

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.Equal(new double?[] { 85, 90, 75, 75 }, d.SensorAlerts.Select(r => r.High).ToArray());
    }
}

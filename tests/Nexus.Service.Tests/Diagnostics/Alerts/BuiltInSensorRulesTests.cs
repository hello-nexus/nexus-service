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

    private static readonly string[] Ids = { "builtin-cpu", "builtin-gpu", "builtin-storage", "builtin-ram" };

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
    public void A_new_install_gets_four_enabled_notifying_built_ins_at_the_new_install_limits()
    {
        var d = new DiagnosticsSettings();

        Assert.True(BuiltInSensorRules.Ensure(d, newInstall: true));

        Assert.Equal(Ids, d.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.Equal(new double?[] { 100, 90, 75, 75 }, d.SensorAlerts.Select(r => r.High).ToArray());
        Assert.All(d.SensorAlerts, r =>
        {
            Assert.True(r.Enabled);
            Assert.True(r.NotifyDesktop);
            Assert.True(r.ShowInHealth);
            Assert.Null(r.Low);
            Assert.Equal(60, r.RaiseAfterSeconds);
            Assert.Equal(5, r.ClearAfterSeconds);
            Assert.Equal("watch", r.Severity);
            Assert.Equal("builtin", r.Sensor.Category);
        });
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
        Assert.Equal(new double?[] { 95, 85, 70, 55 }, d.SensorAlerts.Select(r => r.High).ToArray());
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

    [Theory]
    [InlineData(double.NaN, 90)]
    [InlineData(500, 105)]
    [InlineData(-4, 60)]
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
        BuiltInSensorRules.Ensure(d, newInstall: true);
        Rule(d, "builtin-gpu").High = 80;
        Rule(d, "builtin-gpu").NotifyDesktop = false;
        Rule(d, "builtin-ram").Enabled = false;
        var before = Rules(d);

        Assert.False(BuiltInSensorRules.Ensure(d, newInstall: false));

        Assert.Equal(before, Rules(d));
    }

    [Fact]
    public void A_missing_built_in_is_reseeded_and_user_rules_stay_after_the_built_ins()
    {
        var d = new DiagnosticsSettings();
        d.SensorAlerts.Add(new SensorAlertRule { Id = "u1", High = 50, Sensor = new SensorRef { Category = "cpu", Id = "x", Type = "Temperature" } });
        d.SensorAlerts.Add(new SensorAlertRule { Id = "builtin-gpu", High = 77, BuiltIn = "gpuTemp" });
        d.SensorAlerts.Add(new SensorAlertRule { Id = "u2", High = 60, Sensor = new SensorRef { Category = "cpu", Id = "y", Type = "Temperature" } });

        BuiltInSensorRules.Ensure(d, newInstall: false);

        Assert.Equal(new[] { "builtin-cpu", "builtin-gpu", "builtin-storage", "builtin-ram", "u1", "u2" }, d.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.Equal(77, Rule(d, "builtin-gpu").High);
        Assert.Equal("builtin", Rule(d, "builtin-gpu").Sensor.Category);
    }

    [Fact]
    public void Ensure_re_pins_a_tampered_built_in_sensor_and_drops_its_lower_bound()
    {
        var d = new DiagnosticsSettings();
        BuiltInSensorRules.Ensure(d, newInstall: true);
        var cpu = Rule(d, "builtin-cpu");
        cpu.Sensor = new SensorRef { Category = "gpu", Id = "elsewhere", Name = "x", Type = "Fan" };
        cpu.Low = 20;
        cpu.High = null;

        Assert.True(BuiltInSensorRules.Ensure(d, newInstall: false));

        Assert.Equal("builtin", cpu.Sensor.Category);
        Assert.Equal("cpu", cpu.Sensor.Id);
        Assert.Null(cpu.Low);
        Assert.Equal(100, cpu.High);
    }

    // ---- real prior settings.json shapes through JsonConfigStore ----

    [Fact]
    public void A_real_t1_settings_file_loads_with_four_built_ins_that_keep_the_legacy_limits_and_nothing_notifies()
    {
        var path = CopyFixture("v32-main-t1");
        using var store = new JsonConfigStore(path);

        var loaded = store.Load();

        Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(Ids, loaded.Diagnostics.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.Equal(new double?[] { 90, 85, 70, 60 }, loaded.Diagnostics.SensorAlerts.Select(r => r.High).ToArray());
        Assert.All(loaded.Diagnostics.SensorAlerts, r =>
        {
            Assert.True(r.Enabled);
            Assert.False(r.NotifyDesktop);
        });
        Assert.True(loaded.Diagnostics.Notifications.SensorAlerts);
    }

    [Fact]
    public void An_edited_file_with_ten_user_rules_keeps_them_in_order_after_the_built_ins()
    {
        var path = CopyFixture("v32-main-t1-edited");
        var rawRules = JsonNode.Parse(File.ReadAllText(path))!["diagnostics"]!["sensorAlerts"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToArray();
        using var store = new JsonConfigStore(path);

        var d = store.Load().Diagnostics;

        Assert.Equal(14, d.SensorAlerts.Count);
        Assert.Equal(Ids.Concat(rawRules).ToArray(), d.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.False(Rule(d, "builtin-cpu").Enabled);
        Assert.Equal(95, Rule(d, "builtin-cpu").High);
        Assert.True(Rule(d, "builtin-gpu").Enabled);
        // The master was on but the high-temperature category off: no toast before, none now.
        Assert.All(d.SensorAlerts.Take(4), r => Assert.False(r.NotifyDesktop));
        // The user rules came through untouched.
        Assert.Equal(50, d.SensorAlerts[4].High);
        Assert.Null(d.SensorAlerts[4].BuiltIn);
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
        File.WriteAllText(path, downgraded.ToJsonString());

        using var reopened = new JsonConfigStore(path);
        var d = reopened.Load().Diagnostics;

        Assert.Equal(Ids, d.SensorAlerts.Select(r => r.Id).ToArray());
        Assert.False(Rule(d, "builtin-cpu").Enabled);
        Assert.Equal(95, Rule(d, "builtin-cpu").High);
    }

    [Fact]
    public void A_missing_settings_file_is_a_new_install_with_defaults_that_persist_and_do_not_reseed()
    {
        var path = Path.Combine(_dir, "settings.json");
        string first;
        using (var store = new JsonConfigStore(path))
        {
            var d = store.Load().Diagnostics;
            Assert.Equal(new double?[] { 100, 90, 75, 75 }, d.SensorAlerts.Select(r => r.High).ToArray());
            Assert.All(d.SensorAlerts, r => Assert.True(r.NotifyDesktop));
            first = Serialize(store.Load());
            store.FlushNow();
        }

        using var reopened = new JsonConfigStore(path);
        Assert.Equal(first, Serialize(reopened.Load()));
    }
}

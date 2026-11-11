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
    private static Rig BuiltInRig(Action<NexusSettings>? tweak = null)
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: true);
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

    // ---- built-in resolution ----

    [Fact]
    public void Cpu_builtin_reads_the_package_sensor_and_raises_with_a_built_in_name()
    {
        var rig = BuiltInRig();
        rig.Sensors.Cpu = new[] { Temp("cpu/core", "CPU Core #1", 50), Temp("cpu/pkg", "CPU Package", 101) };

        rig.Seconds(6);

        Assert.Equal(SensorAlertStates.Active, rig.State("builtin-cpu"));
        var notice = Assert.Single(rig.Notices).Notice;
        Assert.StartsWith("CPU temperature above 100 °C", notice.Text);
    }

    [Fact]
    public void Gpu_builtin_follows_the_hottest_discrete_gpu_and_ignores_a_hotter_integrated_one()
    {
        var rig = BuiltInRig();
        rig.Sensors.Gpus.Add(Gpu("Radeon Graphics", integrated: true, 99));
        rig.Sensors.Gpus.Add(Gpu("RTX A", integrated: false, 60));
        rig.Sensors.Gpus.Add(Gpu("RTX B", integrated: false, 92));

        rig.Seconds(6);

        var state = StateOf(rig, "builtin-gpu");
        Assert.Equal(SensorAlertStates.Active, state.State);
        Assert.Equal(92, state.Value);
        Assert.Contains("(RTX B)", rig.Notices.Single().Notice.Text);
    }

    [Fact]
    public void Gpu_builtin_ignores_integrated_graphics_and_is_unavailable_without_a_dedicated_gpu()
    {
        var rig = BuiltInRig();
        rig.Sensors.Gpus.Add(Gpu("Radeon Graphics", integrated: true, 99));

        rig.Seconds(12);

        Assert.Equal(SensorAlertStates.Unavailable, rig.State("builtin-gpu"));
        Assert.Empty(rig.Notices);
    }

    [Fact]
    public void One_failing_source_leaves_the_other_built_ins_live()
    {
        var rig = BuiltInRig();
        rig.Sensors.MemoryRead = () => throw new InvalidOperationException("memory source down");
        rig.SetTemp(104, "cpu/pkg", "CPU Package");

        rig.Seconds(6);

        Assert.Equal(SensorAlertStates.Active, rig.State("builtin-cpu"));
    }

    [Fact]
    public void A_pending_built_in_survives_another_sources_failure()
    {
        var rig = BuiltInRig();
        rig.Sensors.MemoryRead = () => throw new InvalidOperationException("memory source down");
        rig.SetTemp(104, "cpu/pkg", "CPU Package");

        rig.Seconds(3);

        Assert.Equal(SensorAlertStates.Pending, rig.State("builtin-cpu"));
    }

    [Fact]
    public void Storage_builtin_follows_the_hottest_drive_that_is_not_ignored()
    {
        var rig = BuiltInRig(s => s.Diagnostics.IgnoredComponents.Add("storage:hot-ignored"));
        rig.Smart.Drives.Add(Drive("storage:hot-ignored", "Ignored", 88));
        rig.Smart.Drives.Add(Drive("storage:a", "Drive A", 50));
        rig.Smart.Drives.Add(Drive("storage:b", "Drive B", 80));

        rig.Seconds(6);

        var state = StateOf(rig, "builtin-storage");
        Assert.Equal(SensorAlertStates.Active, state.State);
        Assert.Equal(80, state.Value);
        Assert.Contains("(Drive B)", rig.Notices.Single().Notice.Text);
    }

    [Fact]
    public void Ram_builtin_takes_the_hottest_memory_temperature_sensor()
    {
        var rig = BuiltInRig();
        rig.Sensors.Memory = new[] { Temp("m0", "DIMM #1 Temperature", 40), Temp("m1", "DIMM #2 Temperature", 80), Temp("m2", "Chipset Temperature", 99) };

        rig.Seconds(6);

        Assert.Equal(80, StateOf(rig, "builtin-ram").Value);
        Assert.Equal(SensorAlertStates.Active, rig.State("builtin-ram"));
    }

    [Fact]
    public void A_builtin_without_a_source_goes_unavailable_and_never_raises()
    {
        var rig = BuiltInRig();

        rig.Seconds(12);

        Assert.All(BuiltInSensorRules.Ids, id => Assert.Equal(SensorAlertStates.Unavailable, rig.State(id)));
        Assert.Empty(rig.Notices);
    }

    // ---- persist until dismissed ----

    private static Rig RaisedCpuRig(out Action cool)
    {
        var rig = BuiltInRig();
        rig.SetTemp(104, "cpu/pkg", "CPU Package");
        rig.Seconds(6);
        Assert.Equal(SensorAlertStates.Active, rig.State("builtin-cpu"));
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

        var state = StateOf(rig, "builtin-cpu");
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

        Assert.True(rig.Engine.Acknowledge("builtin-cpu"));

        Assert.Null(StateOf(rig, "builtin-cpu").EndedAt);
        Assert.Empty(rig.Engine.HealthComponents());
        Assert.False(rig.Engine.Acknowledge("builtin-cpu"));
    }

    [Fact]
    public void A_finished_alert_expires_a_day_after_it_ended()
    {
        var rig = RaisedCpuRig(out var cool);
        cool();

        rig.Time.Advance(TimeSpan.FromHours(23));
        rig.Engine.Tick();
        Assert.NotNull(StateOf(rig, "builtin-cpu").EndedAt);

        rig.Time.Advance(TimeSpan.FromHours(1.1));
        rig.Engine.Tick();
        Assert.Null(StateOf(rig, "builtin-cpu").EndedAt);
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void An_alert_dismissed_while_active_leaves_nothing_behind_when_it_clears()
    {
        var rig = RaisedCpuRig(out var cool);
        rig.Engine.Acknowledge("builtin-cpu");

        cool();

        Assert.Null(StateOf(rig, "builtin-cpu").EndedAt);
        Assert.Empty(rig.Engine.HealthComponents());
    }

    [Fact]
    public void Active_uses_the_rule_severity_and_a_finished_alert_is_watch()
    {
        var rig = BuiltInRig(s => s.Diagnostics.SensorAlerts.Single(r => r.Id == "builtin-cpu").Severity = "act");
        rig.SetTemp(104, "cpu/pkg", "CPU Package");
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

        var state = StateOf(rig, "builtin-cpu");
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
        rig.Seconds(2);

        Assert.Equal(SensorAlertStates.Ok, rig.State());
        Assert.NotNull(rig.Engine.States().Single().EndedAt);
        Assert.Single(rig.Engine.HealthComponents());
    }

    // ---- notification routing ----

    [Fact]
    public void Each_episode_emits_a_notice_keyed_by_its_rule_and_a_rule_with_notifyDesktop_off_emits_none()
    {
        var rig = BuiltInRig(s => s.Diagnostics.SensorAlerts.Single(r => r.Id == "builtin-gpu").NotifyDesktop = false);
        rig.Sensors.Gpus.Add(Gpu("RTX", integrated: false, 95));
        rig.SetTemp(104, "cpu/pkg", "CPU Package");
        rig.Seconds(6);
        rig.SetTemp(60, "cpu/pkg", "CPU Package");
        rig.Seconds(7);
        rig.SetTemp(104, "cpu/pkg", "CPU Package");
        rig.Seconds(6);

        Assert.Equal(2, rig.Notices.Count);
        Assert.All(rig.Notices, n => Assert.Equal("alert:builtin-cpu", n.Key));
    }

    [Fact]
    public void A_rule_edit_drops_the_finished_alert()
    {
        var rig = RaisedCpuRig(out var cool);
        cool();

        rig.Store.Update(s => s.Diagnostics.SensorAlerts.Single(r => r.Id == "builtin-cpu").High = 95);
        rig.Engine.Reconcile();

        Assert.Null(StateOf(rig, "builtin-cpu").EndedAt);
    }

    [Fact]
    public void Built_in_health_component_is_the_only_one_and_a_second_alert_kind_never_double_notifies()
    {
        var rig = RaisedCpuRig(out _);

        var component = Assert.Single(rig.Engine.HealthComponents());

        Assert.Equal("alert:builtin-cpu", component.Id);
        Assert.Equal("CPU temperature", component.Name);
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
}

using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Alerts;

public partial class SensorAlertEngineTests
{
    private const string HubFanId = "np50:1/port1";
    private const string BoardFanId = "/lpc/nct/control/1";
    private const string BoardTachId = "/lpc/nct/fan/1";

    private static FanChannel HubFan() => new() { Id = HubFanId, Name = "Port 1", DeviceId = "np50:1", DeviceName = "NP50 Hub", Rpm = 700 };

    private static HardwareSensor BoardTach(float rpm = 1200) => new()
    {
        Id = BoardTachId,
        Name = "Fan #1",
        Type = "Fan",
        Value = rpm,
        Units = "RPM",
        Parent = new SensorParent { Id = "mb", Name = "Board" },
    };

    private static void Rename(Rig rig, string key, string? name) => rig.Store.Update(s =>
    {
        if (name is null)
        {
            s.Cooling.FanNames.Remove(key);
        }
        else
        {
            s.Cooling.FanNames[key] = name;
        }
    });

    private static SensorAlertRule BoardFanRule(string name) => new()
    {
        Id = "custom-fan",
        Sensor = new SensorRef { Category = "motherboard", Id = BoardTachId, Name = name, Type = "Fan", ParentName = "Board" },
        RaiseAfterSeconds = 5,
        ClearAfterSeconds = 5,
    };

    private static Rig BoardFanRig()
    {
        var rig = BuiltInRig(r =>
        {
            r.Fans.Channels.Add(new FanChannel { Id = BoardFanId, Name = "Fan #1", RpmSensorId = BoardTachId, Rpm = 1200 });
            r.Sensors.Mobo = new[] { BoardTach() };
        });
        return rig;
    }

    private static SensorAlertRule FanEntry(Rig rig, string channelId) => rig.Engine.Rules().Single(x => x.Id == FanRule(channelId));

    [Fact]
    public void The_engine_keeps_hardware_names_and_stored_rules_through_a_rename_and_its_clearing()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(HubFan()));
        var seriesBefore = rig.Engine.BuiltInDevices().Single(d => d.Kind == BuiltInSensorRules.FanSpeed).SeriesId;
        var storedBefore = rig.Store.Load().Diagnostics.SensorAlerts.Select(Snapshot).ToList();
        var ignoredBefore = rig.Store.Load().Diagnostics.IgnoredComponents.ToList();

        Rename(rig, HubFanId, "Front intake");
        Rename(rig, "np50:1", "Radiator hub");
        rig.Engine.Tick();

        var rule = FanEntry(rig, HubFanId);
        Assert.Equal("Port 1", rule.Sensor.Name);
        Assert.Equal("NP50 Hub", rule.Source!.HardwareName);
        Assert.Equal(seriesBefore, rig.Engine.BuiltInDevices().Single(d => d.Kind == BuiltInSensorRules.FanSpeed).SeriesId);
        Assert.Equal("fan:np50:1-port1", seriesBefore);
        Assert.Equal(storedBefore, rig.Store.Load().Diagnostics.SensorAlerts.Select(Snapshot).ToList());
        Assert.Equal(ignoredBefore, rig.Store.Load().Diagnostics.IgnoredComponents);

        Rename(rig, HubFanId, null);
        Rename(rig, "np50:1", null);
        rig.Engine.Tick();

        Assert.Equal(storedBefore, rig.Store.Load().Diagnostics.SensorAlerts.Select(Snapshot).ToList());
    }

    private static string Snapshot(SensorAlertRule r) =>
        $"{r.Id}|{r.Enabled}|{r.High}|{r.Low}|{r.Sensor.Category}|{r.Sensor.Id}|{r.Sensor.Name}|{r.Sensor.ParentName}";

    [Fact]
    public void An_active_fan_episode_survives_a_rename_without_a_reset_or_a_second_notice()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(new FanChannel { Id = "fan1", Name = "Rear", Rpm = 500 }));
        EditStored(rig, FanRule("fan1"), x => x.Low = 800);
        rig.Seconds(10);
        var before = StateOf(rig, FanRule("fan1"));
        Assert.Equal(SensorAlertStates.Active, before.State);
        var notices = rig.Notices.Count;

        Rename(rig, "fan1", "Back fan");
        rig.Seconds(3);

        var after = StateOf(rig, FanRule("fan1"));
        Assert.Equal(SensorAlertStates.Active, after.State);
        Assert.Equal(before.Since, after.Since);
        Assert.Equal(notices, rig.Notices.Count);
        Assert.DoesNotContain(rig.Engine.History(), h => h.EndedUtc is not null);
    }

    [Fact]
    public void A_fan_first_seen_while_renamed_is_seeded_with_the_hardware_name()
    {
        var rig = new Rig();
        rig.Store.Update(s => BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: true));
        Rename(rig, "late", "Custom label");
        rig.Fans.Channels.Add(new FanChannel { Id = "late", Name = "Late Hub", Rpm = 700 });

        rig.Engine.Rules();

        var stored = rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == FanRule("late"));
        Assert.Equal("Late Hub", stored.Sensor.Name);
    }

    [Fact]
    public void The_heal_by_name_pass_ignores_a_cooling_name_equal_to_another_fans_hardware_name()
    {
        var other = BoardTach();
        other.Id = "/lpc/nct/fan/2";
        other.Name = "Fan #2";
        var list = new[] { BoardTach(), other };
        var reference = new SensorRef { Category = "motherboard", Id = "gone", Name = "Fan #2", Type = "Fan", ParentName = "Board" };

        var sample = SensorAlertEngine.Resolve(list, reference, out var healed, new Dictionary<string, string> { [BoardTachId] = "Fan #2" });

        Assert.True(sample.Valid);
        Assert.Equal("/lpc/nct/fan/2", healed);
    }

    [Fact]
    public void The_heal_by_name_pass_does_not_match_a_cooling_name_two_fans_share()
    {
        var other = BoardTach();
        other.Id = "/lpc/nct/fan/2";
        other.Name = "Fan #2";
        var renames = new Dictionary<string, string> { [BoardTachId] = "Shared", ["/lpc/nct/fan/2"] = "Shared" };
        var reference = new SensorRef { Category = "motherboard", Id = "gone", Name = "Shared", Type = "Fan", ParentName = "Board" };

        var sample = SensorAlertEngine.Resolve(new[] { BoardTach(), other }, reference, out var healed, renames);

        Assert.False(sample.Valid);
        Assert.Null(healed);
    }

    [Fact]
    public void A_rule_stored_under_a_cooling_name_resolves_by_id_and_heals_to_the_hardware_name()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        rig.AddRule(BoardFanRule("CPU fan"));

        rig.Seconds(2);

        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);
        Assert.Equal("Fan #1", rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "custom-fan").Sensor.Name);

        Rename(rig, BoardFanId, null);
        rig.Seconds(2);

        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);
    }

    [Fact]
    public void The_name_heal_never_touches_a_built_in_rule()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        var before = Snapshot(rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == FanRule(BoardFanId)));

        rig.Seconds(3);

        Assert.Equal(before, Snapshot(rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == FanRule(BoardFanId))));
    }

    [Fact]
    public void A_custom_rule_created_before_a_rename_still_resolves_after_it()
    {
        var rig = BoardFanRig();
        rig.AddRule(BoardFanRule("Fan #1"));
        rig.Seconds(1);
        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);

        Rename(rig, BoardFanId, "CPU fan");
        rig.Seconds(1);

        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);
        Assert.Equal("Fan #1", rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "custom-fan").Sensor.Name);
    }

    [Fact]
    public void A_custom_rule_created_from_the_renamed_picker_resolves()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        rig.AddRule(BoardFanRule("CPU fan"));

        rig.Seconds(1);

        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);
    }

    [Fact]
    public void Resolve_without_the_rename_map_cannot_match_a_renamed_key()
    {
        var reference = BoardFanRule("CPU fan").Sensor;
        var list = new[] { BoardTach() };

        var without = SensorAlertEngine.Resolve(list, reference, out _);
        var with = SensorAlertEngine.Resolve(list, reference, out _, new Dictionary<string, string> { [BoardTachId] = "CPU fan" });

        Assert.False(without.Valid);
        Assert.True(with.Valid);
    }

    [Fact]
    public void A_rule_created_from_the_renamed_picker_is_stored_raw_and_survives_clearing_the_rename()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        var rule = BoardFanRule("CPU fan");

        rig.Engine.RestoreRawSensorName(rule.Sensor);
        rig.AddRule(rule);
        rig.Seconds(1);

        Assert.Equal("Fan #1", rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "custom-fan").Sensor.Name);
        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);

        Rename(rig, BoardFanId, null);
        rig.Seconds(1);

        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);
    }

    [Fact]
    public void Restoring_the_raw_name_leaves_other_sensors_and_hardware_names_alone()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        var other = new SensorRef { Category = "motherboard", Id = "other", Name = "CPU fan", Type = "Fan" };
        var raw = BoardFanRule("Fan #1").Sensor;

        rig.Engine.RestoreRawSensorName(other);
        rig.Engine.RestoreRawSensorName(raw);

        Assert.Equal("CPU fan", other.Name);
        Assert.Equal("Fan #1", raw.Name);
    }

    [Fact]
    public void A_tick_reads_the_fan_channels_once_with_or_without_renames()
    {
        var rig = BoardFanRig();
        rig.AddRule(BoardFanRule("Fan #1"));
        rig.Seconds(1);
        var before = rig.Fans.Reads;
        rig.Seconds(1);
        var plain = rig.Fans.Reads - before;

        Rename(rig, BoardFanId, "CPU fan");
        before = rig.Fans.Reads;
        rig.Seconds(1);

        Assert.Equal(plain, rig.Fans.Reads - before);
        Assert.Equal(1, plain);
    }

    [Fact]
    public void The_name_heal_skips_a_rule_retargeted_to_another_sensor_meanwhile()
    {
        var rig = BoardFanRig();
        var rule = BoardFanRule("CPU fan");
        rule.Sensor.Id = "/lpc/nct/fan/2";
        rig.AddRule(rule);

        rig.Engine.HealSensorNames(new List<(string, string, string, string, string)> { ("custom-fan", "CPU fan", "Fan", BoardTachId, "Fan #1") });

        Assert.Equal("CPU fan", rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "custom-fan").Sensor.Name);
    }

    [Fact]
    public void The_name_heal_of_a_legacy_rule_does_not_restart_its_episode()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        var rule = BoardFanRule("CPU fan");
        rule.High = 1000;
        rig.AddRule(rule);
        rig.Seconds(1);
        Assert.Equal(SensorAlertStates.Pending, StateOf(rig, "custom-fan").State);

        // The raise delay counts from the first breach, so a heal that restarted the rule would still be pending here.
        rig.Seconds(5);

        var state = StateOf(rig, "custom-fan");
        Assert.Equal("Fan #1", rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "custom-fan").Sensor.Name);
        Assert.Equal(SensorAlertStates.Active, state.State);
        Assert.Single(rig.Notices);
    }

    [Fact]
    public void A_stalled_renamed_fan_raises_a_notice_health_entry_and_history_with_the_cooling_name_and_clearing_restores_it()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(new FanChannel { Id = "fan1", Name = "Rear", Rpm = 500 }));
        EditStored(rig, FanRule("fan1"), x => x.Low = 800);
        Rename(rig, "fan1", "Back fan");

        rig.Seconds(10);

        Assert.StartsWith("Back fan below", Assert.Single(rig.Notices).Notice.Text);
        var component = Assert.Single(rig.Engine.HealthComponents(), x => x.Id == "alert:" + FanRule("fan1"));
        Assert.Equal("Back fan", component.Name);
        Assert.StartsWith("Back fan below", component.Reasons[0].Summary);
        Assert.Equal("Back fan", component.Reasons[0].Params!["sensorName"]);
        Assert.Equal("Back fan", rig.Engine.History().Single().SensorName);
        Assert.Equal("Rear", rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == FanRule("fan1")).Sensor.Name);
        Assert.Equal(SensorAlertStates.Active, StateOf(rig, FanRule("fan1")).State);

        Rename(rig, "fan1", null);

        Assert.Equal("Rear", rig.Engine.HealthComponents().Single(x => x.Id == "alert:" + FanRule("fan1")).Name);
        Assert.Equal("Rear", rig.Engine.History().Single().SensorName);
    }

    [Fact]
    public void A_custom_rule_on_a_renamed_tach_raises_with_the_cooling_name_and_a_cleared_rename_goes_back_to_raw()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        var rule = BoardFanRule("Fan #1");
        rule.Low = 1500;
        rule.RaiseAfterSeconds = 0;
        rig.AddRule(rule);
        rig.Seconds(2);

        Assert.StartsWith("CPU fan below", rig.Notices.Single().Notice.Text);
        Assert.Equal("CPU fan", rig.Engine.History().Single().SensorName);

        Rename(rig, BoardFanId, null);
        rig.Seconds(1);

        Assert.StartsWith("Fan #1", rig.Engine.HealthComponents().Single(x => x.Id == "alert:custom-fan").Reasons[0].Summary);
        Assert.Equal("Fan #1", rig.Engine.History().Single().SensorName);
    }

    [Fact]
    public void The_engine_rename_map_keeps_the_first_channel_when_two_share_a_tach()
    {
        var rig = BoardFanRig();
        rig.Fans.Channels.Add(new FanChannel { Id = "/lpc/ctl/2", Name = "Other", RpmSensorId = BoardTachId });
        Rename(rig, BoardFanId, "First");
        Rename(rig, "/lpc/ctl/2", "Second");
        rig.AddRule(BoardFanRule("First"));

        rig.Seconds(2);

        Assert.Equal(1200, StateOf(rig, "custom-fan").Value);
        Assert.Equal("Fan #1", rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == "custom-fan").Sensor.Name);
    }

    private const string SecondFanId = "/lpc/ctl/2";
    private const string SecondTachId = "/lpc/nct/fan/2";

    private static Rig TwoBoardFanRig(out SensorAlertRule rule)
    {
        var rig = BuiltInRig(r =>
        {
            r.Fans.Channels.Add(new FanChannel { Id = BoardFanId, Name = "Fan #1", RpmSensorId = BoardTachId, Rpm = 1200 });
            r.Fans.Channels.Add(new FanChannel { Id = SecondFanId, Name = "Fan #2", RpmSensorId = SecondTachId, Rpm = 2000 });
            var second = BoardTach(2000);
            second.Id = SecondTachId;
            second.Name = "Fan #2";
            r.Sensors.Mobo = new[] { BoardTach(), second };
        });
        rule = BoardFanRule("Fan #1");
        rule.Low = 1500;
        rule.RaiseAfterSeconds = 0;
        rig.AddRule(rule);
        rig.Seconds(2);
        Assert.Single(rig.Engine.History());
        return rig;
    }

    private static void Repoint(Rig rig) => rig.Store.Update(s =>
    {
        var stored = s.Diagnostics.SensorAlerts.Single(r => r.Id == "custom-fan");
        stored.Sensor.Id = SecondTachId;
        stored.Sensor.Name = "Fan #2";
    });

    [Fact]
    public void A_rule_re_pointed_from_an_unrenamed_fan_to_a_renamed_one_keeps_its_old_rows_as_recorded()
    {
        var rig = TwoBoardFanRig(out _);
        Rename(rig, SecondFanId, "B fan");

        Repoint(rig);
        rig.Seconds(2);

        Assert.Equal("Fan #1", Assert.Single(rig.Engine.History()).SensorName);
    }

    [Fact]
    public void A_rule_re_pointed_from_a_renamed_fan_to_another_renamed_one_does_not_take_the_new_name_on_old_rows()
    {
        var rig = TwoBoardFanRig(out _);
        Rename(rig, BoardFanId, "A fan");
        Assert.Equal("A fan", rig.Engine.History().Single().SensorName);
        Rename(rig, SecondFanId, "B fan");

        Repoint(rig);
        rig.Seconds(2);

        Assert.Equal("Fan #1", Assert.Single(rig.Engine.History()).SensorName);
    }
}

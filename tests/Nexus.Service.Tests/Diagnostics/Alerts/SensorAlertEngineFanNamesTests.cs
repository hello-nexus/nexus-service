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
        Assert.Empty(rig.Engine.History().Where(h => h.EndedUtc is not null));
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
}

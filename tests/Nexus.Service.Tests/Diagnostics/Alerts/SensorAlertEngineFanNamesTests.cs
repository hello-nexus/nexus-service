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
    public void A_fan_entry_shows_the_renamed_channel_and_group_names()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(HubFan()));
        Assert.Equal("Port 1", FanEntry(rig, HubFanId).Sensor.Name);
        Assert.Equal("NP50 Hub", FanEntry(rig, HubFanId).Source!.HardwareName);

        Rename(rig, HubFanId, "Front intake");
        Rename(rig, "np50:1", "Radiator hub");
        rig.Engine.Tick();

        var rule = FanEntry(rig, HubFanId);
        Assert.Equal("Front intake", rule.Sensor.Name);
        Assert.Equal("Radiator hub", rule.Source!.HardwareName);
        Assert.Contains(rig.Engine.BuiltInDevices(), d => d.Name == "Front intake");
    }

    [Fact]
    public void A_renamed_fan_with_a_tach_sensor_shows_the_name_on_the_source_sensor()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "CPU fan");
        rig.Engine.Tick();

        var source = FanEntry(rig, BoardFanId).Source!;

        Assert.Equal("sensor", source.Kind);
        Assert.Equal("CPU fan", source.Sensor!.Name);
        Assert.Equal(BoardTachId, source.Sensor.Id);
    }

    [Fact]
    public void A_rename_changes_no_rule_id_history_key_or_stored_rule()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(HubFan()));
        var seriesBefore = rig.Engine.BuiltInDevices().Single(d => d.Kind == BuiltInSensorRules.FanSpeed).SeriesId;
        var storedBefore = rig.Store.Load().Diagnostics.SensorAlerts.Select(Snapshot).ToList();
        var ignoredBefore = rig.Store.Load().Diagnostics.IgnoredComponents.ToList();

        Rename(rig, HubFanId, "Front intake");
        Rename(rig, "np50:1", "Radiator hub");
        rig.Engine.Tick();
        var renamed = rig.Store.Load();

        Assert.Equal(seriesBefore, rig.Engine.BuiltInDevices().Single(d => d.Kind == BuiltInSensorRules.FanSpeed).SeriesId);
        Assert.Equal("fan:np50:1-port1", seriesBefore);
        Assert.Equal(FanRule(HubFanId), FanEntry(rig, HubFanId).Id);
        Assert.Equal(storedBefore, renamed.Diagnostics.SensorAlerts.Select(Snapshot).ToList());
        Assert.Equal(ignoredBefore, renamed.Diagnostics.IgnoredComponents);

        Rename(rig, HubFanId, null);
        Rename(rig, "np50:1", null);
        rig.Engine.Tick();

        Assert.Equal(storedBefore, rig.Store.Load().Diagnostics.SensorAlerts.Select(Snapshot).ToList());
        Assert.Equal(seriesBefore, rig.Engine.BuiltInDevices().Single(d => d.Kind == BuiltInSensorRules.FanSpeed).SeriesId);
    }

    private static string Snapshot(SensorAlertRule r) =>
        $"{r.Id}|{r.Enabled}|{r.High}|{r.Low}|{r.Sensor.Category}|{r.Sensor.Id}|{r.Sensor.Name}|{r.Sensor.ParentName}";

    [Fact]
    public void Clearing_a_rename_restores_the_hardware_names()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(HubFan()));
        Rename(rig, HubFanId, "Front intake");
        Rename(rig, "np50:1", "Radiator hub");
        rig.Engine.Tick();
        Assert.Equal("Front intake", FanEntry(rig, HubFanId).Sensor.Name);

        Rename(rig, HubFanId, null);
        Rename(rig, "np50:1", null);
        rig.Engine.Tick();

        var rule = FanEntry(rig, HubFanId);
        Assert.Equal("Port 1", rule.Sensor.Name);
        Assert.Equal("NP50 Hub", rule.Source!.HardwareName);
    }

    [Fact]
    public void A_blank_rename_is_ignored()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(HubFan()));
        Rename(rig, HubFanId, "  ");
        rig.Engine.Tick();

        Assert.Equal("Port 1", FanEntry(rig, HubFanId).Sensor.Name);
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

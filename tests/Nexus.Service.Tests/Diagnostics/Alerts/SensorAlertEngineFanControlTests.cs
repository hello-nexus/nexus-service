using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Alerts;

public partial class SensorAlertEngineTests
{
    private static bool FanEntryEnabled(Rig rig, string channelId) => FanEntry(rig, channelId).Enabled;

    [Fact]
    public void A_fan_entry_reads_off_while_the_channel_is_not_under_nexus_control_and_on_again_after()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(new FanChannel { Id = "fan1", Name = "Rear", Rpm = 500 }));
        Assert.True(FanEntryEnabled(rig, "fan1"));

        FanControlledState.SetControlled("fan1", false, rig.Store);
        Assert.False(FanEntryEnabled(rig, "fan1"));

        FanControlledState.SetControlled("fan1", true, rig.Store);
        Assert.True(FanEntryEnabled(rig, "fan1"));
    }

    [Fact]
    public void A_channel_first_seen_while_uncontrolled_is_seeded_off()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            BuiltInSensorRules.Ensure(s.Diagnostics, newInstall: true);
            FanControlledState.SetControlledIn(s, "late", false);
        });
        rig.Fans.Channels.Add(new FanChannel { Id = "late", Name = "Late", Rpm = 0 });

        rig.Engine.Rules();

        Assert.False(rig.Store.Load().Diagnostics.SensorAlerts.Single(r => r.Id == FanRule("late")).Enabled);
    }

    [Fact]
    public void A_legacy_ignore_entry_still_suppresses_a_controlled_fan_and_does_not_touch_control()
    {
        var rig = BuiltInRig(
            r => r.Fans.Channels.Add(new FanChannel { Id = "fan1", Name = "Rear", Rpm = 500 }),
            tweak: s => s.Diagnostics.IgnoredComponents.Add("cooling:fan1"));

        Assert.False(FanEntryEnabled(rig, "fan1"));
        Assert.True(FanControlledState.IsControlled("fan1", rig.Store.Load()));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    public void Fan_entry_enabled_needs_control_and_no_ignore_entry_and_a_pump_needs_only_the_latter(bool controlled, bool notIgnored, bool expected, bool linked)
    {
        var source = new BuiltInSource { Kind = "channel", ControlChannel = linked ? "x" : null };
        var device = new BuiltInDevice(BuiltInSensorRules.FanSpeed, "fan:x", "X", false, "cooling:x", source);
        var diagnostics = new Nexus.Service.Persistence.DiagnosticsSettings();
        if (!notIgnored)
        {
            diagnostics.IgnoredComponents.Add("cooling:x");
        }

        Assert.Equal(expected, BuiltInSensorRules.FanEnabled(device, diagnostics, controlled));
        Assert.Equal("x", BuiltInSensorRules.FanChannelId(device));
    }

    [Fact]
    public void A_pump_entry_stays_on_while_not_under_nexus_control_and_has_no_control_channel()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(new FanChannel { Id = "pump1", Name = "Pump", Kind = FanKinds.Pump, Rpm = 2000 }));
        FanControlledState.SetControlled("pump1", false, rig.Store);

        var entry = FanEntry(rig, "pump1");

        Assert.True(entry.Enabled);
        Assert.Null(entry.Source!.ControlChannel);
    }

    [Fact]
    public void A_fan_entry_names_its_control_channel()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(new FanChannel { Id = "fan1", Name = "Rear", Rpm = 500 }));

        Assert.Equal("fan1", FanEntry(rig, "fan1").Source!.ControlChannel);
    }

    [Fact]
    public void A_board_header_entry_reads_under_the_board_name_not_the_chip()
    {
        var rig = BoardFanRig();

        var source = FanEntry(rig, BoardFanId).Source!;

        Assert.Equal("TestMobo", source.HardwareName);
        Assert.Equal("Fan #1", source.Sensor!.Name);
        // The tach reference itself still names the chip it sits on, so rule matching is untouched.
        Assert.Equal("Board", source.Sensor.ParentName);
    }

    [Fact]
    public void A_renamed_board_header_keeps_its_custom_fan_name_under_the_board_name()
    {
        var rig = BoardFanRig();
        Rename(rig, BoardFanId, "Top exhaust");

        var rule = FanEntry(rig, BoardFanId);

        Assert.Equal("Fan #1", rule.Sensor.Name);
        Assert.Equal("TestMobo", rule.Source!.HardwareName);
    }

    [Fact]
    public void A_tachless_board_header_reads_under_the_board_name_too()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(new FanChannel { Id = "board:hdr2", Name = "Fan #2", Rpm = 900 }));

        Assert.Equal("TestMobo", FanEntry(rig, "board:hdr2").Source!.HardwareName);
    }

    [Fact]
    public void A_hub_channel_keeps_its_device_name_and_a_gpu_fan_reads_under_the_gpu_name()
    {
        var gpuTach = new HardwareSensor
        {
            Id = "/gpu/fan/1", Name = "GPU Fan 1", Type = "Fan", Value = 1500, Units = "RPM",
            Parent = new SensorParent { Id = "gpu0", Name = "RTX 5080" },
        };
        var rig = BuiltInRig(r =>
        {
            r.Fans.Channels.Add(HubFan());
            r.Fans.Channels.Add(new FanChannel { Id = "gpu:fan1", Name = "GPU Fan 1", IsGpu = true, RpmSensorId = "/gpu/fan/1", Rpm = 1500 });
            r.Sensors.Gpus.Add(new GpuReadout { Id = "gpu0", Name = "RTX 5080", Sensors = new List<HardwareSensor> { gpuTach } });
        });

        Assert.Equal("NP50 Hub", FanEntry(rig, HubFanId).Source!.HardwareName);
        Assert.Equal("RTX 5080", FanEntry(rig, "gpu:fan1").Source!.HardwareName);
    }
}

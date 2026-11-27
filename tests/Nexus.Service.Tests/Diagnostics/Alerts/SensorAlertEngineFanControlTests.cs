using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models.Cooling;
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
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Fan_entry_enabled_needs_control_and_no_ignore_entry(bool controlled, bool notIgnored, bool expected)
    {
        var device = new BuiltInDevice(BuiltInSensorRules.FanSpeed, "fan:x", "X", false, "cooling:x");
        var diagnostics = new Nexus.Service.Persistence.DiagnosticsSettings();
        if (!notIgnored)
        {
            diagnostics.IgnoredComponents.Add("cooling:x");
        }

        Assert.Equal(expected, BuiltInSensorRules.FanEnabled(device, diagnostics, controlled));
        Assert.Equal("x", BuiltInSensorRules.FanChannelId(device));
    }

    [Fact]
    public void Apply_off_sets_uncontrolled_and_on_clears_it()
    {
        var rig = BuiltInRig(r => r.Fans.Channels.Add(new FanChannel { Id = "fan1", Name = "Rear", Rpm = 500 }));

        FanControlledState.Apply("fan1", false, rig.Fans, rig.Store);
        Assert.Contains("fan1", rig.Store.Load().Cooling.UncontrolledFanChannels);

        FanControlledState.Apply("fan1", true, rig.Fans, rig.Store);
        Assert.DoesNotContain("fan1", rig.Store.Load().Cooling.UncontrolledFanChannels);
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
}

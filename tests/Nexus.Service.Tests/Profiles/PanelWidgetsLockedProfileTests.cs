using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Profiles;

public class PanelWidgetsLockedProfileTests
{
    private static NexusSettings WithMonitor(bool? locked)
    {
        var s = new NexusSettings();
        s.PanelDevices["m"] = new PanelDeviceRecord
        {
            Id = "m",
            DisplayId = "D1",
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor },
            WidgetsLocked = locked,
        };
        return s;
    }

    [Fact]
    public void Profile_file_carries_the_lock_and_applies_it_to_the_live_record()
    {
        var file = new NexusSettings();
        PanelProfileData.WriteTo(file, WithMonitor(true));
        Assert.True(file.PanelDevices["m"].WidgetsLocked);

        var live = WithMonitor(null);
        PanelProfileData.ApplyToLive(live, file);

        Assert.True(live.PanelDevices["m"].WidgetsLocked);
    }

    [Fact]
    public void Applying_a_profile_saved_unlocked_clears_the_live_lock()
    {
        var file = new NexusSettings();
        PanelProfileData.WriteTo(file, WithMonitor(null));
        var live = WithMonitor(true);

        PanelProfileData.ApplyToLive(live, file);

        Assert.Null(live.PanelDevices["m"].WidgetsLocked);
    }

    [Fact]
    public void A_locked_record_is_not_a_default_look()
    {
        Assert.True(PanelProfileData.IsDefaultLook(WithMonitor(null).PanelDevices["m"]));
        Assert.False(PanelProfileData.IsDefaultLook(WithMonitor(true).PanelDevices["m"]));
    }

    [Fact]
    public void Profile_wide_reset_clears_the_lock()
    {
        var s = WithMonitor(true);

        PanelProfileData.Reset(s);

        Assert.Null(s.PanelDevices["m"].WidgetsLocked);
    }
}

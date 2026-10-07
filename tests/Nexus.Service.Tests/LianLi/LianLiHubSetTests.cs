using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLi;

public class LianLiHubSetTests
{
    [Theory]
    [InlineData("lianli", 0)]
    [InlineData("lianli:port0", 0)]
    [InlineData("lianli:port0:inner", 0)]
    [InlineData("lianli2", 1)]
    [InlineData("lianli4:port3", 3)]
    [InlineData("lianli5", -1)]
    [InlineData("lianli1", -1)]
    [InlineData("lianli02", -1)]
    [InlineData("lianli-wireless", -1)]
    [InlineData("lianli-tl:fan0", -1)]
    [InlineData("smarthub:abc", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void SlotOf_reads_the_hub_from_any_id(string? id, int slot)
    {
        Assert.Equal(slot, LianLiHubSet.SlotOf(id));
    }

    [Fact]
    public void Hubs_carry_their_slot_ids()
    {
        var set = new LianLiHubSet();
        Assert.Equal(new[] { "lianli", "lianli2", "lianli3", "lianli4" }, System.Linq.Enumerable.Select(set.Hubs, h => h.DeviceId));
        Assert.Same(set.Hubs[2], set.Owner("lianli3:port1"));
        Assert.Null(set.Owner("lianli-wireless:AA"));
    }

    [Fact]
    public void The_first_hub_keeps_the_original_settings_fields()
    {
        var devices = new DevicesSettings();
        devices.LianLi.SetFans(0, 2);
        devices.LianLiLighting.Mode = "tide";

        Assert.Same(devices.LianLi, LianLiHubSet.FansOf(devices, "lianli"));
        Assert.Same(devices.LianLiLighting, LianLiHubSet.LightingOf(devices, "lianli"));
        Assert.Empty(devices.LianLiExtraHubs);
    }

    [Fact]
    public void An_extra_hub_reads_defaults_until_edited_then_keeps_its_own()
    {
        var devices = new DevicesSettings();
        Assert.Equal(4, LianLiHubSet.FansOf(devices, "lianli2").GetFans(0));
        Assert.Empty(devices.LianLiExtraHubs);

        var before = devices.LianLiExtraHubs;
        LianLiHubSet.Editable(devices, "lianli2").Fans.SetFans(0, 1);

        Assert.NotSame(before, devices.LianLiExtraHubs);
        Assert.Equal(1, LianLiHubSet.FansOf(devices, "lianli2").GetFans(0));
        Assert.Equal(4, LianLiHubSet.FansOf(devices, "lianli").GetFans(0));
    }

    [Fact]
    public void A_resume_reaches_every_hub()
    {
        var set = new LianLiHubSet();
        set.OnSystemResumed();
        Assert.All(set.Hubs, h => Assert.Equal(1, h.ResumeEpoch));
    }
}

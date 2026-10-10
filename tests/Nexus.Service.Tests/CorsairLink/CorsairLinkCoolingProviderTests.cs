using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Tests.CorsairLink;

// Pump/AIO duty floors mirror OpenLinkHub (lsh.go). The hub here has no HID
// device, so SetDuties is a no-op; the floored value still lands in the
// provider's pending-duty state, which is what these assert.
public class CorsairLinkCoolingProviderTests
{
    private static CorsairLinkCoolingProvider ProviderWith(CorsairLinkClass cls, int type)
    {
        var hub = new CorsairLinkHub();
        hub.State.IsConnected = true;
        hub.State.Devices = new[]
        {
            new CorsairLinkDevice { Channel = 1, Type = type, Class = cls, HasSpeed = true, Name = "dev" },
        };
        var hubs = new CorsairLinkHubs();
        hubs.Add(hub);
        return new CorsairLinkCoolingProvider(hubs);
    }

    private static int CurveDutyOf(CorsairLinkCoolingProvider p)
    {
        return p.GetFanChannels().Single(c => c.Id == "corsair:ch1").DutyPercent;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(100)]
    public void Fan_duty_is_never_floored(int duty)
    {
        Assert.Equal(duty, ProviderWith(CorsairLinkClass.Fan, 1).SetFanSpeed("corsair:ch1", duty));
    }

    [Fact]
    public void Manual_write_floors_any_pump_at_50()
    {
        Assert.Equal(50, ProviderWith(CorsairLinkClass.Aio, 7).SetFanSpeed("corsair:ch1", 30));
        Assert.Equal(50, ProviderWith(CorsairLinkClass.Aio, TitanType).SetFanSpeed("corsair:ch1", 20));
        Assert.Equal(50, ProviderWith(CorsairLinkClass.Pump, 12).SetFanSpeed("corsair:ch1", 10));
    }

    [Fact]
    public void Manual_write_leaves_a_pump_above_the_floor_unchanged()
    {
        Assert.Equal(80, ProviderWith(CorsairLinkClass.Aio, 7).SetFanSpeed("corsair:ch1", 80));
    }

    [Fact]
    public void Curve_write_floors_standard_aio_at_70()
    {
        var p = ProviderWith(CorsairLinkClass.Aio, 7);
        p.DriveFanSpeed("corsair:ch1", 30);
        Assert.Equal(70, CurveDutyOf(p));
    }

    [Fact]
    public void Curve_write_floors_titan_aio_at_31()
    {
        var p = ProviderWith(CorsairLinkClass.Aio, TitanType);
        p.DriveFanSpeed("corsair:ch1", 20);
        Assert.Equal(31, CurveDutyOf(p));
    }

    [Fact]
    public void Curve_write_floors_standalone_pump_at_30()
    {
        var p = ProviderWith(CorsairLinkClass.Pump, 12);
        p.DriveFanSpeed("corsair:ch1", 10);
        Assert.Equal(30, CurveDutyOf(p));
    }

    [Fact]
    public void Curve_write_leaves_a_fan_unchanged()
    {
        var p = ProviderWith(CorsairLinkClass.Fan, 1);
        p.DriveFanSpeed("corsair:ch1", 15);
        Assert.Equal(15, CurveDutyOf(p));
    }

    private const int TitanType = 17;
}

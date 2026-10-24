using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class SuperIoReadGuardTests
{
    private static float?[] A(params float?[] v) => v;

    [Fact]
    public void All_full_duty_and_zero_rpm_after_a_spin_trips()
    {
        Assert.True(SuperIoReadGuard.IsAllOnesRead(A(100f, 100f, 100f), A(0f, 0f, 0f), true));
    }

    [Fact]
    public void Duty_of_exactly_the_floor_trips_and_just_below_does_not()
    {
        Assert.True(SuperIoReadGuard.IsAllOnesRead(A(99.5f, 100f), A(0f), true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(99.4f, 100f), A(0f), true));
    }

    [Fact]
    public void Single_pump_stall_with_other_fans_at_varied_duty_does_not_trip()
    {
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(100f, 45f, 60f), A(0f, 900f, 1100f), true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(100f, 45f, 60f), A(0f, 0f, 0f), true));
    }

    [Fact]
    public void Unpopulated_headers_at_zero_rpm_with_normal_duty_do_not_trip()
    {
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(40f, 40f, 40f), A(0f, 0f, 0f), true));
    }

    [Fact]
    public void Nonzero_rpm_anywhere_does_not_trip()
    {
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(100f, 100f), A(0f, 700f), true));
    }

    [Fact]
    public void No_prior_spin_does_not_trip()
    {
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(100f, 100f), A(0f, 0f), false));
    }

    [Fact]
    public void Single_control_chip_does_not_trip()
    {
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(100f), A(0f), true));
    }

    [Fact]
    public void Missing_duty_reading_does_not_trip()
    {
        Assert.False(SuperIoReadGuard.IsAllOnesRead(A(100f, null), A(0f), true));
    }
}

using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class SuperIoReadGuardTests
{
    private static float?[] A(params float?[] v) => v;

    // The all-ones decode: every control full, every tach zero, every voltage at or above 0.008 * 255.
    private static SuperIoSnapshot AllOnes() => new(A(100f, 100f, 100f), A(0f, 0f, 0f), A(2.04f, 4.08f, 6.0f, 2.04f));

    private static SuperIoSnapshot Normal(float?[]? controls = null, float?[]? fans = null) =>
        new(controls ?? A(100f, 100f, 100f), fans ?? A(0f, 0f, 0f), A(1.1f, 4.9f, 3.3f, 12.1f));

    [Fact]
    public void All_ones_signature_after_a_spin_trips()
    {
        Assert.True(SuperIoReadGuard.IsAllOnesRead(AllOnes(), true));
    }

    [Fact]
    public void Sole_pump_death_with_every_header_at_full_duty_and_normal_voltages_does_not_trip()
    {
        Assert.False(SuperIoReadGuard.IsAllOnesRead(Normal(), true));
    }

    [Fact]
    public void One_voltage_below_the_all_ones_code_does_not_trip()
    {
        var s = new SuperIoSnapshot(A(100f, 100f), A(0f), A(2.04f, 1.2f, 6.0f));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(s, true));
    }

    [Fact]
    public void Undecoded_voltages_are_ignored_but_two_must_decode()
    {
        Assert.True(SuperIoReadGuard.IsAllOnesRead(new SuperIoSnapshot(A(100f, 100f), A(0f), A(null, 2.04f, 6.0f)), true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(new SuperIoSnapshot(A(100f, 100f), A(0f), A(null, 2.04f)), true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(new SuperIoSnapshot(A(100f, 100f), A(0f), A()), true));
    }

    [Fact]
    public void Duty_floor_edge()
    {
        var at = new SuperIoSnapshot(A(99.5f, 100f), A(0f), A(2.04f, 6.0f));
        var below = new SuperIoSnapshot(A(99.4f, 100f), A(0f), A(2.04f, 6.0f));
        Assert.True(SuperIoReadGuard.IsAllOnesRead(at, true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(below, true));
    }

    [Fact]
    public void Varied_duty_nonzero_rpm_no_prior_spin_and_single_control_do_not_trip()
    {
        var v = A(2.04f, 6.0f);
        Assert.False(SuperIoReadGuard.IsAllOnesRead(new SuperIoSnapshot(A(100f, 45f), A(0f, 0f), v), true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(new SuperIoSnapshot(A(100f, 100f), A(0f, 700f), v), true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(AllOnes(), false));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(new SuperIoSnapshot(A(100f), A(0f), v), true));
        Assert.False(SuperIoReadGuard.IsAllOnesRead(new SuperIoSnapshot(A(100f, null), A(0f), v), true));
    }

    [Fact]
    public void Evaluate_needs_an_earlier_spin_on_the_same_chip_and_clears_on_a_valid_sweep()
    {
        var guard = new SuperIoReadGuard();
        Assert.False(guard.Evaluate("/lpc/nct6701d/0", AllOnes()));
        Assert.False(guard.Evaluate("/lpc/nct6701d/0", Normal(A(40f, 40f), A(900f, 0f))));
        Assert.False(guard.Evaluate("/lpc/nct6799d/0", AllOnes()));
        Assert.True(guard.Evaluate("/lpc/nct6701d/0", AllOnes()));
        Assert.False(guard.Evaluate("/lpc/nct6701d/0", Normal(A(40f, 40f), A(900f, 0f))));
    }
}

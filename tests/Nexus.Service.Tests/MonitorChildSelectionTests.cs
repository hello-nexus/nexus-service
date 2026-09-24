using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// The adapter-child rule behind every Windows monitor identity read: an
/// idle sibling devnode at child 0 must not shadow the ACTIVE monitor.
/// </summary>
public sealed class MonitorChildSelectionTests
{
    private const uint Active = MonitorChildSelection.DisplayDeviceActive;
    private const uint Attached = 0x2; // DISPLAY_DEVICE_ATTACHED, not sufficient on its own

    [Fact]
    public void IdleSiblingAtChildZero_YieldsTheActiveChild()
    {
        Assert.Equal(1, MonitorChildSelection.Pick(new uint[] { Attached, Active | Attached }));
    }

    [Fact]
    public void ActiveAtChildZero_KeepsChildZero()
    {
        Assert.Equal(0, MonitorChildSelection.Pick(new uint[] { Active | Attached, Attached }));
    }

    [Fact]
    public void NoActiveFlagAnywhere_FallsBackToChildZero()
    {
        Assert.Equal(0, MonitorChildSelection.Pick(new uint[] { 0, Attached }));
    }

    [Fact]
    public void SeveralActive_TakesTheFirst()
    {
        Assert.Equal(1, MonitorChildSelection.Pick(new uint[] { 0, Active, Active }));
    }

    [Fact]
    public void NoChildren_ReportsNone()
    {
        Assert.Equal(-1, MonitorChildSelection.Pick(new uint[0]));
    }
}

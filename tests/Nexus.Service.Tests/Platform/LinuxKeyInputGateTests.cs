using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Tests.Platform;

/// <summary>The privacy gate for the Linux key source: input flows only for an unlocked user session on seat0, and fails closed.</summary>
public class LinuxKeyInputGateTests
{
    private static LinuxKeyInputGate.SeatState Seat(bool active = true, string? cls = "user", bool? locked = false)
        => new(active, cls, locked);

    [Fact]
    public void Decide_UnlockedUserSessionIsAllowed()
        => Assert.True(LinuxKeyInputGate.Decide(Seat(), lockerActive: null).Allowed);

    [Fact]
    public void Decide_LockerReportingInactiveIsAllowed()
        => Assert.True(LinuxKeyInputGate.Decide(Seat(), lockerActive: false).Allowed);

    [Fact]
    public void Decide_LogindUnreachableIsBlocked()
        => Assert.False(LinuxKeyInputGate.Decide(null, lockerActive: false).Allowed);

    [Fact]
    public void Decide_NoActiveSessionIsBlocked()
        => Assert.False(LinuxKeyInputGate.Decide(Seat(active: false, cls: null, locked: null), lockerActive: null).Allowed);

    [Theory]
    [InlineData("greeter")]
    [InlineData("lock-screen")]
    [InlineData("background")]
    [InlineData("manager")]
    [InlineData("")]
    [InlineData(null)]
    public void Decide_NonUserSessionClassIsBlocked(string? cls)
        => Assert.False(LinuxKeyInputGate.Decide(Seat(cls: cls), lockerActive: null).Allowed);

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Decide_LockedOrUnknownLockedHintIsBlocked(bool? locked)
        => Assert.False(LinuxKeyInputGate.Decide(Seat(locked: locked), lockerActive: null).Allowed);

    [Fact]
    public void Decide_ScreenLockerActiveIsBlockedEvenWhenLogindSaysUnlocked()
        => Assert.False(LinuxKeyInputGate.Decide(Seat(locked: false), lockerActive: true).Allowed);

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task Loop_IsClosedUntilStartedAndFollowsTheSession()
    {
        LinuxKeyInputGate.SeatState? seat = Seat();
        bool? locker = null;
        using var gate = new LinuxKeyInputGate(() => Task.FromResult(seat), () => Task.FromResult(locker), intervalMs: 10);
        Assert.False(gate.Allowed);

        gate.SetActive(true);
        Assert.True(await WaitFor(() => gate.Allowed));

        seat = Seat(locked: true);
        Assert.True(await WaitFor(() => !gate.Allowed));

        seat = Seat();
        Assert.True(await WaitFor(() => gate.Allowed));

        locker = true;
        Assert.True(await WaitFor(() => !gate.Allowed));

        locker = false;
        seat = null;
        Assert.True(await WaitFor(() => !gate.Allowed));

        seat = Seat();
        Assert.True(await WaitFor(() => gate.Allowed));
        gate.SetActive(false);
        Assert.False(gate.Allowed);
    }

    [Fact]
    public async Task Loop_AReadFailureBlocks()
    {
        var fail = false;
        using var gate = new LinuxKeyInputGate(
            () => fail ? throw new InvalidOperationException("bus") : Task.FromResult<LinuxKeyInputGate.SeatState?>(Seat()),
            () => Task.FromResult<bool?>(null),
            intervalMs: 10);
        gate.SetActive(true);
        Assert.True(await WaitFor(() => gate.Allowed));
        fail = true;
        Assert.True(await WaitFor(() => !gate.Allowed));
    }
}

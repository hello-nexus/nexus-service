using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Tests.Platform;

/// <summary>The privacy gate for the Linux key source: input flows only for an unlocked user session on seat0, and fails closed.</summary>
public class LinuxKeyInputGateTests
{
    private const uint SessionUser = 1000;

    private static LinuxKeyInputGate.SeatState Seat(bool active = true, string? cls = "user", bool? locked = false, uint? uid = SessionUser)
        => new(active, cls, locked, uid);

    [Fact]
    public void Decide_UnlockedUserSessionIsAllowed()
        => Assert.True(LinuxKeyInputGate.Decide(Seat(), lockerActive: null, SessionUser).Allowed);

    [Fact]
    public void Decide_LockerReportingInactiveIsAllowed()
        => Assert.True(LinuxKeyInputGate.Decide(Seat(), lockerActive: false, SessionUser).Allowed);

    [Fact]
    public void Decide_LogindUnreachableIsBlocked()
        => Assert.False(LinuxKeyInputGate.Decide(null, lockerActive: false, SessionUser).Allowed);

    [Fact]
    public void Decide_NoActiveSessionIsBlocked()
        => Assert.False(LinuxKeyInputGate.Decide(Seat(active: false, cls: null, locked: null, uid: null), lockerActive: null, SessionUser).Allowed);

    [Theory]
    [InlineData("greeter")]
    [InlineData("lock-screen")]
    [InlineData("background")]
    [InlineData("manager")]
    [InlineData("")]
    [InlineData(null)]
    public void Decide_NonUserSessionClassIsBlocked(string? cls)
        => Assert.False(LinuxKeyInputGate.Decide(Seat(cls: cls), lockerActive: null, SessionUser).Allowed);

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Decide_LockedOrUnknownLockedHintIsBlocked(bool? locked)
        => Assert.False(LinuxKeyInputGate.Decide(Seat(locked: locked), lockerActive: null, SessionUser).Allowed);

    [Fact]
    public void Decide_ScreenLockerActiveIsBlockedEvenWhenLogindSaysUnlocked()
        => Assert.False(LinuxKeyInputGate.Decide(Seat(locked: false), lockerActive: true, SessionUser).Allowed);

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
        using var gate = new LinuxKeyInputGate(() => Task.FromResult(seat), () => Task.FromResult(locker), () => SessionUser, intervalMs: 10);
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
            () => SessionUser,
            intervalMs: 10);
        gate.SetActive(true);
        Assert.True(await WaitFor(() => gate.Allowed));
        fail = true;
        Assert.True(await WaitFor(() => !gate.Allowed));
    }

    [Fact]
    public void Decide_AnotherUsersSessionIsBlocked()
        => Assert.False(LinuxKeyInputGate.Decide(Seat(uid: 1001), lockerActive: false, SessionUser).Allowed);

    [Fact]
    public void Decide_UnknownSessionUserOrUnknownExpectedUserIsBlocked()
    {
        Assert.False(LinuxKeyInputGate.Decide(Seat(uid: null), lockerActive: false, SessionUser).Allowed);
        Assert.False(LinuxKeyInputGate.Decide(Seat(), lockerActive: false, expectedUid: null).Allowed);
    }

    [Fact]
    public async Task ReArm_StartsBlockedEvenIfTheStoppedLoopFinishesItsRead()
    {
        var seatReads = 0;
        var first = new TaskCompletionSource<LinuxKeyInputGate.SeatState?>();
        var never = new TaskCompletionSource<LinuxKeyInputGate.SeatState?>();
        var published = new SemaphoreSlim(0);
        using var gate = new LinuxKeyInputGate(
            () => Interlocked.Increment(ref seatReads) == 1 ? first.Task : never.Task,
            () => Task.FromResult<bool?>(null),
            () => { published.Release(); return SessionUser; },
            intervalMs: 10);

        gate.SetActive(true);
        Assert.True(await WaitFor(() => Volatile.Read(ref seatReads) == 1));
        gate.SetActive(false);
        gate.SetActive(true);
        Assert.True(await WaitFor(() => Volatile.Read(ref seatReads) == 2));

        first.SetResult(Seat());
        Assert.True(await published.WaitAsync(5000));
        // The stale loop publishes right after reading the expected user; a regression would show here.
        await Task.Delay(50);
        Assert.False(gate.Allowed);
    }

    [Fact]
    public async Task Locker_ErrorsBlockAndAbsenceDoesNot()
    {
        using var gate = new LinuxKeyInputGate(
            () => Task.FromResult<LinuxKeyInputGate.SeatState?>(Seat()), () => Task.FromResult<bool?>(null), () => SessionUser, 10);
        const string gnome = "org.gnome.ScreenSaver";

        Assert.Null(await gate.CheckLockerAsync(_ => Task.FromResult(false), _ => Task.FromResult(true)));
        Assert.False(await gate.CheckLockerAsync(n => Task.FromResult(n == gnome), _ => Task.FromResult(false)));
        Assert.True(await gate.CheckLockerAsync(n => Task.FromResult(n == gnome), _ => Task.FromResult(true)));
        Assert.True(await gate.CheckLockerAsync(n => Task.FromResult(n == gnome), _ => throw new TimeoutException()));
        Assert.True(await gate.CheckLockerAsync(_ => throw new TimeoutException(), _ => Task.FromResult(false)));
    }
}

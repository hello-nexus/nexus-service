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
            () => SessionUser,
            intervalMs: 10);
        gate.AfterPublish = () => published.Release();

        gate.SetActive(true);
        Assert.True(await WaitFor(() => Volatile.Read(ref seatReads) == 1));
        gate.SetActive(false);
        gate.SetActive(true);
        Assert.True(await WaitFor(() => Volatile.Read(ref seatReads) == 2));

        first.SetResult(Seat());
        Assert.True(await published.WaitAsync(5000));
        Assert.False(gate.Allowed);
    }

    private static LinuxKeyInputGate NewGate()
        => new(() => Task.FromResult<LinuxKeyInputGate.SeatState?>(Seat()), () => Task.FromResult<bool?>(null), () => SessionUser, 10);

    private const string Gnome = "org.gnome.ScreenSaver";
    private const string Freedesktop = "org.freedesktop.ScreenSaver";

    [Fact]
    public async Task Locker_AbsentIsNullAndTheEmptyLookupIsCached()
    {
        using var gate = NewGate();
        var lookups = 0;
        Task<bool> None(string _) { lookups++; return Task.FromResult(false); }

        Assert.Null(await gate.CheckLockerAsync(None, _ => Task.FromResult(true)));
        var afterFirst = lookups;
        Assert.Null(await gate.CheckLockerAsync(None, _ => Task.FromResult(true)));

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, lookups);
    }

    [Fact]
    public async Task Locker_ReportsItsAnswer()
    {
        using var inactive = NewGate();
        Assert.False(await inactive.CheckLockerAsync(n => Task.FromResult(n == Gnome), _ => Task.FromResult(false)));
        using var active = NewGate();
        Assert.True(await active.CheckLockerAsync(n => Task.FromResult(n == Gnome), _ => Task.FromResult(true)));
    }

    [Fact]
    public async Task Locker_TimeoutsAndOtherErrorsBlock()
    {
        using var queryFails = NewGate();
        Assert.True(await queryFails.CheckLockerAsync(n => Task.FromResult(n == Gnome), _ => throw new TimeoutException()));
        using var otherError = NewGate();
        Assert.True(await otherError.CheckLockerAsync(
            n => Task.FromResult(n == Gnome),
            _ => throw new InvalidOperationException("D-Bus error org.freedesktop.DBus.Error.AccessDenied: no")));
        using var lookupFails = NewGate();
        Assert.True(await lookupFails.CheckLockerAsync(_ => throw new TimeoutException(), _ => Task.FromResult(false)));
    }

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.UnknownMethod")]
    [InlineData("org.freedesktop.DBus.Error.UnknownInterface")]
    public async Task Locker_OwnerWithoutGetActiveIsNotALocker(string error)
    {
        using var only = NewGate();
        Assert.Null(await only.CheckLockerAsync(
            n => Task.FromResult(n == Freedesktop),
            _ => throw new InvalidOperationException($"D-Bus error {error}: x")));

        using var withReal = NewGate();
        Assert.True(await withReal.CheckLockerAsync(
            n => Task.FromResult(n is Gnome or Freedesktop),
            l => l.Name == Freedesktop ? throw new InvalidOperationException($"D-Bus error {error}: x") : Task.FromResult(true)));
    }

    [Fact]
    public async Task ForceBlocked_ClosesAtOnceAndDiscardsAReadInFlight_ThenPollingReopens()
    {
        var reads = 0;
        var slow = new TaskCompletionSource<LinuxKeyInputGate.SeatState?>();
        var published = new SemaphoreSlim(0);
        using var gate = new LinuxKeyInputGate(
            () => Interlocked.Increment(ref reads) switch
            {
                1 => Task.FromResult<LinuxKeyInputGate.SeatState?>(Seat()),
                2 => slow.Task,
                _ => Task.FromResult<LinuxKeyInputGate.SeatState?>(Seat()),
            },
            () => Task.FromResult<bool?>(null), () => SessionUser, intervalMs: 10);
        gate.SetActive(true);
        Assert.True(await WaitFor(() => gate.Allowed));
        Assert.True(await WaitFor(() => Volatile.Read(ref reads) == 2));

        gate.ForceBlocked();
        Assert.False(gate.Allowed);

        // The in-flight read began before the lock, so its allowed result must not reopen the gate.
        gate.AfterPublish = () => published.Release();
        slow.SetResult(Seat());
        Assert.True(await published.WaitAsync(5000));
        Assert.False(gate.Allowed);

        // The next poll starts after the lock and may reopen it.
        Assert.True(await WaitFor(() => gate.Allowed));
    }

    [Fact]
    public async Task Loop_ACancellationNotFromItsTokenEndsBlocked()
    {
        var calls = 0;
        using var gate = new LinuxKeyInputGate(
            () => Interlocked.Increment(ref calls) == 1
                ? Task.FromResult<LinuxKeyInputGate.SeatState?>(Seat())
                : throw new OperationCanceledException(),
            () => Task.FromResult<bool?>(null), () => SessionUser, intervalMs: 10);

        var sawAllowed = false;
        gate.AfterPublish = () => { if (gate.Allowed) sawAllowed = true; };

        gate.SetActive(true);
        Assert.True(await WaitFor(() => Volatile.Read(ref sawAllowed) && !gate.Allowed));
    }
}

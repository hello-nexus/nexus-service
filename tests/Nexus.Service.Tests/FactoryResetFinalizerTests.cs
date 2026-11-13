using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests;

public class FactoryResetFinalizerTests
{
    private sealed class FakeProcess : FactoryReset.IWaitableProcess
    {
        internal bool ExitsByItself;
        internal bool ExitsAfterKill = true;
        internal bool Killed;
        internal bool Disposed;
        internal Exception? KillThrows;
        internal readonly List<TimeSpan> Waits = new();

        public bool WaitForExit(TimeSpan timeout)
        {
            Waits.Add(timeout);
            return Killed ? ExitsAfterKill : ExitsByItself;
        }

        public void Kill()
        {
            if (KillThrows is not null)
            {
                throw KillThrows;
            }
            Killed = true;
        }

        public void Dispose() => Disposed = true;
    }

    private readonly List<string> _log = new();

    private bool Ensure(FakeProcess? process, int pid = 42) =>
        FactoryReset.EnsureParentGone(pid, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), _ => process, _log.Add);

    [Fact]
    public void A_parent_that_exits_in_time_is_not_killed()
    {
        var process = new FakeProcess { ExitsByItself = true };

        Assert.True(Ensure(process));

        Assert.False(process.Killed);
        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, process.Waits);
        Assert.True(process.Disposed);
        Assert.Contains(_log, l => l.Contains("exited"));
    }

    [Fact]
    public void A_parent_that_will_not_exit_is_force_killed_then_waited_on_again()
    {
        var process = new FakeProcess();

        Assert.True(Ensure(process));

        Assert.True(process.Killed);
        Assert.Equal(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15) }, process.Waits);
        Assert.Contains(_log, l => l.Contains("force-killing"));
        Assert.Contains(_log, l => l.Contains("killed"));
    }

    [Fact]
    public void A_parent_that_survives_the_kill_is_reported_and_the_restart_still_goes_ahead()
    {
        var process = new FakeProcess { ExitsAfterKill = false };

        Assert.False(Ensure(process));

        Assert.Contains(_log, l => l.Contains("survived the kill"));
    }

    [Fact]
    public void A_parent_that_is_already_gone_needs_no_wait()
    {
        Assert.True(Ensure(null));

        Assert.Contains(_log, l => l.Contains("already gone"));
    }

    [Fact]
    public void No_parent_pid_needs_no_wait()
    {
        Assert.True(Ensure(new FakeProcess(), pid: 0));

        Assert.Empty(_log);
    }

    [Fact]
    public void A_kill_that_throws_is_logged_and_never_escapes()
    {
        var process = new FakeProcess { KillThrows = new InvalidOperationException("access denied") };

        Assert.False(Ensure(process));

        Assert.Contains(_log, l => l.Contains("access denied"));
        Assert.True(process.Disposed);
    }

    [Fact]
    public void An_open_that_throws_is_logged_and_never_escapes()
    {
        var ok = FactoryReset.EnsureParentGone(42, TimeSpan.Zero, TimeSpan.Zero, _ => throw new InvalidOperationException("nope"), _log.Add);

        Assert.False(ok);
        Assert.Contains(_log, l => l.Contains("nope"));
    }

    [Fact]
    public void The_start_retries_until_it_succeeds()
    {
        var codes = new Queue<int>(new[] { 1056, 1056, 0 });
        var pauses = 0;

        var attempt = FactoryReset.StartWithRetries(() => codes.Dequeue(), FactoryReset.StartAttempts, () => pauses++, _log.Add);

        Assert.Equal(3, attempt);
        Assert.Equal(2, pauses);
        Assert.Contains(_log, l => l.Contains("succeeded on attempt 3"));
    }

    [Fact]
    public void The_start_gives_up_after_the_bounded_number_of_attempts_and_says_so()
    {
        var starts = 0;

        var attempt = FactoryReset.StartWithRetries(() => ++starts, 7, () => { }, _log.Add);

        Assert.Equal(0, attempt);
        Assert.Equal(7, starts);
        Assert.Contains(_log, l => l.Contains("failed after 7 attempts"));
    }

    [Fact]
    public void The_start_bound_outlasts_the_old_five_second_one()
    {
        Assert.True(FactoryReset.StartAttempts * 500 >= 30_000);
    }
}

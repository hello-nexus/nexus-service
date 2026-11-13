using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests.Lifecycle;

public class ResumeSelfRestartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-self-restart-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc);
    private int _restarts;
    private bool _restartResult = true;

    public ResumeSelfRestartTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Stamp => Path.Combine(_dir, "resume-self-restart.stamp");

    private ResumeSelfRestart Create(string? stampPath = null) =>
        new(stampPath ?? Stamp, () =>
        {
            _restarts++;
            return _restartResult;
        }, () => _now);

    [Fact]
    public void The_first_restart_runs_and_stamps()
    {
        var policy = Create();

        Assert.True(policy.TryRestart("test"));

        Assert.Equal(1, _restarts);
        Assert.True(File.Exists(Stamp));
    }

    [Fact]
    public void A_second_restart_inside_the_loop_guard_window_is_refused_even_from_a_new_instance()
    {
        Assert.True(Create().TryRestart("test"));
        _now += ResumeSelfRestart.LoopGuard - TimeSpan.FromMinutes(1);

        Assert.False(Create().TryRestart("test"));

        Assert.Equal(1, _restarts);
    }

    [Fact]
    public void A_restart_after_the_loop_guard_window_runs_again()
    {
        Assert.True(Create().TryRestart("test"));
        _now += ResumeSelfRestart.LoopGuard + TimeSpan.FromMinutes(1);

        Assert.True(Create().TryRestart("test"));

        Assert.Equal(2, _restarts);
    }

    [Fact]
    public void A_refused_restart_does_not_spend_the_loop_guard()
    {
        _restartResult = false;
        Assert.False(Create().TryRestart("test"));
        Assert.False(File.Exists(Stamp));

        _restartResult = true;
        Assert.True(Create().TryRestart("test"));
    }

    [Fact]
    public void An_unwritable_stamp_means_no_restart_because_the_loop_guard_could_not_hold()
    {
        var policy = Create(Path.Combine(_dir, "missing-dir", "stamp"));

        Assert.False(policy.TryRestart("test"));

        Assert.Equal(0, _restarts);
    }

    [Fact]
    public void The_start_marker_is_consumed_once()
    {
        Assert.True(Create().TryRestart("test"));
        Assert.True(File.Exists(Stamp + ".started"));

        Create().LogStartMarker();

        Assert.False(File.Exists(Stamp + ".started"));
        Assert.True(File.Exists(Stamp));
    }

    [Fact]
    public void A_stamp_dated_in_the_future_is_expired_and_does_not_block_a_restart()
    {
        Assert.True(Create().TryRestart("test"));
        _now -= TimeSpan.FromHours(3);

        Assert.True(Create().TryRestart("test"));

        Assert.Equal(2, _restarts);
    }

    // ── The restart action ──

    private sealed class Harness
    {
        internal bool Flashing;
        internal int InstallGate;
        internal bool BeginResult = true;
        internal Exception? BeginThrows;
        internal int Begins;
        internal readonly TaskCompletionSource Stopped = new();

        internal bool Run() => ResumeSelfRestart.Perform(
            () => Flashing,
            () => Interlocked.CompareExchange(ref InstallGate, 1, 0) == 0,
            () => Interlocked.Exchange(ref InstallGate, 0),
            () =>
            {
                Begins++;
                if (BeginThrows is not null)
                {
                    throw BeginThrows;
                }
                return BeginResult;
            },
            () => Stopped.SetResult());
    }

    [Fact]
    public async Task The_restart_action_claims_the_finalizer_stops_the_host_and_keeps_the_install_gate()
    {
        var h = new Harness();

        Assert.True(h.Run());

        await h.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, h.Begins);
        Assert.Equal(1, h.InstallGate);
    }

    [Fact]
    public void A_firmware_flash_refuses_the_restart_before_anything_is_claimed()
    {
        var h = new Harness { Flashing = true };

        Assert.False(h.Run());

        Assert.Equal(0, h.Begins);
        Assert.Equal(0, h.InstallGate);
        Assert.False(h.Stopped.Task.IsCompleted);
    }

    [Fact]
    public void A_running_install_refuses_the_restart_and_is_left_alone()
    {
        var h = new Harness { InstallGate = 1 };

        Assert.False(h.Run());

        Assert.Equal(0, h.Begins);
        Assert.Equal(1, h.InstallGate);
        Assert.False(h.Stopped.Task.IsCompleted);
    }

    [Fact]
    public void A_refused_finalizer_claim_gives_the_install_gate_back()
    {
        var h = new Harness { BeginResult = false };

        Assert.False(h.Run());

        Assert.Equal(1, h.Begins);
        Assert.Equal(0, h.InstallGate);
        Assert.False(h.Stopped.Task.IsCompleted);
    }

    [Fact]
    public void A_finalizer_claim_that_throws_gives_the_install_gate_back_and_rethrows()
    {
        var h = new Harness { BeginThrows = new InvalidOperationException("spawn failed") };

        Assert.Throws<InvalidOperationException>(() => h.Run());

        Assert.Equal(0, h.InstallGate);
    }

    [Fact]
    public async Task An_install_cannot_start_once_the_restart_holds_the_gate()
    {
        var h = new Harness();
        Assert.True(h.Run());
        await h.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The install's own claim is the same compare-and-swap on the same gate.
        Assert.NotEqual(0, Interlocked.CompareExchange(ref h.InstallGate, 1, 0));
    }
}

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
}

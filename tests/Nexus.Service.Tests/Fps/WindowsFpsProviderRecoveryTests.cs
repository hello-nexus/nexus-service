#if WINDOWS
using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests.Fps;

public class WindowsFpsProviderRecoveryTests
{
    private const double BaseBackoff = 10;

    private sealed class FakeScreenTime : IScreenTimeProvider
    {
        public event Action? FocusChanged { add { } remove { } }
        public FocusSession? GetCurrentSession() => new() { Id = "1234", Name = "game" };
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(double seconds) =>
            Interlocked.Exchange(ref _ticks, Interlocked.Read(ref _ticks) + (long)(seconds * TimeSpan.TicksPerSecond));
    }

    private sealed class FakeRunner : IFpsTraceRunner
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource> _live = new();
        private readonly List<string> _started = new();
        private readonly List<string> _stopped = new();
        private Action<int, long>? _onPresent;
        public Exception? ThrowOnRun;

        public void Run(string sessionName, Action<int, long> onPresent, CancellationToken token)
        {
            // A capture cancelled before its Run starts never registers, so a
            // stale one cannot land after the final capture and shadow it.
            if (token.IsCancellationRequested)
                return;

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _started.Add(sessionName);
                _live[sessionName] = done;
                _onPresent = onPresent;
            }

            if (ThrowOnRun is not null)
                throw ThrowOnRun;

            using var reg = token.Register(() => done.TrySetResult());
            done.Task.Wait();
        }

        public void Stop(string sessionName)
        {
            lock (_lock)
            {
                _stopped.Add(sessionName);
                if (_live.TryGetValue(sessionName, out var d)) d.TrySetResult();
            }
        }

        public void SweepStale(string currentSessionName) { }

        // Simulates `logman stop`: the logger ends and Run returns without a cancel.
        public void KillExternally(string sessionName)
        {
            lock (_lock) { _live[sessionName].TrySetResult(); }
        }

        public List<string> Started { get { lock (_lock) return new List<string>(_started); } }
        public List<string> Stopped { get { lock (_lock) return new List<string>(_stopped); } }
        public int StartedCount { get { lock (_lock) return _started.Count; } }
        public Action<int, long>? OnPresent { get { lock (_lock) return _onPresent; } }
    }

    private static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }

    // The focus loop publishes the target pid asynchronously, so keep feeding
    // presents through the latest callback until the provider counts them.
    private static bool FeedUntilCounted(FakeRunner runner, WindowsFpsProvider provider)
    {
        long ticks = 1;
        return WaitFor(() =>
        {
            runner.OnPresent?.Invoke(1234, ticks);
            ticks += TimeSpan.TicksPerSecond / 60;
            return provider.TryReadCurrentFps(out _);
        });
    }

    private static (FakeRunner Runner, ManualTime Time, WindowsFpsProvider Provider) Create()
    {
        var runner = new FakeRunner();
        var time = new ManualTime();
        return (runner, time, new WindowsFpsProvider(new FakeScreenTime(), runner, BaseBackoff, time));
    }

    // Kills the logger and waits until the provider has registered the failure.
    private static void KillAndAwaitFailure(FakeRunner runner, int index)
    {
        var name = runner.Started[index];
        runner.KillExternally(name);
        Assert.True(WaitFor(() => runner.Stopped.Contains(name)));
    }

    [Fact]
    public void NextSessionName_IsUniqueAndKeepsPrefix()
    {
        var names = Enumerable.Range(0, 50).Select(_ => WindowsFpsProvider.NextSessionName()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("Nexus-Fps-" + Environment.ProcessId + "-", n));
    }

    [Theory]
    [InlineData("Nexus-Fps-100-3", "Nexus-Fps-100-5", true)]
    [InlineData("Nexus-Fps-100-5", "Nexus-Fps-100-5", false)]
    [InlineData("Nexus-Fps-100-6", "Nexus-Fps-100-5", false)]
    [InlineData("Nexus-Fps-200-9", "Nexus-Fps-100-5", true)]
    [InlineData("Nexus-Fps-200", "Nexus-Fps-100-5", true)]
    [InlineData("Other-Logger-1", "Nexus-Fps-100-5", false)]
    public void ShouldSweep_KeepsOnlyThisProcessCurrentAndNewerCaptures(string name, string current, bool expected)
    {
        Assert.Equal(expected, EtwFpsTraceRunner.ShouldSweep(name, current));
    }

    [Fact]
    public void RapidToggle_EachCaptureGetsItsOwnSessionAndTheLastOneIsLive()
    {
        var (runner, _, provider) = Create();
        using (provider)
        {
            for (var i = 0; i < 20; i++)
            {
                provider.SetDemand("t", true);
                provider.SetDemand("t", false);
            }
            provider.SetDemand("t", true);

            // The final capture's Run is the latest registered callback; wait
            // until it is up and the stale ones have been stopped.
            Assert.True(WaitFor(() => runner.StartedCount >= 1));
            Assert.True(FeedUntilCounted(runner, provider));

            var started = runner.Started;
            Assert.Equal(started.Count, started.Distinct().Count());
            // Only the final capture can still be running: every earlier name was stopped.
            Assert.True(WaitFor(() => started.Take(started.Count - 1).All(n => runner.Stopped.Contains(n))));
            Assert.DoesNotContain(started[^1], runner.Stopped);
            Assert.True(provider.TryReadCurrentFps(out _));
        }
    }

    [Fact]
    public void CleanExitOfTheLogger_IsTreatedAsFailureAndRestartsAfterBackoff()
    {
        var (runner, time, provider) = Create();
        using (provider)
        {
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 1));
            var first = runner.Started[0];

            KillAndAwaitFailure(runner, 0);

            // Inside the backoff the demand tick must not restart.
            time.Advance(BaseBackoff - 1);
            provider.SetDemand("t", true);
            Assert.Empty(provider.GetComponent().Sensors);

            time.Advance(2);
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 2));
            Assert.NotEqual(first, runner.Started[1]);
        }
    }

    [Fact]
    public void StaleTaskStop_DoesNotTearDownTheNewerCapture()
    {
        var (runner, _, provider) = Create();
        using (provider)
        {
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 1));
            provider.SetDemand("t", false);
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 2));

            Assert.True(FeedUntilCounted(runner, provider));
            Assert.True(provider.TryReadCurrentFps(out _));
            Assert.DoesNotContain(runner.Started[1], runner.Stopped);
        }
    }

    [Fact]
    public void ConsecutiveCleanExits_DoubleTheBackoff_AndAPresentResetsIt()
    {
        var (runner, time, provider) = Create();
        using (provider)
        {
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 1));
            KillAndAwaitFailure(runner, 0);
            time.Advance(BaseBackoff + 1);
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 2));

            // Second exit with no present delivered: the backoff doubles the base.
            KillAndAwaitFailure(runner, 1);
            time.Advance(BaseBackoff + 1);
            provider.SetDemand("t", true);
            Assert.Empty(provider.GetComponent().Sensors);
            time.Advance(BaseBackoff);
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 3));

            // A delivered present resets the streak: the next backoff is the base again.
            Assert.True(FeedUntilCounted(runner, provider));
            KillAndAwaitFailure(runner, 2);
            time.Advance(BaseBackoff + 1);
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 4));
        }
    }

    [Fact]
    public void ARunLongerThanTheHealthyWindow_ResetsTheBackoffEvenWithoutPresents()
    {
        var (runner, time, provider) = Create();
        using (provider)
        {
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 1));
            KillAndAwaitFailure(runner, 0);
            time.Advance(BaseBackoff + 1);
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 2));

            // Streak is 1 here; a long healthy run must bring the backoff back to the base.
            time.Advance(120);
            KillAndAwaitFailure(runner, 1);
            time.Advance(BaseBackoff + 1);
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 3));
        }
    }

    [Fact]
    public void AccessDenied_BacksOffPermanently()
    {
        var (runner, time, provider) = Create();
        runner.ThrowOnRun = new UnauthorizedAccessException("denied");
        using (provider)
        {
            provider.SetDemand("t", true);
            Assert.True(WaitFor(() => runner.StartedCount == 1));
            Assert.True(WaitFor(() => runner.Stopped.Count >= 1));

            time.Advance(60 * 60 * 24 * 365);
            provider.SetDemand("t", true);
            Assert.Empty(provider.GetComponent().Sensors);
        }
    }
}
#endif

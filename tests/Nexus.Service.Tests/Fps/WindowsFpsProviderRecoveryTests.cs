#if WINDOWS
using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests.Fps;

public class WindowsFpsProviderRecoveryTests
{
    private sealed class FakeScreenTime : IScreenTimeProvider
    {
        public event Action? FocusChanged { add { } remove { } }
        public FocusSession? GetCurrentSession() => new() { Id = "1234", Name = "game" };
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private sealed class FakeRunner : IFpsTraceRunner
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource> _live = new();
        public readonly List<string> Started = new();
        public readonly List<string> Stopped = new();
        public Action<int, long>? OnPresent;

        public void Run(string sessionName, Action<int, long> onPresent, CancellationToken token)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                Started.Add(sessionName);
                _live[sessionName] = done;
                OnPresent = onPresent;
            }
            using var reg = token.Register(() => done.TrySetResult());
            done.Task.Wait();
        }

        public void Stop(string sessionName)
        {
            lock (_lock)
            {
                Stopped.Add(sessionName);
                if (_live.TryGetValue(sessionName, out var d)) d.TrySetResult();
            }
        }

        public void SweepStale(string keepPrefix) { }

        // Simulates `logman stop`: the logger ends and Run returns without a cancel.
        public void KillExternally(string sessionName)
        {
            lock (_lock) { _live[sessionName].TrySetResult(); }
        }

        public int StartedCount { get { lock (_lock) return Started.Count; } }
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

    [Fact]
    public void NextSessionName_IsUniqueAndKeepsPrefix()
    {
        var names = Enumerable.Range(0, 50).Select(_ => WindowsFpsProvider.NextSessionName()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("Nexus-Fps-" + Environment.ProcessId + "-", n));
    }

    [Fact]
    public void RapidToggle_EachCaptureGetsItsOwnSessionAndLastOneSurvives()
    {
        var runner = new FakeRunner();
        using var provider = new WindowsFpsProvider(new FakeScreenTime(), runner, 0.3);

        for (var i = 0; i < 20; i++)
        {
            provider.SetDemand("t", true);
            provider.SetDemand("t", false);
        }
        provider.SetDemand("t", true);

        Assert.True(WaitFor(() => runner.StartedCount >= 1));
        var last = runner.Started[^1];
        Thread.Sleep(100);
        // The live capture's name was never stopped by a stale task.
        Assert.DoesNotContain(last, runner.Stopped);
        Assert.Equal(runner.Started.Count, runner.Started.Distinct().Count());
    }

    [Fact]
    public void CleanExitOfTheLogger_IsTreatedAsFailureAndRestartsAfterBackoff()
    {
        var runner = new FakeRunner();
        using var provider = new WindowsFpsProvider(new FakeScreenTime(), runner, 0.3);

        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 1));
        var first = runner.Started[0];

        runner.KillExternally(first);
        Assert.True(WaitFor(() => runner.Stopped.Contains(first)));

        // Inside the backoff the demand tick must not restart yet.
        provider.SetDemand("t", true);
        Assert.Equal(1, runner.StartedCount);

        Thread.Sleep(450);
        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 2));
        Assert.NotEqual(first, runner.Started[1]);
    }

    [Fact]
    public void StaleTaskStop_DoesNotTearDownTheNewerCapture()
    {
        var runner = new FakeRunner();
        using var provider = new WindowsFpsProvider(new FakeScreenTime(), runner, 0.3);

        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 1));
        provider.SetDemand("t", false);
        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 2));
        Thread.Sleep(200);

        // Capture 2 must still be live: presents for the target pid register.
        runner.OnPresent!(1234, 1);
        runner.OnPresent!(1234, 1 + TimeSpan.TicksPerSecond / 60);
        Thread.Sleep(300);
        Assert.DoesNotContain(runner.Started[1], runner.Stopped);
    }

    [Fact]
    public void ConsecutiveCleanExits_DoubleTheBackoff_AndAPresentResetsIt()
    {
        var runner = new FakeRunner();
        using var provider = new WindowsFpsProvider(new FakeScreenTime(), runner, 0.3);

        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 1));
        runner.KillExternally(runner.Started[0]);
        Assert.True(WaitFor(() => runner.Stopped.Contains(runner.Started[0])));
        Thread.Sleep(450);
        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 2));

        // Second exit with no present delivered: backoff doubles to 0.6 s.
        runner.KillExternally(runner.Started[1]);
        Assert.True(WaitFor(() => runner.Stopped.Contains(runner.Started[1])));
        Thread.Sleep(400);
        provider.SetDemand("t", true);
        Assert.Equal(2, runner.StartedCount);
        Thread.Sleep(350);
        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 3));

        // A delivered present resets the streak: next backoff is the base again.
        Thread.Sleep(500);
        runner.OnPresent!(1234, 1);
        runner.KillExternally(runner.Started[2]);
        Assert.True(WaitFor(() => runner.Stopped.Contains(runner.Started[2])));
        Thread.Sleep(450);
        provider.SetDemand("t", true);
        Assert.True(WaitFor(() => runner.StartedCount == 4));
    }
}
#endif

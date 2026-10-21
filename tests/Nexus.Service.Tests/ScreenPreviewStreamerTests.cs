using Nexus.Service.Lighting.Capture;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public class ScreenPreviewStreamerTests
{
    private sealed class FakeSource : IScreenFrameSource
    {
        public int Starts, Stops, Acquires;
        public volatile string? LastMonitor;
        public bool ThrowOnAcquire;
        public void Start(string monitorId, int width, int height) { Starts++; LastMonitor = monitorId; }
        public void Stop() { Stops++; }
        public byte[]? TryAcquireFrame(out int width, out int height)
        {
            Interlocked.Increment(ref Acquires);
            if (ThrowOnAcquire) throw new InvalidOperationException("capture failed");
            width = 16;
            height = 9;
            return new byte[16 * 9 * 3];
        }
    }

    private static bool WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) Thread.Sleep(10);
        return cond();
    }

    private static ScreenPreviewStreamer Make(SharedScreenFrameSource shared, Func<string> monitor, Func<int> clients)
        => new(new ScreenPreviewHub(), shared.CreateObserver, monitor, new PostProcessState(), clients);

    [Fact]
    public void NoClients_NoCapture()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        using var streamer = Make(shared, () => "m1", () => 0);
        streamer.Reconcile();
        Assert.False(streamer.Running);
        Assert.Equal(0, inner.Starts);
        Assert.Equal(0, shared.ActiveHandles);
    }

    [Fact]
    public void Subscribe_OpensCapture_LastUnsubscribe_ReleasesHandle()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        var clients = 1;
        using var streamer = Make(shared, () => "m1", () => clients);
        streamer.Reconcile();
        Assert.True(WaitFor(() => shared.ActiveHandles == 1));
        Assert.Equal("m1", inner.LastMonitor);
        clients = 0;
        streamer.Reconcile();
        Assert.True(WaitFor(() => shared.ActiveHandles == 0));
        Assert.Equal(1, inner.Stops);
    }

    [Fact]
    public void DeviceConsumerHoldsCapture_PreviewOnlyReads_NeverRestartsOrRetargets()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        var device = shared.CreateHandle();
        device.Start("m1", 160, 90);
        using var streamer = Make(shared, () => "m2", () => 1);
        streamer.Reconcile();
        Assert.True(WaitFor(() => inner.Acquires >= 4));
        Assert.Equal(1, inner.Starts);
        Assert.Equal("m1", inner.LastMonitor);
        Assert.Equal(0, inner.Stops);
        Assert.Equal(1, shared.ActiveHandles);
    }

    [Fact]
    public void PreviewOwnsCapture_DeviceStartsOnAnotherMonitor_PreviewDoesNotRevertIt()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        using var streamer = Make(shared, () => "m1", () => 1);
        streamer.Reconcile();
        Assert.True(WaitFor(() => shared.ActiveHandles == 1));
        var device = shared.CreateHandle();
        device.Start("m2", 160, 90);
        var seen = inner.Acquires;
        Assert.True(WaitFor(() => inner.Acquires >= seen + 4));
        Assert.Equal("m2", inner.LastMonitor);
        device.Stop();
        // The preview was the other consumer, so it keeps the capture on.
        Assert.Equal(1, shared.ActiveHandles);
    }

    [Fact]
    public void SoleOwner_FollowsMonitorChange()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        var monitor = "m1";
        using var streamer = Make(shared, () => monitor, () => 1);
        streamer.Reconcile();
        Assert.True(WaitFor(() => inner.LastMonitor == "m1"));
        monitor = "m2";
        Assert.True(WaitFor(() => inner.LastMonitor == "m2"));
        Assert.Equal(1, shared.ActiveHandles);
    }

    [Fact]
    public void FrameFailure_DoesNotEndTheLoop()
    {
        var inner = new FakeSource { ThrowOnAcquire = true };
        var shared = new SharedScreenFrameSource(inner);
        using var streamer = Make(shared, () => "m1", () => 1);
        streamer.Reconcile();
        Assert.True(WaitFor(() => inner.Acquires >= 3));
        Assert.True(streamer.Running);
    }

    [Fact]
    public void LeaveThenJoin_LeavesExactlyOneLoopHoldingTheCapture()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        var clients = 1;
        using var streamer = Make(shared, () => "m1", () => clients);
        streamer.Reconcile();
        Assert.True(WaitFor(() => shared.ActiveHandles == 1));
        clients = 0;
        streamer.Reconcile();
        clients = 1;
        streamer.Reconcile();
        // Stale events read the live count and change nothing.
        streamer.Reconcile();
        Assert.True(streamer.Running);
        Assert.True(WaitFor(() => shared.ActiveHandles == 1));
        var seen = inner.Acquires;
        Assert.True(WaitFor(() => inner.Acquires > seen + 2));
        Assert.Equal(1, shared.ActiveHandles);
    }
}

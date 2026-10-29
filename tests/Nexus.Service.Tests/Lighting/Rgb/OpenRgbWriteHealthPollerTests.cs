using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class OpenRgbWriteHealthPollerTests
{
    private sealed class FakeTransport : IOpenRgbWriteHealthTransport
    {
        public Queue<OpenRgbWriteHealthResult> Replies { get; } = new();
        public List<int> Asked { get; } = new();

        public Task<OpenRgbWriteHealthResult> GetWriteHealthAsync(int address, CancellationToken ct = default)
        {
            Asked.Add(address);
            return Task.FromResult(Replies.Dequeue());
        }

        public void Reply(uint failures, uint successes) =>
            Replies.Enqueue(new OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome.Ok, new OpenRgbWriteHealth(failures, successes, 0)));
    }

    private static (OpenRgbWriteHealthPoller Poller, LightingWriteHealth Health, FakeTransport Transport) Make()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var health = new LightingWriteHealth(() => now);
        var transport = new FakeTransport();
        return (new OpenRgbWriteHealthPoller(health, transport), health, transport);
    }

    private static readonly IReadOnlyList<(string, int)> OneFrame = new[] { ("openrgb-s-a", 3) };

    [Fact]
    public async Task The_first_poll_only_sets_the_baseline()
    {
        var (poller, health, transport) = Make();
        transport.Reply(900, 10);

        await poller.PollOnceAsync(OneFrame, default);

        Assert.False(health.IsFailing("openrgb-s-a"));
    }

    [Fact]
    public async Task Failures_in_two_consecutive_polls_flag_the_frame()
    {
        var (poller, health, transport) = Make();
        transport.Reply(0, 100);
        transport.Reply(3, 100);
        transport.Reply(7, 100);

        await poller.PollOnceAsync(OneFrame, default);
        await poller.PollOnceAsync(OneFrame, default);
        Assert.False(health.IsFailing("openrgb-s-a"));
        await poller.PollOnceAsync(OneFrame, default);

        Assert.True(health.IsFailing("openrgb-s-a"));
    }

    [Fact]
    public async Task A_single_poll_with_a_burst_does_not_flag()
    {
        var (poller, health, transport) = Make();
        transport.Reply(0, 0);
        transport.Reply(50, 0);

        await poller.PollOnceAsync(OneFrame, default);
        await poller.PollOnceAsync(OneFrame, default);

        Assert.False(health.IsFailing("openrgb-s-a"));
    }

    [Fact]
    public async Task A_counter_that_goes_backwards_is_a_restarted_daemon_and_resets_the_baseline()
    {
        var (poller, health, transport) = Make();
        transport.Reply(100, 100);
        transport.Reply(5, 5);
        transport.Reply(9, 5);

        await poller.PollOnceAsync(OneFrame, default);
        await poller.PollOnceAsync(OneFrame, default);
        await poller.PollOnceAsync(OneFrame, default);

        Assert.False(health.IsFailing("openrgb-s-a"));
    }

    [Fact]
    public async Task Zones_sharing_a_controller_are_asked_once_and_each_frame_is_reported()
    {
        var (poller, health, transport) = Make();
        transport.Reply(0, 0);
        transport.Reply(3, 0);
        transport.Reply(6, 0);
        var frames = new[] { ("zone-a", 7), ("zone-b", 7) };

        await poller.PollOnceAsync(frames, default);
        await poller.PollOnceAsync(frames, default);
        await poller.PollOnceAsync(frames, default);

        Assert.Equal(new[] { 7, 7, 7 }, transport.Asked);
        Assert.True(health.IsFailing("zone-a"));
        Assert.True(health.IsFailing("zone-b"));
    }

    [Fact]
    public async Task An_unsupported_daemon_is_asked_once_then_left_alone_until_reset()
    {
        var (poller, _, transport) = Make();
        transport.Replies.Enqueue(new OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome.Unsupported, default));

        await poller.PollOnceAsync(OneFrame, default);
        await poller.PollOnceAsync(OneFrame, default);

        Assert.True(poller.Unsupported);
        Assert.Single(transport.Asked);

        poller.Reset();
        transport.Reply(0, 0);
        await poller.PollOnceAsync(OneFrame, default);

        Assert.False(poller.Unsupported);
        Assert.Equal(2, transport.Asked.Count);
    }

    [Fact]
    public async Task An_unknown_controller_is_skipped_alone_and_re_baselines_when_it_returns()
    {
        var (poller, health, transport) = Make();
        var frames = new[] { ("stale", 1), ("live", 2) };
        // Poll 1: both baseline. Poll 2: stale unknown, live rises. Poll 3: stale back at a high count (new baseline), live rises again.
        transport.Reply(0, 0);
        transport.Reply(0, 0);
        transport.Replies.Enqueue(new OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome.UnknownController, default));
        transport.Reply(3, 0);
        transport.Reply(900, 0);
        transport.Reply(6, 0);

        await poller.PollOnceAsync(frames, default);
        await poller.PollOnceAsync(frames, default);
        await poller.PollOnceAsync(frames, default);

        Assert.False(poller.Unsupported);
        Assert.True(health.IsFailing("live"));
        Assert.False(health.IsFailing("stale"));
    }

    [Fact]
    public async Task A_failed_request_reports_nothing_and_keeps_polling()
    {
        var (poller, _, transport) = Make();
        transport.Replies.Enqueue(new OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome.Failed, default));

        await poller.PollOnceAsync(OneFrame, default);

        Assert.False(poller.Unsupported);
    }

    [Fact]
    public void Only_controlled_bridge_frames_are_polled_and_uncontrolled_ones_are_forgotten()
    {
        var health = new LightingWriteHealth();
        var frames = new[]
        {
            new DeviceFrame(0, "openrgb-s-a", 4, physicalIndex: 2),
            new DeviceFrame(1, "openrgb-s-off", 4, physicalIndex: 5),
            new DeviceFrame(2, "keeb:1", 4, physicalIndex: 9),
        };
        var bridge = new HashSet<string> { "openrgb-s-a", "openrgb-s-off" };

        var result = OpenRgbWriteHealthPoller.ControlledFrames(frames, bridge, new[] { "openrgb-s-off" }, health);

        Assert.Equal(new[] { ("openrgb-s-a", 2) }, result);
    }
}

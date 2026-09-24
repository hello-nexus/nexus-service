using System.Linq;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class StreamSessionTests
{
    private static StreamSession NewSession(int fps = 10) => new(
        "session-1",
        new StreamedPanelDeviceInfo
        {
            Serial = "d211_test",
            Profile = new StreamedPanelProfile
            {
                Kind = "test",
                DisplayName = "Test",
                Surface = "monitor",
                CssWidth = 640,
                CssHeight = 480,
                Fps = fps,
            },
        },
        "panel-1");

    private static StreamFrame Frame(bool idr, byte marker = 0) => new()
    {
        Flags = idr ? StreamFraming.FlagIdr : (byte)0,
        Payload = new[] { marker },
    };

    [Fact]
    public void New_session_waits_for_idr_and_skips_to_first_idr()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: false, 1));
        session.Enqueue(Frame(idr: false, 2));
        session.Enqueue(Frame(idr: true, 3));
        session.Enqueue(Frame(idr: false, 4));

        var sent = session.DequeueForTick();

        Assert.Single(sent);
        Assert.Equal(3, sent[0].Payload[0]);
        Assert.True(sent[0].IsIdr);
    }

    [Fact]
    public void Mid_gop_frames_are_never_skipped_in_steady_state()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        Assert.Single(session.DequeueForTick());

        session.Enqueue(Frame(idr: false, 2));
        session.Enqueue(Frame(idr: false, 3));
        var first = session.DequeueForTick();
        var second = session.DequeueForTick();

        Assert.Equal(2, first.Single().Payload[0]);
        Assert.Equal(3, second.Single().Payload[0]);
    }

    [Fact]
    public void Overflow_trims_to_newest_idr()
    {
        // fps 10 -> cap floors at 30 queued frames.
        var session = NewSession(fps: 10);
        session.Enqueue(Frame(idr: true, 1));
        for (byte i = 2; i <= 28; i++) session.Enqueue(Frame(idr: false, i));
        session.Enqueue(Frame(idr: true, 29));
        session.Enqueue(Frame(idr: false, 30));
        Assert.Equal(30, session.QueueDepthForTest);

        session.Enqueue(Frame(idr: false, 31));

        Assert.Equal(3, session.QueueDepthForTest);
        var sent = session.DequeueForTick();
        Assert.Equal(29, sent[0].Payload[0]);
        Assert.True(sent[0].IsIdr);
    }

    [Fact]
    public void Overflow_with_no_idr_clears_and_rearms_resync()
    {
        var session = NewSession(fps: 10);
        session.Enqueue(Frame(idr: true, 1));
        Assert.Single(session.DequeueForTick());

        for (byte i = 0; i < 31; i++) session.Enqueue(Frame(idr: false, i));

        Assert.Equal(0, session.QueueDepthForTest);
        session.Enqueue(Frame(idr: false, 99));
        Assert.Empty(session.DequeueForTick());
        session.Enqueue(Frame(idr: true, 100));
        Assert.Equal(100, session.DequeueForTick().Single().Payload[0]);
    }

    [Fact]
    public void Overflow_with_idr_only_at_head_clears_and_rearms()
    {
        var session = NewSession(fps: 10);
        session.Enqueue(Frame(idr: true, 1));
        for (byte i = 2; i <= 31; i++) session.Enqueue(Frame(idr: false, i));

        Assert.Equal(0, session.QueueDepthForTest);
        session.Enqueue(Frame(idr: true, 99));
        Assert.Equal(99, session.DequeueForTick().Single().Payload[0]);
    }

    [Fact]
    public void Transport_reopen_requires_fresh_idr()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        Assert.Single(session.DequeueForTick());

        session.RequireIdrResync();
        session.Enqueue(Frame(idr: false, 2));
        Assert.Empty(session.DequeueForTick());
        session.Enqueue(Frame(idr: true, 3));
        Assert.Equal(3, session.DequeueForTick().Single().Payload[0]);
    }

    [Fact]
    public void New_ingest_clears_queue_and_rearms_resync()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        session.Enqueue(Frame(idr: false, 2));

        session.ResetForNewIngest();

        Assert.Equal(0, session.QueueDepthForTest);
        Assert.Equal(StreamSessionState.Resync, session.State);
        session.SetTransportUp(true);
        Assert.Equal(StreamSessionState.Live, session.State);
        session.Enqueue(Frame(idr: false, 3));
        Assert.Empty(session.DequeueForTick());
    }

    [Fact]
    public void Closed_session_drops_enqueues()
    {
        var session = NewSession();
        session.Close();
        session.Enqueue(Frame(idr: true, 1));
        Assert.Equal(0, session.QueueDepthForTest);
        Assert.Equal(StreamSessionState.Closed, session.State);
    }

    [Fact]
    public void Catch_up_sends_two_per_tick_when_backlogged()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        for (byte i = 2; i <= 9; i++) session.Enqueue(Frame(idr: false, i));

        var sent = session.DequeueForTick();

        Assert.Equal(2, sent.Count);
        Assert.Equal(1, sent[0].Payload[0]);
        Assert.Equal(2, sent[1].Payload[0]);
    }

    /// <summary>
    /// Pooled payloads are returned by hand, so every path that takes ownership of a frame
    /// has to release it exactly once. A leak only shows up as lost throughput and a double
    /// release hands one array to two frames at once, so neither surfaces as a test failure
    /// anywhere else - these assert the contract directly.
    /// </summary>
    private sealed class CountingPool : System.Buffers.ArrayPool<byte>
    {
        private readonly System.Buffers.ArrayPool<byte> _inner = System.Buffers.ArrayPool<byte>.Create(1024, 4);
        public int Rented;
        public int Returned;
        public override byte[] Rent(int minimumLength) { Rented++; return _inner.Rent(minimumLength); }
        public override void Return(byte[] array, bool clearArray = false) { Returned++; _inner.Return(array, clearArray); }
    }

    private static StreamFrame Pooled(CountingPool pool, bool idr)
    {
        var buf = pool.Rent(8);
        return new StreamFrame
        {
            Flags = idr ? StreamFraming.FlagIdr : (byte)0,
            Payload = buf,
            Length = 8,
            Pooled = true,
            ReturnTo = pool,
        };
    }

    [Fact]
    public void Close_releases_every_queued_payload()
    {
        var pool = new CountingPool();
        var session = NewSession();
        session.Enqueue(Pooled(pool, idr: true));
        session.Enqueue(Pooled(pool, idr: false));

        session.Close();

        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public void ResetForNewIngest_releases_every_queued_payload()
    {
        var pool = new CountingPool();
        var session = NewSession();
        session.Enqueue(Pooled(pool, idr: true));
        session.Enqueue(Pooled(pool, idr: false));

        session.ResetForNewIngest();

        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public void Enqueue_on_a_closed_session_releases_rather_than_leaks()
    {
        var pool = new CountingPool();
        var session = NewSession();
        session.Close();

        session.Enqueue(Pooled(pool, idr: true));

        Assert.Equal(1, pool.Rented);
        Assert.Equal(1, pool.Returned);
    }

    [Fact]
    public void Trim_releases_the_dropped_prefix_and_keeps_the_newest_gop()
    {
        var pool = new CountingPool();
        var session = NewSession(fps: 1);
        // _maxQueuedFrames is max(30, fps*2) for H.264; overshoot it so the trim fires.
        for (var i = 0; i < 40; i++)
        {
            session.Enqueue(Pooled(pool, idr: i == 35));
        }

        // Whatever the trim kept is still owned by the queue; the rest must be back.
        Assert.Equal(pool.Rented - session.QueueDepthForTest, pool.Returned);

        session.Close();
        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public void Dequeued_frames_are_not_released_by_the_session()
    {
        var pool = new CountingPool();
        var session = NewSession();
        session.Enqueue(Pooled(pool, idr: true));

        var frames = session.DequeueForTick();

        // The writer's finally owns them from here; releasing in DequeueForTick would hand
        // the array back while the transport is still reading the span.
        Assert.Equal(0, pool.Returned);
        foreach (var frame in frames) frame.Release();
        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public void Release_is_idempotent_across_owners()
    {
        var pool = new CountingPool();
        var frame = Pooled(pool, idr: true);

        frame.Release();
        frame.Release();
        frame.Release();

        Assert.Equal(1, pool.Returned);
    }
}

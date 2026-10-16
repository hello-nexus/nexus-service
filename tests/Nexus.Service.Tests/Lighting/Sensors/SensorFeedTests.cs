using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Sensors;

public sealed class SensorFeedTests
{
    private long _now = 10_000;
    private readonly Dictionary<string, float> _readings = new();
    private int _resolves;

    private SensorFeed NewFeed(int pollMs = SensorFeed.PollMs, bool background = false) => new(null, (category, sensor) =>
    {
        Interlocked.Increment(ref _resolves);
        return _readings.TryGetValue(category + "/" + sensor, out var v) ? v : float.NaN;
    }, () => Interlocked.Read(ref _now), pollMs, background);

    private static SensorBinding Bind(string sensor = "summary/cpu-usage", float min = 0f, float max = 100f) =>
        new() { Category = "quick", Sensor = sensor, Min = min, Max = max };

    [Fact]
    public void Nothing_is_polled_until_a_channel_is_read()
    {
        using var feed = NewFeed(background: true);
        _ = feed.Acquire(Bind());

        Assert.False(feed.IsPolling);
        Assert.False(feed.Tick(_now));
        Assert.Equal(0, _resolves);
    }

    [Fact]
    public void A_read_channel_gets_the_resolved_value_on_the_next_tick()
    {
        using var feed = NewFeed();
        _readings["quick/summary/cpu-usage"] = 42f;
        var channel = feed.Acquire(Bind());

        Assert.True(float.IsNaN(channel.Read()));
        // The first Read started the poller thread; drive a tick directly so the check is deterministic.
        Assert.True(feed.Tick(_now));

        Assert.Equal(42f, channel.Read());
    }

    [Fact]
    public void A_sensor_the_resolver_cannot_find_stays_NaN_and_normalizes_to_zero()
    {
        using var feed = NewFeed();
        var binding = Bind("no/such");
        var channel = feed.Acquire(binding);
        _ = channel.Read();

        Assert.True(feed.Tick(_now));

        Assert.Equal(SensorFeed.Missing, channel.Read());
        Assert.Equal(0f, binding.Normalize(channel.Read()));
    }

    [Fact]
    public void A_resolver_that_throws_is_a_missing_reading_not_a_failure()
    {
        using var feed = new SensorFeed(null, (_, _) => throw new InvalidOperationException("boom"), () => _now, background: false);
        var channel = feed.Acquire(Bind());
        _ = channel.Read();

        Assert.True(feed.Tick(_now));

        Assert.Equal(SensorFeed.Missing, channel.Read());
    }

    [Fact]
    public void Bindings_that_differ_only_in_range_share_one_channel_and_one_resolve()
    {
        using var feed = NewFeed();
        _readings["quick/summary/cpu-usage"] = 10f;
        var a = feed.Acquire(Bind(min: 0f, max: 100f));
        var b = feed.Acquire(Bind(min: 5f, max: 50f));
        Assert.Same(a, b);
        _ = a.Read();
        _ = b.Read();

        feed.Tick(_now);

        Assert.Equal(1, _resolves);
    }

    [Fact]
    public void Different_sensors_poll_independently()
    {
        using var feed = NewFeed();
        _readings["quick/summary/cpu-usage"] = 10f;
        _readings["quick/summary/memory-usage"] = 70f;
        var cpu = feed.Acquire(Bind("summary/cpu-usage"));
        var mem = feed.Acquire(Bind("summary/memory-usage"));
        _ = cpu.Read();
        _ = mem.Read();

        feed.Tick(_now);

        Assert.Equal(10f, cpu.Read());
        Assert.Equal(70f, mem.Read());
    }

    [Fact]
    public void A_channel_nobody_reads_for_the_idle_window_is_dropped_from_the_poll()
    {
        using var feed = NewFeed();
        var kept = feed.Acquire(Bind("summary/cpu-usage"));
        var idle = feed.Acquire(Bind("summary/memory-usage"));
        _ = kept.Read();
        _ = idle.Read();
        _now += SensorFeed.IdleMs - 1;
        _ = kept.Read();
        _resolves = 0;

        // The idle channel's last read is IdleMs - 1 ago, so both still count; one more tick of time retires it.
        feed.Tick(_now);
        Assert.Equal(2, _resolves);
        _now += 2;
        _resolves = 0;
        _ = kept.Read();
        feed.Tick(_now);

        Assert.Equal(1, _resolves);
    }

    [Fact]
    public void The_poller_ends_once_every_channel_has_gone_idle()
    {
        using var feed = NewFeed();
        _ = feed.Acquire(Bind()).Read();
        Assert.True(feed.Tick(_now));

        _now += SensorFeed.IdleMs + 1;

        Assert.False(feed.Tick(_now));
    }

    [Fact]
    public async Task The_background_poller_starts_on_a_read_and_exits_by_itself_when_reads_stop()
    {
        _readings["quick/summary/cpu-usage"] = 33f;
        using var feed = NewFeed(pollMs: 10, background: true);
        var channel = feed.Acquire(Bind());
        Assert.False(feed.IsPolling);

        _ = channel.Read();
        Assert.True(await WaitFor(() => channel.Read() == 33f));
        Assert.True(feed.IsPolling);

        Interlocked.Add(ref _now, SensorFeed.IdleMs + 1);
        Assert.True(await WaitFor(() => !feed.IsPolling));
    }

    [Fact]
    public async Task A_later_read_restarts_a_stopped_poller()
    {
        _readings["quick/summary/cpu-usage"] = 5f;
        using var feed = NewFeed(pollMs: 10, background: true);
        var channel = feed.Acquire(Bind());
        _ = channel.Read();
        Assert.True(await WaitFor(() => channel.Read() == 5f));
        Interlocked.Add(ref _now, SensorFeed.IdleMs + 1);
        Assert.True(await WaitFor(() => !feed.IsPolling));

        _readings["quick/summary/cpu-usage"] = 6f;
        _ = channel.Read();

        Assert.True(await WaitFor(() => channel.Read() == 6f));
    }

    [Fact]
    public void A_channel_left_unread_past_the_idle_window_reports_no_current_reading_until_polled_again()
    {
        using var feed = NewFeed();
        _readings["quick/summary/cpu-usage"] = 42f;
        var channel = feed.Acquire(Bind());
        _ = channel.Read();
        feed.Tick(_now);
        Assert.Equal(42f, channel.Read());

        _now += SensorFeed.IdleMs + 1;

        Assert.True(float.IsNaN(channel.Read()));
        feed.Tick(_now);
        Assert.Equal(42f, channel.Read());
    }

    private class ThrowingSensors : System.Reflection.DispatchProxy
    {
        public int Walks;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetSensorExtras")
            {
                Interlocked.Increment(ref Walks);
                throw new InvalidOperationException("walk failed");
            }
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    [Fact]
    public void A_throwing_extras_walk_is_a_missing_reading_not_a_failure()
    {
        var provider = System.Reflection.DispatchProxy.Create<Nexus.Service.Sensors.ISensorProvider, ThrowingSensors>();
        using var feed = new SensorFeed(provider, null, () => _now, background: false);
        var channel = feed.Acquire(new SensorBinding { Category = "cooler", Sensor = "x", Min = 0, Max = 1 });
        _ = channel.Read();

        Assert.True(feed.Tick(_now));

        Assert.Equal(SensorFeed.Missing, channel.Read());
    }

    [Fact]
    public async Task A_throwing_extras_walk_keeps_the_poller_on_its_own_cadence_instead_of_spinning()
    {
        var provider = System.Reflection.DispatchProxy.Create<Nexus.Service.Sensors.ISensorProvider, ThrowingSensors>();
        using var feed = new SensorFeed(provider, null, () => Interlocked.Read(ref _now), pollMs: 50, background: true);
        var channel = feed.Acquire(new SensorBinding { Category = "cooler", Sensor = "x", Min = 0, Max = 1 });
        _ = channel.Read();

        Assert.True(await WaitFor(() => ((ThrowingSensors)(object)provider).Walks >= 2));
        var walks = ((ThrowingSensors)(object)provider).Walks;
        // Passes come one per poll gap, not back to back: at 50 ms a 200 ms window holds a handful, never hundreds.
        await WaitFor(() => ((ThrowingSensors)(object)provider).Walks >= walks + 3);
        Assert.InRange(((ThrowingSensors)(object)provider).Walks, 2, 40);
        Assert.True(feed.IsPolling);
        // The clock never moved, so the rate limit lets one line through however many passes ran.
        Assert.Equal(1, feed.LoggedFailures);
    }

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }
}

public sealed class SensorSmoothingTests
{
    [Fact]
    public void A_zero_or_negative_gap_keeps_the_current_value()
    {
        Assert.Equal(0.2f, SensorSmoothing.Step(0.2f, 1f, 0));
        Assert.Equal(0.2f, SensorSmoothing.Step(0.2f, 1f, -5));
    }

    [Fact]
    public void A_step_moves_toward_the_target_without_overshooting()
    {
        var v = SensorSmoothing.Step(0f, 1f, 16);

        Assert.InRange(v, 0.01f, 0.1f);
        Assert.InRange(SensorSmoothing.Step(1f, 0f, 16), 0.9f, 0.99f);
    }

    [Fact]
    public void One_time_constant_covers_about_63_percent()
    {
        Assert.Equal(0.632f, SensorSmoothing.Step(0f, 1f, SensorSmoothing.TauMs), 2);
    }

    [Fact]
    public void Many_frames_converge_to_the_target()
    {
        var v = 0f;
        for (var i = 0; i < 600; i++)
        {
            v = SensorSmoothing.Step(v, 0.8f, 16);
        }

        Assert.Equal(0.8f, v, 3);
    }

    [Fact]
    public void A_long_gap_jumps_to_the_target()
    {
        Assert.Equal(0.9f, SensorSmoothing.Step(0f, 0.9f, SensorSmoothing.SnapAfterMs));
    }
}

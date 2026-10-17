using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public class LightingFrameCapTests
{
    private const string Key = "dev";

    private static (LightingFrameCap Cap, InMemoryConfigStore Store, Func<long> Now, Action<long> Advance) Make()
    {
        long now = 5_000;
        var store = new InMemoryConfigStore();
        var cap = new LightingFrameCap(store, () => now);
        return (cap, store, () => now, ms => now += ms);
    }

    private static void SetCap(InMemoryConfigStore store, string key, int hz) =>
        store.Update(s => s.Devices.LightingMaxRates[key] = hz);

    [Fact]
    public void Off_passes_every_call_and_leaves_no_state()
    {
        var (cap, store, _, advance) = Make();
        for (var i = 0; i < 200; i++)
        {
            Assert.True(cap.TryWrite(Key));
            advance(1);
        }

        // Turning a cap on afterwards starts from a clean slate: the first frame goes at once.
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
        Assert.False(cap.TryWrite(Key));
    }

    [Fact]
    public void A_cap_on_another_device_does_not_touch_this_one()
    {
        var (cap, store, _, _) = Make();
        SetCap(store, "other", 1);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(cap.TryWrite(Key));
        }
    }

    [Theory]
    [InlineData(10, 33, 10)]
    [InlineData(5, 33, 5)]
    [InlineData(1, 33, 1)]
    [InlineData(20, 33, 20)]
    public void The_average_rate_matches_the_cap_on_a_non_divisible_tick(int hz, int tickMs, int expectedPerSecond)
    {
        var (cap, store, _, advance) = Make();
        SetCap(store, Key, hz);
        var passes = 0;
        var seconds = 10;
        for (var t = 0; t < seconds * 1000; t += tickMs)
        {
            if (cap.TryWrite(Key))
            {
                passes++;
            }
            advance(tickMs);
        }
        var perSecond = passes / (double)seconds;
        Assert.InRange(perSecond, expectedPerSecond * 0.9, expectedPerSecond * 1.1 + 0.2);
    }

    [Theory]
    [InlineData(0, 15, 0)]
    [InlineData(30, 0, 30)]
    [InlineData(10, 15, 10)]
    [InlineData(15, 15, 0)]
    [InlineData(30, 15, 0)]
    [InlineData(5, 0, 5)]
    public void The_effective_rate_is_the_stricter_of_the_cap_and_the_device_limit(int userHz, int builtIn, int expected)
    {
        Assert.Equal(expected, LightingFrameCap.EffectiveHz(userHz, builtIn));
    }

    [Fact]
    public void A_device_limit_stricter_than_the_cap_is_left_to_the_device_layer()
    {
        var (cap, store, _, _) = Make();
        SetCap(store, Key, 30);
        for (var i = 0; i < 20; i++)
        {
            Assert.True(cap.TryWrite(Key, builtInMaxHz: 15));
        }
    }

    [Fact]
    public void A_cap_stricter_than_the_device_limit_gates()
    {
        var (cap, store, _, _) = Make();
        SetCap(store, Key, 5);
        Assert.True(cap.TryWrite(Key, builtInMaxHz: 15));
        Assert.False(cap.TryWrite(Key, builtInMaxHz: 15));
    }

    [Fact]
    public void The_next_frame_after_a_hold_goes_within_one_interval()
    {
        var (cap, store, now, advance) = Make();
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
        var sentAt = now();

        // The caller offers the then-current frame every tick; the first pass lands inside one period.
        long landedAfter = -1;
        for (var elapsed = 33; elapsed <= 1500; elapsed += 33)
        {
            advance(33);
            if (cap.TryWrite(Key))
            {
                landedAfter = now() - sentAt;
                break;
            }
        }
        Assert.InRange(landedAfter, 900, 1033);
    }

    [Fact]
    public void Lowering_the_cap_applies_on_the_next_call_and_raising_it_shortens_the_wait()
    {
        var (cap, store, _, advance) = Make();
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
        advance(150);
        Assert.False(cap.TryWrite(Key));

        SetCap(store, Key, 10);
        Assert.True(cap.TryWrite(Key));
    }

    [Fact]
    public void Clearing_the_cap_passes_at_once()
    {
        var (cap, store, _, _) = Make();
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
        Assert.False(cap.TryWrite(Key));
        store.Update(s => s.Devices.LightingMaxRates.Remove(Key));
        Assert.True(cap.TryWrite(Key));
    }

    [Fact]
    public void Devices_are_gated_independently()
    {
        var (cap, store, _, _) = Make();
        SetCap(store, "a", 1);
        SetCap(store, "b", 1);
        Assert.True(cap.TryWrite("a"));
        Assert.True(cap.TryWrite("b"));
        Assert.False(cap.TryWrite("a"));
        Assert.False(cap.TryWrite("b"));
    }

    [Fact]
    public void One_tick_spends_one_pass_however_many_channels_ask()
    {
        var (cap, store, _, advance) = Make();
        SetCap(store, Key, 5);
        var settings = store.Load();

        var first = new FrameCapTick(cap, Key, 0, settings);
        Assert.True(first.Allow());
        Assert.True(first.Allow());
        Assert.True(first.Allow());

        advance(33);
        var blocked = new FrameCapTick(cap, Key, 0, settings);
        Assert.False(blocked.Allow());
        Assert.False(blocked.Allow());
    }

    [Fact]
    public void A_tick_that_never_asks_spends_no_pass()
    {
        var (cap, store, _, _) = Make();
        SetCap(store, Key, 1);
        var settings = store.Load();
        _ = new FrameCapTick(cap, Key, 0, settings);

        var next = new FrameCapTick(cap, Key, 0, settings);
        Assert.True(next.Allow());
    }

    [Fact]
    public void A_tick_without_a_cap_always_allows()
    {
        var tick = new FrameCapTick(null, Key, 0, null);
        Assert.True(tick.Allow());
    }

    [Fact]
    public void A_changed_cap_starts_a_fresh_schedule()
    {
        var (cap, store, _, advance) = Make();
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
        advance(50);
        Assert.False(cap.TryWrite(Key));

        // Lowered: the old due time must not hold the first frame under the new cap.
        SetCap(store, Key, 5);
        Assert.True(cap.TryWrite(Key));
    }

    [Fact]
    public void A_cleared_then_restored_cap_does_not_inherit_the_old_schedule()
    {
        var (cap, store, _, advance) = Make();
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
        store.Update(s => s.Devices.LightingMaxRates.Remove(Key));
        Assert.True(cap.TryWrite(Key));
        advance(10);
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
    }

    [Fact]
    public void Reset_lets_the_first_frame_of_a_new_connection_through()
    {
        var (cap, store, _, advance) = Make();
        SetCap(store, Key, 1);
        Assert.True(cap.TryWrite(Key));
        advance(50);
        Assert.False(cap.TryWrite(Key));

        cap.Reset(Key);

        Assert.True(cap.TryWrite(Key));
    }

    [Fact]
    public void Reset_with_no_cap_in_use_is_a_no_op()
    {
        var (cap, _, _, _) = Make();
        cap.Reset(Key);
        Assert.True(cap.TryWrite(Key));
    }

    [Fact]
    public void Held_window_times_are_untouched_when_off()
    {
        var ticks = new long[] { 1_000_003, 1_000_036, 1_000_069 };
        Assert.Same(ticks, LightingFrameCap.HoldTicks(ticks, 0));
    }

    [Fact]
    public void Held_window_times_snap_to_the_cap_grid_and_step_at_the_cap_rate()
    {
        // The exact window the wireless writer asks for: one render time per frame of the window.
        var ticks = Slv3LightingFrameWriter.WindowTickTimes(0, 1.6, 1_000_000);
        var held = LightingFrameCap.HoldTicks(ticks, 5);

        Assert.Equal(ticks.Length, held.Length);
        Assert.All(held, t => Assert.Equal(0, t % 200));
        for (var i = 0; i < ticks.Length; i++)
        {
            Assert.InRange(ticks[i] - held[i], 0, 199);
            if (i > 0)
            {
                Assert.True(held[i] >= held[i - 1]);
            }
        }
        // 30 frames span about 0.9 s, so a 5 Hz hold shows about 5 distinct looks.
        Assert.InRange(held.Distinct().Count(), 4, 6);
    }

    [Fact]
    public void Consecutive_held_windows_agree_on_the_grid()
    {
        var a = LightingFrameCap.HoldTicks(new long[] { 1_000_150, 1_000_199 }, 5);
        var b = LightingFrameCap.HoldTicks(new long[] { 1_000_199, 1_000_200 }, 5);
        Assert.Equal(a[1], b[0]);
        Assert.Equal(1_000_200, b[1]);
    }

    [Fact]
    public void The_hydroshift_loop_times_hold_at_the_cap_rate()
    {
        var start = 5_000_000L;
        var ticks = new long[HydroShift2LightingFrameWriter.LoopMatchFrames + HydroShift2LightingFrameWriter.LoopMaxFrames];
        for (var k = 0; k < ticks.Length; k++)
        {
            ticks[k] = start + (long)(k * HydroShift2LightingFrameWriter.LoopIntervalTicks * Nexus.Service.Peripherals.BulkPanels.HydroShift2Protocol.RingTickMs);
        }
        var held = LightingFrameCap.HoldTicks(ticks, 1);
        Assert.All(held, t => Assert.Equal(0, t % 1000));
        Assert.True(held.Distinct().Count() < ticks.Length);
        Assert.Same(ticks, LightingFrameCap.HoldTicks(ticks, 0));
    }

    [Fact]
    public void IsCapped_reports_only_a_positive_cap_for_the_key()
    {
        var (cap, store, _, _) = Make();
        Assert.False(cap.IsCapped(Key, store.Load()));
        SetCap(store, Key, 10);
        Assert.True(cap.IsCapped(Key, store.Load()));
        Assert.False(cap.IsCapped("other", store.Load()));
    }
}

public class LightingFrameCapCardTests
{
    private static readonly HashSet<string> NoExclusions = new();

    [Fact]
    public void A_first_party_card_is_offered_the_cap_and_reads_its_owning_devices_rate()
    {
        var rates = new Dictionary<string, int> { ["ibp:km7"] = 10 };
        var zoneA = new Nexus.Service.Models.Devices.LightingDevice { Id = "ibp:km7:leds", ParentDeviceId = "ibp:km7" };
        var zoneB = new Nexus.Service.Models.Devices.LightingDevice { Id = "ibp:km7:zone2", ParentDeviceId = "ibp:km7" };
        var other = new Nexus.Service.Models.Devices.LightingDevice { Id = "cnvs" };

        foreach (var card in new[] { zoneA, zoneB, other })
        {
            CompositeLightingDeviceProvider.MarkRateCap(card, NoExclusions, rates);
        }

        Assert.True(zoneA.RateCapSupported);
        Assert.Equal(10, zoneA.MaxRateHz);
        Assert.Equal(10, zoneB.MaxRateHz);
        Assert.True(other.RateCapSupported);
        Assert.Null(other.MaxRateHz);
    }

    [Fact]
    public void A_standalone_card_is_keyed_by_its_own_id()
    {
        var card = new Nexus.Service.Models.Devices.LightingDevice { Id = "cnvs" };
        CompositeLightingDeviceProvider.MarkRateCap(card, NoExclusions, new Dictionary<string, int> { ["cnvs"] = 5 });
        Assert.Equal(5, card.MaxRateHz);
    }

    [Fact]
    public void An_excluded_card_is_not_offered_the_cap()
    {
        var card = new Nexus.Service.Models.Devices.LightingDevice { Id = "lamp" };
        CompositeLightingDeviceProvider.MarkRateCap(card, new HashSet<string> { "lamp" }, new Dictionary<string, int> { ["lamp"] = 5 });
        Assert.False(card.RateCapSupported);
        Assert.Null(card.MaxRateHz);
    }

    [Fact]
    public void An_openrgb_card_keeps_the_values_it_already_has()
    {
        var card = new Nexus.Service.Models.Devices.LightingDevice { Id = "openrgb-s-MB-0", ParentDeviceId = "openrgb-s-MB", RateCapSupported = true, MaxRateHz = 15 };
        CompositeLightingDeviceProvider.MarkRateCap(card, NoExclusions, new Dictionary<string, int>());
        Assert.Equal(15, card.MaxRateHz);
    }
}

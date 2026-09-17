using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxSlideshowTests
{
    private static readonly string[] Library = ["a.mp4", "b.mp4", "c.mp4"];

    private static TryxSlideshow Build(Func<string, double>? duration = null, Func<double>? random = null)
        => new(duration ?? (_ => 0), random);

    private static TryxSlideshowConfig On(int intervalSec = 10, bool shuffle = false, bool finishVideos = true) => new()
    {
        Enabled = true,
        IntervalSec = intervalSec,
        Shuffle = shuffle,
        FinishVideos = finishVideos,
    };

    [Fact]
    public void Disabled_never_advances()
    {
        var s = Build();
        s.Configure(new TryxSlideshowConfig { Enabled = false }, "a.mp4", Library, 0);

        Assert.Null(s.Tick(() => Library, 1_000_000));
    }

    [Fact]
    public void Holds_the_current_clip_for_the_interval_then_advances_in_library_order()
    {
        var s = Build();
        s.Configure(On(intervalSec: 10), "a.mp4", Library, 0);

        Assert.Null(s.Tick(() => Library, 9_999));
        Assert.Equal("b.mp4", s.Tick(() => Library, 10_000));
        s.Rearm("b.mp4", 10_000);
        Assert.Equal("c.mp4", s.Tick(() => Library, 20_000));
        s.Rearm("c.mp4", 20_000);
        Assert.Equal("a.mp4", s.Tick(() => Library, 30_000));
    }

    [Fact]
    public void Enabling_while_a_preset_shows_advances_on_the_next_tick()
    {
        var s = Build();
        s.Configure(On(), "default_01.mp4.h264_2240x1080", Library, 5_000);

        Assert.Equal("a.mp4", s.Tick(() => Library, 5_000));
    }

    [Fact]
    public void Enabling_while_a_library_clip_shows_gives_it_a_full_hold()
    {
        var s = Build();
        s.Configure(On(intervalSec: 10), "b.mp4", Library, 5_000);

        Assert.Null(s.Tick(() => Library, 14_999));
        Assert.Equal("c.mp4", s.Tick(() => Library, 15_000));
    }

    [Fact]
    public void Reconfiguring_an_enabled_slideshow_never_cuts_a_preset_short()
    {
        var s = Build();
        s.Configure(On(intervalSec: 10), "a.mp4", Library, 0);
        s.Rearm("default_01.mp4.h264_2240x1080", 3_000);

        s.Configure(On(intervalSec: 30), "default_01.mp4.h264_2240x1080", Library, 4_000);

        Assert.Null(s.Tick(() => Library, 33_999));
        Assert.Equal("a.mp4", s.Tick(() => Library, 34_000));
    }

    [Fact]
    public void A_manual_pick_restarts_the_hold()
    {
        var s = Build();
        s.Configure(On(intervalSec: 10), "a.mp4", Library, 0);
        s.Rearm("c.mp4", 8_000);

        Assert.Null(s.Tick(() => Library, 17_999));
        Assert.Equal("a.mp4", s.Tick(() => Library, 18_000));
    }

    [Fact]
    public void Fewer_than_two_clips_never_advances()
    {
        var s = Build();
        s.Configure(On(), "default_01.mp4.h264_2240x1080", ["a.mp4"], 0);

        Assert.Null(s.Tick(() => ["a.mp4"], 100_000));
        Assert.Null(s.Tick(() => [], 200_000));
    }

    [Fact]
    public void Library_is_not_read_until_the_hold_expires()
    {
        var s = Build();
        s.Configure(On(intervalSec: 10), "a.mp4", Library, 0);
        var reads = 0;

        s.Tick(() => { reads++; return Library; }, 5_000);

        Assert.Equal(0, reads);
    }

    [Fact]
    public void Finish_videos_holds_whole_plays_until_the_interval_has_passed()
    {
        // 3 s clip on a 10 s interval: four plays (12 s), like the gallery's ended count.
        var s = Build(duration: name => name == "a.mp4" ? 3 : 0);
        s.Configure(On(intervalSec: 10), "a.mp4", Library, 0);

        Assert.Null(s.Tick(() => Library, 11_999));
        Assert.Equal("b.mp4", s.Tick(() => Library, 12_000));
    }

    [Fact]
    public void Finish_videos_plays_a_long_clip_once()
    {
        var s = Build(duration: _ => 20);
        s.Configure(On(intervalSec: 5), "a.mp4", Library, 0);

        Assert.Null(s.Tick(() => Library, 19_999));
        Assert.Equal("b.mp4", s.Tick(() => Library, 20_000));
    }

    [Fact]
    public void Finish_videos_off_cuts_at_the_interval()
    {
        var s = Build(duration: _ => 20);
        s.Configure(On(intervalSec: 5, finishVideos: false), "a.mp4", Library, 0);

        Assert.Equal("b.mp4", s.Tick(() => Library, 5_000));
    }

    [Fact]
    public void Unknown_duration_holds_for_the_plain_interval()
    {
        var s = Build(duration: _ => 0);
        s.Configure(On(intervalSec: 10), "a.mp4", Library, 0);

        Assert.Null(s.Tick(() => Library, 9_999));
        Assert.Equal("b.mp4", s.Tick(() => Library, 10_000));
    }

    [Fact]
    public void A_failed_select_is_retried_one_interval_later_not_every_tick()
    {
        var s = Build();
        s.Configure(On(intervalSec: 10), "a.mp4", Library, 0);

        Assert.Equal("b.mp4", s.Tick(() => Library, 10_000));
        Assert.Null(s.Tick(() => Library, 11_000));
        Assert.Equal("b.mp4", s.Tick(() => Library, 20_000));
    }

    [Fact]
    public void A_current_clip_missing_from_the_library_restarts_from_the_top()
    {
        var s = Build();
        s.Configure(On(intervalSec: 10), "deleted.mp4", Library, 0);
        s.Rearm("deleted.mp4", 0);

        Assert.Equal("a.mp4", s.Tick(() => Library, 10_000));
    }

    [Fact]
    public void Interval_is_clamped_to_the_supported_range()
    {
        var s = Build();
        s.Configure(On(intervalSec: 1), "a.mp4", Library, 0);
        Assert.Equal(TryxSlideshowConfig.MinIntervalSec, s.Config.IntervalSec);

        s.Configure(On(intervalSec: 999_999), "a.mp4", Library, 0);
        Assert.Equal(TryxSlideshowConfig.MaxIntervalSec, s.Config.IntervalSec);
    }

    [Fact]
    public void Shuffle_shows_every_clip_once_per_lap_and_never_repeats_across_the_boundary()
    {
        var s = Build(random: new Random(7).NextDouble);
        s.Configure(On(intervalSec: 10, shuffle: true), "a.mp4", Library, 0);
        var current = "a.mp4";
        var shown = new List<string>();
        var now = 0L;
        for (var i = 0; i < 9; i++)
        {
            now += 10_000;
            var next = s.Tick(() => Library, now);
            Assert.NotNull(next);
            Assert.NotEqual(current, next);
            shown.Add(next!);
            s.Rearm(next!, now);
            current = next!;
        }

        foreach (var lap in shown.Chunk(3))
        {
            Assert.Equal(Library.OrderBy(n => n), lap.OrderBy(n => n));
        }
    }

    [Fact]
    public void Shuffle_rebuilds_the_lap_when_the_library_changes()
    {
        var s = Build(random: new Random(3).NextDouble);
        s.Configure(On(intervalSec: 10, shuffle: true), "a.mp4", Library, 0);
        var first = s.Tick(() => Library, 10_000)!;
        s.Rearm(first, 10_000);

        var grown = Library.Append("d.mp4").ToArray();
        var seen = new HashSet<string> { first };
        var now = 10_000L;
        for (var i = 0; i < 4; i++)
        {
            now += 10_000;
            var next = s.Tick(() => grown, now)!;
            seen.Add(next);
            s.Rearm(next, now);
        }

        Assert.Contains("d.mp4", seen);
    }

    [Fact]
    public void ShuffledLap_moves_the_avoided_clip_off_the_front()
    {
        // A random source that leaves the order untouched keeps "a.mp4" first.
        var s = Build(random: () => 0.999);
        var lap = s.ShuffledLap(Library, "a.mp4");

        Assert.Equal(3, lap.Count);
        Assert.NotEqual("a.mp4", lap[0]);
        Assert.Equal(Library.OrderBy(n => n), lap.OrderBy(n => n));
    }
}

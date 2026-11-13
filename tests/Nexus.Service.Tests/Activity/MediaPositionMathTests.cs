using System;
using System.Collections.Generic;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class MediaPositionMathTests
{
    private const double Slack = MediaTopicPublisher.PositionSlackMs;

    private static bool Push(
        bool pushedPlaying, double pushed, double pushedDuration, double rate, double sinceSec,
        bool playing, double duration, double live)
        => MediaPositionMath.ShouldPush(
            pushedPlaying, pushed, pushedDuration, rate, TimeSpan.FromSeconds(sinceSec), playing, duration, live, Slack);

    [Theory]
    [InlineData(60_000, 5, 65_000, false)]
    [InlineData(60_000, 5, 66_400, false)]
    [InlineData(60_000, 5, 120_000, true)]
    [InlineData(60_000, 5, 10_000, true)]
    [InlineData(60_000, 5, 60_000, true)]
    public void ShouldPush_PlayingFlagsSeeksOnly(double pushed, int sinceSec, double live, bool push)
    {
        Assert.Equal(push, Push(true, pushed, 200_000, 1.0, sinceSec, true, 200_000, live));
    }

    [Fact]
    public void ShouldPush_PredictionClampsToDuration()
    {
        Assert.False(Push(true, 195_000, 200_000, 1.0, 30, true, 200_000, 200_000));
    }

    [Fact]
    public void ShouldPush_NoDurationNeverAdvances()
    {
        Assert.False(Push(true, 5_000, 0, 1.0, 30, true, 0, 5_000));
        Assert.True(Push(true, 5_000, 0, 1.0, 30, true, 0, 60_000));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldPush_PlayStateChangePushes(bool pushedPlaying, bool playing)
    {
        Assert.True(Push(pushedPlaying, 5_000, 200_000, 1.0, 0, playing, 200_000, 5_000));
    }

    [Theory]
    [InlineData(5_000, false)]
    [InlineData(5_800, false)]
    [InlineData(90_000, true)]
    [InlineData(1_000, true)]
    public void ShouldPush_PausedHoldsStillAndCatchesSeek(double live, bool push)
    {
        Assert.Equal(push, Push(false, 5_000, 200_000, 0, 60, false, 200_000, live));
    }

    [Fact]
    public void ShouldPush_SteadyNonUnityRateStaysQuiet()
    {
        Assert.False(Push(true, 60_000, 200_000, 2.0, 30, true, 200_000, 120_000));
        Assert.True(Push(true, 60_000, 200_000, 2.0, 30, true, 200_000, 90_000));
    }

    [Fact]
    public void ShouldPush_FrozenLiveReadStaysQuietWhenAnchoredAtZeroRate()
    {
        Assert.False(Push(true, 60_000, 200_000, 0, 600, true, 200_000, 60_000));
    }

    [Fact]
    public void ShouldPush_DurationChangePushes()
    {
        Assert.True(Push(true, 5_000, 0, 1.0, 1, true, 200_000, 6_000));
        Assert.True(Push(true, 5_000, 200_000, 1.0, 1, true, 210_000, 6_000));
    }

    [Fact]
    public void EffectiveAdvanceRate_ZeroWhenStampUnusableOrRateNotPositive()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1.5, MediaPositionMath.EffectiveAdvanceRate(now.AddSeconds(-5), now, 1.5));
        Assert.Equal(0, MediaPositionMath.EffectiveAdvanceRate(default, now, 1.0));
        Assert.Equal(0, MediaPositionMath.EffectiveAdvanceRate(now.AddDays(-3), now, 1.0));
        Assert.Equal(0, MediaPositionMath.EffectiveAdvanceRate(now.AddSeconds(-5), now, 0));
    }

    [Fact]
    public void Advance_AddsElapsedTime()
    {
        var result = MediaPositionMath.Advance(10_000, 200_000, TimeSpan.FromSeconds(5));
        Assert.Equal(15_000, result);
    }

    [Fact]
    public void Advance_ClampsToDuration()
    {
        var result = MediaPositionMath.Advance(195_000, 200_000, TimeSpan.FromSeconds(30));
        Assert.Equal(200_000, result);
    }

    [Fact]
    public void Advance_UnknownDurationDoesNotClamp()
    {
        var result = MediaPositionMath.Advance(195_000, 0, TimeSpan.FromSeconds(30));
        Assert.Equal(225_000, result);
    }

    [Fact]
    public void Advance_NegativeElapsedIsIgnored()
    {
        var result = MediaPositionMath.Advance(10_000, 200_000, TimeSpan.FromSeconds(-5));
        Assert.Equal(10_000, result);
    }

    [Fact]
    public void Advance_ScalesByPlaybackRate()
    {
        var result = MediaPositionMath.Advance(10_000, 200_000, TimeSpan.FromSeconds(10), playbackRate: 2.0);
        Assert.Equal(30_000, result);
    }

    [Fact]
    public void Advance_StalledSourceDoesNotAdvance()
    {
        var result = MediaPositionMath.Advance(10_000, 200_000, TimeSpan.FromSeconds(10), playbackRate: 0);
        Assert.Equal(10_000, result);
    }

    [Fact]
    public void SinceTimelineStamp_ReturnsDelta()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = MediaPositionMath.SinceTimelineStamp(now.AddSeconds(-42), now);
        Assert.Equal(TimeSpan.FromSeconds(42), elapsed);
    }

    [Fact]
    public void SinceTimelineStamp_FutureStampIsZero()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.Zero, MediaPositionMath.SinceTimelineStamp(now.AddSeconds(5), now));
    }

    [Fact]
    public void SinceTimelineStamp_GarbageStampIsZero()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.Zero, MediaPositionMath.SinceTimelineStamp(DateTimeOffset.MinValue, now));
    }

    private static MediaSession Session(bool playing, double positionMs, double durationMs, bool stopped = false, double rate = 1.0) => new()
    {
        SourceAppName = "Spotify",
        Song = new MediaSong { Title = "Track", Artist = "Artist", Album = "Album" },
        Playback = new MediaPlayback
        {
            Playing = playing,
            Stopped = stopped,
            PositionMs = positionMs,
            DurationMs = durationMs,
            PlaybackRate = rate,
            RepeatMode = "List",
            Shuffled = true,
        },
        Controls = new MediaControls { IsSeekEnabled = true },
    };

    [Fact]
    public void AdvanceSnapshot_AdvancesOnlyPlayingSessions()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = Session(playing: true, positionMs: 10_000, durationMs: 200_000),
            ["Music"] = Session(playing: false, positionMs: 30_000, durationMs: 100_000),
        };

        var served = MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromSeconds(4));

        Assert.Equal(14_000, served["Spotify"].Playback.PositionMs);
        Assert.Equal(30_000, served["Music"].Playback.PositionMs);
        Assert.Same(snapshot["Music"], served["Music"]);
    }

    [Fact]
    public void AdvanceSnapshot_NeverMutatesTheCachedSnapshot()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = Session(playing: true, positionMs: 10_000, durationMs: 200_000),
        };

        MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromSeconds(4));
        var second = MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromSeconds(4));

        Assert.Equal(10_000, snapshot["Spotify"].Playback.PositionMs);
        Assert.Equal(14_000, second["Spotify"].Playback.PositionMs);
    }

    [Fact]
    public void AdvanceSnapshot_StoppedSessionIsNotAdvanced()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = Session(playing: true, positionMs: 10_000, durationMs: 200_000, stopped: true),
        };

        var served = MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromSeconds(4));
        Assert.Equal(10_000, served["Spotify"].Playback.PositionMs);
    }

    [Fact]
    public void AdvanceSnapshot_SessionWithoutDurationIsNotAdvanced()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["Radio"] = Session(playing: true, positionMs: 10_000, durationMs: 0),
        };

        var served = MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromHours(3));
        Assert.Equal(10_000, served["Radio"].Playback.PositionMs);
    }

    [Fact]
    public void AdvanceSnapshot_HonoursPlaybackRate()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["Podcasts"] = Session(playing: true, positionMs: 10_000, durationMs: 900_000, rate: 1.5),
        };

        var served = MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromSeconds(10));
        Assert.Equal(25_000, served["Podcasts"].Playback.PositionMs);
    }

    [Fact]
    public void AdvanceSnapshot_NeverExceedsTheTrackHoweverStaleTheSnapshot()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = Session(playing: true, positionMs: 10_000, durationMs: 200_000),
        };

        var served = MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromDays(2));
        Assert.Equal(200_000, served["Spotify"].Playback.PositionMs);
    }

    [Fact]
    public void AdvanceSnapshot_KeepsTheSourceComparer()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.Ordinal)
        {
            ["Spotify"] = Session(playing: true, positionMs: 10_000, durationMs: 200_000),
        };

        var served = MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.FromSeconds(4));
        Assert.Same(StringComparer.Ordinal, served.Comparer);
        Assert.False(served.ContainsKey("spotify"));
    }

    [Fact]
    public void AdvanceSnapshot_ZeroElapsedReturnsSnapshotUnchanged()
    {
        var snapshot = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = Session(playing: true, positionMs: 10_000, durationMs: 200_000),
        };

        Assert.Same(snapshot, MediaPositionMath.AdvanceSnapshot(snapshot, TimeSpan.Zero));
    }

    // Reflection over both types, so a field added later is covered without
    // editing this test - the hazard a hand-written copy would reintroduce.
    [Fact]
    public void WithPositionMs_CopiesEveryFieldExceptPosition()
    {
        var original = Session(playing: true, positionMs: 10_000, durationMs: 200_000, rate: 1.25);
        original.IsFocused = true;
        original.TextColor = "#fff";
        original.ColorPalette.Add(new ColorRgba { R = 1, G = 2, B = 3 });

        var copy = original.WithPositionMs(55_000);

        Assert.Equal(55_000, copy.Playback.PositionMs);
        Assert.NotSame(original.Playback, copy.Playback);
        Assert.Equal(10_000, original.Playback.PositionMs);

        foreach (var prop in typeof(MediaSession).GetProperties())
        {
            if (prop.Name == nameof(MediaSession.Playback)) continue;
            Assert.Equal(prop.GetValue(original), prop.GetValue(copy));
        }
        foreach (var prop in typeof(MediaPlayback).GetProperties())
        {
            if (prop.Name == nameof(MediaPlayback.PositionMs)) continue;
            Assert.Equal(prop.GetValue(original.Playback), prop.GetValue(copy.Playback));
        }
    }
}

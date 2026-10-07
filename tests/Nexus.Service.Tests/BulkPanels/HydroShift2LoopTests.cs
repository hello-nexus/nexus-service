using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class HydroShift2LoopTests
{
    private const int FrameBytes = HydroShift2Protocol.RingLedCount * 3;
    private static readonly int Total = HydroShift2LightingFrameWriter.LoopMatchFrames + HydroShift2LightingFrameWriter.LoopMaxFrames;

    private static List<byte[]> Render(Func<int, int, byte> channel) =>
        Enumerable.Range(0, Total)
            .Select(f => Enumerable.Range(0, FrameBytes).Select(c => channel(f, c)).ToArray())
            .ToList();

    [Fact]
    public void A_repeating_effect_loops_on_its_own_period()
    {
        var frames = Render((f, c) => (byte)(((f % 37) * 7) + c));

        var (packed, count) = HydroShift2LightingFrameWriter.FitLoop(frames);

        Assert.Equal(37, count);
        Assert.Equal(frames[HydroShift2LightingFrameWriter.LoopMatchFrames], packed[..FrameBytes]);
    }

    [Fact]
    public void An_effect_that_never_repeats_fades_its_end_into_the_pre_roll()
    {
        var frames = Render((f, c) => (byte)Math.Min(255, f + c));

        var (packed, count) = HydroShift2LightingFrameWriter.FitLoop(frames);

        Assert.Equal(HydroShift2LightingFrameWriter.LoopMaxFrames, count);
        var last = packed[^FrameBytes..];
        var preRollEnd = frames[HydroShift2LightingFrameWriter.LoopMatchFrames - 1];
        Assert.True(Math.Abs(last[0] - preRollEnd[0]) <= 1 + (frames[^1][0] - preRollEnd[0]) / HydroShift2LightingFrameWriter.LoopMatchFrames);
    }

    [Fact]
    public void A_slow_drift_is_not_taken_for_a_short_period()
    {
        var frames = Render((f, c) => (byte)(f / 3));

        var (_, count) = HydroShift2LightingFrameWriter.FitLoop(frames);

        Assert.Equal(HydroShift2LightingFrameWriter.LoopMaxFrames, count);
    }

    [Fact]
    public void A_dim_repeating_effect_is_matched_on_its_raw_colours()
    {
        var source = Render((f, c) => (byte)(((f % 37) * 7) + c));
        var dimmed = source.Select(fr => fr.Select(b => (byte)(b / 16)).ToArray()).ToList();

        var (_, count) = HydroShift2LightingFrameWriter.FitLoop(source, dimmed);

        Assert.Equal(37, count);
    }

    [Fact]
    public void A_loop_too_big_for_one_upload_halves_its_frame_rate()
    {
        var packed = new byte[320 * FrameBytes];
        new Random(3).NextBytes(packed);

        var (frames, count, interval) = HydroShift2LightingFrameWriter.FitUpload(packed, 320, HydroShift2LightingFrameWriter.LoopIntervalTicks);

        Assert.True(count < 320);
        Assert.Equal(count * FrameBytes, frames.Length);
        var lengthTicks = 320 * HydroShift2LightingFrameWriter.LoopIntervalTicks;
        Assert.InRange(count * interval, lengthTicks, lengthTicks + interval);
        HydroShift2Protocol.EncodeRing(frames, count, interval, 1);
    }

    [Fact]
    public void The_longest_loop_of_random_colours_fits_one_upload()
    {
        var random = new Random(7);
        var packed = new byte[HydroShift2LightingFrameWriter.LoopMaxFrames * FrameBytes];
        random.NextBytes(packed);

        var packet = HydroShift2Protocol.EncodeRing(packed, HydroShift2LightingFrameWriter.LoopMaxFrames, HydroShift2LightingFrameWriter.LoopIntervalTicks, 1);

        Assert.True(packet.Length > 512);
    }
}

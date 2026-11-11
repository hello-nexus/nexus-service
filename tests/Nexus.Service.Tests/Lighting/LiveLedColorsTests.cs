using System;
using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Xunit;
using CnvsColor = Nexus.Service.Peripherals.Hyte.Cnvs.RgbColor;
using MiniHubColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;
using Np50Color = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;
using SmartHubColor = Nexus.Service.Peripherals.Hyte.SmartHub.RgbColor;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// The dashboard preview must show what each writer drives, before the channel
/// swap. Each writer family's own filler runs with an RGB-order trim to get the
/// reference, and with the real order to prove the writer output is unchanged
/// (the reference, permuted).
/// </summary>
public class LiveLedColorsTests
{
    private const int Leds = 7;

    private static byte[] Source()
    {
        var src = new byte[Leds * 3];
        for (var i = 0; i < src.Length; i++)
        {
            src[i] = (byte)((i * 37 + 11) % 256);
        }
        return src;
    }

    public static IEnumerable<object?[]> Cases()
    {
        foreach (var mul in new[] { 1.0, 0.5, 0.37, 0.0 })
        {
            foreach (var trim in new[] { false, true })
            {
                foreach (var order in new[] { "RGB", "GRB", "BGR", "BRG" })
                {
                    if (!trim && order != "RGB")
                    {
                        continue;
                    }
                    yield return new object?[] { mul, trim, order };
                }
            }
        }
    }

    private static DeviceColorAdjust Adjust(bool trim, string order) =>
        trim
            ? DeviceColorAdjust.Create(1.2f, 0.9f, 0.7f, 0.3f, 1.4f, order)
            : DeviceColorAdjust.Create(1f, 1f, 1f, 0f, 1f, order);

    private static byte[] Sink(byte[] src, double mul, DeviceColorAdjust adjust, LiveScale scale = LiveScale.Banded)
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.Report("dev", src, Leds, mul, adjust, scale);
        return sink.TryGetFresh("dev")!;
    }

    private static byte[] Permute(byte[] rgb, string order)
    {
        var o = (byte[])rgb.Clone();
        for (var i = 0; i < o.Length; i += 3)
        {
            DeviceColorAdjust.ReorderChannels(order, ref o[i], ref o[i + 1], ref o[i + 2]);
        }
        return o;
    }

    // Runs one writer family's filler and returns its wire bytes as RGB triplets.
    private static void AssertFamily(Func<DeviceColorAdjust, byte[]> writer, double mul, bool trim, string order, LiveScale scale = LiveScale.Banded)
    {
        var src = Source();
        var live = Sink(src, mul, Adjust(trim, order), scale);
        var unordered = writer(Adjust(trim, "RGB"));
        Assert.Equal(unordered, live);
        Assert.Equal(Permute(unordered, order), writer(Adjust(trim, order)));
    }

    private static byte[] Flatten<T>(T[] colors, Func<T, (byte, byte, byte)> split)
    {
        var o = new byte[colors.Length * 3];
        for (var i = 0; i < colors.Length; i++)
        {
            var (r, g, b) = split(colors[i]);
            o[i * 3] = r;
            o[i * 3 + 1] = g;
            o[i * 3 + 2] = b;
        }
        return o;
    }

    [Theory, MemberData(nameof(Cases))]
    public void Np50_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new Np50Color[Leds];
            Np50LightingFrameWriter.FillBufferSlice(dst, 0, Source(), Leds, mul, a, false, 0, 0);
            return Flatten(dst, c => (c.R, c.G, c.B));
        }, mul, trim, order);

    [Theory, MemberData(nameof(Cases))]
    public void MiniHub_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new MiniHubColor[Leds];
            MiniHubLightingFrameWriter.FillBufferSlice(dst, 0, Source(), Leds, mul, a, false, 0, 0);
            return Flatten(dst, c => (c.R, c.G, c.B));
        }, mul, trim, order);

    [Theory, MemberData(nameof(Cases))]
    public void QSeries_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new MiniHubColor[Leds];
            QSeriesLightingFrameWriter.FillBufferSlice(dst, 0, Source(), Leds, mul, a, false, 0, 0);
            return Flatten(dst, c => (c.R, c.G, c.B));
        }, mul, trim, order);

    [Theory, MemberData(nameof(Cases))]
    public void SmartHub_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new SmartHubColor[Leds];
            SmartHubLightingFrameWriter.FillBufferSlice(dst, 0, Source(), Leds, mul, a, false, 0, 0);
            return Flatten(dst, c => (c.R, c.G, c.B));
        }, mul, trim, order);

    [Theory, MemberData(nameof(Cases))]
    public void Cnvs_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new CnvsColor[Leds];
            CnvsLightingFrameWriter.FillBufferSlice(dst, Source(), Leds, mul, a, false, 0, 0);
            return Flatten(dst, c => (c.R, c.G, c.B));
        }, mul, trim, order);

    private static DeviceFrame FrameWith(byte[] src)
    {
        var frame = new DeviceFrame(0, "f", Leds);
        for (var i = 0; i < Leds; i++)
        {
            frame.SetLed(i, src[i * 3], src[i * 3 + 1], src[i * 3 + 2]);
        }
        frame.Publish();
        return frame;
    }

    [Theory, MemberData(nameof(Cases))]
    public void Nollie_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new byte[Leds * 3];
            NollieLightingFrameWriter.FillSlice(dst, 0, Leds, FrameWith(Source()), mul, a, false, 0, 0);
            return dst;
        }, mul, trim, order);

    [Theory, MemberData(nameof(Cases))]
    public void Kraken_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new byte[Leds * 3];
            KrakenLightingFrameWriter.FillSlice(dst, 0, Leds, FrameWith(Source()), mul, a, false, 0, 0);
            return dst;
        }, mul, trim, order);

    [Theory, MemberData(nameof(Cases))]
    public void Y70Backlight_DashboardMatchesWriter(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new byte[Leds * 3];
            if (mul > 0.0)
            {
                Y70GtLightingFrameWriter.Fill(dst, Source(), Leds, mul, a);
            }
            return dst;
        }, mul, trim, order, LiveScale.Direct);

    // The ring and fan writers run the trim math even with no trim set, so a near-full level must not take the pass-through band.
    [Theory, MemberData(nameof(Cases))]
    public void AdjustScale_MatchesApplyWithoutTheBand(double mul, bool trim, string order) =>
        AssertFamily(a =>
        {
            var dst = new byte[Leds * 3];
            var src = Source();
            for (var i = 0; i < Leds; i++)
            {
                a.Apply(src[i * 3], src[i * 3 + 1], src[i * 3 + 2], mul, out dst[i * 3], out dst[i * 3 + 1], out dst[i * 3 + 2]);
            }
            return dst;
        }, mul, trim, order, LiveScale.Adjust);

    [Fact]
    public void DirectScale_HasNoPassThroughBand()
    {
        // 0.9995 is inside the banded pass-through but the Y70 writer still scales.
        Assert.Equal((byte)(255 * 0.9995), Sink(new byte[Leds * 3].Select(_ => (byte)255).ToArray(), 0.9995, DeviceColorAdjust.Identity, LiveScale.Direct)[0]);
        Assert.Equal(255, Sink(new byte[Leds * 3].Select(_ => (byte)255).ToArray(), 0.9995, DeviceColorAdjust.Identity)[0]);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.999)]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public void OpenRgbIdentityScale_MatchesDashboard(double mul)
    {
        var src = Source();
        var live = Sink(src, mul, DeviceColorAdjust.Identity);
        var expected = new byte[Leds * 3];
        for (var i = 0; i < Leds; i++)
        {
            var c = new Nexus.Service.Lighting.Rgb.RgbColor(src[i * 3], src[i * 3 + 1], src[i * 3 + 2]);
            c = mul >= 0.999 ? c : mul <= 0.0 ? Nexus.Service.Lighting.Rgb.RgbColor.Black : c.Scale(mul);
            expected[i * 3] = c.R;
            expected[i * 3 + 1] = c.G;
            expected[i * 3 + 2] = c.B;
        }
        Assert.Equal(expected, live);
    }

    [Fact]
    public void Identify_ReportsTheFlashColor()
    {
        var sink = new LiveLedSink { Enabled = true };
        var start = DateTime.UtcNow.Ticks;
        sink.Report("dev", Source(), Leds, 0.2, DeviceColorAdjust.Identity, true, start, start, 250);
        Assert.All(sink.TryGetFresh("dev")!, b => Assert.Equal(255, b));
        sink.Report("dev", Source(), Leds, 0.2, DeviceColorAdjust.Identity, true, start, start + 300 * TimeSpan.TicksPerMillisecond, 250);
        Assert.All(sink.TryGetFresh("dev")!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void DisabledSink_StoresNothing()
    {
        var sink = new LiveLedSink();
        sink.Report("dev", Source(), Leds, 1.0, DeviceColorAdjust.Identity);
        Assert.Null(sink.TryGetFresh("dev"));
    }

    [Fact]
    public void ApplyUnordered_IsApplyBeforeTheSwap()
    {
        var adjust = DeviceColorAdjust.Create(1.2f, 0.9f, 0.7f, 0.3f, 1.4f, "GBR");
        adjust.ApplyUnordered(200, 100, 50, 0.8, out var ur, out var ug, out var ub);
        adjust.Apply(200, 100, 50, 0.8, out var r, out var g, out var b);
        Assert.Equal((ug, ub, ur), (r, g, b));
    }

    private static byte[] WireFrame(DeviceFrame[] devices, byte[] canvas)
    {
        var list = new List<byte> { 0x03, 1, 0, 1, 0 };
        list.AddRange(canvas);
        list.Add((byte)devices.Length);
        foreach (var d in devices)
        {
            list.Add(DeviceFrameIndex.WireByte(d));
            list.Add((byte)(d.LedCount & 0xFF));
            list.Add((byte)(d.LedCount >> 8));
            list.AddRange(d.LedBytes.ToArray());
        }
        return list.ToArray();
    }

    // Each device section's LED bytes, in frame order.
    private static List<byte[]> Parse(byte[] frame, int length)
    {
        var sections = new List<byte[]>();
        var pos = 5 + (frame[1] | (frame[2] << 8)) * (frame[3] | (frame[4] << 8)) * 3;
        var count = frame[pos++];
        for (var i = 0; i < count; i++)
        {
            var n = (frame[pos + 1] | (frame[pos + 2] << 8)) * 3;
            pos += 3;
            sections.Add(frame[pos..(pos + n)]);
            pos += n;
        }
        Assert.Equal(length, pos);
        return sections;
    }

    [Fact]
    public void Composer_ReportedAndUnreportedDevices()
    {
        var src = Source();
        var driven = FrameWith(src);
        var silent = new DeviceFrame(2, "silent", Leds);
        var cleared = new DeviceFrame(3, "cleared", Leds);
        var devices = new[] { driven, silent, cleared };
        var sink = new LiveLedSink { Enabled = true };
        sink.Report("f", driven.LedBytes, Leds, 0.5, DeviceColorAdjust.Identity);
        sink.Report("cleared", src, Leds, 1.0, DeviceColorAdjust.Identity);
        sink.ReportNone("cleared");

        var dst = Array.Empty<byte>();
        var length = LiveLedFrameComposer.Compose(WireFrame(devices, new byte[3]), devices, sink, ref dst);
        var parsed = Parse(dst, length);

        Assert.Equal(Sink(src, 0.5, DeviceColorAdjust.Identity), parsed[0]);
        // No writer reports for these, so the dashboard gets a zero-length section.
        Assert.Empty(parsed[1]);
        Assert.Empty(parsed[2]);
    }

    [Fact]
    public void Composer_StaleReportIsNotDriven_AndBlackStaysBlack()
    {
        var src = Source();
        var devices = new[] { FrameWith(src) };
        var clock = 0L;
        var sink = new LiveLedSink { Enabled = true, Clock = () => clock };
        sink.Report("f", src, Leds, 0.0, DeviceColorAdjust.Identity);
        var wire = WireFrame(devices, new byte[3]);
        var dst = Array.Empty<byte>();
        var length = LiveLedFrameComposer.Compose(wire, devices, sink, ref dst);
        Assert.Equal(new byte[Leds * 3], Parse(dst, length)[0]);

        clock = LiveLedSink.FreshMs + 1;
        length = LiveLedFrameComposer.Compose(wire, devices, sink, ref dst);
        Assert.Empty(Parse(dst, length)[0]);
    }

    [Fact]
    public void Composer_MapsSectionsByPosition_SoWireByteCollisionsStayApart()
    {
        // Index 1 and 257 share the low byte on the wire.
        var devices = new[] { new DeviceFrame(1, "a", 1), new DeviceFrame(257, "b", 1) };
        var sink = new LiveLedSink { Enabled = true };
        sink.ReportSolid("a", 1, 1, 1, 1);
        sink.ReportSolid("b", 2, 2, 2, 1);
        var dst = Array.Empty<byte>();
        var length = LiveLedFrameComposer.Compose(WireFrame(devices, new byte[3]), devices, sink, ref dst);
        var parsed = Parse(dst, length);
        Assert.Equal(new byte[] { 1, 1, 1 }, parsed[0]);
        Assert.Equal(new byte[] { 2, 2, 2 }, parsed[1]);
    }

    [Fact]
    public void Solid_ReportsOneColorOnEveryLed()
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.ReportSolid("ring", 10, 20, 30, 3);
        Assert.Equal(new byte[] { 10, 20, 30, 10, 20, 30, 10, 20, 30 }, sink.TryGetFresh("ring"));
    }

    [Fact]
    public void Staged_output_publishes_only_on_commit_and_touch_keeps_the_last_alive()
    {
        var src = Source();
        var clock = 0L;
        var sink = new LiveLedSink { Enabled = true, Clock = () => clock };
        sink.Stage("dev", src, Leds, 0.5, DeviceColorAdjust.Identity, false, 0, 0, 0);
        Assert.Null(sink.TryGetFresh("dev"));
        sink.Commit("dev");
        var pushed = sink.TryGetFresh("dev");
        Assert.Equal(Sink(src, 0.5, DeviceColorAdjust.Identity), pushed);

        // A skipped push stages new bytes but publishes none; Touch keeps the pushed ones fresh past the window.
        sink.Stage("dev", src, Leds, 1.0, DeviceColorAdjust.Identity, false, 0, 0, 0);
        clock = 400;
        sink.Touch("dev");
        clock = 800;
        Assert.Equal(pushed, sink.TryGetFresh("dev"));
        clock = 800 + LiveLedSink.FreshMs + 1;
        Assert.Null(sink.TryGetFresh("dev"));
    }

    [Fact]
    public void BlankStaged_BlanksOnlyTheStagedBytes()
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.Stage("dev", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 3, 1.0, DeviceColorAdjust.Identity, false, 0, 0, 0);
        sink.BlankStaged("dev", 1, 1);
        sink.Commit("dev");
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 7, 8, 9 }, sink.TryGetFresh("dev"));
    }

    [Fact]
    public void Prune_DropsDevicesThatLeftTheSet()
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.ReportSolid("gone", 1, 2, 3, 1);
        sink.ReportSolid("kept", 1, 2, 3, 1);
        sink.Prune(new[] { new DeviceFrame(0, "kept", 1) });
        Assert.Null(sink.TryGetFresh("gone"));
        Assert.NotNull(sink.TryGetFresh("kept"));
    }

    [Theory]
    [InlineData("RGB")]
    [InlineData("GRB")]
    [InlineData("BRG")]
    [InlineData("GBR")]
    [InlineData("BGR")]
    [InlineData("RBG")]
    public void Unorder_UndoesTheChannelSwap(string order)
    {
        var original = new byte[] { 10, 20, 30, 40, 50, 60 };
        var ordered = (byte[])original.Clone();
        for (var i = 0; i < ordered.Length; i += 3)
        {
            DeviceColorAdjust.ReorderChannels(order, ref ordered[i], ref ordered[i + 1], ref ordered[i + 2]);
        }
        HydroShift2LightingFrameWriter.Unorder(order, ordered);
        Assert.Equal(original, ordered);
    }

    [Theory]
    [InlineData(4, 200, 200)]
    [InlineData(2, 200, 100)]
    [InlineData(0, 200, 0)]
    public void Galahad2_ReportedColorFollowsTheBrightnessStep(int step, int channel, int expected) =>
        Assert.Equal(expected, Galahad2LightingFrameWriter.AtBrightnessStep((byte)channel, (byte)step));

    [Fact]
    public void ComposeCopy_KeepsAnInFlightFrameIntactAcrossTheNextCompose()
    {
        var devices = new[] { new DeviceFrame(0, "a", 2) };
        var sink = new LiveLedSink { Enabled = true };
        var scratch = Array.Empty<byte>();
        var wire = WireFrame(devices, new byte[3]);

        sink.ReportSolid("a", 1, 1, 1, 2);
        var first = LiveLedFrameComposer.ComposeCopy(wire, devices, sink, ref scratch)!;
        var snapshot = (byte[])first.Clone();

        // The next frame has a different section length, written while the first is still being sent.
        sink.ReportNone("a");
        var second = LiveLedFrameComposer.ComposeCopy(wire, devices, sink, ref scratch)!;

        Assert.Equal(snapshot, first);
        Assert.NotEqual(first.Length, second.Length);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 1 }, Parse(first, first.Length)[0]);
        Assert.Empty(Parse(second, second.Length)[0]);
    }

    [Fact]
    public void Commit_DoesNotRepublishBytesStagedInAnEarlierSession()
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.Stage("dev", Source(), Leds, 1.0, DeviceColorAdjust.Identity, false, 0, 0, 0);
        sink.Commit("dev");
        sink.ReportNone("dev");
        // Nothing staged since, so a second commit must not bring the old bytes back.
        sink.Commit("dev");
        Assert.Null(sink.TryGetFresh("dev"));
    }

    private static List<IReadOnlyList<Nexus.Service.Lighting.Zones.ResolvedZone>> Groups(params string[] ids)
    {
        var groups = new List<IReadOnlyList<Nexus.Service.Lighting.Zones.ResolvedZone>>();
        foreach (var id in ids)
        {
            groups.Add(new[] { new Nexus.Service.Lighting.Zones.ResolvedZone { Id = id } });
        }
        return groups;
    }

    [Fact]
    public void LianLi_PacedTick_KeepsControlledGroupsAndGreysUncontrolledOnes()
    {
        var clock = 0L;
        var sink = new LiveLedSink { Enabled = true, Clock = () => clock };
        sink.ReportSolid("kept", 1, 2, 3, 1);
        sink.ReportSolid("handed-back", 1, 2, 3, 1);

        clock = 400;
        LianLiLightingFrameWriter.HoldLive(sink, Groups("kept", "handed-back"), new List<string> { "handed-back" });
        clock = 800;

        Assert.NotNull(sink.TryGetFresh("kept"));
        Assert.Null(sink.TryGetFresh("handed-back"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Corsair_PublishesOnlyWhenTheHubTookTheColors(bool sent)
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.ReportSolid("old", 9, 9, 9, 1);
        sink.Stage("old", new byte[] { 1, 2, 3 }, 1, 1.0, DeviceColorAdjust.Identity, false, 0, 0, 0);

        CorsairLinkLightingFrameWriter.PublishLive(sink, Groups("old"), sent);

        Assert.Equal(sent ? new byte[] { 1, 2, 3 } : new byte[] { 9, 9, 9 }, sink.TryGetFresh("old"));
    }

    [Fact]
    public void HydroShift2_KeepsTheLoopPhaseOnAnIdenticalUpload()
    {
        Assert.Equal(100, HydroShift2LightingFrameWriter.LoopPhaseStart(queued: false, hadLoop: true, current: 100, now: 900));
        Assert.Equal(900, HydroShift2LightingFrameWriter.LoopPhaseStart(queued: true, hadLoop: true, current: 100, now: 900));
        Assert.Equal(900, HydroShift2LightingFrameWriter.LoopPhaseStart(queued: false, hadLoop: false, current: 100, now: 900));
    }

    [Fact]
    public void Commit_WhileReportingIsOff_DropsWhatWasStaged()
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.Stage("dev", Source(), Leds, 1.0, DeviceColorAdjust.Identity, false, 0, 0, 0);
        sink.Enabled = false;
        sink.Commit("dev");
        sink.Enabled = true;
        sink.Commit("dev");
        Assert.Null(sink.TryGetFresh("dev"));
    }

    [Fact]
    public void Stop_ReportsTheFinalBlackFrameThenTurnsReportingOff()
    {
        var engine = new LightingEngine();
        var frame = FrameWith(Source());
        engine.UpdateDevices(new[] { frame });
        // A writer: reports what it drives from the frame it was handed.
        engine.OnFrame += _ => engine.Live.Report("f", frame.LedBytes, Leds, 1.0, DeviceColorAdjust.Identity);
        byte[]? last = null;
        var feed = new LiveLedDashboardFeed(engine.Live, () => 1, () => engine.IsStopped, copy => last = copy, _ => { });
        engine.FrameSerialized += feed.OnFrame;
        // Earlier frames already turned reporting on.
        engine.Live.Enabled = true;

        engine.Stop();

        Assert.NotNull(last);
        Assert.Equal(new byte[Leds * 3], Parse(last!, last!.Length)[0]);
        Assert.False(engine.Live.Enabled);
    }

    [Fact]
    public void Feed_DropsALitFrameThatArrivesAfterStop_AndKeepsReportingOnUntilTheFinalFrameIsDone()
    {
        var engine = new LightingEngine();
        var frame = FrameWith(Source());
        engine.UpdateDevices(new[] { frame });
        var sent = new List<byte[]>();
        var feed = new LiveLedDashboardFeed(engine.Live, () => 1, () => engine.IsStopped, copy => sent.Add(copy), _ => sent.Add(Array.Empty<byte>()));
        engine.FrameSerialized += feed.OnFrame;
        engine.Live.Enabled = true;
        engine.Stop();
        var afterFinal = sent.Count;

        // A render tick that was already in flight serializes a lit frame after Stop's black one.
        var lit = WireFrame(new[] { frame }, new byte[3]);
        feed.OnFrame(lit, new[] { frame }, false);

        Assert.Equal(afterFinal, sent.Count);
        Assert.False(engine.Live.Enabled);
    }
}

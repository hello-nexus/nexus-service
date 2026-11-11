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

    private static byte[] Sink(byte[] src, double mul, DeviceColorAdjust adjust)
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.Report("dev", src, Leds, mul, adjust);
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
    private static void AssertFamily(Func<DeviceColorAdjust, byte[]> writer, double mul, bool trim, string order)
    {
        var src = Source();
        var live = Sink(src, mul, Adjust(trim, order));
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
        }, mul, trim, order);

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
        sink.Report("dev", Source(), Leds, 0.2, DeviceColorAdjust.Identity, true, start, start);
        Assert.All(sink.TryGetFresh("dev")!, b => Assert.Equal(255, b));
        sink.Report("dev", Source(), Leds, 0.2, DeviceColorAdjust.Identity, true, start, start + 300 * TimeSpan.TicksPerMillisecond);
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

    // Returns each device's section length in LEDs, plus the bytes, from a composed frame.
    private static Dictionary<byte, byte[]> Parse(byte[] frame, int length)
    {
        var map = new Dictionary<byte, byte[]>();
        var pos = 5 + (frame[1] | (frame[2] << 8)) * (frame[3] | (frame[4] << 8)) * 3;
        var count = frame[pos++];
        for (var i = 0; i < count; i++)
        {
            var index = frame[pos];
            var n = (frame[pos + 1] | (frame[pos + 2] << 8)) * 3;
            pos += 3;
            map[index] = frame[pos..(pos + n)];
            pos += n;
        }
        Assert.Equal(length, pos);
        return map;
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

        var wire = WireFrame(devices, new byte[3]);
        var dst = Array.Empty<byte>();
        var length = LiveLedFrameComposer.Compose(wire, devices, sink, ref dst);
        var parsed = Parse(dst, length);

        Assert.Equal(Sink(src, 0.5, DeviceColorAdjust.Identity), parsed[0]);
        // No writer reports for these, so the dashboard gets a zero-length section.
        Assert.Empty(parsed[2]);
        Assert.Empty(parsed[3]);
        Assert.NotEqual(src, parsed[0]);
    }

    [Fact]
    public void Composer_StaleReportIsNotDriven_AndBlackStaysBlack()
    {
        var src = Source();
        var a = FrameWith(src);
        var devices = new[] { a };
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
    public void Solid_and_BlankRange_AdjustTheReportedBytes()
    {
        var sink = new LiveLedSink { Enabled = true };
        sink.ReportSolid("ring", 10, 20, 30, 5);
        Assert.Equal(new byte[] { 10, 20, 30, 10, 20, 30, 10, 20, 30, 10, 20, 30, 10, 20, 30 }, sink.TryGetFresh("ring"));
        sink.BlankRange("ring", 1, 2);
        Assert.Equal(new byte[] { 10, 20, 30, 0, 0, 0, 0, 0, 0, 10, 20, 30, 10, 20, 30 }, sink.TryGetFresh("ring"));
    }

}

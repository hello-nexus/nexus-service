using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>45 degree LCD steps: the split, the rotation math, the round-only gate and what an older install loads.</summary>
public class KrakenOrientationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-kraken-orient-" + Guid.NewGuid().ToString("N")[..8]);

    public KrakenOrientationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(45, 0, true)]
    [InlineData(90, 1, false)]
    [InlineData(135, 1, true)]
    [InlineData(225, 2, true)]
    [InlineData(315, 3, true)]
    [InlineData(360, 0, false)]
    public void Split_is_the_firmware_quarter_turns_plus_the_extra_step(int degrees, int turns, bool extra)
    {
        Assert.Equal((turns, extra), KrakenOrientation.Split(degrees));
        Assert.Equal(degrees % 360, KrakenOrientation.ToDegrees(turns, extra));
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(90, true, true)]
    [InlineData(45, true, true)]
    [InlineData(315, true, true)]
    [InlineData(0, false, true)]
    [InlineData(270, false, true)]
    [InlineData(45, false, false)]
    [InlineData(135, false, false)]
    [InlineData(315, false, false)]
    [InlineData(30, true, false)]
    [InlineData(360, true, false)]
    [InlineData(-45, true, false)]
    public void Only_round_panels_accept_a_45_degree_step(int degrees, bool round, bool allowed) =>
        Assert.Equal(allowed, KrakenOrientation.IsAllowed(degrees, round));

    // A 9x9 frame with one marked pixel top-centre; the pipeline is the hub's: the extra 45 first, then the quarter turns.
    private static (int X, int Y) MarkAfter(int degrees)
    {
        const int n = 9;
        var src = new byte[n * n * 4];
        src[((0 * n) + 4) * 4] = 0xFF;
        var (turns, extra) = KrakenOrientation.Split(degrees);
        var frame = src;
        if (extra)
        {
            frame = new byte[src.Length];
            KrakenOrientation.Rotate45(src, n, frame);
        }
        var wire = KrakenProtocol.ToWireRgba(frame, n, n, turns, sourceIsBgra: false);
        for (int i = 0; i < n * n; i++)
        {
            if (wire[i * 4] == 0xFF)
            {
                return (i % n, i / n);
            }
        }
        throw new InvalidOperationException("marker lost");
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(45, 7, 1)]
    [InlineData(90, 8, 4)]
    [InlineData(135, 7, 7)]
    [InlineData(180, 4, 8)]
    [InlineData(225, 1, 7)]
    [InlineData(270, 0, 4)]
    [InlineData(315, 1, 1)]
    public void The_marker_goes_clockwise_through_every_step(int degrees, int x, int y) =>
        Assert.Equal((x, y), MarkAfter(degrees));

    [Fact]
    public void The_extra_step_blacks_the_corners_and_keeps_the_centre()
    {
        const int n = 9;
        var src = new byte[n * n * 4];
        Array.Fill(src, (byte)0x80);
        var dst = new byte[src.Length];
        KrakenOrientation.Rotate45(src, n, dst);
        Assert.Equal(new byte[] { 0, 0, 0, 0xFF }, dst[0..4]);
        int centre = ((4 * n) + 4) * 4;
        Assert.Equal(new byte[] { 0x80, 0x80, 0x80, 0x80 }, dst[centre..(centre + 4)]);
    }

    [Fact]
    public void Rotate45_does_not_allocate_after_the_first_frame()
    {
        const int n = 64;
        var src = new byte[n * n * 4];
        var dst = new byte[src.Length];
        KrakenOrientation.Rotate45(src, n, dst);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            KrakenOrientation.Rotate45(src, n, dst);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Every_kraken_with_a_panel_is_round_and_one_without_is_not()
    {
        foreach (var model in KrakenModel.All)
        {
            Assert.Equal(model.HasLcd, model.LcdRound);
        }
    }

    private sealed class Rig
    {
        public readonly KrakenHub Hub;
        public int? Saved;

        public Rig(int productId, int firmwareQuarterTurns, int? persistedDegrees)
        {
            var model = KrakenModel.Find(productId)!;
            Hub = new KrakenHub(new NoLcdFactory()) { Clock = () => 1_000_000 };
            Hub.FadeRunner.AutoRun = false;
            Hub.BindLockSleep(new LcdLockState(), () => true);
            Hub.BindOrientation(() => persistedDegrees, d => Saved = d);
            Hub.Attach(new Hid(productId, firmwareQuarterTurns, model.LcdWidth), model, KrakenProtocol.ReportLength);
            Assert.True(Hub.Connect());
        }
    }

    [Theory]
    [InlineData(0x300C, 1, 135, 135)]
    [InlineData(0x300C, 1, 45, 90)]
    [InlineData(0x300C, 1, null, 90)]
    [InlineData(0x300C, 2, 135, 180)]
    [InlineData(0x3012, 3, 315, 315)]
    [InlineData(0x3014, 0, 45, 45)]
    public void The_remembered_extra_step_returns_only_while_the_coolers_quarter_turns_still_match(
        int pid, int firmwareTurns, int? persisted, int expectedDegrees)
    {
        var rig = new Rig(pid, firmwareTurns, persisted);
        Assert.Equal(expectedDegrees, rig.Hub.Snapshot.LcdOrientationDegrees);
        Assert.Equal(firmwareTurns, rig.Hub.Snapshot.LcdOrientationQuarterTurns);
    }

    [Fact]
    public void Setting_a_round_panel_to_135_sends_one_quarter_turn_and_remembers_the_degrees()
    {
        var rig = new Rig(0x300C, 0, null);
        Assert.True(rig.Hub.LcdRound);
        Assert.True(rig.Hub.SetLcdBacklight(70, 1, extra45: true));
        Assert.Equal(1, rig.Hub.Snapshot.LcdOrientationQuarterTurns);
        Assert.True(rig.Hub.Snapshot.LcdExtra45);
        Assert.Equal(135, rig.Saved);
    }

    [Fact]
    public void A_panel_with_no_round_glass_ignores_the_extra_step()
    {
        var rig = new Rig(0x2014, 0, null);
        Assert.False(rig.Hub.LcdRound);
        Assert.True(rig.Hub.SetLcdBacklight(70, 1, extra45: true));
        Assert.False(rig.Hub.Snapshot.LcdExtra45);
        Assert.Equal(90, rig.Saved);
    }

    public static IEnumerable<object[]> Captures() => new[]
    {
        new object[] { "v24-3.1.0-era" }, new object[] { "v25-3.1.5-clean" }, new object[] { "v26-3.1.5-widgets" },
        new object[] { "v27-3.1.7" }, new object[] { "v28-dev" }, new object[] { "v29-dev" },
        new object[] { "v30-3.1.8-beta" }, new object[] { "v32-main-t1" }, new object[] { "v32-main-t1-edited" },
    };

    [Theory]
    [MemberData(nameof(Captures))]
    public void Settings_captured_before_45_degree_steps_load_with_the_coolers_own_rotation(string capture)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "upgrade", capture, "settings.json");
        var path = Path.Combine(_dir, "settings.json");
        File.Copy(source, path);
        var store = new JsonConfigStore(path);
        try
        {
            var devices = store.Load().Devices;
            Assert.Null(devices.KrakenLcdOrientationDegrees);
            var rig = new Rig(0x300C, 2, devices.KrakenLcdOrientationDegrees);
            Assert.Equal(180, rig.Hub.Snapshot.LcdOrientationDegrees);
            Assert.False(rig.Hub.Snapshot.LcdExtra45);
        }
        finally
        {
            store.Dispose();
        }
    }

    private sealed class NoLcdFactory : IKrakenLcdTransportFactory
    {
        public IKrakenLcdTransport? Open(string? serial) => null;
    }

    private sealed class Hid : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        private readonly int _pid;
        private readonly int _turns;
        private readonly int _size;

        public Hid(int pid, int turns, int size)
        {
            _pid = pid;
            _turns = turns;
            _size = size;
        }

        public int VendorId => 0x1E71;
        public int ProductId => _pid;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(report[0] + 1);
            reply[1] = report[1];
            reply[14] = KrakenProtocol.AckOk;
            if (report[0] == 0x10)
            {
                reply[0x11] = 1;
            }
            if (report[0] == 0x30 && report[1] == 0x01)
            {
                reply[20] = (byte)(_size & 0xFF);
                reply[21] = (byte)(_size >> 8);
                reply[22] = (byte)(_size & 0xFF);
                reply[23] = (byte)(_size >> 8);
                reply[24] = 50;
                reply[26] = (byte)_turns;
            }
            _replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var r = _replies.Dequeue();
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }
}

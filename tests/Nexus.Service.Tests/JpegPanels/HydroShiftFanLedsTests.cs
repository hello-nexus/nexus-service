using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// The 360R fan LEDs over a fake HID transport: pins packet bytes, the product gate, the
/// attach and lock ordering, and the writer's rate behavior. It cannot say what the fans do
/// with the bytes.
/// </summary>
public class HydroShiftFanLedsTests
{
    private const int ReportLength = 1024;
    private const int Pid360S = 0x7398;
    private const int Pid360R = 0x7399;
    private const int Pid360Tl = 0x739A;

    private static byte[] Colors()
    {
        var rgb = new byte[72];
        for (int i = 0; i < 72; i++)
        {
            rgb[i] = (byte)(i + 1);
        }
        return rgb;
    }

    private static (HydroShiftFanLeds Leds, JpegPanelHub Hub, FakeDevice Device) Attached(int pid = Pid360R, Func<long>? clock = null)
    {
        var model = JpegPanelModel.HydroShiftLcd with
        {
            Handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24, true, (_, _) => { }, () => 0),
        };
        var hub = new JpegPanelHub(model);
        var device = new FakeDevice(pid);
        Assert.True(hub.Attach(device));
        device.Writes.Clear();
        return (new HydroShiftFanLeds(hub, clock), hub, device);
    }

    [Fact]
    public void Per_led_write_is_a_sequenced_report_b_carrying_the_selector_and_24_colors()
    {
        var report = new byte[ReportLength];
        var rgb = Colors();

        LianLiAioProtocol.FillPerLed(report, rgb);

        Assert.Equal(new byte[] { 0x02, 0x14, 0, 0, 0, 73, 0, 0, 0, 0, 73, 0x01 }, report[..12]);
        Assert.Equal(rgb, report[12..84]);
        Assert.All(report[84..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void The_prime_is_a_then_b_under_the_fan_command_then_the_pump_command()
    {
        var report = new byte[ReportLength];
        var heads = new List<byte[]>();
        for (int i = 0; i < LianLiAioProtocol.PrimePacketCount; i++)
        {
            LianLiAioProtocol.FillFanLedPrime(report, i);
            heads.Add(report[..26]);
        }

        Assert.Equal(new byte[] { 0x01, 0x85, 0, 0, 0, 20, 0x03, 0x04 }, heads[0][..8]);
        Assert.Equal(0x32, heads[0][25]);
        Assert.Equal(new byte[] { 0x01, 0x85, 0, 0, 0, 19, 0x00, 0x03, 0x04 }, heads[1][..9]);
        Assert.Equal(new byte[] { 0x01, 0x83, 0, 0, 0, 20 }, heads[2][..6]);
        Assert.Equal(new byte[] { 0x01, 0x83, 0, 0, 0, 19 }, heads[3][..6]);
    }

    [Fact]
    public void The_first_write_primes_then_sends_colors_and_later_writes_skip_the_prime()
    {
        var (leds, _, device) = Attached();

        Assert.True(leds.Write(Colors()));
        Assert.Equal(new[] { 0x85, 0x85, 0x83, 0x83, 0x14 }, device.Writes.Select(w => (int)w[1]));

        device.Writes.Clear();
        Assert.True(leds.Write(Colors()));
        Assert.Equal(new[] { 0x14 }, device.Writes.Select(w => (int)w[1]));
    }

    [Fact]
    public void A_failed_write_or_a_new_attach_primes_again()
    {
        long now = 0;
        var (leds, hub, device) = Attached(clock: () => now);
        leds.Write(Colors());

        device.FailWrites = true;
        Assert.False(leds.Write(Colors()));
        device.FailWrites = false;
        device.Writes.Clear();
        now += HydroShiftFanLeds.InitialBackoffMs;
        leds.Write(Colors());
        Assert.Equal(5, device.Writes.Count);

        var next = new FakeDevice(Pid360R);
        Assert.True(hub.Attach(next));
        next.Writes.Clear();
        leds.Write(Colors());
        Assert.Equal(0x85, next.Writes[0][1]);
        Assert.Equal(0x14, next.Writes[^1][1]);
    }

    [Theory]
    [InlineData(Pid360S, false)]
    [InlineData(Pid360R, true)]
    [InlineData(Pid360Tl, false)]
    public void Only_the_360R_counts_as_connected_and_takes_writes(int pid, bool expected)
    {
        var (leds, _, device) = Attached(pid);

        Assert.Equal(expected, leds.IsConnected);
        Assert.Equal(expected, leds.Write(Colors()));
        if (!expected)
        {
            Assert.Empty(device.Writes);
        }
    }

    [Fact]
    public void Nothing_is_written_while_detached()
    {
        var hub = new JpegPanelHub(JpegPanelModel.HydroShiftLcd);
        var leds = new HydroShiftFanLeds(hub);

        Assert.False(leds.IsConnected);
        Assert.False(leds.Write(Colors()));
    }

    [Fact]
    public void Nothing_is_written_until_the_attach_has_finished()
    {
        HydroShiftFanLeds? leds = null;
        var attemptsDuringAttach = new List<bool>();
        var model = JpegPanelModel.HydroShiftLcd with
        {
            Handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", 24, true, (_, _) =>
            {
                // The session releases the hub lock across its pauses; a lighting write there must still be refused.
                attemptsDuringAttach.Add(leds!.Write(Colors()));
            }, () => 0),
        };
        var hub = new JpegPanelHub(model);
        leds = new HydroShiftFanLeds(hub);
        var device = new FakeDevice(Pid360R);

        Assert.True(hub.Attach(device));

        Assert.NotEmpty(attemptsDuringAttach);
        Assert.All(attemptsDuringAttach, attempted => Assert.False(attempted));
        Assert.DoesNotContain(device.Writes, w => w[1] == 0x14 || w[1] == 0x85);
        Assert.True(leds.Write(Colors()));
    }

    [Fact]
    public async Task Lighting_writes_never_land_inside_a_frames_chunks()
    {
        var (leds, hub, device) = Attached();
        var jpeg = new byte[1013 * 6];
        Array.Fill(jpeg, (byte)0xAB);
        var done = false;

        var frames = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                hub.SendFrame(jpeg);
            }
            Volatile.Write(ref done, true);
        });
        var lighting = Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                leds.Write(Colors());
            }
        });
        await Task.WhenAll(frames, lighting);

        var writes = device.Snapshot();
        int expectedChunk = 0;
        foreach (var w in writes)
        {
            bool isFrameChunk = w[0] == 0x02 && w[1] == 0x0E;
            if (expectedChunk > 0)
            {
                Assert.True(isFrameChunk, "a non-frame write landed between two chunks of one frame");
                Assert.Equal(expectedChunk, (w[6] << 16) | (w[7] << 8) | w[8]);
            }
            if (isFrameChunk)
            {
                expectedChunk = expectedChunk + 1 >= 6 ? 0 : expectedChunk + 1;
            }
        }
        Assert.Equal(0, expectedChunk);
        Assert.Contains(writes, w => w[1] == 0x14);
    }

    [Fact]
    public void A_rejected_led_write_backs_off_instead_of_priming_on_every_tick()
    {
        long now = 0;
        var (leds, _, device) = Attached(clock: () => now);
        device.FailWrites = true;
        Assert.False(leds.Write(Colors()));
        int writesAfterFailure = device.Writes.Count;

        for (now = 50; now < HydroShiftFanLeds.InitialBackoffMs; now += 50)
        {
            Assert.False(leds.Write(Colors()));
        }
        Assert.Equal(writesAfterFailure, device.Writes.Count);

        device.FailWrites = false;
        now = HydroShiftFanLeds.InitialBackoffMs;
        Assert.True(leds.Write(Colors()));
        Assert.Contains(device.Writes, w => w[1] == 0x14);
    }

    [Fact]
    public void Backoff_doubles_to_a_cap_and_resets_on_success()
    {
        long now = 0;
        var (leds, _, device) = Attached(clock: () => now);
        device.FailWrites = true;
        var gaps = new List<long>();
        for (int i = 0; i < 9; i++)
        {
            Assert.False(leds.Write(Colors()));
            long failedAt = now;
            long probe = now;
            int before = device.Writes.Count;
            while (device.Writes.Count == before)
            {
                probe += 250;
                now = probe;
                leds.Write(Colors());
                if (device.Writes.Count != before) break;
            }
            gaps.Add(probe - failedAt);
        }
        Assert.Equal(1000, gaps[0]);
        Assert.Equal(2000, gaps[1]);
        Assert.Equal(HydroShiftFanLeds.MaxBackoffMs, gaps[^1]);

        device.FailWrites = false;
        now += HydroShiftFanLeds.MaxBackoffMs;
        Assert.True(leds.Write(Colors()));
        device.FailWrites = true;
        device.Writes.Clear();
        leds.Write(Colors());
        device.FailWrites = false;
        now += HydroShiftFanLeds.InitialBackoffMs;
        Assert.True(leds.Write(Colors()));
    }

    [Fact]
    public void Catalog_marks_only_the_360R_as_rgb()
    {
        bool Rgb(string pid) => LightingDevicesCatalog.All
            .Single(d => d.Source == "nexus" && d.VendorId == "0x0416" && d.ProductId == pid)
            .Capabilities.Contains("rgb");

        Assert.True(Rgb("0x7399"));
        Assert.False(Rgb("0x7398"));
        Assert.False(Rgb("0x739A"));
        Assert.Contains("screen", LightingDevicesCatalog.All
            .Single(d => d.Source == "nexus" && d.ProductId == "0x7399" && d.VendorId == "0x0416").Capabilities);
    }

    [Fact]
    public void Provider_lists_one_24_led_fan_zone_only_for_a_connected_360R()
    {
        var (leds, _, _) = Attached();
        leds.Poll();
        var provider = new HydroShiftLcdLightingProvider(leds, new InMemoryConfigStore(), new Np50IdentifyTracker());

        var card = Assert.Single(provider.GetAll().Devices);
        Assert.Equal(HydroShiftLcdLightingProvider.FansZoneId, card.Id);
        Assert.Equal(24, card.LedCount);
        Assert.Equal("lianli-hydroshift-lcd", card.ParentDeviceId);
        var structure = Assert.Single(provider.GetStructures());
        var segment = Assert.Single(structure.Segments);
        Assert.Equal(24, segment.FrameLedCount);
        Assert.Equal(24, segment.DefaultU!.Length);
        var frame = Assert.Single(provider.BuildFrames(5));
        Assert.Equal(24, frame.LedCount);
        Assert.Equal(5, frame.Index);

        var (other, _, _) = Attached(Pid360S);
        var none = new HydroShiftLcdLightingProvider(other, new InMemoryConfigStore(), new Np50IdentifyTracker());
        Assert.Empty(none.GetAll().Devices);
        Assert.Empty(none.GetStructures());
        Assert.Empty(none.BuildFrames(0));
    }

    [Fact]
    public void Writer_sends_a_changed_frame_at_once_and_an_unchanged_one_only_as_a_slow_reassert()
    {
        var (writer, frame, device) = Writer();
        frame.Fill(255, 0, 0);
        frame.Publish();

        writer.Tick(1000);
        var perLed = device.Writes.Where(w => w[1] == 0x14).ToList();
        Assert.Single(perLed);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0 }, perLed[0][12..18]);

        writer.Tick(1050);
        writer.Tick(1100);
        Assert.Single(device.Writes, w => w[1] == 0x14);

        writer.Tick(1000 + HydroShiftLcdLightingFrameWriter.ReassertMs);
        Assert.Equal(2, device.Writes.Count(w => w[1] == 0x14));

        frame.Fill(0, 0, 255);
        frame.Publish();
        writer.Tick(1000 + HydroShiftLcdLightingFrameWriter.ReassertMs + 50);
        Assert.Equal(3, device.Writes.Count(w => w[1] == 0x14));
        Assert.Equal(new byte[] { 0, 0, 255 }, device.Writes[^1][12..15]);
    }

    [Fact]
    public void Writer_goes_dark_when_switched_off_and_leaves_an_uncontrolled_device_alone()
    {
        var (writer, frame, device, store) = WriterWithStore();
        frame.Fill(255, 255, 255);
        frame.Publish();

        store.Update(s => s.Devices.UncontrolledLightingDevices.Add(HydroShiftLcdLightingProvider.FansZoneId));
        writer.Tick(1000);
        Assert.Empty(device.Writes);

        store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices.Clear();
            s.Devices.DisabledLightingDevices.Add(HydroShiftLcdLightingProvider.FansZoneId);
        });
        writer.Tick(2000);
        var write = Assert.Single(device.Writes, w => w[1] == 0x14);
        Assert.All(write[12..84], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Writer_stays_quiet_on_a_360S()
    {
        var (writer, frame, device, _) = WriterWithStore(Pid360S);
        frame.Fill(255, 255, 255);
        frame.Publish();

        writer.Tick(1000);

        Assert.Empty(device.Writes);
    }

    [Fact]
    public void The_write_rate_is_capped_at_twenty_per_second()
    {
        Assert.True(HydroShiftLcdLightingFrameWriter.TickMs >= 50);
    }

    private static (HydroShiftLcdLightingFrameWriter Writer, DeviceFrame Frame, FakeDevice Device) Writer()
    {
        var (writer, frame, device, _) = WriterWithStore();
        return (writer, frame, device);
    }

    private static (HydroShiftLcdLightingFrameWriter Writer, DeviceFrame Frame, FakeDevice Device, InMemoryConfigStore Store) WriterWithStore(int pid = Pid360R)
    {
        var (leds, _, device) = Attached(pid);
        var store = new InMemoryConfigStore();
        var engine = new LightingEngine();
        var frame = new DeviceFrame(0, HydroShiftLcdLightingProvider.FansZoneId, 24);
        engine.UpdateDevices(new[] { frame });
        return (new HydroShiftLcdLightingFrameWriter(engine, leds, store, new Np50IdentifyTracker()), frame, device, store);
    }

    private sealed class FakeDevice : IHidDevice
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _writes = new();

        public FakeDevice(int productId)
        {
            ProductId = productId;
        }

        /// <summary>Single-threaded reads of the log; the concurrent test uses <see cref="Snapshot"/>.</summary>
        public List<byte[]> Writes => _writes;

        public bool FailWrites { get; set; }

        public int VendorId => 0x0416;
        public int ProductId { get; }
        public string Path => "/dev/fake-hydroshift-360r";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF1A;
        public int Usage => 0x93;

        public byte[][] Snapshot()
        {
            lock (_gate)
            {
                return _writes.ToArray();
            }
        }

        public bool Write(ReadOnlySpan<byte> report)
        {
            lock (_gate)
            {
                _writes.Add(report.ToArray());
            }
            return !FailWrites;
        }

        public int Read(Span<byte> buffer, int timeoutMs) => 0;

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public void Dispose() { }
    }
}

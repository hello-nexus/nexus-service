using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Lighting;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.SteelSeries;
using Xunit;

namespace Nexus.Service.Tests.SteelSeries;

public class ApexOledStreamTransportTests
{
    internal sealed class FakeScreen : IHidDevice
    {
        public readonly List<byte[]> Features = new();
        public readonly List<byte[]> Outputs = new();
        public volatile bool AcceptFeatures = true;
        public volatile bool AcceptOutputs = true;
        public volatile bool Disposed;
        public ManualResetEventSlim? HoldFeature;
        public ManualResetEventSlim? HoldOutput;
        public readonly ManualResetEventSlim InOutput = new(false);
        public readonly ManualResetEventSlim InFeature = new(false);

        public int VendorId => ApexOledProtocol.VendorId;
        public int ProductId { get; init; }
        public string Path => "fake-apex";
        public string? Serial => "SN1";
        public int UsagePage => ApexOledProtocol.ControlUsagePage;
        public int Usage => 1;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            lock (Features) { Features.Add(report.ToArray()); }
            InFeature.Set();
            HoldFeature?.Wait(5000);
            return AcceptFeatures;
        }

        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;

        public bool Write(ReadOnlySpan<byte> report)
        {
            lock (Outputs) { Outputs.Add(report.ToArray()); }
            InOutput.Set();
            HoldOutput?.Wait(5000);
            return AcceptOutputs;
        }

        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeHid : IHidEnumerator
    {
        public readonly List<HidDeviceInfo> Infos = new();
        public readonly Dictionary<string, FakeScreen> Screens = new();

        public FakeScreen Add(int pid, int usagePage = ApexOledProtocol.ControlUsagePage, string? serial = "SN1", int featureLength = ApexOledProtocol.FeatureReportLength)
        {
            var path = $"fake-{pid:x}-{usagePage:x}-{serial}-{featureLength}";
            Infos.Add(new HidDeviceInfo { VendorId = ApexOledProtocol.VendorId, ProductId = pid, Path = path, UsagePage = usagePage, Serial = serial, FeatureReportByteLength = featureLength });
            return Screens[path] = new FakeScreen { ProductId = pid };
        }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            Infos.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();

        public IReadOnlyList<HidDeviceInfo> FindAll() => Infos;
        public IHidDevice? Open(string path, bool forInput = false) => Screens.GetValueOrDefault(path);
    }

    private static ApexOledStreamTransport Create(FakeHid hid, int pid, LcdLockState? state = null, Func<long>? clock = null, AttachBackoff? backoff = null, int tickMs = 20, int disposeWaitMs = 1000, bool enforceLength = true)
    {
        var screen = ApexOledScreens.Find(hid, enforceLength).First(s => s.Model.ProductId == pid);
        return new ApexOledStreamTransport(hid, screen.Model, ApexOledPanelDiscovery.PanelSerial(screen), state, clock, backoff, tickMs, disposeWaitMs, enforceLength);
    }

    private static ApexOledStreamTransport Open(FakeHid hid, int pid, LcdLockState? state = null, Func<long>? clock = null)
    {
        var transport = Create(hid, pid, state, clock);
        transport.Open();
        return transport;
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var until = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < until)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(5);
        }
        return condition();
    }

    private static byte[] Frame(byte gray) => ApexOledMonoTests.Solid(gray, gray, gray);

    [Fact]
    public void A_frame_is_thresholded_packed_and_sent_as_one_feature_report()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        using var transport = Open(hid, 0x1628);

        transport.Write(Frame(255));

        var report = Assert.Single(screen.Features);
        Assert.Equal(ApexOledProtocol.FeatureReportLength, report.Length);
        Assert.Equal(new byte[] { 0x00, 0x1F, 0x81 }, report[..3]);
        Assert.All(report[3..643], b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void The_columns_model_gets_the_column_packing()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1642);
        using var transport = Open(hid, 0x1642);
        var frame = Frame(0);
        // Pixel (0,1) lit: row packing would set 0x80 of byte 16, column packing sets bit 1 of byte 0.
        frame[(1 * ApexOledProtocol.Width) * 4 + 1] = 255;

        transport.Write(frame);

        Assert.Equal(0x02, screen.Features.Single()[3]);
        Assert.Equal(0x00, screen.Features.Single()[3 + 16]);
    }

    [Fact]
    public void A_frame_split_across_writes_is_sent_once_whole()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        using var transport = Open(hid, 0x1628);
        var frame = Frame(255);

        transport.Write(frame.AsSpan(0, 1000));
        Assert.Empty(screen.Features);
        transport.Write(frame.AsSpan(1000));

        Assert.Single(screen.Features);
    }

    [Fact]
    public void An_unchanged_frame_is_skipped_until_the_keepalive_then_resent()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        long now = 1000;
        using var transport = Open(hid, 0x1628, clock: () => now);

        transport.Write(Frame(255));
        now += ApexOledStreamTransport.KeepAliveMs - 1;
        transport.Write(Frame(255));
        Assert.Single(screen.Features);

        now += 1;
        transport.Write(Frame(255));
        Assert.Equal(2, screen.Features.Count);

        transport.Write(Frame(0));
        Assert.Equal(3, screen.Features.Count);
    }

    [Fact]
    public void A_refused_frame_fails_the_transport()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.AcceptFeatures = false;
        using var transport = Open(hid, 0x1628);

        Assert.Throws<IOException>(() => transport.Write(Frame(255)));
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void Dispose_hands_the_screen_back_and_releases_the_handle()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        var transport = Open(hid, 0x1628);
        transport.Write(Frame(255));

        transport.Dispose();

        Assert.Equal(ApexOledProtocol.HandBackReport(), Assert.Single(screen.Outputs));
        Assert.True(screen.Disposed);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void Dispose_after_a_failure_does_not_write_to_the_dead_handle()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.AcceptFeatures = false;
        var transport = Open(hid, 0x1628);
        Assert.Throws<IOException>(() => transport.Write(Frame(255)));

        transport.Dispose();

        Assert.Empty(screen.Outputs);
        Assert.True(screen.Disposed);
    }

    [Fact]
    public void A_locked_session_hands_the_screen_back_once_and_unlock_resumes_frames()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        var state = new LcdLockState();
        using var transport = Open(hid, 0x1628, state);
        transport.Write(Frame(255));

        state.Set(true);
        transport.Write(Frame(255));
        transport.Write(Frame(0));

        Assert.Single(screen.Features);
        Assert.Equal(ApexOledProtocol.HandBackReport(), Assert.Single(screen.Outputs));

        state.Set(false);
        transport.Write(Frame(255));

        // Resent even though the content matches the pre-lock frame: the firmware drew over it.
        Assert.Equal(2, screen.Features.Count);
        transport.Dispose();
        // Already live again, so dispose hands back a second time.
        Assert.Equal(2, screen.Outputs.Count);
    }

    [Fact]
    public void Disposing_while_locked_does_not_hand_back_twice()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        var state = new LcdLockState();
        var transport = Open(hid, 0x1628, state);
        state.Set(true);
        transport.Write(Frame(255));

        transport.Dispose();

        Assert.Single(screen.Outputs);
    }

    [Fact]
    public void A_flipped_mount_turns_the_frame_before_it_is_sent()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        using var transport = Open(hid, 0x1628);
        transport.BindOrientation(() => (true, false));
        var frame = Frame(0);
        frame[0] = frame[1] = frame[2] = 255; // top-left pixel

        transport.Write(frame);

        // Flipped 180: the lit pixel lands at the bottom-right, the last bit of the last row.
        var report = screen.Features.Single();
        Assert.Equal(0x00, report[3]);
        Assert.Equal(0x01, report[3 + ApexOledProtocol.FrameBytes - 1]);
    }

    [Fact]
    public void Open_without_the_screen_interface_throws()
    {
        var hid = new FakeHid();
        hid.Add(0x1628, usagePage: 0x0001);
        var transport = new ApexOledStreamTransport(hid, ApexOledProtocol.ModelFor(0x1628)!, "steelseries-apex-oled:1628:SN1");

        Assert.Throws<IOException>(transport.Open);
    }

    [Fact]
    public void A_static_page_is_repainted_by_the_timer_after_the_keepalive()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        long now = 1000;
        using var transport = Create(hid, 0x1628, clock: () => Interlocked.Read(ref now));
        transport.Open();
        transport.StartPlayer();
        transport.Write(Frame(255));
        Assert.Single(screen.Features);

        Interlocked.Add(ref now, ApexOledStreamTransport.KeepAliveMs);

        Assert.True(WaitFor(() => screen.Features.Count >= 2));
        Assert.Equal(screen.Features[0], screen.Features[1]);
    }

    [Fact]
    public void The_timer_hands_back_on_lock_and_repaints_on_unlock_without_new_frames()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        var state = new LcdLockState();
        using var transport = Create(hid, 0x1628, state);
        transport.Open();
        transport.StartPlayer();
        transport.Write(Frame(255));

        state.Set(true);
        Assert.True(WaitFor(() => screen.Outputs.Count == 1));

        state.Set(false);
        Assert.True(WaitFor(() => screen.Features.Count == 2));
        Assert.Single(screen.Outputs);
    }

    [Fact]
    public void A_refused_hand_back_is_retried_not_counted_as_done()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.AcceptOutputs = false;
        var state = new LcdLockState();
        using var transport = Create(hid, 0x1628, state);
        transport.Open();
        transport.StartPlayer();
        transport.Write(Frame(255));

        state.Set(true);
        Assert.True(WaitFor(() => screen.Outputs.Count >= 2));

        screen.AcceptOutputs = true;
        var seen = screen.Outputs.Count;
        Assert.True(WaitFor(() => screen.Outputs.Count > seen));
        Thread.Sleep(100);
        var settled = screen.Outputs.Count;
        Thread.Sleep(100);
        Assert.Equal(settled, screen.Outputs.Count);
    }

    [Fact]
    public void Dispose_leaves_the_handle_to_a_write_still_in_the_keyboard()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.HoldFeature = new ManualResetEventSlim(false);
        var transport = Create(hid, 0x1628, disposeWaitMs: 50);
        transport.Open();
        var writer = new Thread(() => transport.Write(Frame(255)));
        writer.Start();
        Assert.True(screen.InFeature.Wait(3000));

        transport.Dispose();

        Assert.False(screen.Disposed);
        Assert.Empty(screen.Outputs);

        screen.HoldFeature.Set();
        writer.Join(3000);
        Assert.True(screen.Disposed);
        Assert.Single(screen.Outputs);
    }

    [Fact]
    public void Refused_frames_back_discovery_off_instead_of_retrying_every_pass()
    {
        long now = 1000;
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.AcceptFeatures = false;
        var discovery = new ApexOledPanelDiscovery(hid, clock: () => Interlocked.Read(ref now), enforceLength: true);
        var info = Assert.Single(discovery.Discover());
        using var transport = discovery.CreateTransport(info);
        transport.Open();

        Assert.Throws<IOException>(() => transport.Write(Frame(255)));

        Assert.Empty(discovery.Discover());
        Interlocked.Add(ref now, AttachBackoff.BaseDelayMs);
        Assert.Single(discovery.Discover());
    }

    [Fact]
    public void A_collection_too_short_for_a_frame_is_never_opened()
    {
        var hid = new FakeHid();
        hid.Add(0x1628, featureLength: 64);
        var transport = new ApexOledStreamTransport(hid, ApexOledProtocol.ModelFor(0x1628)!, "steelseries-apex-oled:1628:SN1", enforceLength: true);

        Assert.Empty(ApexOledScreens.Find(hid, true));
        Assert.Throws<IOException>(transport.Open);
        // Without the Windows-only rule the collection is used as found.
        Assert.Single(ApexOledScreens.Find(hid, false));
    }

    [Fact]
    public void The_collection_that_fits_is_chosen_over_a_short_one_on_the_same_keyboard()
    {
        var hid = new FakeHid();
        hid.Add(0x1628, featureLength: 64);
        var fits = hid.Add(0x1628, featureLength: 644);
        using var transport = Open(hid, 0x1628);

        transport.Write(Frame(255));

        Assert.Single(fits.Features);
        Assert.Single(ApexOledScreens.Find(hid, true));
    }

    [Fact]
    public void Two_keyboards_are_two_panels_and_each_transport_drives_its_own()
    {
        var hid = new FakeHid();
        var a = hid.Add(0x1628, serial: "A");
        var b = hid.Add(0x1628, serial: "B");
        var discovery = new ApexOledPanelDiscovery(hid);
        var infos = discovery.Discover();
        Assert.Equal(2, infos.Count);
        Assert.NotEqual(infos[0].Serial, infos[1].Serial);

        using var second = discovery.CreateTransport(infos[1]);
        second.Open();
        second.Write(Frame(255));

        Assert.Empty(a.Features);
        Assert.Single(b.Features);
    }

    [Fact]
    public void A_transport_opened_while_locked_repaints_at_unlock()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        var state = new LcdLockState();
        state.Set(true);
        using var transport = Create(hid, 0x1628, state);
        transport.Open();
        transport.StartPlayer();
        transport.Write(Frame(255));
        Assert.Empty(screen.Features);

        state.Set(false);

        Assert.True(WaitFor(() => screen.Features.Count == 1));
    }

    [Fact]
    public void One_keyboards_refusals_do_not_hide_or_clear_another()
    {
        long now = 1000;
        var hid = new FakeHid();
        var a = hid.Add(0x1628, serial: "A");
        hid.Add(0x1628, serial: "B");
        a.AcceptFeatures = false;
        var discovery = new ApexOledPanelDiscovery(hid, clock: () => Interlocked.Read(ref now));
        var infos = discovery.Discover();
        using var failing = discovery.CreateTransport(infos[0]);
        failing.Open();
        Assert.Throws<IOException>(() => failing.Write(Frame(255)));

        var left = Assert.Single(discovery.Discover());
        Assert.EndsWith(":B", left.Serial);
    }

    [Fact]
    public void A_keyboard_that_leaves_starts_over_when_it_returns()
    {
        long now = 1000;
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.AcceptFeatures = false;
        var discovery = new ApexOledPanelDiscovery(hid, clock: () => Interlocked.Read(ref now));
        var info = Assert.Single(discovery.Discover());
        using var transport = discovery.CreateTransport(info);
        transport.Open();
        Assert.Throws<IOException>(() => transport.Write(Frame(255)));
        Assert.Empty(discovery.Discover());

        var infos = hid.Infos.ToList();
        hid.Infos.Clear();
        Assert.Empty(discovery.Discover());
        hid.Infos.AddRange(infos);

        Assert.Single(discovery.Discover());
    }

    [Fact]
    public void A_gap_between_passes_means_the_gate_was_off_and_clears_the_delay()
    {
        long now = 1000;
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.AcceptFeatures = false;
        var discovery = new ApexOledPanelDiscovery(hid, clock: () => Interlocked.Read(ref now));
        var info = Assert.Single(discovery.Discover());
        using var transport = discovery.CreateTransport(info);
        transport.Open();
        Assert.Throws<IOException>(() => transport.Write(Frame(255)));
        Assert.Empty(discovery.Discover());

        Interlocked.Add(ref now, 20_000);

        Assert.Single(discovery.Discover());
    }

    [Fact]
    public void A_keyboard_without_a_usb_serial_keeps_a_key_that_survives_a_replug()
    {
        var hid = new FakeHid();
        hid.Add(0x1628, serial: null);
        var first = Assert.Single(ApexOledScreens.Find(hid)).Key;
        hid.Infos.Clear();
        hid.Screens.Clear();
        hid.Add(0x1628, serial: null, featureLength: 700);

        Assert.Equal(first, Assert.Single(ApexOledScreens.Find(hid)).Key);
    }

    [Fact]
    public void A_stuck_hand_back_during_dispose_does_not_leak_the_handle()
    {
        var hid = new FakeHid();
        var screen = hid.Add(0x1628);
        screen.HoldOutput = new ManualResetEventSlim(false);
        var state = new LcdLockState();
        var transport = Create(hid, 0x1628, state, disposeWaitMs: 50);
        transport.Open();
        transport.StartPlayer();
        state.Set(true);
        Assert.True(screen.InOutput.Wait(3000));

        transport.Dispose();
        Assert.False(screen.Disposed);

        screen.HoldOutput.Set();
        Assert.True(WaitFor(() => screen.Disposed));
    }
}

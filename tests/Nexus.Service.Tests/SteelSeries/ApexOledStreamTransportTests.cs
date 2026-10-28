using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public bool AcceptFeatures = true;
        public bool Disposed;

        public int VendorId => ApexOledProtocol.VendorId;
        public int ProductId { get; init; }
        public string Path => "fake-apex";
        public string? Serial => "SN1";
        public int UsagePage => ApexOledProtocol.ControlUsagePage;
        public int Usage => 1;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            Features.Add(report.ToArray());
            return AcceptFeatures;
        }

        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Outputs.Add(report.ToArray());
            return true;
        }

        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeHid : IHidEnumerator
    {
        public readonly List<HidDeviceInfo> Infos = new();
        public readonly Dictionary<string, FakeScreen> Screens = new();

        public FakeScreen Add(int pid, int usagePage = ApexOledProtocol.ControlUsagePage, string? serial = "SN1")
        {
            var path = $"fake-{pid:x}-{usagePage:x}";
            Infos.Add(new HidDeviceInfo { VendorId = ApexOledProtocol.VendorId, ProductId = pid, Path = path, UsagePage = usagePage, Serial = serial });
            return Screens[path] = new FakeScreen { ProductId = pid };
        }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            Infos.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();

        public IReadOnlyList<HidDeviceInfo> FindAll() => Infos;
        public IHidDevice? Open(string path, bool forInput = false) => Screens.GetValueOrDefault(path);
    }

    private static ApexOledStreamTransport Open(FakeHid hid, int pid, LcdLockState? state = null, Func<long>? clock = null)
    {
        var transport = new ApexOledStreamTransport(hid, ApexOledProtocol.ModelFor(pid)!, "apex", state, clock);
        transport.Open();
        return transport;
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
        var transport = new ApexOledStreamTransport(hid, ApexOledProtocol.ModelFor(0x1628)!, "apex");

        Assert.Throws<IOException>(transport.Open);
    }
}

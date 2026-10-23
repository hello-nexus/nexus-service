using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>Hub sequencing on a scripted HID layer: what goes on the wire, in what order, and that onboard resources are never touched.</summary>
public class GalleonSdHubTests
{
    private sealed class ScriptedDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public Queue<byte[]> Replies { get; } = new();
        public Func<byte[], byte[]?>? OnWrite { get; set; }
        public bool FailWrites { get; set; }
        public bool Disposed { get; private set; }

        public int VendorId => GalleonSdProtocol.VendorId;
        public int ProductId => GalleonSdProtocol.ProductId;
        public string Path => "galleon";
        public string? Serial => "SN1";
        public int UsagePage => GalleonSdProtocol.VendorUsagePage;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            if (FailWrites) return false;
            var wire = report[1..].ToArray();
            Writes.Add(wire);
            var reply = OnWrite?.Invoke(wire);
            if (reply is not null) Replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (!Replies.TryDequeue(out var r)) return 0;
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() => Disposed = true;
    }

    private sealed class ScriptedHid : IHidEnumerator
    {
        public ScriptedDevice Device { get; } = new();
        public int OutLen { get; set; } = 129;
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => FindAll();
        public IReadOnlyList<HidDeviceInfo> FindAll() => new[]
        {
            new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x2B0C, Path = "galleon", Serial = "SN1", UsagePage = 0xFF42, OutputReportByteLength = OutLen, InputReportByteLength = OutLen },
            new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x2B0C, Path = "kbd", Serial = "SN1", UsagePage = 1, OutputReportByteLength = 2 },
        };
        public IHidDevice? Open(string path, bool forInput = false) => path == "galleon" ? Device : null;
    }

    private static long _clock;

    private static GalleonSdHub NewHub(ScriptedHid hid) => new(hid, () => _clock);

    private static IEnumerable<byte[]> Commands(ScriptedDevice d, byte cmd) => d.Writes.Where(w => w[3] == cmd);

    private static readonly RgbColor[] Frame = Enumerable.Repeat(new RgbColor(10, 20, 30), GalleonSdProtocol.LedCount).ToArray();

    [Fact]
    public void Reconcile_opens_only_the_128_byte_command_interface_and_probes_identity_with_gets()
    {
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);

        Assert.True(hub.Reconcile(true));

        Assert.Equal("corsair-galleon:SN1", hub.Attached!.DeviceId);
        Assert.All(hid.Device.Writes, w => Assert.Equal(GalleonSdProtocol.CmdGet, w[3]));
        Assert.Equal(new byte[] { 0x12, 0x11, 0x41, 0x5F, 0xE1 }, hid.Device.Writes.Select(w => w[4]));
    }

    [Fact]
    public void First_frame_enters_software_mode_then_opens_only_the_direct_resource()
    {
        _clock = 1_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        Assert.True(hub.WriteFrame(Frame));

        var w = hid.Device.Writes;
        Assert.Equal(new byte[] { 0, 1, 0, 0x01, 0x03, 0x00, 0x02 }, w[0][..7]);
        Assert.Equal(new byte[] { 0, 1, 0, 0x0D, 0x00, 0x22, 0x00 }, w[1][..7]);
        Assert.Equal(GalleonSdProtocol.CmdBlockFirst, w[2][3]);
        Assert.Equal(new[] { GalleonSdProtocol.CmdBlockNext }, w.Skip(3).Take(1).Select(x => x[3]).Distinct());
        Assert.Equal(GalleonSdProtocol.CmdStopTx, w[^1][3]);
        Assert.Equal(1 + 1 + 4 + 1, w.Count);
        foreach (var start in Commands(hid.Device, GalleonSdProtocol.CmdStartTx))
        {
            Assert.Equal(0x22, start[5]);
            Assert.Equal(0x00, start[6]);
        }
        Assert.Empty(Commands(hid.Device, 0x0C));
    }

    [Fact]
    public void Release_returns_to_hardware_mode_and_never_writes_onboard_resources()
    {
        _clock = 2_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Frame);

        hub.ReleaseToHardware();

        var last = hid.Device.Writes[^1];
        Assert.Equal(new byte[] { 0, 1, 0, 0x01, 0x03, 0x00, 0x01 }, last[..7]);
        Assert.All(Commands(hid.Device, GalleonSdProtocol.CmdStartTx), s => Assert.Equal(0x0022, s[5] | (s[6] << 8)));
        Assert.All(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst), b => Assert.Equal(0, b[4]));
    }

    [Fact]
    public void Release_without_ever_taking_the_board_sends_nothing()
    {
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        var before = hid.Device.Writes.Count;

        hub.ReleaseToHardware();
        hub.Reconcile(false);

        Assert.Equal(before, hid.Device.Writes.Count);
        Assert.Null(hub.Attached);
        Assert.True(hid.Device.Disposed);
    }

    [Fact]
    public void Gate_off_after_streaming_hands_the_board_back_before_closing()
    {
        _clock = 3_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Frame);

        hub.Reconcile(false);

        Assert.Equal(GalleonSdProtocol.ModeHardware, hid.Device.Writes[^1][6]);
        Assert.True(hid.Device.Disposed);
    }

    [Fact]
    public void Rejected_start_reply_retries_the_next_handle()
    {
        _clock = 4_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        var dev = hid.Device;
        dev.Writes.Clear();
        dev.OnWrite = wire =>
        {
            var reply = new byte[128];
            reply[3] = wire[3];
            if (wire[3] == GalleonSdProtocol.CmdStartTx && wire[4] == 0) reply[4] = 3;
            return reply;
        };

        hub.WriteFrame(Frame);

        var starts = Commands(dev, GalleonSdProtocol.CmdStartTx).ToList();
        Assert.Equal(new byte[] { 0, 1 }, starts.Select(x => x[4]));
        Assert.Equal(1, Commands(dev, GalleonSdProtocol.CmdBlockFirst).Single()[4]);
    }

    [Fact]
    public void Unchanged_frames_inside_the_keepalive_window_are_not_resent()
    {
        _clock = 5_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Frame);
        var count = hid.Device.Writes.Count;

        _clock += System.Diagnostics.Stopwatch.Frequency; // 1 s later, identical colors
        Assert.True(hub.WriteFrame(Frame));

        Assert.Equal(count, hid.Device.Writes.Count);
    }

    [Fact]
    public void Five_consecutive_write_failures_drop_the_interface()
    {
        _clock = 6_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.FailWrites = true;

        for (var i = 0; i < 5; i++)
        {
            _clock += System.Diagnostics.Stopwatch.Frequency;
            Assert.False(hub.WriteFrame(Frame));
        }

        Assert.Null(hub.Attached);
    }
}

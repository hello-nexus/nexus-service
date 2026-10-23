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
        public bool FailWritesAfterRecording { get; set; }
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
            if (FailWritesAfterRecording)
            {
                OnWrite?.Invoke(wire);
                return false;
            }
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

    private long _clock;

    private GalleonSdHub NewHub(ScriptedHid hid) => new(hid, () => _clock);

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
    public void Rejected_start_reply_retries_the_next_handle_without_stopping_the_rejected_one()
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
        Assert.Equal(new byte[] { 1 }, Commands(dev, GalleonSdProtocol.CmdStopTx).Select(x => x[5]));
    }

    [Fact]
    public void Start_rejected_on_every_handle_writes_no_data_and_renegotiates_later()
    {
        _clock = 4_500_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        var dev = hid.Device;
        dev.Writes.Clear();
        dev.OnWrite = wire =>
        {
            var reply = new byte[128];
            reply[3] = wire[3];
            if (wire[3] == GalleonSdProtocol.CmdStartTx) reply[4] = 3;
            return reply;
        };

        Assert.True(hub.WriteFrame(Frame));

        Assert.Equal(GalleonSdProtocol.MaxHandle + 1, Commands(dev, GalleonSdProtocol.CmdStartTx).Count());
        Assert.Empty(Commands(dev, GalleonSdProtocol.CmdBlockFirst));
        Assert.Empty(Commands(dev, GalleonSdProtocol.CmdBlockNext));
        Assert.Empty(Commands(dev, GalleonSdProtocol.CmdStopTx));

        dev.OnWrite = wire =>
        {
            var ok = new byte[128];
            ok[3] = wire[3];
            return ok;
        };
        _clock += 10 * System.Diagnostics.Stopwatch.Frequency;
        Assert.True(hub.WriteFrame(Frame));
        Assert.Single(Commands(dev, GalleonSdProtocol.CmdBlockFirst));
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

        _clock += System.Diagnostics.Stopwatch.Frequency;
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

    [Fact]
    public void Dropping_after_io_failures_attempts_a_hardware_mode_handback_first()
    {
        _clock = 7_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Frame);
        var dev = hid.Device;
        var attempted = 0;
        dev.OnWrite = wire =>
        {
            if (wire[3] == GalleonSdProtocol.CmdSet && wire[6] == GalleonSdProtocol.ModeHardware) attempted++;
            return null;
        };
        dev.FailWritesAfterRecording = true;

        for (var i = 0; i < 5; i++)
        {
            _clock += System.Diagnostics.Stopwatch.Frequency;
            hub.WriteFrame(Enumerable.Repeat(new RgbColor((byte)(i + 50), 0, 0), GalleonSdProtocol.LedCount).ToArray());
        }

        Assert.Null(hub.Attached);
        Assert.Equal(1, attempted);
    }

    private static Func<byte[], byte[]?> RejectStarts(Func<bool> reject) => wire =>
    {
        var reply = new byte[128];
        reply[3] = wire[3];
        if (reject() && wire[3] == GalleonSdProtocol.CmdStartTx) reply[4] = 3;
        return reply;
    };

    [Fact]
    public void Settled_handle_rejected_mid_stream_backs_off_then_renegotiates()
    {
        _clock = 8_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        var rejecting = false;
        hid.Device.OnWrite = RejectStarts(() => rejecting);
        hub.WriteFrame(Frame);
        hid.Device.Writes.Clear();

        rejecting = true;
        _clock += System.Diagnostics.Stopwatch.Frequency * 11;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(1, 1, 1), GalleonSdProtocol.LedCount).ToArray());
        Assert.Empty(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst));
        var startsAfterReject = Commands(hid.Device, GalleonSdProtocol.CmdStartTx).Count();

        _clock += System.Diagnostics.Stopwatch.Frequency / 2;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(2, 2, 2), GalleonSdProtocol.LedCount).ToArray());
        Assert.Equal(startsAfterReject, Commands(hid.Device, GalleonSdProtocol.CmdStartTx).Count());

        rejecting = false;
        _clock += System.Diagnostics.Stopwatch.Frequency * 3;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(3, 3, 3), GalleonSdProtocol.LedCount).ToArray());
        Assert.Single(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst));
    }

    [Fact]
    public void Repeated_failed_negotiations_hand_the_board_back_and_stop_reasserting_software_mode()
    {
        _clock = 9_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.OnWrite = RejectStarts(() => true);
        var dev = hid.Device;

        for (var i = 0; i < 3; i++)
        {
            _clock += System.Diagnostics.Stopwatch.Frequency * 3;
            hub.WriteFrame(Enumerable.Repeat(new RgbColor((byte)(i + 1), 0, 0), GalleonSdProtocol.LedCount).ToArray());
        }

        Assert.Equal(GalleonSdProtocol.ModeHardware, dev.Writes[^1][6]);
        var modeSets = Commands(dev, GalleonSdProtocol.CmdSet).Count();
        _clock += System.Diagnostics.Stopwatch.Frequency / 2;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(9, 0, 0), GalleonSdProtocol.LedCount).ToArray());
        Assert.Equal(modeSets, Commands(dev, GalleonSdProtocol.CmdSet).Count());
        Assert.Empty(Commands(dev, GalleonSdProtocol.CmdBlockFirst));
    }

    [Fact]
    public void Replies_that_arrive_but_never_parse_fail_the_start_closed()
    {
        _clock = 10_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.OnWrite = wire => new byte[128];
        hid.Device.Writes.Clear();

        hub.WriteFrame(Frame);

        Assert.Empty(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst));
        Assert.Empty(Commands(hid.Device, GalleonSdProtocol.CmdBlockNext));
    }

    [Fact]
    public void A_late_reply_from_before_the_start_is_drained_and_not_read_as_its_ok()
    {
        _clock = 11_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        var staleOk = new byte[128];
        staleOk[3] = GalleonSdProtocol.CmdStartTx;
        hid.Device.Replies.Enqueue(staleOk);
        hid.Device.OnWrite = RejectStarts(() => true);
        hid.Device.Writes.Clear();

        hub.WriteFrame(Frame);

        Assert.Empty(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst));
    }

    private void HandBack(ScriptedHid hid, GalleonSdHub hub, Func<bool> rejecting)
    {
        hid.Device.OnWrite = RejectStarts(rejecting);
        for (var i = 0; i < 3; i++)
        {
            _clock += System.Diagnostics.Stopwatch.Frequency * 3;
            hub.WriteFrame(Enumerable.Repeat(new RgbColor((byte)(i + 1), 0, 0), GalleonSdProtocol.LedCount).ToArray());
        }
    }

    [Fact]
    public void Retries_after_a_handback_back_off_with_doubling_waits()
    {
        _clock = 12_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        HandBack(hid, hub, () => true);
        var freq = System.Diagnostics.Stopwatch.Frequency;
        var sets = Commands(hid.Device, GalleonSdProtocol.CmdSet).Count();

        _clock += freq * 5 / 2;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(7, 0, 0), GalleonSdProtocol.LedCount).ToArray());
        Assert.Equal(sets, Commands(hid.Device, GalleonSdProtocol.CmdSet).Count());

        _clock += freq * 2;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(8, 0, 0), GalleonSdProtocol.LedCount).ToArray());
        var afterRetry = Commands(hid.Device, GalleonSdProtocol.CmdSet).Count();
        Assert.True(afterRetry > sets);

        _clock += freq * 5;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(9, 0, 0), GalleonSdProtocol.LedCount).ToArray());
        Assert.Equal(afterRetry, Commands(hid.Device, GalleonSdProtocol.CmdSet).Count());

        _clock += freq * 4;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(10, 0, 0), GalleonSdProtocol.LedCount).ToArray());
        Assert.True(Commands(hid.Device, GalleonSdProtocol.CmdSet).Count() > afterRetry);
    }

    [Fact]
    public void Streaming_resumes_once_the_board_accepts_a_start_after_a_handback()
    {
        _clock = 13_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        var rejecting = true;
        HandBack(hid, hub, () => rejecting);
        Assert.Empty(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst));

        rejecting = false;
        _clock += System.Diagnostics.Stopwatch.Frequency * 5;
        hub.WriteFrame(Enumerable.Repeat(new RgbColor(50, 0, 0), GalleonSdProtocol.LedCount).ToArray());

        Assert.Single(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst));
        Assert.Equal(GalleonSdProtocol.ModeSoftware, Commands(hid.Device, GalleonSdProtocol.CmdSet).Last()[6]);
    }

    [Fact]
    public void Dispose_for_shutdown_hands_back_hardware_mode_and_blocks_a_reconnect()
    {
        _clock = 14_000_000_000;
        var hid = new ScriptedHid();
        var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Frame);

        hub.Dispose();

        Assert.Equal(GalleonSdProtocol.ModeHardware, hid.Device.Writes[^1][6]);
        Assert.True(hid.Device.Disposed);
        hid.Device.Writes.Clear();
        Assert.False(hub.Reconcile(true));
        Assert.False(hub.WriteFrame(Frame));
        Assert.Empty(hid.Device.Writes);
    }
}

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

    private GalleonSdHub NewHub(ScriptedHid hid) => new(hid, () => _clock, matchOnLinux: true);

    private static IEnumerable<byte[]> Commands(ScriptedDevice d, byte cmd) => d.Writes.Where(w => w[3] == cmd);

    private static readonly RgbColor[] Frame = Enumerable.Repeat(new RgbColor(10, 20, 30), GalleonSdProtocol.LedCount).ToArray();

    [Fact]
    public void Linux_attaches_nothing_by_default_until_the_report_length_is_validated_there()
    {
        using var hub = new GalleonSdHub(new ScriptedHid(), () => _clock);

        Assert.Equal(!OperatingSystem.IsLinux(), hub.Reconcile(true));
        Assert.Equal(!OperatingSystem.IsLinux(), hub.IsConnected);
    }

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

    private static byte[] Reply(byte cmd, params byte[] body)
    {
        var r = new byte[128];
        r[1] = 1;
        r[3] = cmd;
        body.CopyTo(r, 8);
        return r;
    }

    private static readonly byte[] Registry = { 0x13, 0x00, 0x02, 0x00, 0x60, 0x6D, 0x65, 0x6D };
    private static readonly byte[] Profile = { 0x1E, 0xF1, 1, 2, 3, 4, 0x66, 0x6D, 0x61, 0x6D, 0, 0, 0x62, 0x6D, 0x63, 0x6D, 0, 0 };
    private static readonly byte[] LayerList = { 0x69, 0x6C, 0x02, 0x00, 0x08, 0x00, 0x6B, 0x6D, 0x08, 0x00, 0x6F, 0x6D };

    // Replays the board's object store: reads answer with the object last opened.
    private static Func<byte[], byte[]?> ObjectStore(List<ushort> opened)
    {
        ushort current = 0;
        return wire =>
        {
            switch (wire[3])
            {
                case GalleonSdProtocol.CmdStartTx:
                    current = (ushort)(wire[5] | (wire[6] << 8));
                    opened.Add(current);
                    return Reply(wire[3]);
                case GalleonSdProtocol.CmdRead:
                    return current switch
                    {
                        0x000F => Reply(wire[3], Registry),
                        0x6D60 => Reply(wire[3], Profile),
                        0x6D62 => Reply(wire[3], LayerList),
                        _ => Reply(wire[3], 0x7E, 0x20),
                    };
                default:
                    return Reply(wire[3]);
            }
        };
    }

    private static readonly byte[] ForbiddenCommands = { 0x06, 0x07, 0x0B, 0x0C };

    [Fact]
    public void Saved_mode_sends_no_direct_frames_and_holds_hardware_mode()
    {
        _clock = 5_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);
        Assert.True(hub.WriteFrame(Frame));
        hub.SetSavedToDevice(true);

        Assert.DoesNotContain(Commands(hid.Device, GalleonSdProtocol.CmdStartTx), w => (w[5] | (w[6] << 8)) == GalleonSdProtocol.DirectResource);
        Assert.DoesNotContain(hid.Device.Writes, w => w[3] is 0x06 or 0x07);
        var modes = Commands(hid.Device, GalleonSdProtocol.CmdSet).Select(w => w[6]).ToArray();
        Assert.Equal(GalleonSdProtocol.ModeHardware, modes[0]);
        Assert.Equal(GalleonSdProtocol.ModeHardware, modes[^1]);
    }

    [Fact]
    public void Saved_mode_writes_software_mode_only_inside_the_probe_bracket()
    {
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);
        hub.SetSavedToDevice(true);

        var sets = hid.Device.Writes.Select((w, i) => (w, i)).Where(x => x.w[3] == GalleonSdProtocol.CmdSet).ToList();
        var software = sets.Where(x => x.w[6] == GalleonSdProtocol.ModeSoftware).ToList();
        Assert.Single(software);
        var bracketStart = software[0].i;
        var bracketEnd = sets.Last(x => x.w[6] == GalleonSdProtocol.ModeHardware).i;
        Assert.True(bracketEnd > bracketStart);
        Assert.Equal(GalleonSdProtocol.ModeHardware, sets[^1].w[6]);
        Assert.Equal(1, hid.Device.Writes.Count(w => w[3] == GalleonSdProtocol.CmdGet && w[4] == 0x6E));
    }

    [Fact]
    public void Probe_sends_only_read_commands_and_follows_registry_profile_layer_list_and_layers()
    {
        var hid = new ScriptedHid();
        var opened = new List<ushort>();
        hid.Device.OnWrite = ObjectStore(opened);
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);

        Assert.DoesNotContain(hid.Device.Writes, w => ForbiddenCommands.Contains(w[3]));
        var allowed = new byte[] { GalleonSdProtocol.CmdSet, GalleonSdProtocol.CmdGet, GalleonSdProtocol.CmdStartTx, GalleonSdProtocol.CmdSize, GalleonSdProtocol.CmdRead, GalleonSdProtocol.CmdStopTx };
        Assert.All(hid.Device.Writes, w => Assert.Contains(w[3], allowed));
        Assert.Equal(new ushort[] { 0x000F, 0x6D60, 0x6D62, 0x6D6B, 0x6D6F, 0x6D65 }, opened);
    }

    [Fact]
    public void Probe_runs_once_per_session()
    {
        var hid = new ScriptedHid();
        var opened = new List<ushort>();
        hid.Device.OnWrite = ObjectStore(opened);
        using var hub = NewHub(hid);
        hub.Reconcile(true);

        hub.SetSavedToDevice(true);
        var after = opened.Count;
        hub.SetSavedToDevice(false);
        hub.SetSavedToDevice(true);

        Assert.True(after > 0);
        Assert.Equal(after, opened.Count);
    }

    [Fact]
    public void Turning_saved_mode_off_resumes_the_live_stream_immediately()
    {
        _clock = 6_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.SetSavedToDevice(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(false);
        Assert.True(hub.WriteFrame(Frame));

        var w = hid.Device.Writes;
        Assert.Equal(GalleonSdProtocol.CmdSet, w[0][3]);
        Assert.Equal(GalleonSdProtocol.ModeSoftware, w[0][6]);
        Assert.Single(Commands(hid.Device, GalleonSdProtocol.CmdBlockFirst));
    }

    [Fact]
    public void Saved_mode_after_streaming_hands_the_board_back_and_stops_frames()
    {
        _clock = 7_000_000_000;
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Frame);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);
        _clock += 5_000_000_000;
        hub.WriteFrame(Frame);

        Assert.Equal(GalleonSdProtocol.ModeHardware, Commands(hid.Device, GalleonSdProtocol.CmdSet).First()[6]);
        Assert.DoesNotContain(hid.Device.Writes, w => w[3] is 0x06 or 0x07);
    }

    [Fact]
    public void Refused_open_is_followed_by_no_size_read_or_close_and_hardware_is_restored()
    {
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.OnWrite = wire =>
        {
            var r = Reply(wire[3]);
            if (wire[3] == GalleonSdProtocol.CmdStartTx) r[4] = 3;
            return r;
        };
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);

        Assert.Single(Commands(hid.Device, GalleonSdProtocol.CmdStartTx));
        Assert.DoesNotContain(hid.Device.Writes, w => w[3] is GalleonSdProtocol.CmdSize or GalleonSdProtocol.CmdRead or GalleonSdProtocol.CmdStopTx);
        Assert.Equal(GalleonSdProtocol.ModeHardware, Commands(hid.Device, GalleonSdProtocol.CmdSet).Last()[6]);
    }

    [Fact]
    public void Unanswered_open_gets_one_close_in_case_it_opened_late_and_stops_the_probe()
    {
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);

        Assert.Single(Commands(hid.Device, GalleonSdProtocol.CmdStartTx));
        Assert.Single(Commands(hid.Device, GalleonSdProtocol.CmdStopTx));
        Assert.DoesNotContain(hid.Device.Writes, w => w[3] is GalleonSdProtocol.CmdSize or GalleonSdProtocol.CmdRead);
    }

    [Fact]
    public void Every_open_has_a_matching_close_in_order()
    {
        var hid = new ScriptedHid();
        hid.Device.OnWrite = ObjectStore(new List<ushort>());
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);

        var txn = hid.Device.Writes.Where(w => w[3] is GalleonSdProtocol.CmdStartTx or GalleonSdProtocol.CmdStopTx).Select(w => w[3]).ToArray();
        Assert.NotEmpty(txn);
        for (var i = 0; i < txn.Length; i++)
        {
            Assert.Equal(i % 2 == 0 ? GalleonSdProtocol.CmdStartTx : GalleonSdProtocol.CmdStopTx, txn[i]);
        }
        Assert.Equal(0, txn.Length % 2);
    }

    [Fact]
    public void Unsolicited_input_report_during_a_read_is_not_used_as_the_body()
    {
        var hid = new ScriptedHid();
        var opened = new List<ushort>();
        var store = ObjectStore(opened);
        hid.Device.OnWrite = wire =>
        {
            if (wire[3] == GalleonSdProtocol.CmdRead)
            {
                var noise = Reply(0x77, Registry);
                hid.Device.Replies.Enqueue(noise);
                return Reply(wire[3]);
            }
            return store(wire);
        };
        using var hub = NewHub(hid);
        hub.Reconcile(true);

        hub.SetSavedToDevice(true);

        Assert.Equal(new ushort[] { 0x000F }, opened);
    }

    [Fact]
    public void A_failed_hardware_restore_is_retried_and_release_still_writes_hardware_mode()
    {
        var hid = new ScriptedHid();
        var store = ObjectStore(new List<ushort>());
        hid.Device.OnWrite = wire =>
        {
            var reply = store(wire);
            if (wire[3] == GalleonSdProtocol.CmdStopTx) hid.Device.FailWrites = true;
            return reply;
        };
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);
        var modesAfterProbe = Commands(hid.Device, GalleonSdProtocol.CmdSet).Select(w => w[6]).ToArray();
        Assert.Equal(GalleonSdProtocol.ModeSoftware, modesAfterProbe[^1]);

        hid.Device.FailWrites = false;
        hub.ReleaseToHardware();

        Assert.Equal(GalleonSdProtocol.ModeHardware, Commands(hid.Device, GalleonSdProtocol.CmdSet).Last()[6]);
    }

    [Fact]
    public void A_failing_interface_in_saved_mode_is_dropped_instead_of_retried_forever()
    {
        var hid = new ScriptedHid();
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.FailWrites = true;

        for (var i = 0; i < 10; i++) hub.SetSavedToDevice(true);

        Assert.Null(hub.Attached);
    }

    [Fact]
    public void Probe_is_skipped_when_the_direct_resource_was_contended()
    {
        _clock = 8_000_000_000;
        var hid = new ScriptedHid();
        var opened = new List<ushort>();
        hid.Device.OnWrite = wire =>
        {
            var r = ObjectStore(opened)(wire);
            if (r is not null && wire[3] == GalleonSdProtocol.CmdStartTx) r[4] = 3;
            return r;
        };
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hub.WriteFrame(Frame);
        opened.Clear();

        hub.SetSavedToDevice(true);

        Assert.Empty(opened);
    }

    [Fact]
    public void Error_status_to_the_saved_mode_hold_is_counted_and_the_interface_dropped_not_resent_forever()
    {
        var hid = new ScriptedHid();
        hid.Device.OnWrite = wire =>
        {
            var r = Reply(wire[3]);
            if (wire[3] == GalleonSdProtocol.CmdSet) r[4] = 3;
            return r;
        };
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        for (var i = 0; i < 20; i++) hub.SetSavedToDevice(true);

        Assert.Null(hub.Attached);
        Assert.True(Commands(hid.Device, GalleonSdProtocol.CmdSet).Count() <= 6);
        Assert.Empty(Commands(hid.Device, GalleonSdProtocol.CmdStartTx));
    }

    [Fact]
    public void Probe_never_starts_on_an_unconfirmed_restore_and_a_retry_that_also_errors_is_bounded()
    {
        var hid = new ScriptedHid();
        var opened = new List<ushort>();
        var store = ObjectStore(opened);
        var bracket = false;
        hid.Device.OnWrite = wire =>
        {
            var r = store(wire);
            if (wire[3] == GalleonSdProtocol.CmdSet && wire[6] == GalleonSdProtocol.ModeSoftware) bracket = true;
            else if (wire[3] == GalleonSdProtocol.CmdSet && bracket && r is not null) r[4] = 3;
            return r;
        };
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        for (var i = 0; i < 20; i++) hub.SetSavedToDevice(true);

        Assert.Equal(1, opened.Count(r => r == 0x000F));
        Assert.Equal(1, Commands(hid.Device, GalleonSdProtocol.CmdSet).Count(w => w[6] == GalleonSdProtocol.ModeSoftware));
        Assert.Null(hub.Attached);
        Assert.True(Commands(hid.Device, GalleonSdProtocol.CmdSet).Count(w => w[6] == GalleonSdProtocol.ModeHardware) <= 12);
    }

    [Fact]
    public void Disconnect_between_resources_stops_the_walk_and_writes_hardware_mode_once()
    {
        var hid = new ScriptedHid();
        var opened = new List<ushort>();
        var store = ObjectStore(opened);
        GalleonSdHub? hubRef = null;
        var fired = false;
        hid.Device.OnWrite = wire =>
        {
            var r = store(wire);
            if (wire[3] == GalleonSdProtocol.CmdStopTx && !fired)
            {
                fired = true;
                hubRef!.Disconnect();
            }
            return r;
        };
        using var hub = NewHub(hid);
        hubRef = hub;
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);

        Assert.Equal(new ushort[] { 0x000F }, opened);
        Assert.Null(hub.Attached);
        var afterClose = hid.Device.Writes.SkipWhile(w => w[3] != GalleonSdProtocol.CmdStopTx).Where(w => w[3] == GalleonSdProtocol.CmdSet).ToList();
        Assert.Single(afterClose);
        Assert.Equal(GalleonSdProtocol.ModeHardware, afterClose[0][6]);
    }

    [Fact]
    public void Probe_stops_at_its_time_budget_and_still_closes_and_restores()
    {
        _clock = 9_000_000_000;
        var hid = new ScriptedHid();
        var opened = new List<ushort>();
        var store = ObjectStore(opened);
        hid.Device.OnWrite = wire =>
        {
            _clock += System.Diagnostics.Stopwatch.Frequency * 4 / 10;
            return store(wire);
        };
        using var hub = NewHub(hid);
        hub.Reconcile(true);
        hid.Device.Writes.Clear();

        hub.SetSavedToDevice(true);

        Assert.Equal(new ushort[] { 0x000F }, opened);
        Assert.Single(Commands(hid.Device, GalleonSdProtocol.CmdStopTx));
        Assert.DoesNotContain(hid.Device.Writes, w => w[3] is GalleonSdProtocol.CmdSize or GalleonSdProtocol.CmdRead);
        Assert.Equal(GalleonSdProtocol.ModeHardware, Commands(hid.Device, GalleonSdProtocol.CmdSet).Last()[6]);
    }
}

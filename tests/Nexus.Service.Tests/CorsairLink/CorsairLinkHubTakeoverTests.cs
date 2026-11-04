using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkHubTakeoverTests
{
    /// <summary>Answers like the measured hub: status 07 on software mode until a hardware mode command has been seen (when <see cref="RefuseUntilHardware"/>), 03 on an unopened handle.</summary>
    private sealed class TakeoverHub : IHidDevice
    {
        public readonly List<byte[]> Commands = new();
        public readonly HashSet<byte> OpenHandles = new();
        public byte Mode = CorsairLinkProtocol.ModeHardware;
        public bool RefuseUntilHardware;
        public bool NeverAcceptSoftware;
        public Action<byte[]>? OnCommand;
        /// <summary>Withholds the first software-mode reply (the read times out) and delivers it ahead of the next command's reply.</summary>
        public bool DelayFirstSoftwareReply;
        /// <summary>Withholds the first reply to a command matching this predicate, with the same late delivery.</summary>
        public Func<byte[], bool>? HoldFirstWhere;
        /// <summary>Commands matching this predicate get no reply at all, as when the reply is lost with a closed handle.</summary>
        public Func<byte[], bool>? DropWhere;
        /// <summary>Runs after a held reply is queued and before the current command's own reply.</summary>
        public Action<byte[]>? OnBeforeReply;
        /// <summary>Overrides the echoed command byte of the firmware reply.</summary>
        public byte? FirmwareEcho;
        /// <summary>The firmware reply is only readable by a read waiting at least this long.</summary>
        public int FirmwareReplyDelayMs;
        /// <summary>Per-command reply latency; a read shorter than it fails but the time it waited counts toward the next read.</summary>
        public Func<byte[], int>? ReplyDelayMs;
        /// <summary>Reads that waited the full timeout and got nothing.</summary>
        public int EmptyReads;
        private byte[]? _held;
        private bool _heldOnce;
        private bool _hardwareSeen;
        private readonly Dictionary<byte, byte> _resource = new();
        private readonly List<(byte[] Report, int NeedMs)> _responses = new();

        public int VendorId => CorsairLinkProtocol.VendorId;
        public int ProductId => CorsairLinkProtocol.ProductId;
        public string Path => "fake";
        public string? Serial => null;
        public int UsagePage => CorsairLinkProtocol.VendorUsagePage;
        public int Usage => CorsairLinkProtocol.VendorUsage;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var cmd = report.Slice(CorsairLinkProtocol.HeaderSize).ToArray();
            lock (Commands) Commands.Add(cmd);
            OnCommand?.Invoke(cmd);
            var status = 0;
            var payload = Array.Empty<byte>();
            switch (cmd[0])
            {
                case 0x02 when cmd[1] == 0x13:
                    payload = new byte[] { 4, 1, 0x90, 0x02 };
                    break;
                case 0x02 when cmd[1] == 0x03:
                    payload = new[] { Mode };
                    break;
                case 0x01 when cmd[3] == CorsairLinkProtocol.ModeHardware:
                    _hardwareSeen = true;
                    Mode = CorsairLinkProtocol.ModeHardware;
                    break;
                case 0x01:
                    var refused = NeverAcceptSoftware || (RefuseUntilHardware && !_hardwareSeen);
                    status = refused ? 7 : 0;
                    if (!refused) Mode = CorsairLinkProtocol.ModeSoftware;
                    break;
                case 0x09:
                    status = OpenHandles.Contains(cmd[1]) ? 0 : 3;
                    break;
                case 0x05:
                    OpenHandles.Remove(cmd[2]);
                    break;
                case 0x0D:
                    if (Mode != CorsairLinkProtocol.ModeSoftware || OpenHandles.Contains(cmd[1])) status = 3;
                    else
                    {
                        OpenHandles.Add(cmd[1]);
                        _resource[cmd[1]] = cmd[2];
                    }
                    break;
                case 0x08:
                    if (!_resource.TryGetValue(cmd[1], out var res) || !OpenHandles.Contains(cmd[1])) status = 3;
                    else payload = ReadResource(res);
                    break;
            }
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            var isFirmware = cmd[0] == 0x02 && cmd[1] == 0x13;
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = isFirmware && FirmwareEcho is { } fe ? fe : cmd[0];
            resp[1 + CorsairLinkProtocol.ResponseStatusOffset] = (byte)status;
            payload.CopyTo(resp, 1 + 4);
            if (DropWhere?.Invoke(cmd) ?? false) return true;
            lock (_responses)
            {
                var hold = !_heldOnce
                    && ((DelayFirstSoftwareReply && cmd[0] == 0x01 && cmd[3] == CorsairLinkProtocol.ModeSoftware)
                        || (HoldFirstWhere?.Invoke(cmd) ?? false));
                if (hold)
                {
                    _heldOnce = true;
                    _held = resp;
                    return true;
                }
                if (_held != null)
                {
                    _responses.Add((_held, 0));
                    _held = null;
                }
                OnBeforeReply?.Invoke(cmd);
                _responses.Add((resp, Math.Max(isFirmware ? FirmwareReplyDelayMs : 0, ReplyDelayMs?.Invoke(cmd) ?? 0)));
            }
            return true;
        }

        private static byte[] ReadResource(byte resource)
        {
            switch (resource)
            {
                case 0x36:
                    var serial = Encoding.ASCII.GetBytes("010018B88203729CFC00014F1F");
                    return new byte[] { 0x21, 0x00, 1, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, (byte)serial.Length }
                        .Concat(serial).ToArray();
                case 0x17:
                    return new byte[] { 0x25, 0x00, 2, 1, 0, 0, 0, 0xe0, 0x01 };
                case 0x21:
                    return new byte[] { 0x10, 0x00, 2, 1, 0, 0, 0, 0x0c, 0x01 };
                default:
                    return Array.Empty<byte>();
            }
        }

        public void Inject(byte echo)
        {
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = echo;
            lock (_responses) _responses.Add((resp, 0));
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            byte[] resp;
            lock (_responses)
            {
                if (_responses.Count == 0)
                {
                    EmptyReads++;
                    return 0;
                }
                var head = _responses[0];
                if (head.NeedMs > timeoutMs)
                {
                    _responses[0] = (head.Report, head.NeedMs - timeoutMs);
                    return 0;
                }
                resp = head.Report;
                _responses.RemoveAt(0);
            }
            resp.CopyTo(buffer);
            return resp.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private static CorsairLinkHub NewHub(TakeoverHub device, Func<long>? clock = null)
    {
        var hub = new CorsairLinkHub(modeCycleWaitMs: 0);
        if (clock is not null) hub.NowMs = clock;
        hub.Attach(device);
        return hub;
    }

    // Compact names for the commands the sequence tests compare.
    private static string Name(byte[] c) => c[0] switch
    {
        0x02 when c[1] == 0x03 => "mode?",
        0x02 when c[1] == 0x13 => "info",
        0x09 => $"probe{c[1]}",
        0x05 when c[1] == 0x01 => $"close{c[2]}",
        0x01 when c[3] == CorsairLinkProtocol.ModeSoftware => "SOFT",
        0x01 => "HARD",
        0x0D => $"open{c[1]}",
        _ => "",
    };

    private static string[] Sequence(TakeoverHub d, int skip = 0)
    {
        lock (d.Commands) return d.Commands.Skip(skip).Select(Name).Where(n => n.Length > 0).ToArray();
    }

    private static string Join(IEnumerable<string> names) => string.Join(" ", names);

    [Fact]
    public void A_late_software_mode_reply_does_not_shift_the_stream_into_a_false_foreign_host()
    {
        var device = new TakeoverHub { DelayFirstSoftwareReply = true };
        using var hub = NewHub(device);

        var ok = hub.Initialize();

        Assert.True(ok, hub.LastInitTrace);
        Assert.False(hub.ForeignHostSeen, hub.LastInitTrace);
        Assert.Equal("4.1.656", hub.State.Firmware);
    }

    [Fact]
    public void A_firmware_reply_that_does_not_echo_the_firmware_command_is_not_parsed()
    {
        var device = new TakeoverHub { FirmwareEcho = 0x01 };
        using var hub = NewHub(device);

        hub.Initialize();

        Assert.Equal("", hub.State.Firmware);
    }

    [Fact]
    public void A_late_mode_reply_owed_for_the_same_command_byte_is_skipped()
    {
        var device = new TakeoverHub { HoldFirstWhere = c => c[0] == 0x02 && c[1] == 0x03 };
        using var hub = NewHub(device);

        Assert.False(hub.Initialize(), "the first mode read times out");
        var ok = hub.Initialize();

        Assert.True(ok, hub.LastInitTrace);
        Assert.False(hub.ForeignHostSeen, hub.LastInitTrace);
        Assert.Equal("4.1.656", hub.State.Firmware);
    }

    [Fact]
    public void A_slow_own_reply_behind_a_late_reply_is_still_read()
    {
        var device = new TakeoverHub { DelayFirstSoftwareReply = true, FirmwareReplyDelayMs = 100 };
        using var hub = NewHub(device);

        var ok = hub.Initialize();

        Assert.True(ok, hub.LastInitTrace);
        Assert.False(hub.ForeignHostSeen, hub.LastInitTrace);
        Assert.Equal("4.1.656", hub.State.Firmware);
    }

    [Fact]
    public void A_reply_lost_for_good_does_not_swallow_the_next_command_of_that_byte()
    {
        var device = new TakeoverHub();
        using var hub = NewHub(device);
        Assert.True(hub.Initialize(), hub.LastInitTrace);
        var dropped = false;
        device.DropWhere = c =>
        {
            if (dropped || c[0] != 0x08) return false;
            dropped = true;
            return true;
        };

        hub.Poll();
        device.DropWhere = null;

        Assert.True(hub.Poll());
        Assert.True(hub.Poll());
        Assert.False(hub.ForeignHostSeen);
    }

    [Fact]
    public void A_mode_reply_lost_with_the_closed_handle_does_not_block_the_next_connect()
    {
        var device = new TakeoverHub();
        using var hub = NewHub(device);
        Assert.True(hub.Initialize(), hub.LastInitTrace);
        var modeReads = 0;
        device.DropWhere = c => c[0] == 0x02 && c[1] == 0x03 && ++modeReads == 2;

        hub.Detach();
        device.DropWhere = null;
        hub.Attach(device);
        var ok = hub.Initialize();

        Assert.True(ok, hub.LastInitTrace);
        Assert.False(hub.ForeignHostSeen, hub.LastInitTrace);
    }

    [Fact]
    public void Two_slow_same_byte_replies_in_a_row_leave_no_stray_reply_to_read_as_another_program()
    {
        var device = new TakeoverHub();
        var modeReads = 0;
        device.ReplyDelayMs = c => c[0] == 0x02 && c[1] == 0x03 && ++modeReads is 1 or 2 ? 300 : 0;
        using var hub = NewHub(device);

        Assert.True(hub.Transfer(CorsairLinkProtocol.CmdGetMode) <= 0, "the first reply is slower than the read");
        Assert.True(hub.Transfer(CorsairLinkProtocol.CmdGetMode) > 0);

        // The second reply is still on its way; it belongs to Nexus.
        Assert.False(hub.HearsOtherHost(device, 100, out var heard, out _), heard);
        Assert.False(hub.ForeignHostSeen);
    }

    [Fact]
    public void A_lost_reply_costs_one_empty_read_not_one_per_command()
    {
        var device = new TakeoverHub();
        var modeReads = 0;
        device.DropWhere = c => c[0] == 0x02 && c[1] == 0x03 && ++modeReads == 1;
        using var hub = NewHub(device);

        Assert.True(hub.Transfer(CorsairLinkProtocol.CmdGetMode) <= 0);
        var afterTimeout = device.EmptyReads;
        for (var i = 0; i < 5; i++) Assert.True(hub.Transfer(CorsairLinkProtocol.CmdGetMode) > 0);

        Assert.Equal(afterTimeout + 1, device.EmptyReads);
    }

    [Fact]
    public void A_lost_reply_debt_stops_discounting_reports_after_the_age_cap()
    {
        long now = 1_000_000;
        var device = new TakeoverHub();
        using var hub = NewHub(device, () => now);
        Assert.True(hub.Initialize(), hub.LastInitTrace);
        var dropped = false;
        device.DropWhere = c =>
        {
            if (dropped || c[0] != 0x08) return false;
            dropped = true;
            return true;
        };
        hub.Poll();
        device.DropWhere = null;
        hub.Poll();

        device.Inject(0x08);
        Assert.False(hub.HearsOtherHost(device, 50, out _, out _));
        device.Inject(0x08);
        now += 31_000;
        Assert.True(hub.HearsOtherHost(device, 50, out _, out _));
    }

    [Fact]
    public void A_foreign_echo_after_a_timed_out_command_still_marks_another_program()
    {
        var device = new TakeoverHub { DelayFirstSoftwareReply = true };
        device.OnBeforeReply = c =>
        {
            if (c[0] == 0x02 && c[1] == 0x13) device.Inject(0x77);
        };
        using var hub = NewHub(device);

        hub.Initialize();

        Assert.True(hub.ForeignHostSeen, hub.LastInitTrace);
    }

    [Fact]
    public void Takeover_runs_the_measured_order_before_the_endpoint_setup()
    {
        var device = new TakeoverHub { Mode = CorsairLinkProtocol.ModeHardware };
        var hub = NewHub(device);

        Assert.True(hub.Initialize());

        var seq = Join(Sequence(device));
        Assert.StartsWith("mode? probe0 probe1 probe2 SOFT info HARD mode? probe0 probe1 probe2 SOFT mode?", seq);
        // The colour endpoint is opened last, after the device list and sensors were read.
        Assert.EndsWith("open0", seq);
        Assert.Equal(CorsairLinkProtocol.ModeSoftware, device.Mode);
        Assert.Contains("software mode confirmed", hub.LastInitTrace);
    }

    [Fact]
    public void Takeover_closes_only_the_handles_that_answer_open()
    {
        var device = new TakeoverHub();
        device.OpenHandles.Add(1);
        var hub = NewHub(device);

        Assert.True(hub.Initialize());

        var first = Sequence(device).TakeWhile(n => n != "SOFT").ToArray();
        Assert.Equal(new[] { "mode?", "probe0", "probe1", "close1", "probe2" }, first);
    }

    [Fact]
    public void A_refused_software_mode_is_cleared_by_the_cycle_and_the_connect_continues()
    {
        var device = new TakeoverHub { RefuseUntilHardware = true };
        var hub = NewHub(device);

        Assert.True(hub.Initialize());

        var seq = Sequence(device);
        Assert.Equal(2, seq.Count(n => n == "SOFT"));
        Assert.Equal(1, seq.Count(n => n == "HARD"));
        Assert.Contains("software mode refused", hub.LastInitTrace);
        Assert.DoesNotContain("refused after cycle", hub.LastInitTrace);
    }

    [Fact]
    public void A_hub_that_stays_refused_fails_with_the_whole_trace()
    {
        var device = new TakeoverHub { NeverAcceptSoftware = true };
        var hub = NewHub(device);

        Assert.False(hub.Initialize());

        Assert.Contains("software mode refused after cycle", hub.LastInitTrace);
        Assert.Contains("init failed", hub.LastInitTrace);
        Assert.Contains("out 09 02", hub.LastInitTrace);
    }

    [Fact]
    public void The_mode_cycle_runs_at_most_once_a_minute()
    {
        var now = 1_000_000L;
        var device = new TakeoverHub { NeverAcceptSoftware = true };
        var hub = NewHub(device, () => now);

        Assert.False(hub.Initialize());
        Assert.Equal(1, Sequence(device).Count(n => n == "HARD"));

        now += CorsairLinkHub.CycleMinIntervalMs - 1;
        Assert.False(hub.Initialize());
        Assert.Equal(1, Sequence(device).Count(n => n == "HARD"));
        Assert.DoesNotContain("step=cycle", hub.LastInitTrace);

        now += 2;
        Assert.False(hub.Initialize());
        Assert.Equal(2, Sequence(device).Count(n => n == "HARD"));
    }

    [Fact]
    public void The_takeover_never_sends_a_flash_writing_or_redetect_command()
    {
        var device = new TakeoverHub { NeverAcceptSoftware = true };
        var hub = NewHub(device);

        hub.Initialize();

        // 0b create, 0c delete, 15 commit, 1a detect. 06/07 only appear on a handle, never before the colour open.
        var forbidden = new byte[] { 0x0B, 0x0C, 0x15, 0x1A };
        Assert.DoesNotContain(device.Commands, c => forbidden.Contains(c[0]));
        Assert.DoesNotContain(device.Commands, c => c[0] is 0x06 or 0x07);
    }

    [Fact]
    public void Hand_back_closes_the_colour_handle_then_sets_hardware_mode_and_reads_it_back()
    {
        var device = new TakeoverHub();
        var hub = NewHub(device);
        Assert.True(hub.Initialize());
        var before = device.Commands.Count;

        hub.Detach(handBack: true);

        Assert.Equal("mode? close0 probe1 probe2 HARD mode?", Join(Sequence(device, before)));
        Assert.Equal(CorsairLinkProtocol.ModeHardware, device.Mode);
        Assert.Empty(device.OpenHandles);
    }

    [Fact]
    public void Hand_back_closes_a_stray_open_data_handle_too()
    {
        var device = new TakeoverHub();
        var hub = NewHub(device);
        Assert.True(hub.Initialize());
        device.OpenHandles.Add(2);
        var before = device.Commands.Count;

        hub.Detach(handBack: true);

        Assert.Equal("mode? close0 probe1 probe2 close2 HARD mode?", Join(Sequence(device, before)));
    }

    [Fact]
    public void Detach_without_hand_back_writes_nothing()
    {
        var device = new TakeoverHub();
        var hub = NewHub(device);
        Assert.True(hub.Initialize());
        var before = device.Commands.Count;

        hub.Detach(handBack: false);

        Assert.Empty(Sequence(device, before));
        Assert.Equal(CorsairLinkProtocol.ModeSoftware, device.Mode);
    }

    [Fact]
    public void Detach_after_another_program_answered_never_hands_back()
    {
        var device = new TakeoverHub();
        var hub = NewHub(device);
        Assert.True(hub.Initialize());
        var before = device.Commands.Count;
        // A reply echoing a command Nexus never sent marks another host on the hub.
        hub.SendColors(new byte[3]);
        Assert.False(hub.ForeignHostSeen);
        device.Inject(0x1F);
        hub.SendColors(new byte[3]);
        Assert.True(hub.ForeignHostSeen);
        var afterForeign = device.Commands.Count;

        hub.Detach(handBack: true);

        Assert.Empty(Sequence(device, afterForeign));
        Assert.True(afterForeign >= before);
    }

    [Fact]
    public async Task Other_entry_points_stand_down_during_the_mode_cycle_dwell()
    {
        var device = new TakeoverHub();
        var hub = NewHub(device);
        hub.ModeCycleWaitMs = 400;
        using var inDwell = new ManualResetEventSlim();
        var hardwareSeen = 0;
        device.OnCommand = c =>
        {
            if (c[0] == 0x01 && c[3] == CorsairLinkProtocol.ModeHardware && Interlocked.Increment(ref hardwareSeen) == 1) inDwell.Set();
        };

        var init = Task.Run(hub.Initialize);
        Assert.True(inDwell.Wait(5000));
        var polled = hub.Poll();
        var sent = hub.SendColors(new byte[3]);

        Assert.False(polled);
        Assert.False(sent);
        Assert.True(await init);
    }

    // A real, non-zero dwell: the first hardware-mode command of the takeover signals that the lock is released.
    private static (CorsairLinkHub Hub, TakeoverHub Device, ManualResetEventSlim InDwell) DwellingHub(int dwellMs)
    {
        var device = new TakeoverHub();
        var hub = new CorsairLinkHub(modeCycleWaitMs: dwellMs);
        hub.Attach(device);
        var inDwell = new ManualResetEventSlim();
        var seen = 0;
        device.OnCommand = c =>
        {
            if (c[0] == 0x01 && c[3] == CorsairLinkProtocol.ModeHardware && Interlocked.Increment(ref seen) == 1) inDwell.Set();
        };
        return (hub, device, inDwell);
    }

    [Fact]
    public async Task A_detach_during_the_dwell_wakes_the_takeover_at_once()
    {
        var (hub, _, inDwell) = DwellingHub(20_000);
        var init = Task.Run(hub.Initialize);
        Assert.True(inDwell.Wait(5000));
        var started = Environment.TickCount64;

        hub.Detach(handBack: true);

        Assert.False(await init);
        Assert.True(Environment.TickCount64 - started < 5000);
    }

    [Fact]
    public async Task A_retire_during_the_dwell_ends_the_takeover_and_refuses_every_later_one()
    {
        var (hub, _, inDwell) = DwellingHub(20_000);
        var init = Task.Run(hub.Initialize);
        Assert.True(inDwell.Wait(5000));

        hub.Retire(handBack: true);

        Assert.False(await init);
        var later = new TakeoverHub();
        hub.Attach(later);
        Assert.False(hub.Initialize());
        Assert.Empty(later.Commands);
    }

    [Fact]
    public async Task Another_programs_traffic_during_the_dwell_stops_the_takeover_before_any_handle_is_closed()
    {
        var (hub, device, inDwell) = DwellingHub(300);
        var init = Task.Run(hub.Initialize);
        Assert.True(inDwell.Wait(5000));
        device.Inject(0x0D);

        Assert.False(await init);

        Assert.True(hub.ForeignHostSeen);
        Assert.Contains("dwell", hub.ForeignReplyHex);
        // The sequence ends at the hardware mode command: no probe or close after the dwell.
        Assert.Equal("HARD", Sequence(device).Last());
    }

    [Fact]
    public void Two_hubs_take_over_and_hand_back_independently()
    {
        var first = new TakeoverHub();
        var second = new TakeoverHub();
        var hubA = new CorsairLinkHub(modeCycleWaitMs: 0);
        var hubB = new CorsairLinkHub("corsair:B:", 2, modeCycleWaitMs: 0);
        hubA.Attach(first);
        hubB.Attach(second);

        Assert.True(hubA.Initialize());
        Assert.True(hubB.Initialize());
        Assert.Equal(Sequence(first), Sequence(second));
        var secondBefore = second.Commands.Count;

        hubA.Detach(handBack: true);

        Assert.Equal(CorsairLinkProtocol.ModeHardware, first.Mode);
        Assert.Equal(CorsairLinkProtocol.ModeSoftware, second.Mode);
        Assert.Equal(secondBefore, second.Commands.Count);
        hubB.Detach(handBack: true);
        Assert.Equal(CorsairLinkProtocol.ModeHardware, second.Mode);
    }
}


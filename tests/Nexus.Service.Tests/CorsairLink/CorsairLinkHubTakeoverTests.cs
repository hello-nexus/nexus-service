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
        /// <summary>Runs after a command is handled and before its own reply is queued.</summary>
        public Action<byte[]>? OnBeforeReply;
        /// <summary>Overrides the echoed command byte of the firmware reply.</summary>
        public byte? FirmwareEcho;
        /// <summary>Only the first firmware reply carries <see cref="FirmwareEcho"/>.</summary>
        public bool FirmwareEchoOnce;
        /// <summary>Per-command reply latency; a read shorter than it fails, but the time it waited counts toward the next read.</summary>
        public Func<byte[], int>? ReplyDelayMs;
        /// <summary>Answers colour writes with a refusal status.</summary>
        public bool RefuseColorWrites;
        /// <summary>LED count the hub reports for channel 1; 0 leaves it unreported.</summary>
        public int ReportedLeds;
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
                case 0x06 or 0x07:
                    if (RefuseColorWrites) status = 3;
                    break;
                case 0x08:
                    if (!_resource.TryGetValue(cmd[1], out var res) || !OpenHandles.Contains(cmd[1])) status = 3;
                    else payload = res == 0x20 ? LedsResource() : ReadResource(res);
                    break;
            }
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            var isFirmware = cmd[0] == 0x02 && cmd[1] == 0x13;
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = isFirmware && FirmwareEcho is { } fe ? fe : cmd[0];
            if (isFirmware && FirmwareEchoOnce) FirmwareEcho = null;
            resp[1 + CorsairLinkProtocol.ResponseStatusOffset] = (byte)status;
            payload.CopyTo(resp, 1 + 4);
            OnBeforeReply?.Invoke(cmd);
            lock (_responses) _responses.Add((resp, ReplyDelayMs?.Invoke(cmd) ?? 0));
            return true;
        }

        private byte[] LedsResource() =>
            ReportedLeds == 0
                ? Array.Empty<byte>()
                : new byte[] { 0x0f, 0x00, 1, 0, 0, 0, 0, 2, 0, (byte)ReportedLeds, (byte)(ReportedLeds >> 8) };

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

        public void Inject(byte echo, int needMs = 0)
        {
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = echo;
            lock (_responses) _responses.Add((resp, needMs));
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            byte[] resp;
            lock (_responses)
            {
                if (_responses.Count == 0) return 0;
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

    // The first software-mode command's reply is produced after the given latency.
    private static Func<byte[], int> SlowFirstSoftwareReply(int ms)
    {
        var seen = false;
        return c =>
        {
            if (seen || c[0] != 0x01 || c[3] != CorsairLinkProtocol.ModeSoftware) return 0;
            seen = true;
            return ms;
        };
    }

    [Fact]
    public void A_late_software_mode_reply_does_not_shift_the_stream_into_a_false_foreign_host()
    {
        var device = new TakeoverHub { ReplyDelayMs = SlowFirstSoftwareReply(300) };
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
    public void A_stray_reply_on_the_firmware_read_is_retried_once_for_the_version()
    {
        var device = new TakeoverHub { FirmwareEcho = 0x01, FirmwareEchoOnce = true };
        using var hub = NewHub(device);

        Assert.True(hub.Initialize(), hub.LastInitTrace);

        Assert.Equal("4.1.656", hub.State.Firmware);
    }

    [Fact]
    public void A_late_software_mode_reply_on_the_firmware_read_leaves_the_real_firmware_reply_to_be_read()
    {
        var device = new TakeoverHub { ReplyDelayMs = SlowFirstSoftwareReply(700) };
        using var hub = NewHub(device);

        var ok = hub.Initialize();

        Assert.True(ok, hub.LastInitTrace);
        Assert.False(hub.ForeignHostSeen, hub.LastInitTrace);
        Assert.Equal("4.1.656", hub.State.Firmware);
    }

    [Fact]
    public void A_foreign_report_inside_the_drain_window_marks_another_program()
    {
        var device = new TakeoverHub { ReplyDelayMs = SlowFirstSoftwareReply(300) };
        device.OnBeforeReply = c =>
        {
            if (c[0] == 0x01 && c[3] == CorsairLinkProtocol.ModeSoftware) device.Inject(0x77, 300);
        };
        using var hub = NewHub(device);

        Assert.False(hub.Initialize());

        Assert.True(hub.ForeignHostSeen, hub.LastInitTrace);
        Assert.DoesNotContain("drained late software-mode reply", hub.LastInitTrace);
    }

    [Fact]
    public void A_foreign_echo_after_a_late_software_mode_reply_still_marks_another_program()
    {
        var device = new TakeoverHub { ReplyDelayMs = SlowFirstSoftwareReply(300) };
        device.OnBeforeReply = c =>
        {
            if (c[0] == 0x02 && c[1] == 0x13) device.Inject(0x77);
        };
        using var hub = NewHub(device);

        hub.Initialize();

        Assert.True(hub.ForeignHostSeen, hub.LastInitTrace);
    }

    [Fact]
    public void A_software_mode_reply_later_than_the_drain_bound_is_not_waited_for()
    {
        var device = new TakeoverHub { ReplyDelayMs = SlowFirstSoftwareReply(900) };
        using var hub = NewHub(device);

        var ok = hub.Initialize();

        // The drain gives up after one empty read: the firmware read times out too and the version stays unset (the guard keeps a stale reply from being parsed), while init still connects.
        Assert.True(ok, hub.LastInitTrace);
        Assert.False(hub.ForeignHostSeen, hub.LastInitTrace);
        Assert.Equal("", hub.State.Firmware);
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

    private static bool IsColorWrite(byte[] c) => c[0] == 0x06;
    private static bool IsBlackFrame(byte[] c) => c.Skip(8).Take(3 * 34).All(b => b == 0);

    private static (CorsairLinkHub Hub, TakeoverHub Device, List<string> Events, List<string> Warnings, List<string> Infos) ColorHub()
    {
        var device = new TakeoverHub();
        var events = new List<string>();
        var warnings = new List<string>();
        var infos = new List<string>();
        device.OnCommand = c =>
        {
            if (c[0] == 0x0D) events.Add("open");
            else if (IsColorWrite(c)) events.Add(IsBlackFrame(c) ? "black" : "color");
        };
        var hub = NewHub(device);
        hub.Settle = ms => events.Add($"settle{ms}");
        hub.LogWarn = warnings.Add;
        hub.LogInfo = infos.Add;
        return (hub, device, events, warnings, infos);
    }

    private static byte[] RealFrame() => Enumerable.Repeat((byte)0xFF, 34 * 3).ToArray();

    [Fact]
    public void Initial_open_sends_a_black_frame_and_settles_before_the_first_real_frame()
    {
        var (hub, _, events, _, _) = ColorHub();

        Assert.True(hub.Initialize());
        Assert.True(hub.SendColors(RealFrame()));

        var tail = events.Skip(events.LastIndexOf("open")).ToArray();
        Assert.Equal(new[] { "open", "black", "settle40", "color" }, tail);
    }

    [Fact]
    public void Reopen_after_a_refused_write_sends_a_black_frame_and_settles_before_the_next_real_frame()
    {
        long now = 1000;
        var (hub, device, events, _, _) = ColorHub();
        hub.NowMs = () => now;
        Assert.True(hub.Initialize());
        device.RefuseColorWrites = true;
        Assert.False(hub.SendColors(RealFrame()));
        device.RefuseColorWrites = false;
        now += CorsairLinkHub.ColorReopenBackoffMs + 1;
        events.Clear();

        Assert.True(hub.SendColors(RealFrame()));

        Assert.Equal(new[] { "open", "black", "settle40", "color" }, events);
    }

    [Fact]
    public void Led_count_mismatch_against_the_model_table_is_logged_once_per_connect()
    {
        var (hub, device, _, warnings, _) = ColorHub();
        device.ReportedLeds = 18;

        Assert.True(hub.Initialize());
        hub.Poll();

        var line = Assert.Single(warnings, w => w.Contains("led counts"));
        Assert.Contains("ch1 hub=18 table=34 MISMATCH", line);
    }

    [Fact]
    public void Matching_led_counts_are_logged_without_a_mismatch_flag()
    {
        var (hub, device, _, warnings, infos) = ColorHub();
        device.ReportedLeds = 34;

        Assert.True(hub.Initialize());

        Assert.DoesNotContain(warnings, w => w.Contains("led counts"));
        Assert.Contains(infos, i => i.Contains("ch1 hub=34 table=34") && !i.Contains("MISMATCH"));
    }

    [Fact]
    public void Failed_colour_writes_are_logged_first_then_rate_limited()
    {
        long now = 1000;
        var (hub, device, _, warnings, _) = ColorHub();
        hub.NowMs = () => now;
        Assert.True(hub.Initialize());
        device.RefuseColorWrites = true;

        for (var i = 0; i < CorsairLinkHub.ColorFailureLogEvery; i++)
        {
            hub.SendColors(RealFrame());
            now += CorsairLinkHub.ColorReopenBackoffMs + 1;
        }

        var failures = warnings.Where(w => w.Contains("colour write")).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.Contains("failed (refused", failures[0]);
    }
}

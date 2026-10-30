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
        private bool _hardwareSeen;
        private readonly Dictionary<byte, byte> _resource = new();
        private readonly Queue<byte[]> _responses = new();

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
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = cmd[0];
            resp[1 + CorsairLinkProtocol.ResponseStatusOffset] = (byte)status;
            payload.CopyTo(resp, 1 + 4);
            lock (_responses) _responses.Enqueue(resp);
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
            lock (_responses) _responses.Enqueue(resp);
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            byte[] resp;
            lock (_responses)
            {
                if (_responses.Count == 0) return 0;
                resp = _responses.Dequeue();
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
        var hub = new CorsairLinkHub { ModeCycleWaitMs = 0 };
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
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkHubHandleTests
{
    private const byte Colour = 0x22;
    private const byte Devices = 0x36;
    private const byte SpeedSet = 0x18;

    /// <summary>Answers like the iCUE LINK hub measured over USB on fw 4.1.656.</summary>
    private sealed class FakeLinkHub : IHidDevice
    {
        public readonly Dictionary<byte, byte> Handles = new();
        public readonly HashSet<byte> Blocked = new();
        public readonly List<byte[]> Commands = new();
        public int AcceptedColourFrames;
        public Action<byte>? AfterClose;
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
            Commands.Add(cmd);
            var (status, payload) = Handle(cmd);
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = cmd[0];
            resp[1 + CorsairLinkProtocol.ResponseStatusOffset] = (byte)status;
            payload.CopyTo(resp, 1 + 4);
            _responses.Enqueue(resp);
            return true;
        }

        /// <summary>Queues a reply another program's command produced; every open handle receives it.</summary>
        public void InjectReply(byte echo)
        {
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = echo;
            _responses.Enqueue(resp);
        }

        /// <summary>Reads that time out while their reply stays queued, as a late reply does.</summary>
        public int TimedOutReads;

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (TimedOutReads > 0)
            {
                TimedOutReads--;
                return 0;
            }
            if (_responses.Count == 0) return 0;
            var resp = _responses.Dequeue();
            resp.CopyTo(buffer);
            return resp.Length;
        }

        private (int Status, byte[] Payload) Handle(byte[] cmd)
        {
            switch (cmd[0])
            {
                case 0x02 when cmd[1] == 0x13:
                    return (0, new byte[] { 4, 1, 0x90, 0x02 });
                case 0x01:
                    return (0, Array.Empty<byte>());
                case 0x0D:
                    if (Handles.ContainsKey(cmd[1])) return (3, Array.Empty<byte>());
                    Handles[cmd[1]] = cmd[2];
                    return (0, Array.Empty<byte>());
                case 0x05:
                    Handles.Remove(cmd[2]);
                    Blocked.Remove(cmd[2]);
                    AfterClose?.Invoke(cmd[2]);
                    return (0, Array.Empty<byte>());
                case 0x08:
                    return Handles.TryGetValue(cmd[1], out var res) ? (0, ReadResource(res)) : (3, Array.Empty<byte>());
                case 0x06:
                    if (!Handles.TryGetValue(cmd[1], out var target) || Blocked.Contains(cmd[1])) return (3, Array.Empty<byte>());
                    if (!TagMatches(target, cmd[6], cmd[7]))
                    {
                        Blocked.Add(cmd[1]);
                        return (1, Array.Empty<byte>());
                    }
                    if (target == Colour) AcceptedColourFrames++;
                    return (0, Array.Empty<byte>());
                case 0x07:
                    return Handles.ContainsKey(cmd[1]) && !Blocked.Contains(cmd[1]) ? (0, Array.Empty<byte>()) : (3, Array.Empty<byte>());
                default:
                    return (0, Array.Empty<byte>());
            }
        }

        private static bool TagMatches(byte resource, byte lo, byte hi) => (resource, lo, hi) switch
        {
            (Colour, 0x12, 0x00) => true,
            (SpeedSet, 0x07, 0x00) => true,
            _ => false,
        };

        private static byte[] ReadResource(byte resource)
        {
            switch (resource)
            {
                case Devices:
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

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private static readonly byte[] Frame = Enumerable.Repeat((byte)0x40, 34 * 3).ToArray();

    private static (CorsairLinkHub Hub, FakeLinkHub Device) Connected(Action<FakeLinkHub>? leftBehind = null)
    {
        var device = new FakeLinkHub();
        leftBehind?.Invoke(device);
        var hub = new CorsairLinkHub();
        hub.Attach(device);
        Assert.True(hub.Initialize());
        return (hub, device);
    }

    [Fact]
    public void Initialize_frees_a_colour_handle_another_host_left_on_a_different_resource()
    {
        var (hub, device) = Connected(d => d.Handles[0] = Devices);

        Assert.True(hub.SendColors(Frame));

        Assert.Equal(Colour, device.Handles[0]);
        Assert.Equal(1, device.AcceptedColourFrames);
    }

    [Fact]
    public void Initialize_reads_the_chain_after_a_host_left_both_handles_open()
    {
        var (hub, _) = Connected(d =>
        {
            d.Handles[0] = Colour;
            d.Handles[1] = 0x21;
        });

        Assert.Single(hub.State.Devices);
        Assert.Equal("4.1.656", hub.State.Firmware);
    }

    [Fact]
    public void SendColors_writes_only_colour_data_once_the_handle_is_open()
    {
        var (hub, device) = Connected();
        var before = device.Commands.Count;

        for (var i = 0; i < 30; i++) Assert.True(hub.SendColors(Frame));

        var sent = device.Commands.Skip(before).ToList();
        Assert.Equal(30, sent.Count);
        Assert.All(sent, c => Assert.Equal(new byte[] { 0x06, 0x00 }, c.Take(2)));
        Assert.Equal(30, device.AcceptedColourFrames);
    }

    [Fact]
    public void Telemetry_poll_leaves_the_colour_handle_open()
    {
        var (hub, device) = Connected();

        Assert.True(hub.Poll());
        Assert.True(hub.SendColors(Frame));

        Assert.Equal(Colour, device.Handles[0]);
        Assert.Equal(1, device.AcceptedColourFrames);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Detach_hands_the_hub_back_to_hardware_mode_only_when_asked(bool handBack, int hardwareModeWrites)
    {
        var (hub, device) = Connected();

        hub.Detach(handBack);

        Assert.Equal(hardwareModeWrites, device.Commands.Count(c => c.Take(4).SequenceEqual(new byte[] { 0x01, 0x03, 0x00, 0x01 })));
    }

    [Fact]
    public void A_refused_write_reopens_the_handle_once_after_the_backoff()
    {
        var (hub, device) = Connected();
        long now = 0;
        hub.NowMs = () => now;
        device.Handles[0] = SpeedSet;

        Assert.False(hub.SendColors(Frame));
        var afterRefusal = device.Commands.Count;
        Assert.False(hub.SendColors(Frame));
        Assert.Equal(afterRefusal, device.Commands.Count);

        now += CorsairLinkHub.ColorReopenBackoffMs;
        Assert.True(hub.SendColors(Frame));

        Assert.Equal(Colour, device.Handles[0]);
        Assert.Equal(1, device.AcceptedColourFrames);
    }

    [Fact]
    public void A_refused_reopen_waits_another_backoff_before_trying_again()
    {
        var (hub, device) = Connected();
        long now = 0;
        hub.NowMs = () => now;
        device.Handles[0] = SpeedSet;
        Assert.False(hub.SendColors(Frame));

        now += CorsairLinkHub.ColorReopenBackoffMs;
        // A competing host re-takes handle 0 between our close and open.
        device.AfterClose = h => { if (h == 0) device.Handles[0] = Devices; };
        Assert.False(hub.SendColors(Frame));
        var attempts = device.Commands.Count;
        Assert.False(hub.SendColors(Frame));

        Assert.Equal(attempts, device.Commands.Count);
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x02)]
    [InlineData(0x0C)]
    public void A_reply_to_another_programs_command_stops_every_write(byte echo)
    {
        var (hub, device) = Connected();
        device.InjectReply(echo);
        hub.SendColors(Frame);
        var sent = device.Commands.Count;

        Assert.True(hub.ForeignHostSeen);
        Assert.False(hub.SendColors(Frame));
        Assert.False(hub.SetDuties(new[] { (1, 50) }));
        Assert.False(hub.Poll());
        hub.Detach(handBack: true);

        Assert.Equal(sent, device.Commands.Count);
    }

    [Fact]
    public void Nexus_own_commands_do_not_read_as_another_program()
    {
        var (hub, _) = Connected();

        for (var i = 0; i < 5; i++) Assert.True(hub.SendColors(Frame));
        Assert.True(hub.Poll());
        Assert.True(hub.SetDuties(new[] { (1, 50) }));

        Assert.False(hub.ForeignHostSeen);
    }

    [Fact]
    public void A_late_reply_to_the_firmware_read_is_not_another_program()
    {
        var device = new FakeLinkHub { TimedOutReads = 1 };
        var hub = new CorsairLinkHub();
        hub.Attach(device);

        hub.Initialize();

        Assert.False(hub.ForeignHostSeen);
    }

    [Fact]
    public void Attach_clears_a_previous_sessions_foreign_host()
    {
        var (hub, device) = Connected();
        device.InjectReply(0x09);
        hub.SendColors(Frame);
        hub.Detach(handBack: false);

        hub.Attach(new FakeLinkHub());

        Assert.False(hub.ForeignHostSeen);
        Assert.True(hub.Initialize());
    }

    [Fact]
    public void HearsAnotherHost_is_true_only_when_a_report_arrives_unprompted()
    {
        var quiet = new FakeLinkHub();
        var busy = new FakeLinkHub();
        busy.InjectReply(0x08);

        Assert.False(CorsairLinkHub.HearsAnotherHost(quiet, 10));
        Assert.True(CorsairLinkHub.HearsAnotherHost(busy, 10));
        Assert.Empty(busy.Commands);
    }
}

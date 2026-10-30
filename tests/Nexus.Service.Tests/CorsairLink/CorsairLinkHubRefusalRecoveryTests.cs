using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkHubRefusalRecoveryTests
{
    /// <summary>A hub that answers software mode with status 07 until a hardware mode command has been seen.</summary>
    private sealed class RefusingHub : IHidDevice
    {
        public readonly List<byte[]> Commands = new();
        public bool AcceptAfterHardware = true;
        private bool _hardwareSeen;
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
            var status = 0;
            var payload = Array.Empty<byte>();
            switch (cmd[0])
            {
                case 0x02 when cmd[1] == 0x13:
                    payload = new byte[] { 4, 1, 0x90, 0x02 };
                    break;
                case 0x01 when cmd[3] == 0x01:
                    _hardwareSeen = true;
                    break;
                case 0x01:
                    status = _hardwareSeen && AcceptAfterHardware ? 0 : 7;
                    _softwareOk = status == 0;
                    break;
                case 0x05:
                    break;
                case 0x0D:
                    // A refusing hub answers every open with 03, as the captured traces show.
                    if (!_softwareOk) status = 3;
                    break;
                case 0x08:
                    if (!_softwareOk) status = 3;
                    else payload = Read(cmd[1]);
                    break;
            }
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = cmd[0];
            resp[1 + CorsairLinkProtocol.ResponseStatusOffset] = (byte)status;
            payload.CopyTo(resp, 1 + 4);
            _responses.Enqueue(resp);
            if (cmd[0] == 0x0D && status == 0) _opened = cmd[2];
            return true;
        }

        private byte _opened;
        private bool _softwareOk;

        private byte[] Read(byte handle)
        {
            switch (_opened)
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

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_responses.Count == 0) return 0;
            var resp = _responses.Dequeue();
            resp.CopyTo(buffer);
            return resp.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private static CorsairLinkHub NewHub(RefusingHub device, Func<long>? clock = null)
    {
        var hub = new CorsairLinkHub { RecoveryHardwareWaitMs = 0 };
        if (clock is not null) hub.NowMs = clock;
        hub.Attach(device);
        return hub;
    }

    private static int SoftwareModeSends(RefusingHub d) =>
        d.Commands.Count(c => c[0] == 0x01 && c[3] == 0x02);

    [Fact]
    public void A_refused_software_mode_runs_the_recovery_and_continues_when_accepted()
    {
        var device = new RefusingHub();
        var hub = NewHub(device);

        Assert.True(hub.Initialize());

        // First software-mode send is refused, the recovery cycles hardware then software.
        Assert.Equal(2, SoftwareModeSends(device));
        Assert.Contains("step=recover-handles", hub.LastInitTrace);
        Assert.Contains("recover: software mode accepted", hub.LastInitTrace);
        var handleCloses = device.Commands.Where(c => c[0] == 0x05 && c.Length > 2).Select(c => c[2]).Distinct().ToArray();
        for (byte h = 0; h < CorsairLinkProtocol.RecoveryHandleCount; h++)
        {
            Assert.Contains(h, handleCloses);
        }
        Assert.Contains(device.Commands, c => c[0] == 0x02 && c[1] == 0x03);
        Assert.Contains(device.Commands, c => c[0] == 0x02 && c[1] == 0x14);
        Assert.Contains(device.Commands, c => c[0] == 0x12);
    }

    [Fact]
    public void An_accepted_software_mode_never_runs_the_recovery()
    {
        var device = new RefusingHub();
        var hub = NewHub(device);
        // Pretend hardware mode was already seen, so the first software-mode send is accepted.
        device.Write(new byte[] { 0, 0, 1, 0x01, 0x03, 0x00, 0x01 });
        device.Commands.Clear();

        Assert.True(hub.Initialize());

        Assert.Equal(1, SoftwareModeSends(device));
        Assert.DoesNotContain("step=recover", hub.LastInitTrace);
    }

    [Fact]
    public void A_hub_that_stays_refused_fails_with_the_full_recovery_trace()
    {
        var device = new RefusingHub { AcceptAfterHardware = false };
        var hub = NewHub(device);

        Assert.False(hub.Initialize());

        Assert.Contains("recover: software mode still refused", hub.LastInitTrace);
        Assert.Contains("init failed", hub.LastInitTrace);
        // Every probe and close of the 8 handles stays in the trace despite its length cap.
        Assert.Contains("out 05 01 07", hub.LastInitTrace);
    }

    [Fact]
    public void The_recovery_runs_at_most_once_a_minute()
    {
        var now = 1_000_000L;
        var device = new RefusingHub { AcceptAfterHardware = false };
        var hub = NewHub(device, () => now);

        Assert.False(hub.Initialize());
        var first = device.Commands.Count(c => c[0] == 0x01 && c[3] == 0x01);
        Assert.Equal(1, first);

        now += CorsairLinkHub.RecoveryMinIntervalMs - 1;
        Assert.False(hub.Initialize());
        Assert.Equal(1, device.Commands.Count(c => c[0] == 0x01 && c[3] == 0x01));
        Assert.DoesNotContain("step=recover", hub.LastInitTrace);

        now += 2;
        Assert.False(hub.Initialize());
        Assert.Equal(2, device.Commands.Count(c => c[0] == 0x01 && c[3] == 0x01));
    }

    [Fact]
    public void The_recovery_never_sends_a_flash_writing_or_redetect_command()
    {
        var device = new RefusingHub { AcceptAfterHardware = false };
        var hub = NewHub(device);

        hub.Initialize();

        // 0b create, 0c delete, 06/07 write, 15 commit, 1a detect.
        var forbidden = new byte[] { 0x0B, 0x0C, 0x06, 0x07, 0x15, 0x1A };
        Assert.DoesNotContain(device.Commands, c => forbidden.Contains(c[0]));
    }
}

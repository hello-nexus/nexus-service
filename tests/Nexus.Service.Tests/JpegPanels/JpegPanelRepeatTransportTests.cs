using System;
using System.Collections.Generic;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

public sealed class JpegPanelRepeatTransportTests
{
    private const int RepeatMs = 1000;

    private sealed class Fixture : IDisposable
    {
        public readonly JpegPanelHub Hub = new(JpegPanelModel.CorsairCapellix);
        public readonly Device Hid = new();
        public readonly JpegPanelStreamTransport Transport;
        public long Now;

        public Fixture()
        {
            Hub.Attach(Hid);
            Transport = new JpegPanelStreamTransport(Hub, "serial") { Clock = () => Now };
            Hid.Writes.Clear();
            var frame = new byte[JpegPanelModel.CorsairCapellix.Width * JpegPanelModel.CorsairCapellix.Height * 4];
            Transport.Write(frame);
            Hid.Writes.Clear();
        }

        public void Dispose()
        {
            Transport.Dispose();
            Hub.Dispose();
        }
    }

    [Fact]
    public void A_due_repeat_resends_the_last_frame()
    {
        using var f = new Fixture();
        f.Now = RepeatMs;

        f.Transport.RepeatLast(RepeatMs);

        Assert.NotEmpty(f.Hid.Writes);
    }

    [Fact]
    public void No_repeat_before_the_idle_window()
    {
        using var f = new Fixture();
        f.Now = RepeatMs - 1;

        f.Transport.RepeatLast(RepeatMs);

        Assert.Empty(f.Hid.Writes);
    }

    [Fact]
    public void A_closed_gate_blocks_the_repeat()
    {
        using var f = new Fixture();
        var allowed = false;
        f.Transport.BindRepeatGate(() => allowed);
        f.Now = RepeatMs;

        f.Transport.RepeatLast(RepeatMs);
        Assert.Empty(f.Hid.Writes);

        allowed = true;
        f.Transport.RepeatLast(RepeatMs);
        Assert.NotEmpty(f.Hid.Writes);
    }

    [Fact]
    public void A_frame_in_flight_makes_the_repeat_stand_down()
    {
        using var f = new Fixture();
        f.Now = RepeatMs;
        f.Transport.LiveSends = 1;

        f.Transport.RepeatLast(RepeatMs);

        Assert.Empty(f.Hid.Writes);
    }

    [Fact]
    public void No_repeat_after_dispose()
    {
        using var f = new Fixture();
        f.Now = RepeatMs;
        f.Transport.Dispose();

        f.Transport.RepeatLast(RepeatMs);

        Assert.Empty(f.Hid.Writes);
    }

    [Fact]
    public void The_hub_stands_down_when_the_caller_withdraws_under_its_lock()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.CorsairCapellix);
        var device = new Device();
        hub.Attach(device);
        device.Writes.Clear();

        Assert.True(hub.SendFrame(new byte[] { 1, 2, 3 }, () => false));

        Assert.Empty(device.Writes);
    }

    private sealed class Device : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x1B1C;
        public int ProductId => 0x0C39;
        public string Path => "/dev/fake-capellix";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose()
        {
        }
    }
}

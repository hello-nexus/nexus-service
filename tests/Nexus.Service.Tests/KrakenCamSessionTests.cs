using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>The 2023 Kraken (0x300E) follows the vendor app's session init and backlight layout; every other model does not.</summary>
public class KrakenCamSessionTests
{
    private sealed class Rig
    {
        public readonly KrakenHub Hub;
        public readonly Hid Device = new();
        public long Now = 1_000_000;

        public Rig(int productId)
        {
            var model = KrakenModel.Find(productId)!;
            Hub = new KrakenHub(new Factory(new Lcd())) { Clock = () => Now };
            Hub.FadeRunner.AutoRun = false;
            Hub.Attach(Device, model, KrakenProtocol.ReportLength);
            Assert.True(Hub.Connect());
        }

        public byte[] Frame()
        {
            var f = new byte[Hub.LcdFrameBytes];
            Array.Fill(f, (byte)7);
            return f;
        }

        public string[] Ids() => Device.Writes.Select(w => $"{w[0]:X2}{w[1]:X2}").ToArray();
    }

    [Fact]
    public void Connect_on_the_2023_Kraken_sets_the_vendor_update_interval()
    {
        var rig = new Rig(0x300E);
        var report = rig.Device.Writes.Single(w => w[0] == 0x70 && w[1] == 0x02);
        Assert.Equal(new byte[] { 0x70, 0x02, 0x01, 0xB8, 0x0B }, report[..5]);
    }

    [Fact]
    public void The_first_stream_frame_on_the_2023_Kraken_opens_the_session_in_the_vendor_order()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));

        Assert.Equal(new[] { "7401", "3604", "3001", "3603", "3601", "3602" }, rig.Ids());
        var start = rig.Device.Writes[4];
        Assert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x01, 0x06 }, start[..5]);
    }

    [Fact]
    public void Later_frames_do_not_reopen_the_session()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        for (int i = 0; i < 20; i++)
        {
            rig.Now += 40;
            var frame = rig.Frame();
            frame[0] = (byte)i;
            Assert.True(rig.Hub.PushStreamFrame(frame));
        }

        Assert.Single(rig.Ids(), "3604");
        Assert.Single(rig.Ids(), "3001");
        Assert.Single(rig.Ids(), "3603");
        Assert.Single(rig.Ids(), "7401");
    }

    [Fact]
    public void The_Plus_V2_session_is_unchanged()
    {
        var rig = new Rig(0x3014);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));

        Assert.Equal(new[] { "3604", "3603", "3601", "3602" }, rig.Ids());
    }

    [Fact]
    public void The_2023_Kraken_backlight_uses_the_vendor_layout_and_keeps_rotation_in_byte_7()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.SetLcdBacklight(80, 0));
        var report = rig.Device.Writes.Last(w => w[0] == 0x30 && w[1] == 0x02);
        Assert.Equal(new byte[] { 0x30, 0x02, 0x01, 80, 0x00, 0x00, 0x00, 0x00, 0x1E }, report[..9]);

        Assert.True(rig.Hub.SetLcdBacklight(80, 2));
        report = rig.Device.Writes.Last(w => w[0] == 0x30 && w[1] == 0x02);
        Assert.Equal(2, report[7]);
        Assert.Equal(0x1E, report[8]);
    }

    [Theory]
    [InlineData(0x3014)]
    [InlineData(0x3012)]
    [InlineData(0x300C)]
    [InlineData(0x3008)]
    public void Other_models_keep_the_original_backlight_layout(int productId)
    {
        var rig = new Rig(productId);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.SetLcdBacklight(80, 2));

        var report = rig.Device.Writes.Last(w => w[0] == 0x30 && w[1] == 0x02);
        Assert.Equal(KrakenProtocol.EncodeSetBacklight(80, 2), report);
        Assert.Equal(0x01, report[6]);
        Assert.Equal(0x00, report[8]);
    }

    [Fact]
    public void The_default_backlight_encoding_is_byte_identical_to_before()
    {
        var report = KrakenProtocol.EncodeSetBacklight(50, 3);
        var expected = new byte[KrakenProtocol.ReportLength];
        expected[0] = 0x30;
        expected[1] = 0x02;
        expected[2] = 0x01;
        expected[3] = 50;
        expected[6] = 0x01;
        expected[7] = 3;
        Assert.Equal(expected, report);
    }

    [Fact]
    public void Only_the_2023_Kraken_follows_the_vendor_session()
    {
        Assert.Equal(new[] { 0x300E }, KrakenModel.All.Where(m => m.CamSessionInit).Select(m => m.ProductId));
    }

    private sealed class Hid : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x1E71;
        public int ProductId => 0x300E;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(copy[0] + 1);
            reply[1] = copy[1];
            reply[14] = KrakenProtocol.AckOk;
            if (copy[0] == 0x10)
            {
                reply[0x11] = 1;
                reply[0x12] = 0;
            }
            _replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var r = _replies.Dequeue();
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private sealed class Lcd : IKrakenLcdTransport
    {
        public bool Write(ReadOnlySpan<byte> data) => true;

        public void Dispose() { }
    }

    private sealed class Factory : IKrakenLcdTransportFactory
    {
        private readonly IKrakenLcdTransport _lcd;
        public Factory(IKrakenLcdTransport lcd) { _lcd = lcd; }
        public IKrakenLcdTransport? Open(string? serial) => _lcd;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;
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
        public readonly List<(int Ms, int WritesBefore)> Delays = new();

        public Rig(int productId)
        {
            var model = KrakenModel.Find(productId)!;
            Hub = new KrakenHub(new Factory(new Lcd())) { Clock = () => Now };
            Hub.FadeRunner.AutoRun = false;
            Hub.DelayMs = ms => Delays.Add((ms, Device.Writes.Count));
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

        public int Count(string id) => Ids().Count(i => i == id);

        // Opens the stream, then silences the fake so only injected reports reach the hub.
        public void OpenStream(int brightness = 80)
        {
            Assert.True(Hub.SetLcdBacklight(brightness, 0));
            Assert.True(Hub.PushStreamFrame(Frame()));
            Device.Echo = false;
            Device.Writes.Clear();
            Delays.Clear();
        }

        public bool PollWithReport(double liquidC = 31.5)
        {
            Device.Push(Status(liquidC));
            return Hub.Poll();
        }

        public static byte[] Status(double liquidC)
        {
            var r = new byte[KrakenProtocol.ReportLength];
            r[0] = 0x75;
            r[1] = 0x02;
            r[15] = (byte)liquidC;
            r[16] = (byte)Math.Round((liquidC - Math.Floor(liquidC)) * 10);
            return r;
        }
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

        Assert.Equal(
            new[] { "1002", "7002", "7401", "3604", "3001", "3603", "3002", "3002", "3601", "3602" }, rig.Ids());
        Assert.Equal(new byte[] { 0x70, 0x02, 0x01, 0xB8, 0x0B }, rig.Device.Writes[1][..5]);
        var start = rig.Device.Writes[8];
        Assert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x01, 0x06 }, start[..5]);
    }

    [Fact]
    public void Session_open_goes_dark_first_then_restores_the_real_backlight_after_the_delay()
    {
        var rig = new Rig(0x300E);
        Assert.True(rig.Hub.SetLcdBacklight(80, 2));
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));

        Assert.Equal(
            new byte[] { 0x30, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E },
            rig.Device.Writes[6][..9]);
        Assert.Equal(
            new byte[] { 0x30, 0x02, 0x01, 80, 0x00, 0x00, 0x00, 0x02, 0x1E },
            rig.Device.Writes[7][..9]);
        var delay = Assert.Single(rig.Delays);
        Assert.Equal(70, delay.Ms);
        Assert.Equal(7, delay.WritesBefore);
    }

    [Fact]
    public void No_status_request_is_sent_while_the_stream_is_open_and_reports_keep_arriving()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();

        for (int i = 0; i < 20; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.PollWithReport());
        }

        Assert.Equal(0, rig.Count("7401"));
        Assert.Equal(31.5, rig.Hub.Snapshot.LiquidTempC, 1);
    }

    [Fact]
    public void A_status_request_is_the_fallback_once_no_report_was_read_for_three_seconds()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        rig.Device.Push(Rig.Status(30));
        Assert.True(rig.Hub.Poll());

        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        Assert.Equal(0, rig.Count("7401"));

        rig.Now += 1000;
        rig.Device.Echo = true;
        Assert.True(rig.Hub.Poll());
        Assert.Equal(1, rig.Count("7401"));

        // The fallback's reply counts as a report, so the next window starts over.
        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        Assert.Equal(1, rig.Count("7401"));
    }

    [Fact]
    public void The_fallback_is_logged_at_most_once_a_minute()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        const string Marker = "while streaming; polling once";
        int Logged() => ServiceLog.RecentLines().Split('\n').Count(l => l.Contains(Marker));
        var before = Logged();

        for (int i = 0; i < 30; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.Hub.Poll());
        }

        Assert.True(rig.Count("7401") >= 10);
        Assert.InRange(Logged() - before, 0, 1);
    }

    [Fact]
    public void A_keepalive_goes_out_every_28_seconds_while_the_stream_is_open()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();

        for (int i = 0; i < 27; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.PollWithReport());
        }
        Assert.Equal(0, rig.Count("1002"));

        rig.Now += 1000;
        Assert.True(rig.PollWithReport());
        Assert.Equal(1, rig.Count("1002"));

        for (int i = 0; i < 28; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.PollWithReport());
        }
        Assert.Equal(2, rig.Count("1002"));
    }

    [Fact]
    public void Before_a_stream_opens_the_2023_Kraken_polls_status_as_always_and_sends_no_keepalive()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        for (int i = 0; i < 40; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.Hub.Poll());
        }

        Assert.Equal(40, rig.Count("7401"));
        Assert.Equal(0, rig.Count("1002"));
    }

    [Fact]
    public void The_stream_quiet_flag_follows_the_open_session()
    {
        var rig = new Rig(0x300E);
        Assert.False(rig.Hub.StreamQuiet);

        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));
        Assert.True(rig.Hub.StreamQuiet);

        rig.Hub.Detach();
        Assert.False(rig.Hub.StreamQuiet);
    }

    [Theory]
    [InlineData(0x3014)]
    [InlineData(0x3012)]
    [InlineData(0x300C)]
    [InlineData(0x3008)]
    public void Other_models_keep_polling_and_never_send_the_keepalive_or_session_extras(int productId)
    {
        var rig = new Rig(productId);
        rig.Device.Writes.Clear();
        rig.Hub.PushStreamFrame(rig.Frame());
        var open = rig.Ids();
        Assert.DoesNotContain("1002", open);
        Assert.DoesNotContain("7002", open);

        rig.Device.Writes.Clear();
        for (int i = 0; i < 40; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.Hub.Poll());
        }

        Assert.Equal(40, rig.Count("7401"));
        Assert.Equal(0, rig.Count("1002"));
        Assert.False(rig.Hub.StreamQuiet);
        Assert.True(rig.Hub.StatusFresh);
    }

    [Fact]
    public void Temperature_is_withheld_from_cooling_once_reports_stop_for_too_long()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        var cooling = new KrakenCoolingProvider(rig.Hub);
        rig.Now += 1000;
        Assert.True(rig.PollWithReport(40));
        Assert.Equal(40f, cooling.ReadTemperature("nzxt-kraken:liquid"));
        Assert.Single(cooling.GetTemperatureSources());

        rig.Now += 9000;
        Assert.True(rig.Hub.StatusFresh);
        rig.Now += 2000;

        Assert.False(rig.Hub.StatusFresh);
        Assert.Null(cooling.ReadTemperature("nzxt-kraken:liquid"));
        Assert.Empty(cooling.GetTemperatureSources());

        rig.Device.Push(Rig.Status(41));
        Assert.True(rig.Hub.Poll());
        Assert.Equal(41f, cooling.ReadTemperature("nzxt-kraken:liquid"));
    }

    [Fact]
    public void The_thirty_second_summary_counts_frames_written_skipped_and_reports_read()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        var frame = rig.Frame();
        for (int i = 0; i < 5; i++)
        {
            rig.Now += 100;
            Assert.True(rig.Hub.PushStreamFrame(frame));
        }
        Assert.True(rig.PollWithReport());
        rig.Now += 31_000;
        Assert.True(rig.PollWithReport());

        var line = ServiceLog.RecentLines().Split('\n')
            .Last(l => l.Contains("[nzxt-kraken] 30 s:"));
        Assert.Contains("frames written", line);
        Assert.Contains("skipped as unchanged", line);
        Assert.Contains("status reports read", line);
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
        Assert.Single(rig.Ids(), "1002");
        Assert.Single(rig.Ids(), "7002");
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
        public bool Echo { get; set; } = true;

        public void Push(byte[] report) => _replies.Enqueue(report);

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
            if (!Echo)
            {
                return true;
            }
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

using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>Byte sequences are the MB Manager traffic in the customer's Nexus Echo captures, trailing zeros trimmed.</summary>
public class GlacialLcdTests
{
    private static byte[] Report(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty));
        var report = new byte[GlacialLcdProtocol.ReportBytes];
        bytes.CopyTo(report, 0);
        return report;
    }

    [Fact]
    public void Query_and_hello_match_the_capture()
    {
        Assert.Equal(Report("EC DC"), GlacialLcdProtocol.EncodeQuery());
        Assert.Equal(Report("EC 82"), GlacialLcdProtocol.EncodeHello());
    }

    [Theory]
    [InlineData(50, 0x00, false, "EC 5C 01 00 00 32 00 00 00 32 00 00 00 00 00 32")]
    [InlineData(50, 0x14, false, "EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32")]
    [InlineData(30, 0x14, false, "EC 5C 01 00 00 1E 00 00 00 1E 14 00 00 00 00 1E")]
    [InlineData(70, 0x14, false, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46")]
    [InlineData(70, 0x14, true, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46 14")]
    public void Settings_write_matches_the_captured_brightness_mode_and_sleep_fields(int percent, byte mode, bool keepOn, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeSettings(percent, mode, keepOn));
    }

    [Fact]
    public void Settings_brightness_is_clamped_to_a_percent()
    {
        Assert.Equal(100, GlacialLcdProtocol.EncodeSettings(250, 0, false)[5]);
        Assert.Equal(0, GlacialLcdProtocol.EncodeSettings(-5, 0, false)[5]);
    }

    [Fact]
    public void State_reply_parses_the_fields_the_settings_write_sets()
    {
        var reply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32");
        var state = GlacialLcdProtocol.ParseState(reply);
        Assert.Equal((50, (byte)0x14, false), state);
        Assert.Null(GlacialLcdProtocol.ParseState(Report("EC 51")));
    }

    [Theory]
    [InlineData(GlacialLcdProtocol.ModeAnimatedAsset, 0, "EC 51 14")]
    [InlineData(GlacialLcdProtocol.ModeAnimatedAsset, 1, "EC 51 14 00 01")]
    [InlineData(GlacialLcdProtocol.ModeStaticAsset, 3, "EC 51 11 00 03")]
    [InlineData(GlacialLcdProtocol.ModeStaticAsset, 4, "EC 51 11 00 04")]
    public void Mode_select_matches_the_captured_asset_picks(byte mode, int asset, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeMode(mode, asset));
    }

    [Fact]
    public void Off_and_hardware_monitor_modes_match_the_capture()
    {
        Assert.Equal(Report("EC 51"), GlacialLcdProtocol.EncodeOff());
        Assert.Equal(Report("EC 51 21"), GlacialLcdProtocol.EncodeHardwareMonitor());
    }

    [Theory]
    [InlineData(1, "EC 52")]
    [InlineData(2, "EC 52 00 01")]
    [InlineData(3, "EC 52 00 02")]
    public void Slot_count_matches_one_two_and_three_widget_captures(int slots, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeSlotCount(slots));
    }

    [Fact]
    public void Slot_text_matches_the_captured_cpu_temperature_and_voltage_widgets()
    {
        Assert.Equal(
            Report("EC 53 00 43 50 55 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 34 33 2E 30 E2 84 83"),
            GlacialLcdProtocol.EncodeSlot(0, "CPU", "43.0℃"));
        Assert.Equal(
            Report("EC 53 01 43 50 55 20 50 61 63 6B 61 67 65 00 00 00 00 00 00 00 35 37 2E 30 E2 84 83"),
            GlacialLcdProtocol.EncodeSlot(1, "CPU Package", "57.0℃"));
        Assert.Equal(
            Report("EC 53 02 43 50 55 20 56 44 44 49 4F 20 2F 20 4D 43 00 00 00 00 31 2E 34 34 37 E2 86 8A"),
            GlacialLcdProtocol.EncodeSlot(2, "CPU VDDIO / MC", "1.447↊"));
    }

    [Fact]
    public void Slot_label_is_cut_on_a_character_boundary()
    {
        var report = GlacialLcdProtocol.EncodeSlot(0, new string('℃', 10), "1");
        // Six whole three-byte characters fill the label field; the rest is dropped, not split.
        Assert.Equal(0xE2, report[3]);
        Assert.Equal(0x83, report[GlacialLcdProtocol.SlotValueOffset - 1]);
        Assert.Equal((byte)'1', report[GlacialLcdProtocol.SlotValueOffset]);
    }

    [Fact]
    public void Custom_image_prelude_matches_the_capture()
    {
        var reports = GlacialLcdProtocol.EncodeCustomImagePrelude();
        Assert.Equal(
            new[] { Report("EC 71 01 01"), Report("EC F1"), Report("EC 72 01 01"), Report("EC 73 01") },
            reports);
    }

    [Fact]
    public void Jpeg_announce_matches_the_captured_size_field()
    {
        Assert.Equal(Report("EC 7F 02 DC B6 04"), GlacialLcdProtocol.EncodeJpegAnnounce(0x04B6DC));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlacialLcdProtocol.EncodeJpegAnnounce(0));
    }

    // ── Driver ──

    private static byte[] Frame()
    {
        var frame = new byte[GlacialLcdProtocol.Width * GlacialLcdProtocol.Height * 4];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = (byte)(i >> 10);
            frame[i + 1] = 0x80;
            frame[i + 2] = 0x20;
            frame[i + 3] = 0xFF;
        }
        return frame;
    }

    [Fact]
    public void Connect_sends_the_vendor_opening_and_reports_the_jpeg_size()
    {
        var driver = new GlacialLcdPanelDriver(0);
        var hid = new FakeHid();

        var geometry = driver.Connect(new FakePipe(), hid);

        Assert.Equal((GlacialLcdProtocol.Width, GlacialLcdProtocol.Height), geometry);
        Assert.Equal(new[] { GlacialLcdProtocol.EncodeHello(), GlacialLcdProtocol.EncodeQuery() }, hid.Writes);
        Assert.Null(driver.Connect(new FakePipe(), null));
    }

    [Fact]
    public void A_frame_uploads_as_the_captured_prelude_then_announce_then_4k_chunks_of_one_jpeg()
    {
        var driver = new GlacialLcdPanelDriver(0);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        hid.Writes.Clear();

        Assert.True(driver.SendFrame(pipe, hid, Frame()));
        Assert.True(driver.WaitIdle(30_000));

        var prelude = GlacialLcdProtocol.EncodeCustomImagePrelude();
        Assert.Equal(GlacialLcdProtocol.EncodeQuery(), hid.Writes[0]);
        Assert.Equal(prelude, hid.Writes.Skip(1).Take(4));
        var announce = hid.Writes[5];
        int size = announce[3] | (announce[4] << 8) | (announce[5] << 16);
        Assert.Equal(Report("EC 7F 02").Take(3), announce.Take(3));
        var jpeg = pipe.Writes.SelectMany(w => w).ToArray();
        Assert.Equal(size, jpeg.Length);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, jpeg.Take(3));
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg.TakeLast(2));
        Assert.All(pipe.Writes.SkipLast(1), w => Assert.Equal(GlacialLcdProtocol.ChunkBytes, w.Length));
        Assert.InRange(pipe.Writes[^1].Length, 1, GlacialLcdProtocol.ChunkBytes);
    }

    [Fact]
    public void A_second_frame_inside_the_refresh_interval_is_not_uploaded()
    {
        var driver = new GlacialLcdPanelDriver(0, 60_000);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(driver.WaitIdle(30_000));
        int chunks = pipe.Writes.Count;
        var other = Frame();
        other[100] ^= 0xFF;
        Assert.True(driver.SendFrame(pipe, hid, other));
        Assert.True(driver.WaitIdle(30_000));

        Assert.Equal(chunks, pipe.Writes.Count);
    }

    [Fact]
    public void An_identical_frame_after_the_interval_is_not_uploaded_again()
    {
        var driver = new GlacialLcdPanelDriver(0, 0);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(driver.WaitIdle(30_000));
        int chunks = pipe.Writes.Count;
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(driver.WaitIdle(30_000));

        Assert.Equal(chunks, pipe.Writes.Count);
    }

    [Fact]
    public void A_failed_chunk_write_lets_the_next_frame_retry()
    {
        var driver = new GlacialLcdPanelDriver(0, 60_000);
        var pipe = new FakePipe { FailWrites = true };
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(driver.WaitIdle(30_000));
        pipe.FailWrites = false;
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(driver.WaitIdle(30_000));

        Assert.NotEmpty(pipe.Writes);
    }

    [Fact]
    public void Brightness_follows_the_query_then_settings_sequence_and_keeps_the_panel_state()
    {
        var driver = new GlacialLcdPanelDriver(0);
        var hid = new FakeHid();
        hid.Replies.Add(Report("EC 51"));
        hid.Replies.Add(Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32 14"));

        Assert.True(driver.SetBrightness(new FakePipe(), hid, 70));

        Assert.Equal(
            new[] { GlacialLcdProtocol.EncodeQuery(), GlacialLcdProtocol.EncodeSettings(70, 0x14, true) },
            hid.Writes);
    }

    [Fact]
    public void Brightness_without_a_state_reply_still_writes_settings()
    {
        var driver = new GlacialLcdPanelDriver(0);
        var hid = new FakeHid();

        Assert.True(driver.SetBrightness(new FakePipe(), hid, 30));

        Assert.Equal(GlacialLcdProtocol.EncodeSettings(30, GlacialLcdProtocol.ModeAnimatedAsset, false), hid.Writes[1]);
    }

    [Fact]
    public void Driver_identity_and_policy_defaults()
    {
        var driver = new GlacialLcdPanelDriver();
        Assert.Equal(0x0B05, driver.VendorId);
        Assert.Equal(new[] { 0x1D93 }, driver.ProductIds);
        Assert.Equal(0x02, driver.WritePipeId);
        Assert.Equal(0x82, driver.ReadPipeId);
        Assert.True(driver.NeedsHidChannel);
        Assert.True(driver.SupportsBrightness);
        Assert.Equal(0, ((IBulkPanelDriver)driver).KeepaliveMs);
    }

    private sealed class FakePipe : IBulkUsbPipe
    {
        public List<byte[]> Writes { get; } = new();
        public bool FailWrites { get; set; }

        public bool Write(ReadOnlySpan<byte> data)
        {
            if (FailWrites)
            {
                return false;
            }
            Writes.Add(data.ToArray());
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class FakeHid : IHidDevice
    {
        private int _replyIndex;

        public List<byte[]> Writes { get; } = new();
        public List<byte[]> Replies { get; } = new();

        public int VendorId => 0x0B05;
        public int ProductId => GlacialLcdProtocol.ProductId;
        public string Path => "/dev/fake-glacial";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF72;
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

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replyIndex >= Replies.Count)
            {
                return 0;
            }
            var reply = Replies[_replyIndex++];
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() { }
    }
}

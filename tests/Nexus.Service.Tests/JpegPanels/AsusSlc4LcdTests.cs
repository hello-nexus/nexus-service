using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// Vectors are bytes taken from a customer's USB capture of the vendor app driving a
/// ROG Strix SLC IV LCD (0B05:1DE6). They verify the encoder against the app's own output,
/// not against a unit: no device reply was captured.
/// </summary>
public class AsusSlc4LcdTests
{
    // Wire bytes of the first captured control messages, closing 0x5A included, zero padding dropped.
    [Theory]
    [InlineData("waterBlockScreen", 14, 1792452123667L, "{\"enable\":true}",
        "5a0075504f5354207761746572426c6f636b53637265656e20310d0a5365714e756d6265723d31340d0a446174653d313739323435323132333636370d0a436f6e74656e74547970653d6a736f6e0d0a436f6e74656e744c656e6774683d31350d0a0d0a7b22656e61626c65223a747275657dab5a")]
    [InlineData("conn", 0, 1792451887450L, null,
        "5a0035504f535420636f6e6e20310d0a5365714e756d6265723d300d0a446174653d313739323435313838373435300d0a0d0a5d5a")]
    [InlineData("power", 1, 1792451887782L, "{\"event\":\"resume\"}",
        "5a006c504f535420706f77657220310d0a5365714e756d6265723d310d0a446174653d313739323435313838373738320d0a436f6e74656e74547970653d6a736f6e0d0a436f6e74656e744c656e6774683d31380d0a0d0a7b226576656e74223a22726573756d65227d0e5a")]
    [InlineData("devStateGet", 2, 1792451888804L, null,
        "5a003c504f535420646576537461746547657420310d0a5365714e756d6265723d320d0a446174653d313739323435313838383830340d0a0d0a1c5a")]
    [InlineData("realtimeDisplay", 3, 1792451889816L, "{\"enable\":true}",
        "5a0073504f5354207265616c74696d65446973706c617920310d0a5365714e756d6265723d330d0a446174653d313739323435313838393831360d0a436f6e74656e74547970653d6a736f6e0d0a436f6e74656e744c656e6774683d31350d0a0d0a7b22656e61626c65223a747275657d405a")]
    [InlineData("brightness", 8, 1792452040085L, "{\"value\":10}",
        "5a006b504f5354206272696768746e65737320310d0a5365714e756d6265723d380d0a446174653d313739323435323034303038350d0a436f6e74656e74547970653d6a736f6e0d0a436f6e74656e744c656e6774683d31320d0a0d0a7b2276616c7565223a31307d8b5a")]
    [InlineData("realtimeDisplay", 12, 1792452112823L, "{\"enable\":false}",
        "5a0075504f5354207265616c74696d65446973706c617920310d0a5365714e756d6265723d31320d0a446174653d313739323435323131323832330d0a436f6e74656e74547970653d6a736f6e0d0a436f6e74656e744c656e6774683d31360d0a0d0a7b22656e61626c65223a66616c73657da85a")]
    public void Control_message_matches_the_captured_wire_bytes(
        string command, int sequence, long unixMs, string? body, string expectedHex)
    {
        var text = AsRockLcdProtocol.BuildMessage("POST", command, sequence, unixMs, body);
        var encoded = AsRockLcdProtocol.Encode(Encoding.ASCII.GetBytes(text));

        // Encode prepends the HID report id.
        Assert.Equal(0x00, encoded[0]);
        Assert.Equal(expectedHex, Convert.ToHexString(encoded.AsSpan(1)).ToLowerInvariant());
    }

    private static byte[] CapturedFrameShapedJpeg()
    {
        // The first captured frame: 67267 bytes, so 67 full chunks and a 267 byte tail.
        var jpeg = new byte[67267];
        Convert.FromHexString("ffd8ffe000104a46").CopyTo(jpeg, 0);
        Convert.FromHexString("f050272800610230").CopyTo(jpeg, 67000);
        Convert.FromHexString("97db2d5489a3ffd9").CopyTo(jpeg, jpeg.Length - 8);
        return jpeg;
    }

    [Fact]
    public void First_chunk_header_matches_the_capture()
    {
        var jpeg = CapturedFrameShapedJpeg();
        var report = new byte[1025];

        int took = JpegPanelProtocol.FillChunk(report, JpegPanelHeaderStyle.AsusFramed, 0x5C, jpeg, 0, 0, frameStamp: 9);

        Assert.Equal(1000, took);
        // Captured: 5C 03 FD 09 00 44 00 00 01, fifteen zeros, then the JPEG's SOI and JFIF marker.
        Assert.Equal(
            "005c03fd090044000001000000000000000000000000000000ffd8ffe000104a46",
            Convert.ToHexString(report.AsSpan(0, 33)).ToLowerInvariant());
    }

    [Fact]
    public void Last_chunk_header_and_padding_match_the_capture()
    {
        var jpeg = CapturedFrameShapedJpeg();
        var report = new byte[1025];

        int took = JpegPanelProtocol.FillChunk(report, JpegPanelHeaderStyle.AsusFramed, 0x5C, jpeg, 67000, 67, frameStamp: 9);

        Assert.Equal(267, took);
        // Captured: 5C 01 20 09 00 44 00 43 01 ... then f0 50 27 28 00 61 02 30.
        Assert.Equal(
            "005c0120090044004301000000000000000000000000000000f050272800610230",
            Convert.ToHexString(report.AsSpan(0, 33)).ToLowerInvariant());
        Assert.Equal("97db2d5489a3ffd9", Convert.ToHexString(report.AsSpan(25 + 267 - 8, 8)).ToLowerInvariant());
        Assert.All(report[(25 + 267)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Chunk_index_walks_the_frame_in_captured_order()
    {
        var jpeg = CapturedFrameShapedJpeg();
        var report = new byte[1025];
        int offset = 0;
        int index = 0;
        while (offset < jpeg.Length)
        {
            offset += JpegPanelProtocol.FillChunk(report, JpegPanelHeaderStyle.AsusFramed, 0x5C, jpeg, offset, index, 21);
            Assert.Equal(21, report[4]);
            Assert.Equal(0x44, report[6]);
            Assert.Equal(index, report[8]);
            index++;
        }
        Assert.Equal(68, index);
    }

    [Fact]
    public void A_frame_needing_more_than_255_chunks_is_refused_not_wrapped()
    {
        var report = new byte[1025];
        var jpeg = new byte[256_000];

        Assert.Equal(0, JpegPanelProtocol.FillChunk(report, JpegPanelHeaderStyle.AsusFramed, 0x5C, jpeg, 0, 0));
    }

    [Fact]
    public void Asrock_style_still_writes_a_zero_stamp()
    {
        var report = new byte[1025];

        JpegPanelProtocol.FillChunk(report, JpegPanelHeaderStyle.AsRockFramed, 0x5C, new byte[100], 0, 0, frameStamp: 9);

        Assert.Equal(0, report[4]);
    }

    [Fact]
    public void A_noise_frame_is_stepped_down_until_it_fits_the_chunk_counter()
    {
        var model = JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() };
        using var hub = new JpegPanelHub(model);
        var device = new RecordingHidDevice { Responder = AllOk };
        Assert.True(hub.Attach(device));
        using var transport = new Nexus.Service.Panel.Streams.JpegPanelStreamTransport(hub, "SLC4");
        transport.Open();

        // Incompressible content: at the default quality this is far past 255 chunks.
        var frame = new byte[720 * 720 * 4];
        new Random(7).NextBytes(frame);
        device.Writes.Clear();

        transport.Write(frame);

        Assert.InRange(device.Writes.Count, 1, 255);
        Assert.Equal(device.Writes.Count, device.Writes[0][6]);
    }

    [Fact]
    public void Model_is_a_720_square_on_the_captured_ids()
    {
        var model = JpegPanelModel.Find(0x0B05, 0x1DE6);

        Assert.Same(JpegPanelModel.AsusSlc4Lcd, model);
        Assert.Equal(720, model!.Width);
        Assert.Equal(720, model.Height);
        Assert.Equal(1025, model.ReportLength);
        Assert.True(model.SupportsBrightness);
    }

    [Fact]
    public void Attach_replays_the_vendor_start_sequence_in_order()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() });
        var device = new RecordingHidDevice { Responder = AllOk };

        Assert.True(hub.Attach(device));

        // conn and power, then the vendor's screen-on exchange.
        Assert.Equal(
            new[]
            {
                "POST conn 1", "POST power 1", "POST waterBlockScreen 1",
                "POST devStateGet 1", "POST realtimeDisplay 1",
            },
            Requests(device.Writes));
        Assert.Contains("{\"event\":\"resume\"}", Text(device.Writes[1]));
        Assert.Contains("{\"enable\":true}", Text(device.Writes[2]));
        Assert.Contains("SeqNumber=4", Text(device.Writes[4]));
        Assert.Contains("{\"enable\":true}", Text(device.Writes[4]));
    }

    [Fact]
    public void An_acknowledged_enable_is_not_resent_while_streaming()
    {
        var handshake = new AsusSlc4LcdHandshake();
        var device = new RecordingHidDevice { Responder = AllOk };
        Assert.True(handshake.OnAttach(device, 1025));
        device.Writes.Clear();

        Assert.True(handshake.BeforeFrame(device, 1025, Environment.TickCount64 + 60_000));

        Assert.Empty(device.Writes);
    }

    private static string Reply(int status, int sequence) =>
        $"HTTP/1.1 {status} OK\r\nSeqNumber={sequence}\r\n\r\n";

    private static string? AllOk(string command, int sequence) => Reply(200, sequence);

    private static string? AllFail(string command, int sequence) => Reply(500, sequence);

    private static byte[] Framed(string text) => AsRockLcdProtocol.Encode(Encoding.ASCII.GetBytes(text));

    [Fact]
    public void An_unacknowledged_enable_is_resent_at_a_low_rate_and_then_given_up()
    {
        var handshake = new AsusSlc4LcdHandshake();
        var device = new RecordingHidDevice { Responder = AllFail };
        Assert.True(handshake.OnAttach(device, 1025));
        device.Responder = null;
        device.Writes.Clear();
        long now = Environment.TickCount64;

        handshake.BeforeFrame(device, 1025, now + 1_000);
        Assert.Empty(device.Writes);

        for (int i = 1; i <= 3; i++)
        {
            handshake.BeforeFrame(device, 1025, now + 6_000 * i);
            Assert.Equal(i, device.Writes.Count);
            Assert.Equal("POST realtimeDisplay 1", Requests(device.Writes)[^1]);
        }

        handshake.BeforeFrame(device, 1025, now + 60_000);
        handshake.BeforeFrame(device, 1025, now + 120_000);
        Assert.Equal(3, device.Writes.Count);
    }

    [Fact]
    public void A_late_reply_to_another_command_does_not_confirm_the_enable()
    {
        var handshake = new AsusSlc4LcdHandshake();
        // The enable (sequence 4) goes unanswered; everything before it is answered.
        var device = new RecordingHidDevice { Responder = (c, n) => c == "realtimeDisplay" ? null : Reply(200, n) };
        Assert.True(handshake.OnAttach(device, 1025));
        device.Responder = null;
        device.Writes.Clear();
        long now = Environment.TickCount64;

        // A stray OK for devStateGet (sequence 3), already answered, is unmatched.
        device.Replies.Enqueue(Framed(Reply(200, 3)));
        handshake.BeforeFrame(device, 1025, now + 6_000);
        Assert.Single(device.Writes);

        // The real answer, matched by sequence, confirms it and stops the retries.
        device.Replies.Enqueue(Framed(Reply(200, 4)));
        handshake.BeforeFrame(device, 1025, now + 7_000);
        handshake.BeforeFrame(device, 1025, now + 60_000);
        Assert.Single(device.Writes);
    }

    [Fact]
    public void A_reply_without_a_sequence_is_matched_by_command_name()
    {
        var handshake = new AsusSlc4LcdHandshake();
        var device = new RecordingHidDevice { Responder = (c, n) => c == "realtimeDisplay" ? null : Reply(200, n) };
        Assert.True(handshake.OnAttach(device, 1025));
        device.Responder = null;
        device.Writes.Clear();

        device.Replies.Enqueue(Framed("HTTP/1.1 200 OK\r\nrealtimeDisplay\r\n\r\n"));
        handshake.BeforeFrame(device, 1025, Environment.TickCount64);
        handshake.BeforeFrame(device, 1025, Environment.TickCount64 + 60_000);

        Assert.Empty(device.Writes);
    }

    [Fact]
    public void A_definite_error_status_wins_over_a_stray_200_in_the_text()
    {
        var handshake = new AsusSlc4LcdHandshake();
        var device = new RecordingHidDevice
        {
            Responder = (c, n) => c == "realtimeDisplay" ? "HTTP/1.1 500 ERR\r\nSeqNumber=" + n + "\r\nnote 200 \r\n\r\n" : Reply(200, n),
        };
        Assert.True(handshake.OnAttach(device, 1025));
        device.Responder = null;
        device.Writes.Clear();

        handshake.BeforeFrame(device, 1025, Environment.TickCount64 + 6_000);

        Assert.Single(device.Writes);
    }

    [Fact]
    public void Attach_succeeds_after_waiting_out_every_missing_reply()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() });
        var device = new RecordingHidDevice();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(hub.Attach(device));

        // Five messages, each waited for in full.
        Assert.True(clock.ElapsedMilliseconds >= 5 * 1400, $"attach returned after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(5, device.Writes.Count);
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void Brightness_change_does_not_wait_for_a_reply()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() });
        var device = new RecordingHidDevice { Responder = AllOk };
        hub.Attach(device);
        device.Writes.Clear();
        device.ReadCalls = 0;

        Assert.True(hub.SetBrightness(40));

        Assert.Equal(0, device.ReadCalls);
    }

    [Fact]
    public void Attach_with_a_rejected_write_drops_the_handle()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() });
        var device = new RecordingHidDevice { FailWrites = true };

        Assert.False(hub.Attach(device));
        Assert.False(hub.IsConnected);
        Assert.True(device.Disposed);
    }

    [Fact]
    public void Brightness_sends_the_captured_value_message_and_is_reasserted_on_attach()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() });
        var device = new RecordingHidDevice { Responder = AllOk };
        hub.Attach(device);
        device.Writes.Clear();

        Assert.True(hub.SetBrightness(50));

        Assert.Equal(new[] { "POST brightness 1" }, Requests(device.Writes));
        Assert.Contains("{\"value\":50}", Text(device.Writes[0]));

        var second = new RecordingHidDevice { Responder = AllOk };
        hub.Attach(second);
        Assert.Equal("POST brightness 1", Requests(second.Writes)[^1]);
    }

    [Fact]
    public void Brightness_is_clamped_to_a_percent()
    {
        var handshake = new AsusSlc4LcdHandshake();

        handshake.SetBrightness(250);
        Assert.Equal(100, handshake.Brightness);
        handshake.SetBrightness(-4);
        Assert.Equal(0, handshake.Brightness);
    }

    [Fact]
    public void Detach_stops_the_realtime_display_without_a_disconnect()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() });
        var device = new RecordingHidDevice { Responder = AllOk };
        hub.Attach(device);
        device.Writes.Clear();

        hub.Detach();

        Assert.Equal(new[] { "POST realtimeDisplay 1" }, Requests(device.Writes));
        Assert.Contains("{\"enable\":false}", Text(device.Writes[0]));
    }

    [Fact]
    public void SendFrame_writes_one_report_per_chunk_with_the_report_id_first()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.AsusSlc4Lcd with { Handshake = new AsusSlc4LcdHandshake() });
        var device = new RecordingHidDevice { Responder = AllOk };
        hub.Attach(device);
        device.Writes.Clear();

        Assert.True(hub.SendFrame(CapturedFrameShapedJpeg()));

        Assert.Equal(68, device.Writes.Count);
        Assert.All(device.Writes, w =>
        {
            Assert.Equal(1025, w.Length);
            Assert.Equal(0x00, w[0]);
            Assert.Equal(0x5C, w[1]);
        });
    }

    private static string Text(byte[] report)
    {
        // Past the report id, the 0x5A marker and the length: the message runs to its CRLF CRLF.
        var s = Encoding.ASCII.GetString(report, 4, report.Length - 4);
        int end = s.IndexOf('\0');
        return end < 0 ? s : s[..end];
    }

    private static string[] Requests(List<byte[]> writes)
    {
        var lines = new List<string>();
        foreach (var w in writes)
        {
            lines.Add(Text(w).Split("\r\n")[0]);
        }
        return lines.ToArray();
    }

    private sealed class RecordingHidDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public bool FailWrites { get; set; }
        public Func<string, int, string?>? Responder { get; set; }
        public Queue<byte[]> Replies { get; } = new();
        public int ReadCalls { get; set; }
        public bool Disposed { get; private set; }

        public int VendorId => 0x0B05;
        public int ProductId => 0x1DE6;
        public string Path => "/dev/fake-slc4";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            if (Responder is not null && report.Length > 1 && report[1] == 0x5A)
            {
                var sent = Text(report.ToArray());
                var head = sent.Split("\r\n")[0].Split(' ');
                int sequence = int.Parse(sent.Split("SeqNumber=")[1].Split("\r\n")[0]);
                var answer = Responder(head[1], sequence);
                if (answer is not null)
                {
                    Replies.Enqueue(Framed(answer));
                }
            }
            return !FailWrites;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            ReadCalls++;
            if (Replies.Count == 0)
            {
                return 0;
            }
            var reply = Replies.Dequeue();
            reply.CopyTo(buffer);
            return reply.Length;
        }
        public void Dispose() => Disposed = true;
    }
}

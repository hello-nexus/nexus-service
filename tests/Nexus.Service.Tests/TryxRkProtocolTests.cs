using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRkProtocolTests
{
    // Byte-for-byte camera-verified reference frames, captured against the physical
    // RK-firmware Panorama panel (VID 0x391A). Any change to these bytes must be
    // re-verified against real hardware, not derived from the protobuf writer.

    [Fact]
    public void BuildHeartbeat_matches_camera_verified_frame()
    {
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x0c, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0x52, 0x08, 0x0a, 0x06, 0x68, 0x65, 0x6c, 0x6c, 0x6f, 0x3f,
        };

        var actual = TryxRkProtocol.BuildHeartbeat();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildConfig_screenOn_100_matches_camera_verified_frame()
    {
        // screen on -> f5 carries f1:1 (0x08 0x01) before f2:brightness.
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x0b, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0xc2, 0x0c, 0x06, 0x2a, 0x04, 0x08, 0x01, 0x10, 0x64,
        };

        var actual = TryxRkProtocol.BuildConfig(screenOn: true, 100);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildConfig_screenOff_50_matches_camera_verified_frame()
    {
        // screen off -> f5 omits f1; carries only f2:50 (0x10 0x32).
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x09, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0xc2, 0x0c, 0x04, 0x2a, 0x02, 0x10, 0x32,
        };

        var actual = TryxRkProtocol.BuildConfig(screenOn: false, 50);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(150, 100)]
    public void BuildConfig_clamps_out_of_range_values(int input, int clamped)
    {
        var expected = TryxRkProtocol.BuildConfig(screenOn: true, clamped);

        var actual = TryxRkProtocol.BuildConfig(screenOn: true, input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildPreset_matches_the_captured_kanali_config()
    {
        // Byte-for-byte equal to Kanali's own preset-select config (USBPcap capture,
        // default_02 wallpaper, screen on, brightness 26): f200 with f1=power-on,
        // f2=standby, f3=wallpaper (nested f3), f5=screen+brightness.
        var expected = Convert.FromHexString(
            "545259587a0000000a00c20c750a240a2264656661756c745f706f7765726f" +
            "6e2e6d70342e683236345f32323430783130383012260801122264656661756c" +
            "745f7374616e6462792e6d70342e683236345f3232343078313038301a1f1a1d" +
            "64656661756c745f30322e6d70342e683236345f3232343078313038302a0408" +
            "01101a");

        var actual = TryxRkProtocol.BuildPreset(
            TryxRkProtocol.PresetMediaFile(2), screenOn: true, 26);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PresetMediaFile_zero_pads_the_index()
    {
        Assert.Equal("default_02.mp4.h264_2240x1080", TryxRkProtocol.PresetMediaFile(2));
        Assert.Equal("default_21.mp4.h264_2240x1080", TryxRkProtocol.PresetMediaFile(21));
    }

    [Fact]
    public void PresetMediaFile_preserves_a_panel_reported_id_verbatim()
    {
        Assert.Equal("default_007.mp4.h264_2240x1080", TryxRkProtocol.PresetMediaFile("default_007"));
    }

    [Fact]
    public void BuildFileBegin_matches_the_captured_kanali_frame()
    {
        // USBPcap capture of a Kanali custom-video upload: f1{f2:sessionId} +
        // f400{f1:fileName, f2:fileSize}. session 668387, size 2278333.
        var expected = Convert.FromHexString(
            "545259583a0000000a0410e3e5288219310a2a323032362d30372d30315f3232" +
            "2d30342d32342d3233372e6d70342e683236345f3232343078313038301" +
            "0bd878b01");

        var actual = TryxRkProtocol.BuildFileBegin(
            668387, "2026-07-01_22-04-24-237.mp4.h264_2240x1080", 2278333);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildFileRemove_carries_the_header_and_file_remove()
    {
        var text = Encoding.ASCII.GetString(TryxRkProtocol.BuildFileRemove("clip.mp4", "BYZL9"));

        Assert.StartsWith("TRYX", text);
        Assert.Contains("CMD_File_Remove", text);
        Assert.Contains("BYZL9", text);   // sn = header field 3 (locked-command requirement)
        Assert.Contains("clip.mp4", text);
        Assert.Contains("media", text);
    }

    [Fact]
    public void BuildGetFileList_carries_the_cmd_serial_and_body_case()
    {
        var frame = TryxRkProtocol.BuildGetFileList("BYZL123");
        var text = Encoding.ASCII.GetString(frame);

        Assert.StartsWith("TRYX", text);
        Assert.Contains("CMD_Get_FileList", text);
        Assert.Contains("BYZL123", text); // sn = header field 3
        // The panel dispatches on the body oneof, so the (empty) get_file_list body (field 103,
        // tag 0xBA 0x06) MUST be present or the panel answers BodyCaseNotSupported.
        Assert.Contains("BA0600", Convert.ToHexString(frame));
    }

    [Fact]
    public void BuildGetDeviceInfo_carries_the_cmd_and_body_case()
    {
        var frame = TryxRkProtocol.BuildGetDeviceInfo();
        var text = Encoding.ASCII.GetString(frame);

        Assert.StartsWith("TRYX", text);
        Assert.Contains("CMD_Get_DeviceInfo", text);
        // get_device_info body (field 100, tag 0xA2 0x06) - dispatched on the body oneof.
        Assert.Contains("A20600", Convert.ToHexString(frame));
    }

    [Fact]
    public void BuildFileCommit_matches_the_captured_kanali_frame()
    {
        // f1{f2:668387} + f402{f1:"media"}.
        var expected = Convert.FromHexString("54525958100000000a0410e3e5289219070a056d65646961");

        var actual = TryxRkProtocol.BuildFileCommit(668387, "media");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildFileChunk_wraps_the_payload_in_f401_under_the_session_envelope()
    {
        var chunk = new byte[] { 0xde, 0xad, 0xbe, 0xef };

        var frame = TryxRkProtocol.BuildFileChunk(668387, chunk);

        // f1{f2:668387} (0a04 10 e3e528) then f401 (tag 8a19) len 6: 0a04 deadbeef.
        var expected = Convert.FromHexString("54525958" + "0f000000" + "0a0410e3e528" + "8a19060a04deadbeef");
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void WrapMediaContainer_prefixes_the_header_and_appends_the_stream()
    {
        var h264 = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x11, 0x22 };

        var c = TryxRkProtocol.WrapMediaContainer(h264, fps: 60, width: 2240, height: 1080, frameCount: 172, id: 1297631300);

        var headerLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(c);
        var header = System.Text.Encoding.ASCII.GetString(c, 4, (int)headerLen);
        Assert.Contains("Tryx media header v1, fps=60, size=2240x1080", header);
        // The raw stream is appended verbatim after the header.
        Assert.Equal(h264, c[(4 + (int)headerLen)..]);
    }

    // BuildOverlay tests below assert structural properties of the hand-rolled
    // protobuf; they are not camera-verified reference frames like the tests above.


    [Fact]
    public void BuildFilePullRequest_matches_the_bench_verified_frame()
    {
        // Sent to the Y70 panel (firmware v2.0.6.20260713); it answered with the file's first 64 KiB.
        var expected = System.Convert.FromHexString(
            "54525958350000000a00b219300a2a323032362d30372d30355f31302d31372d30342d3538362e6d70342e" +
            "683236345f32323430783130383010021800");

        var actual = TryxRkProtocol.BuildFilePullRequest("2026-07-05_10-17-04-586.mp4.h264_2240x1080", 2, 0);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnmaskPulledData_restores_the_container_header_from_captured_bytes()
    {
        // First bytes of a pulled upload as the panel sent them, and the Tryx container they encode.
        byte[] data = [0x43, 0x01, 0x02, 0x03, 0x0c, 0xc1, 0x96, 0xe6, 0xe2, 0x0d, 0x18, 0x27, 0x58, 0x7f, 0x77, 0x77];

        TryxRkProtocol.UnmaskPulledData(data, fileOffset: 0);

        Assert.Equal(new byte[] { 0x43, 0, 0, 0, 0x08 }, data[..5]);
        Assert.Equal("Tryx", Encoding.ASCII.GetString(data, 12, 4));
    }

    [Fact]
    public void UnmaskPulledData_keys_on_the_absolute_file_offset()
    {
        byte[] data = [0x00, 0x00];

        TryxRkProtocol.UnmaskPulledData(data, fileOffset: 65536 + 255);

        Assert.Equal(new byte[] { 0xFF, 0x00 }, data);
    }

    private static readonly (double X, double Y)[] NoPositions = Array.Empty<(double, double)>();

    [Fact]
    public void BuildOverlay_starts_with_a_valid_frame_header()
    {
        var frame = TryxRkProtocol.BuildOverlay(
            new[] { new TryxOverlayLine("CPU Temperature", "44C") }, NoPositions,
            colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "left");

        Assert.Equal((byte)'T', frame[0]);
        Assert.Equal((byte)'R', frame[1]);
        Assert.Equal((byte)'Y', frame[2]);
        Assert.Equal((byte)'X', frame[3]);
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4));
        Assert.Equal((uint)(frame.Length - 8), payloadLen);
    }

    [Fact]
    public void BuildOverlay_with_empty_lines_sends_an_empty_field201()
    {
        var frame = TryxRkProtocol.BuildOverlay(
            System.Array.Empty<TryxOverlayLine>(), NoPositions,
            colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "left");

        // f1{} empty (0x0a 0x00) then f201{} empty: tag (201<<3|2) varint 0xca 0x0c, length 0.
        byte[] expectedPayload = { 0x0a, 0x00, 0xca, 0x0c, 0x00 };
        Assert.Equal(expectedPayload, frame[8..]);
    }

    [Fact]
    public void BuildOverlay_contains_the_font_label_and_value_text()
    {
        var lines = new[] { new TryxOverlayLine("cpu temperature", "44°C") };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, NoPositions, colorRgb: 0xFF3030, fontName: "roboto-regular", sizePercent: 100, align: "left");

        var text = Encoding.UTF8.GetString(frame);
        Assert.Contains("roboto-regular", text);
        Assert.Contains("44°C", text);
        // Labels render in their given case; the builder no longer force-uppercases.
        Assert.Contains("cpu temperature", text);
    }

    [Fact]
    public void BuildOverlay_writes_the_configured_font_name()
    {
        var lines = new[] { new TryxOverlayLine("cpu temperature", "44°C") };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, NoPositions, colorRgb: 0xFFFFFF, fontName: "monospace", sizePercent: 100, align: "left");

        var text = Encoding.UTF8.GetString(frame);
        Assert.Contains("monospace", text);
        Assert.DoesNotContain("roboto-regular", text);
    }

    [Fact]
    public void BuildOverlay_encodes_the_color_as_a_field10_varint()
    {
        var lines = new[] { new TryxOverlayLine("GPU Usage", "62%") };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, NoPositions, colorRgb: 0x40C0FF, fontName: "roboto-regular", sizePercent: 100, align: "left");

        byte[] colorTagAndValue = { 0x50, 0xff, 0x81, 0x83, 0x02 };
        Assert.True(ContainsSequence(frame, colorTagAndValue));
    }

    [Fact]
    public void BuildOverlay_emits_two_widgets_per_line()
    {
        var lines = new[]
        {
            new TryxOverlayLine("CPU Temperature", "44C"),
            new TryxOverlayLine("GPU Temperature", "50C"),
        };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, NoPositions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "left");
        var payload = frame[8..];

        var top = ParseLengthDelimitedFields(payload);
        var f201 = Assert.Single(top, f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();

        Assert.Equal(4, widgets.Count);
        foreach (var widget in widgets)
        {
            var elems = ParseLengthDelimitedFields(widget.Value).Where(f => f.Number == 8).ToList();
            Assert.Single(elems);
        }
    }

    // ── Position / size / font layout math ──

    private static (int WidgetId, int X, int Y, int W, int H, int Align, int FontSize) DecodeWidget(byte[] widget)
    {
        var elem = ParseLengthDelimitedFields(widget).Single(f => f.Number == 8).Value;
        return (
            WidgetId: (int)ReadVarintField(widget, 1),
            X: (int)ReadVarintField(widget, 2),
            Y: (int)ReadVarintField(widget, 3),
            W: (int)ReadVarintField(widget, 4),
            H: (int)ReadVarintField(widget, 5),
            Align: (int)ReadVarintField(widget, 6),
            FontSize: (int)ReadVarintField(elem, 9));
    }

    // Reads a top-level varint field's value; mirrors ParseLengthDelimitedFields'
    // traversal but returns a matching wireType-0 field instead of skipping it.
    private static ulong ReadVarintField(byte[] buf, int wantField)
    {
        var i = 0;
        while (i < buf.Length)
        {
            var (tag, tagLen) = ReadVarint(buf, i);
            i += tagLen;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (wireType == 0)
            {
                var (value, valueLen) = ReadVarint(buf, i);
                i += valueLen;
                if (fieldNumber == wantField)
                {
                    return value;
                }
            }
            else
            {
                var (len, lenLen) = ReadVarint(buf, i);
                i += lenLen + (int)len;
            }
        }
        throw new InvalidOperationException($"field {wantField} not found");
    }

    [Fact]
    public void BuildOverlay_places_the_value_widget_at_the_scaled_position()
    {
        var lines = new[] { new TryxOverlayLine("CPU Temperature", "44C") };
        var positions = new[] { (X: 0.03, Y: 0.10) };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, positions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "left");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var value = DecodeWidget(widgets[0].Value);

        Assert.Equal((int)Math.Round(0.03 * 2240), value.X);
        Assert.Equal((int)Math.Round(0.10 * 1080), value.Y);
        Assert.Equal(2240 - value.X, value.W);
        Assert.Equal(1, value.Align);
        Assert.Equal(130, value.FontSize);
    }

    [Fact]
    public void BuildOverlay_places_the_label_widget_below_the_value_by_the_scaled_gap()
    {
        var lines = new[] { new TryxOverlayLine("CPU Temperature", "44C") };
        var positions = new[] { (X: 0.03, Y: 0.10) };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, positions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "left");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var label = DecodeWidget(widgets[1].Value);

        Assert.Equal((int)Math.Round(0.03 * 2240), label.X);
        Assert.Equal((int)Math.Round(0.10 * 1080 + 150), label.Y);
        Assert.Equal(52, label.FontSize);
    }

    [Fact]
    public void BuildOverlay_scales_font_sizes_and_the_vertical_gap_by_size_percent()
    {
        var lines = new[] { new TryxOverlayLine("CPU Temperature", "44C") };
        var positions = new[] { (X: 0.03, Y: 0.10) };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, positions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 50, align: "left");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var value = DecodeWidget(widgets[0].Value);
        var label = DecodeWidget(widgets[1].Value);

        Assert.Equal(65, value.FontSize);
        Assert.Equal(26, label.FontSize);
        Assert.Equal((int)Math.Round(0.10 * 1080 + 75), label.Y);
    }

    [Fact]
    public void BuildOverlay_defaults_a_missing_position_to_the_left_stack()
    {
        var lines = new[]
        {
            new TryxOverlayLine("CPU Temperature", "44C"),
            new TryxOverlayLine("GPU Temperature", "50C"),
        };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, NoPositions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "left");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var firstValue = DecodeWidget(widgets[0].Value);
        var secondValue = DecodeWidget(widgets[2].Value);

        Assert.Equal((int)Math.Round(0.04 * 2240), firstValue.X);
        Assert.Equal((int)Math.Round(0.12 * 1080), firstValue.Y);
        Assert.Equal((int)Math.Round(0.04 * 2240), secondValue.X);
        Assert.Equal((int)Math.Round(0.28 * 1080), secondValue.Y);
    }

    [Fact]
    public void BuildOverlay_center_align_grows_symmetrically_around_the_anchor()
    {
        var lines = new[] { new TryxOverlayLine("CPU Temperature", "44C") };
        var positions = new[] { (X: 0.5, Y: 0.10) };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, positions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "center");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var value = DecodeWidget(widgets[0].Value);

        var anchorX = (int)Math.Round(0.5 * 2240);
        var half = Math.Min(anchorX, 2240 - anchorX);
        Assert.Equal(2, value.Align);
        Assert.Equal(anchorX - half, value.X);
        Assert.Equal(2 * half, value.W);
    }

    [Fact]
    public void BuildOverlay_center_align_shrinks_to_the_shorter_side_near_an_edge()
    {
        // Anchor near the right edge: the panel-right distance is the binding
        // constraint, so the box shrinks rather than overflowing the panel.
        var lines = new[] { new TryxOverlayLine("CPU Temperature", "44C") };
        var positions = new[] { (X: 0.9, Y: 0.10) };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, positions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "center");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var value = DecodeWidget(widgets[0].Value);

        var anchorX = (int)Math.Round(0.9 * 2240);
        var half = 2240 - anchorX;
        Assert.Equal(anchorX - half, value.X);
        Assert.Equal(2 * half, value.W);
        Assert.True(value.X + value.W <= 2240);
    }

    [Fact]
    public void BuildOverlay_right_align_pins_the_anchor_as_the_right_edge()
    {
        var lines = new[] { new TryxOverlayLine("CPU Temperature", "44C") };
        var positions = new[] { (X: 0.7, Y: 0.10) };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, positions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "right");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var value = DecodeWidget(widgets[0].Value);
        var label = DecodeWidget(widgets[1].Value);

        var anchorX = (int)Math.Round(0.7 * 2240);
        Assert.Equal(3, value.Align);
        Assert.Equal(0, value.X);
        Assert.Equal(anchorX, value.W);
        Assert.Equal(3, label.Align);
        Assert.Equal(0, label.X);
        Assert.Equal(anchorX, label.W);
    }

    [Fact]
    public void BuildOverlay_unknown_align_falls_back_to_left()
    {
        var lines = new[] { new TryxOverlayLine("CPU Temperature", "44C") };
        var positions = new[] { (X: 0.3, Y: 0.10) };

        var frame = TryxRkProtocol.BuildOverlay(
            lines, positions, colorRgb: 0xFFFFFF, fontName: "roboto-regular", sizePercent: 100, align: "bogus");

        var f201 = ParseLengthDelimitedFields(frame[8..]).Single(f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();
        var value = DecodeWidget(widgets[0].Value);

        var anchorX = (int)Math.Round(0.3 * 2240);
        Assert.Equal(1, value.Align);
        Assert.Equal(anchorX, value.X);
        Assert.Equal(2240 - anchorX, value.W);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return true;
            }
        }
        return false;
    }

    // Minimal protobuf reader (varint + length-delimited fields only) used to
    // structurally verify BuildOverlay's output without hardcoding byte offsets.
    private static List<(int Number, byte[] Value)> ParseLengthDelimitedFields(byte[] buf)
    {
        var result = new List<(int, byte[])>();
        var i = 0;
        while (i < buf.Length)
        {
            var (tag, tagLen) = ReadVarint(buf, i);
            i += tagLen;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (wireType == 0)
            {
                var (_, valueLen) = ReadVarint(buf, i);
                i += valueLen;
            }
            else
            {
                var (len, lenLen) = ReadVarint(buf, i);
                i += lenLen;
                result.Add((fieldNumber, buf[i..(i + (int)len)]));
                i += (int)len;
            }
        }
        return result;
    }

    private static (ulong Value, int Length) ReadVarint(byte[] buf, int offset)
    {
        ulong value = 0;
        var shift = 0;
        var i = offset;
        while (true)
        {
            var b = buf[i];
            value |= (ulong)(b & 0x7F) << shift;
            i++;
            if ((b & 0x80) == 0)
            {
                break;
            }
            shift += 7;
        }
        return (value, i - offset);
    }
}

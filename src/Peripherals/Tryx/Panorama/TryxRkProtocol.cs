using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>One overlay stat line: the raw stat label and its formatted value string.</summary>
public readonly record struct TryxOverlayLine(string Label, string Value);

/// <summary>
/// Hand-rolled protobuf writer for the RK-firmware Panorama (VID 0x391A, "RK PANO").
/// A frame is ASCII "TRYX" followed by a little-endian uint32 payload length, followed
/// by the protobuf body; tags are (fieldNumber &lt;&lt; 3) | wireType varints. Camera-verified
/// against the physical panel. The heartbeat, brightness, and overlay layout commands
/// are decoded; the rest of the schema is unknown, so this class exposes nothing else.
/// </summary>
public static class TryxRkProtocol
{
    private static readonly byte[] FrameMagic = Encoding.ASCII.GetBytes("TRYX");

    // Camera-verified: widget f7=19 is required-present. Widget f6 selects the
    // text's horizontal alignment within the widget box (1=left, 2=center,
    // 3=right per Kanali's own sysinfo overlay).
    private const int OverlayWidgetF7 = 19;
    private const int OverlayAlignLeftF6 = 1;
    private const int OverlayAlignCenterF6 = 2;
    private const int OverlayAlignRightF6 = 3;

    // Coordinate space is 2240x1080 (camera-verified). A stat's label sits
    // OverlayValueLabelOffsetY below its value; both widths span from x to the
    // panel's right edge.
    public const int OverlayPanelWidth = 2240;
    public const int OverlayPanelHeight = 1080;
    private const int OverlayValueLabelOffsetY = 150;
    private const int OverlayValueWidgetHeight = 220;
    private const int OverlayLabelWidgetHeight = 160;
    private const int OverlayValueFontSize = 130;
    private const int OverlayLabelFontSize = 52;

    // Fallback stack for a stat with no configured position (normalized 0..1).
    public const double OverlayDefaultPosX = 0.04;
    public const double OverlayDefaultPosY = 0.12;
    public const double OverlayDefaultPosYStep = 0.16;

    // Panel fonts that render distinctly (camera-verified); any other name
    // silently falls back to the panel's default sans font.
    private static readonly HashSet<string> ValidOverlayFonts = new(StringComparer.Ordinal)
    {
        "roboto-regular", "roboto-thin", "roboto-light", "roboto-medium",
        "roboto-bold", "roboto-black", "roboto-italic", "roboto-condensed", "monospace",
    };

    public static bool IsValidOverlayFont(string? font) => font is not null && ValidOverlayFonts.Contains(font);

    /// <summary>
    /// Session keep-alive. The panel drops the screen to standby after about 10
    /// seconds without one, so the caller must resend on roughly a 1 Hz cadence.
    /// Field 1 is an empty submessage; field 10 carries a fixed "hello?" string.
    /// </summary>
    public static byte[] BuildHeartbeat()
    {
        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());

        var greeting = new List<byte>();
        WriteLengthDelimited(greeting, fieldNumber: 1, Encoding.ASCII.GetBytes("hello?"));
        WriteLengthDelimited(payload, fieldNumber: 10, greeting.ToArray());

        return WrapFrame(payload);
    }

    /// <summary>
    /// Minimal screen + brightness write, camera-verified not to disturb the
    /// currently playing media or any other panel state. Field 200 nests field 5:
    /// field 1 = screen-enable (present/1 = on; omitted = screen off/black), field
    /// 2 = brightness percent. Setting brightness carries the current screen state;
    /// toggling the screen carries the current brightness.
    /// </summary>
    public static byte[] BuildConfig(bool screenOn, int brightnessPercent)
    {
        var clamped = Math.Clamp(brightnessPercent, 0, 100);

        var selector = new List<byte>();
        if (screenOn)
        {
            WriteVarintField(selector, fieldNumber: 1, 1);
        }
        WriteVarintField(selector, fieldNumber: 2, (ulong)clamped);

        var configBlock = new List<byte>();
        WriteLengthDelimited(configBlock, fieldNumber: 5, selector.ToArray());

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 200, configBlock.ToArray());

        return WrapFrame(payload);
    }

    // The panel's built-in wallpapers are named default_NN.mp4.h264_2240x1080; the
    // power-on and standby clips are fixed. Camera/capture-verified: switching a
    // preset is a field 200 config where f1=power-on media, f2=standby media (f1=1
    // + name), f3=the active wallpaper (nested f3=name), f5=screen+brightness.
    private const string PresetPowerOnMedia = "default_poweron.mp4.h264_2240x1080";
    private const string PresetStandbyMedia = "default_standby.mp4.h264_2240x1080";

    /// <summary>
    /// Media filename for the 1-based preset index, e.g. 2 -> the string the panel
    /// stores for its second built-in wallpaper.
    /// </summary>
    public static string PresetMediaFile(int presetNumber)
        => PresetMediaFile($"default_{presetNumber:D2}");

    /// <summary>
    /// Media filename for a preset id as the panel reports it. Preserves the id
    /// verbatim so a panel-reported name outside the default_NN padding round-trips
    /// to the exact file the panel stores.
    /// </summary>
    public static string PresetMediaFile(string presetId)
        => $"{presetId}.mp4.h264_2240x1080";

    /// <summary>
    /// Selects a built-in wallpaper. <paramref name="wallpaperMedia"/> is the active
    /// clip (see <see cref="PresetMediaFile"/>); screen state and brightness ride
    /// along in the same config so the panel keeps them.
    /// </summary>
    public static byte[] BuildPreset(string wallpaperMedia, bool screenOn, int brightnessPercent)
    {
        var clamped = Math.Clamp(brightnessPercent, 0, 100);

        var powerOn = new List<byte>();
        WriteLengthDelimited(powerOn, fieldNumber: 1, Encoding.UTF8.GetBytes(PresetPowerOnMedia));

        var standby = new List<byte>();
        WriteVarintField(standby, fieldNumber: 1, 1);
        WriteLengthDelimited(standby, fieldNumber: 2, Encoding.UTF8.GetBytes(PresetStandbyMedia));

        var wallpaper = new List<byte>();
        WriteLengthDelimited(wallpaper, fieldNumber: 3, Encoding.UTF8.GetBytes(wallpaperMedia));

        var selector = new List<byte>();
        if (screenOn)
        {
            WriteVarintField(selector, fieldNumber: 1, 1);
        }
        WriteVarintField(selector, fieldNumber: 2, (ulong)clamped);

        var configBlock = new List<byte>();
        WriteLengthDelimited(configBlock, fieldNumber: 1, powerOn.ToArray());
        WriteLengthDelimited(configBlock, fieldNumber: 2, standby.ToArray());
        WriteLengthDelimited(configBlock, fieldNumber: 3, wallpaper.ToArray());
        WriteLengthDelimited(configBlock, fieldNumber: 5, selector.ToArray());

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 200, configBlock.ToArray());

        return WrapFrame(payload);
    }

    /// <summary>
    /// Sensor/text overlay layout. Field 201 nests a repeated field 1 per widget:
    /// f1=widgetId, f2=x, f3=y, f4=w, f5=h, f6=align, f7 fixed, then a repeated
    /// field 8 per text element (f1=elemId, f2=1 flag, f8=font, f9=fontSize,
    /// f10=RGB color, f11=text). Each stat renders as two stacked widgets, a large
    /// value and a small label <see cref="OverlayValueLabelOffsetY"/> below it.
    /// <paramref name="positions"/>[i] (normalized 0..1) is the justification
    /// anchor: left keeps the anchor as the widget's left edge, right as its
    /// right edge, center as its midpoint (symmetric within the panel width so
    /// the text visually centers on the anchor). A stat with no matching entry
    /// in <paramref name="positions"/> falls back to a left stack starting at
    /// (<see cref="OverlayDefaultPosX"/>, <see cref="OverlayDefaultPosY"/>)
    /// stepping <see cref="OverlayDefaultPosYStep"/> per line. Font size and the
    /// value-to-label gap scale by <paramref name="sizePercent"/>/100. An empty
    /// <paramref name="lines"/> list sends a field 201 with zero widgets, which
    /// clears the overlay.
    /// </summary>
    public static byte[] BuildOverlay(
        IReadOnlyList<TryxOverlayLine> lines,
        IReadOnlyList<(double X, double Y)> positions,
        int colorRgb,
        string fontName,
        int sizePercent,
        string align)
    {
        var scale = sizePercent / 100.0;

        var f201Body = new List<byte>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (rawX, rawY) = i < positions.Count
                ? positions[i]
                : (OverlayDefaultPosX, OverlayDefaultPosY + i * OverlayDefaultPosYStep);
            // Clamped here (not just at the route boundary) since x/w below feed a
            // ulong varint write; an out-of-range x would otherwise underflow w.
            var nx = Math.Clamp(rawX, 0.0, 1.0);
            var ny = Math.Clamp(rawY, 0.0, 1.0);

            var anchorX = (int)Math.Round(nx * OverlayPanelWidth);
            var (alignF6, x, w) = ResolveAlignBox(anchorX, align);
            var valueY = (int)Math.Round(ny * OverlayPanelHeight);
            var labelY = (int)Math.Round(ny * OverlayPanelHeight + OverlayValueLabelOffsetY * scale);
            var valueFontSize = (int)Math.Round(OverlayValueFontSize * scale);
            var labelFontSize = (int)Math.Round(OverlayLabelFontSize * scale);
            var valueHeight = (int)Math.Round(OverlayValueWidgetHeight * scale);
            var labelHeight = (int)Math.Round(OverlayLabelWidgetHeight * scale);

            var widgetIdBase = i * 2;
            AppendOverlayWidget(
                f201Body, widgetIdBase, x, valueY, w, valueHeight, alignF6,
                valueFontSize, colorRgb, fontName, lines[i].Value);
            AppendOverlayWidget(
                f201Body, widgetIdBase + 1, x, labelY, w, labelHeight, alignF6,
                labelFontSize, colorRgb, fontName, lines[i].Label);
        }

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 201, f201Body.ToArray());

        return WrapFrame(payload);
    }

    /// <summary>Resolves a widget's f6/x/width from its justification anchor.
    /// Left and right pin the anchor to the box's near edge and grow toward the
    /// opposite edge; center grows both directions, shrunk to the shorter side
    /// so the box never leaves the panel.</summary>
    private static (int AlignF6, int X, int W) ResolveAlignBox(int anchorX, string align) => align switch
    {
        "center" => CenterBox(anchorX),
        "right" => (OverlayAlignRightF6, 0, anchorX),
        _ => (OverlayAlignLeftF6, anchorX, OverlayPanelWidth - anchorX),
    };

    private static (int AlignF6, int X, int W) CenterBox(int anchorX)
    {
        var half = Math.Min(anchorX, OverlayPanelWidth - anchorX);
        return (OverlayAlignCenterF6, anchorX - half, 2 * half);
    }

    private static void AppendOverlayWidget(
        List<byte> f201Body, int widgetId, int x, int y, int w, int h, int alignF6, int fontSize, int colorRgb, string fontName, string text)
    {
        var elem = new List<byte>();
        WriteVarintField(elem, fieldNumber: 1, (ulong)widgetId);
        WriteVarintField(elem, fieldNumber: 2, 1);
        WriteLengthDelimited(elem, fieldNumber: 8, Encoding.UTF8.GetBytes(fontName));
        WriteVarintField(elem, fieldNumber: 9, (ulong)fontSize);
        WriteVarintField(elem, fieldNumber: 10, (ulong)colorRgb);
        WriteLengthDelimited(elem, fieldNumber: 11, Encoding.UTF8.GetBytes(text));

        var widget = new List<byte>();
        WriteVarintField(widget, fieldNumber: 1, (ulong)widgetId);
        WriteVarintField(widget, fieldNumber: 2, (ulong)x);
        WriteVarintField(widget, fieldNumber: 3, (ulong)y);
        WriteVarintField(widget, fieldNumber: 4, (ulong)w);
        WriteVarintField(widget, fieldNumber: 5, (ulong)h);
        WriteVarintField(widget, fieldNumber: 6, (ulong)alignF6);
        WriteVarintField(widget, fieldNumber: 7, OverlayWidgetF7);
        WriteLengthDelimited(widget, fieldNumber: 8, elem.ToArray());

        WriteLengthDelimited(f201Body, fieldNumber: 1, widget.ToArray());
    }

    // File transfer (custom media + cloud themes). Chunk payload size matches Kanali's
    // capture; the panel reassembles by declared fileSize so the exact value is not
    // load-bearing, but staying at Kanali's size avoids surprising the firmware.
    public const int FileChunkSize = 65485;

    /// <summary>Transfer BEGIN: f400{ f1:fileName, f2:fileSize }. All transfer frames
    /// carry the session envelope f1{f2:sessionId} (control frames use an empty f1).</summary>
    public static byte[] BuildFileBegin(uint sessionId, string fileName, long fileSize)
    {
        var begin = new List<byte>();
        WriteLengthDelimited(begin, fieldNumber: 1, Encoding.UTF8.GetBytes(fileName));
        WriteVarintField(begin, fieldNumber: 2, (ulong)fileSize);

        var payload = SessionEnvelope(sessionId);
        WriteLengthDelimited(payload, fieldNumber: 400, begin.ToArray());
        return WrapFrame(payload);
    }

    /// <summary>Transfer DATA: f401{ f1:&lt;chunk&gt; }, repeated in order until the file is sent.</summary>
    public static byte[] BuildFileChunk(uint sessionId, ReadOnlySpan<byte> chunk)
    {
        var block = new List<byte>();
        WriteLengthDelimited(block, fieldNumber: 1, chunk.ToArray());

        var payload = SessionEnvelope(sessionId);
        WriteLengthDelimited(payload, fieldNumber: 401, block.ToArray());
        return WrapFrame(payload);
    }

    /// <summary>Transfer COMMIT: f402{ f1:fileType }. fileType is "media" for a directly
    /// playable file (custom video) or "tmp" for an encrypted file awaiting a decrypt job.</summary>
    public static byte[] BuildFileCommit(uint sessionId, string fileType)
    {
        var block = new List<byte>();
        WriteLengthDelimited(block, fieldNumber: 1, Encoding.ASCII.GetBytes(fileType));

        var payload = SessionEnvelope(sessionId);
        WriteLengthDelimited(payload, fieldNumber: 402, block.ToArray());
        return WrapFrame(payload);
    }

    /// <summary>Deletes a stored file on the panel: top-level file_remove command (field 403,
    /// sibling of the transfer's file_transmit_begin/data/end at 400-402), carrying the
    /// FileRemove message { file_name = 1, file_type = 2 }. Kanali sends fileType "media" for
    /// custom/preset media. One-shot command, so an empty header like the config frames - the
    /// panel dispatches on the field number, not header.cmd. Field ids decoded from Kanali's
    /// UDB.exe protobuf descriptor; matches the CMD_File_Remove frame it emits.</summary>
    public static byte[] BuildFileRemove(string deviceFileName, string serialNumber)
    {
        // file_remove is a locked command: the panel silently ignores it unless the header
        // carries cmd (field 1) + the panel serial_number (sn = field 3). Matches Kanali's
        // {header:{cmd:"CMD_File_Remove", sn}, fileRemove:{file_name, file_type:"media"}}.
        var header = new List<byte>();
        WriteLengthDelimited(header, fieldNumber: 1, Encoding.ASCII.GetBytes("CMD_File_Remove"));
        WriteLengthDelimited(header, fieldNumber: 3, Encoding.ASCII.GetBytes(serialNumber ?? string.Empty));

        var fileRemove = new List<byte>();
        WriteLengthDelimited(fileRemove, fieldNumber: 1, Encoding.UTF8.GetBytes(deviceFileName));
        WriteLengthDelimited(fileRemove, fieldNumber: 2, Encoding.ASCII.GetBytes("media"));

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, header.ToArray());
        WriteLengthDelimited(payload, fieldNumber: 403, fileRemove.ToArray());
        return WrapFrame(payload);
    }

    /// <summary>Requests the panel's stored-media list (CMD_Get_FileList). This command has no
    /// payload field, so it is identified by the header cmd (field 1); the panel also requires
    /// its serial_number in the header (sn = field 3, serial_number_locked=true on device), so
    /// this must be the BYZL... serial from device_info, not the USB chip id. The panel replies
    /// with the file_list (f503) push it otherwise only sends unprompted on a cold boot.
    /// Kanali: {header:{cmd:"CMD_Get_FileList", sn}}.</summary>
    public static byte[] BuildGetFileList(string serialNumber)
    {
        var header = new List<byte>();
        WriteLengthDelimited(header, fieldNumber: 1, Encoding.ASCII.GetBytes("CMD_Get_FileList"));
        WriteLengthDelimited(header, fieldNumber: 3, Encoding.ASCII.GetBytes(serialNumber ?? string.Empty));

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, header.ToArray());
        // The panel dispatches on the ReqPackagePb body oneof, not the header cmd string, so the
        // (empty) get_file_list body (field 103) MUST be set - the header alone yields
        // "BodyCaseNotSupported". get_file_list = ReqPackagePb field 103 (Kanali's UDB.exe descriptor).
        WriteLengthDelimited(payload, fieldNumber: 103, Array.Empty<byte>());
        return WrapFrame(payload);
    }

    /// <summary>Requests the panel's device info (CMD_Get_DeviceInfo). The bootstrap command:
    /// no sn (it is what tells us the sn), header cmd only. The reply is a device_info (field
    /// 500) message whose serial_number (field 8) is the BYZL... serial the other commands
    /// need. Kanali: {header:{cmd:"CMD_Get_DeviceInfo"}}.</summary>
    public static byte[] BuildGetDeviceInfo()
    {
        var header = new List<byte>();
        WriteLengthDelimited(header, fieldNumber: 1, Encoding.ASCII.GetBytes("CMD_Get_DeviceInfo"));

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, header.ToArray());
        // Body oneof dispatch: set the (empty) get_device_info body (ReqPackagePb field 100), or
        // the panel answers "BodyCaseNotSupported". No sn - this is the bootstrap that learns it.
        WriteLengthDelimited(payload, fieldNumber: 100, Array.Empty<byte>());
        return WrapFrame(payload);
    }

    /// <summary>Requests one chunk of a stored file: file_pull_request (ReqPackagePb field 406)
    /// { file_name = 1, session_id = 2, file_offset = 3 }. The panel answers with a
    /// file_pull_response (field 805) of up to 64 KiB, masked (see <see cref="UnmaskPulledData"/>).
    /// A basename or the full /userdata path both resolve.</summary>
    public static byte[] BuildFilePullRequest(string deviceFileName, ulong sessionId, long offset)
    {
        var pull = new List<byte>();
        WriteLengthDelimited(pull, fieldNumber: 1, Encoding.UTF8.GetBytes(deviceFileName));
        WriteVarintField(pull, fieldNumber: 2, sessionId);
        WriteVarintField(pull, fieldNumber: 3, (ulong)offset);

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 406, pull.ToArray());
        return WrapFrame(payload);
    }

    /// <summary>Restores pulled file bytes in place: the panel XORs each byte with the low byte
    /// of its file offset (bench-verified on uploads, presets and cloud themes).</summary>
    public static void UnmaskPulledData(Span<byte> data, long fileOffset)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= (byte)(fileOffset + i);
        }
    }

    private static List<byte> SessionEnvelope(uint sessionId)
    {
        var f1 = new List<byte>();
        WriteVarintField(f1, fieldNumber: 2, sessionId);
        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, f1.ToArray());
        return payload;
    }

    /// <summary>Wraps a raw H.264 Annex-B elementary stream in the "Tryx media" container the
    /// panel expects: a little-endian uint32 header length, a protobuf header (f1=id,
    /// f2=magic-string, f3=4, f4=1, f5=fps, f6=width, f7=height, f8=frameCount), then the
    /// stream. Layout decoded from a Kanali capture (MediaX.dll MX_ConvertToH264Raw output).</summary>
    public static byte[] WrapMediaContainer(
        ReadOnlySpan<byte> h264AnnexB, int fps, int width, int height, int frameCount, uint id)
    {
        var hdr = new List<byte>();
        WriteVarintField(hdr, fieldNumber: 1, id);
        WriteLengthDelimited(hdr, fieldNumber: 2,
            Encoding.ASCII.GetBytes($"Tryx media header v1, fps={fps}, size={width}x{height}"));
        WriteVarintField(hdr, fieldNumber: 3, 4);
        WriteVarintField(hdr, fieldNumber: 4, 1);
        WriteVarintField(hdr, fieldNumber: 5, (ulong)fps);
        WriteVarintField(hdr, fieldNumber: 6, (ulong)width);
        WriteVarintField(hdr, fieldNumber: 7, (ulong)height);
        WriteVarintField(hdr, fieldNumber: 8, (ulong)frameCount);

        var result = new byte[4 + hdr.Count + h264AnnexB.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)hdr.Count);
        hdr.CopyTo(result, 4);
        h264AnnexB.CopyTo(result.AsSpan(4 + hdr.Count));
        return result;
    }

    private static byte[] WrapFrame(List<byte> payload)
    {
        var frame = new byte[FrameMagic.Length + 4 + payload.Count];
        FrameMagic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(FrameMagic.Length, 4), (uint)payload.Count);
        payload.CopyTo(frame, FrameMagic.Length + 4);
        return frame;
    }

    private static void WriteVarintField(List<byte> buf, int fieldNumber, ulong value)
    {
        WriteTag(buf, fieldNumber, wireType: 0);
        WriteVarint(buf, value);
    }

    private static void WriteLengthDelimited(List<byte> buf, int fieldNumber, byte[] value)
    {
        WriteTag(buf, fieldNumber, wireType: 2);
        WriteVarint(buf, (ulong)value.Length);
        buf.AddRange(value);
    }

    private static void WriteTag(List<byte> buf, int fieldNumber, int wireType)
        => WriteVarint(buf, ((ulong)(uint)fieldNumber << 3) | (uint)wireType);

    private static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }
}

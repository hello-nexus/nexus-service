using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Parses the RK panel's stored-media list, which it pushes unsolicited on its
/// usbprint IN endpoint on connect (protobuf, UTF-8 file paths). Rather than
/// decode the protobuf framing, this scans the decoded text for
/// "default_&lt;digits&gt;.mp4" occurrences, which is all the preset filter needs.
/// </summary>
public static class TryxMediaList
{
    private const string StoreDirMarker = "/userdata/default/";
    private const string PresetPrefix = "default_";
    private const string VideoExtension = ".mp4";

    /// <summary>Distinct wallpaper preset ids (e.g. "default_01") found in
    /// <paramref name="data"/>, sorted ascending. Empty if the buffer is not the
    /// media-list payload (does not contain <see cref="StoreDirMarker"/>).</summary>
    public static IReadOnlyList<string> ParsePresetIds(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var text = Encoding.UTF8.GetString(data);
        if (!text.Contains(StoreDirMarker, StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var searchStart = 0;
        while (true)
        {
            var prefixIndex = text.IndexOf(PresetPrefix, searchStart, StringComparison.Ordinal);
            if (prefixIndex < 0)
            {
                break;
            }

            var digitsStart = prefixIndex + PresetPrefix.Length;
            var digitsEnd = digitsStart;
            while (digitsEnd < text.Length && char.IsAsciiDigit(text[digitsEnd]))
            {
                digitsEnd++;
            }
            searchStart = digitsEnd;

            var hasDigits = digitsEnd > digitsStart;
            var followedByExtension = digitsEnd + VideoExtension.Length <= text.Length
                && text.AsSpan(digitsEnd, VideoExtension.Length).SequenceEqual(VideoExtension);
            if (hasDigits && followedByExtension)
            {
                ids.Add(text[prefixIndex..digitsEnd]);
            }
        }

        return ids.Count == 0 ? Array.Empty<string>() : new List<string>(ids);
    }

    /// <summary>One file the panel's media-list push reports: <paramref name="Name"/> is the
    /// basename (path prefix stripped), <paramref name="SizeBytes"/> its f3 size, and
    /// <paramref name="IsCustom"/> true for a user upload (path under /userdata/user/) vs a
    /// preset or cloud download (/userdata/default/).</summary>
    public readonly record struct MediaEntry(string Name, long SizeBytes, bool IsCustom);

    private const string CustomDirMarker = "/userdata/user/";

    /// <summary>Per-file entries from the media-list push: outer <c>f503 { repeated mediaFileList=1
    /// { entry }, repeated presetFileList=2 { entry } }</c> where entry = <c>{ f1:path, f2:ext,
    /// f3:sizeBytes, f4:readOnly }</c>. Both arrays are parsed (custom uploads live under
    /// /userdata/user/ in mediaFileList, presets under /userdata/default/ in presetFileList);
    /// any length field that parses as an entry (has a path + size) is included, so the field
    /// number itself doesn't gate it. Returns null when the buffer is not a complete media-list
    /// frame (another IN read, or a list split across reads) - only whole-frame reads count, a
    /// very large list that spills past a single drain read reports null until it fits.</summary>
    public static IReadOnlyList<MediaEntry>? ParseMediaEntries(ReadOnlySpan<byte> data)
    {
        // Custom uploads live under /userdata/user/, built-in presets under /userdata/default/;
        // match either so a list containing only custom media still parses.
        if (data.IsEmpty || data.IndexOf("/userdata/"u8) < 0) return null;
        if (!TryGetLenField(data, fieldNumber: 503, out var fileList)) return null;

        var entries = new List<MediaEntry>();
        var pos = 0;
        while (pos < fileList.Length)
        {
            if (!TryReadVarint(fileList, ref pos, out var tag)) break;
            var fn = (int)(tag >> 3);
            var wt = (int)(tag & 7);
            if (wt != WireLen)
            {
                if (!TrySkipField(fileList, ref pos, wt)) break;
                continue;
            }
            // Clamp against the remaining span BEFORE the int cast: a device-corrupt length
            // varint (bit 31 set, or huge) would make (int)len negative / overflow pos+len and
            // slip past a `pos + (int)len > Length` guard, then throw in Slice - and a throw here
            // kills the drain thread, re-arming the ~70s panel reset loop this transport avoids.
            if (!TryReadVarint(fileList, ref pos, out var len) || len > (ulong)(fileList.Length - pos)) break;
            var entry = fileList.Slice(pos, (int)len);
            pos += (int)len;
            // mediaFileList (custom, field 1) and presetFileList (field 2) both hold entries;
            // parse any length field that is a valid entry rather than gating on field number.
            if (TryParseEntry(entry, out var parsed))
            {
                entries.Add(parsed);
            }
        }
        return entries.Count == 0 ? null : entries;
    }

    /// <summary>Panel serial_number from a device_info reply: top-level device_info = field 500,
    /// DeviceInfo.serial_number = field 8. Null if the buffer is not a device_info frame. Field
    /// ids decoded from Kanali's UDB.exe descriptor.</summary>
    public static string? ParseSerialNumber(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        if (!TryGetLenField(data, fieldNumber: 500, out var deviceInfo)) return null;
        if (!TryGetLenField(deviceInfo, fieldNumber: 8, out var serial)) return null;
        return Encoding.UTF8.GetString(serial);
    }

    /// <summary>One file_pull_response chunk; <paramref name="Data"/> is still masked.</summary>
    public readonly record struct FilePullChunk(bool Ok, ulong SessionId, long Offset, long FileSize, byte[] Data);

    /// <summary>A file_pull_response (RspPackagePb field 805) { status = 1 (0 OK, 1 FileError),
    /// file_name = 2, session_id = 3, file_offset = 4, file_size = 5, file_data = 6 }; null for
    /// any other reply. The panel answers FileError for a missing file or an offset at EOF.</summary>
    public static FilePullChunk? ParseFilePullResponse(ReadOnlySpan<byte> payload)
    {
        if (!TryGetLenField(payload, fieldNumber: 805, out var body)) return null;
        ulong status = 0, session = 0, offset = 0, size = 0;
        var data = Array.Empty<byte>();
        var pos = 0;
        while (pos < body.Length)
        {
            if (!TryReadVarint(body, ref pos, out var tag)) return null;
            var fn = (int)(tag >> 3);
            var wt = (int)(tag & 7);
            if (wt == WireVarint)
            {
                if (!TryReadVarint(body, ref pos, out var v)) return null;
                switch (fn)
                {
                    case 1: status = v; break;
                    case 3: session = v; break;
                    case 4: offset = v; break;
                    case 5: size = v; break;
                }
                continue;
            }
            if (fn == 6 && wt == WireLen)
            {
                if (!TryReadVarint(body, ref pos, out var len) || len > (ulong)(body.Length - pos)) return null;
                data = body.Slice(pos, (int)len).ToArray();
                pos += (int)len;
                continue;
            }
            if (!TrySkipField(body, ref pos, wt)) return null;
        }
        return new FilePullChunk(status == 0, session, (long)offset, (long)size, data);
    }

    /// <summary>Non-zero ErrorPb code of a reply (RspPackagePb field 2 { code = 1, why = 2 }), e.g.
    /// 2 = BodyCaseNotSupported for a command this firmware lacks; null when the reply succeeded.</summary>
    public static int? ParseErrorCode(ReadOnlySpan<byte> payload)
    {
        if (!TryGetLenField(payload, fieldNumber: 2, out var error)) return null;
        var pos = 0;
        while (pos < error.Length)
        {
            if (!TryReadVarint(error, ref pos, out var tag)) return null;
            if (tag == (1 << 3 | WireVarint))
            {
                return TryReadVarint(error, ref pos, out var code) && code != 0 ? (int)code : null;
            }
            if (!TrySkipField(error, ref pos, (int)(tag & 7))) return null;
        }
        return null;
    }

    private static bool TryParseEntry(ReadOnlySpan<byte> entry, out MediaEntry result)
    {
        result = default;
        string? path = null;
        long size = -1;
        var epos = 0;
        while (epos < entry.Length)
        {
            if (!TryReadVarint(entry, ref epos, out var etag)) break;
            var efn = (int)(etag >> 3);
            var ewt = (int)(etag & 7);
            if (efn == 1 && ewt == WireLen)
            {
                if (!TryReadVarint(entry, ref epos, out var nlen) || nlen > (ulong)(entry.Length - epos)) break;
                path = Encoding.UTF8.GetString(entry.Slice(epos, (int)nlen));
                epos += (int)nlen;
                continue;
            }
            if (efn == 3 && ewt == WireVarint)
            {
                if (!TryReadVarint(entry, ref epos, out var s)) break;
                size = (long)s;
                continue;
            }
            if (!TrySkipField(entry, ref epos, ewt)) break;
        }
        if (path is null || size < 0) return false;
        // Basename after the last '/', so both /userdata/default/<f> and /userdata/user/<f> map
        // to just <f> (the device filename the rest of the code keys on).
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var isCustom = path.StartsWith(CustomDirMarker, StringComparison.Ordinal);
        result = new MediaEntry(name, size, isCustom);
        return true;
    }

    /// <summary>Bytes stored on the panel's /userdata, summed from <see cref="ParseMediaEntries"/>.
    /// Null under the same conditions that method returns null.</summary>
    public static long? ParseMediaUsedBytes(ReadOnlySpan<byte> data)
    {
        var entries = ParseMediaEntries(data);
        if (entries is null) return null;
        long total = 0;
        foreach (var e in entries)
        {
            total += e.SizeBytes;
        }
        return total;
    }

    private const int WireVarint = 0;
    private const int WireLen = 2;

    private static bool TryReadVarint(ReadOnlySpan<byte> b, ref int pos, out ulong val)
    {
        val = 0;
        var shift = 0;
        while (pos < b.Length && shift < 64)
        {
            var x = b[pos++];
            val |= (ulong)(x & 0x7f) << shift;
            if ((x & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> b, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case WireVarint: return TryReadVarint(b, ref pos, out _);
            case 1: pos += 8; return pos <= b.Length;   // 64-bit
            case 5: pos += 4; return pos <= b.Length;   // 32-bit
            case WireLen:
                // Reject a length past the remaining span before casting - see the note in
                // ParseMediaEntries; a negative (int)len here would leave pos negative and the
                // next varint read would index out of bounds and throw.
                if (!TryReadVarint(b, ref pos, out var len) || len > (ulong)(b.Length - pos)) return false;
                pos += (int)len;
                return true;
            default: return false;
        }
    }

    /// <summary>Content of the first length-delimited field matching <paramref name="fieldNumber"/>.</summary>
    private static bool TryGetLenField(ReadOnlySpan<byte> b, int fieldNumber, out ReadOnlySpan<byte> content)
    {
        content = default;
        var pos = 0;
        while (pos < b.Length)
        {
            if (!TryReadVarint(b, ref pos, out var tag)) return false;
            var fn = (int)(tag >> 3);
            var wt = (int)(tag & 7);
            if (wt == WireLen)
            {
                if (!TryReadVarint(b, ref pos, out var len) || len > (ulong)(b.Length - pos)) return false;
                if (fn == fieldNumber) { content = b.Slice(pos, (int)len); return true; }
                pos += (int)len;
            }
            else if (!TrySkipField(b, ref pos, wt))
            {
                return false;
            }
        }
        return false;
    }
}

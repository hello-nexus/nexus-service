using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxMediaListTests
{
    private const string SampleMediaListPayload =
        "/userdata/default/default_01.mp4.h264_2240x1080\n" +
        "/userdata/default/default_02.mp4.h264_2240x1080\n" +
        "/userdata/default/default_03.mp4.h264_2240x1080\n" +
        "/userdata/default/default_04.mp4.h264_2240x1080\n" +
        "/userdata/default/default_05.mp4.h264_2240x1080\n" +
        "/userdata/default/default_06.mp4.h264_2240x1080\n" +
        "/userdata/default/start.mp4.h264_2240x1080\n" +
        "/userdata/default/screensaver.mp4.h264_2240x1080\n" +
        "/userdata/default/default_poweron.mp4.h264_2240x1080\n";

    [Fact]
    public void ParsePresetIds_returns_only_the_default_NN_wallpapers()
    {
        var data = Encoding.UTF8.GetBytes(SampleMediaListPayload);

        var ids = TryxMediaList.ParsePresetIds(data);

        Assert.Equal(
            new[] { "default_01", "default_02", "default_03", "default_04", "default_05", "default_06" },
            ids);
    }

    [Fact]
    public void ParsePresetIds_returns_empty_when_buffer_is_not_the_media_list()
    {
        var data = Encoding.UTF8.GetBytes("some unrelated heartbeat ack payload");

        var ids = TryxMediaList.ParsePresetIds(data);

        Assert.Empty(ids);
    }

    [Fact]
    public void ParsePresetIds_returns_empty_for_an_empty_buffer()
    {
        var ids = TryxMediaList.ParsePresetIds(ReadOnlySpan<byte>.Empty);

        Assert.Empty(ids);
    }

    [Fact]
    public void ParseMediaUsedBytes_sums_the_per_file_f3_sizes()
    {
        var blob = BuildMediaListBlob(
            ("/userdata/default/default_01.mp4.h264_2240x1080", 3790601),
            ("/userdata/default/default_06.mp4.h264_2240x1080", 12774063),
            ("/userdata/default/start.mp4.h264_2240x1080", 846687));

        Assert.Equal(3790601L + 12774063L + 846687L, TryxMediaList.ParseMediaUsedBytes(blob));
    }

    [Fact]
    public void ParseMediaUsedBytes_returns_null_when_not_a_media_list()
    {
        Assert.Null(TryxMediaList.ParseMediaUsedBytes(Encoding.UTF8.GetBytes("some heartbeat ack payload")));
        Assert.Null(TryxMediaList.ParseMediaUsedBytes(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseMediaUsedBytes_returns_null_for_the_marker_without_protobuf_sizes()
    {
        // The legacy text list carries the /userdata marker but no f503/f3 framing.
        Assert.Null(TryxMediaList.ParseMediaUsedBytes(Encoding.UTF8.GetBytes(SampleMediaListPayload)));
    }

    [Fact]
    public void ParseMediaUsedBytes_does_not_throw_on_a_corrupt_length()
    {
        // The panel's USB-FFS link is known to corrupt frames; a length varint with bit 31 set
        // (0x80000000) must not slip past the bounds check and throw in Slice - a throw would kill
        // the drain thread and re-arm the ~70s reset loop.
        var marker = Encoding.UTF8.GetBytes("/userdata/default/x");
        var inner = new List<byte>();
        WriteTag(inner, 2, 2);                                        // an entry (field 2, len-delimited)...
        inner.AddRange(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 });  // ...with a length claiming 0x80000000 bytes
        var b = new List<byte>();
        WriteTag(b, 1, 2); WriteVarint(b, (ulong)marker.Length); b.AddRange(marker);   // marker so the guard proceeds
        WriteTag(b, 503, 2); WriteVarint(b, (ulong)inner.Count); b.AddRange(inner);

        Assert.Null(TryxMediaList.ParseMediaUsedBytes(b.ToArray()));
    }

    [Fact]
    public void ParseMediaEntries_returns_names_stripped_of_the_store_dir_and_their_sizes()
    {
        var blob = BuildMediaListBlob(
            ("/userdata/default/default_01.mp4.h264_2240x1080", 3790601),
            ("/userdata/default/myclip.mp4.h264_2240x1080", 12774063));

        var entries = TryxMediaList.ParseMediaEntries(blob);

        Assert.NotNull(entries);
        Assert.Equal(2, entries!.Count);
        Assert.Equal("default_01.mp4.h264_2240x1080", entries[0].Name);
        Assert.Equal(3790601L, entries[0].SizeBytes);
        Assert.Equal("myclip.mp4.h264_2240x1080", entries[1].Name);
        Assert.Equal(12774063L, entries[1].SizeBytes);
    }

    [Fact]
    public void ParseMediaEntries_parses_both_arrays_and_flags_user_partition_as_custom()
    {
        // The panel reports two arrays: mediaFileList (field 1, custom uploads under
        // /userdata/user/) and presetFileList (field 2, presets/downloads under
        // /userdata/default/). Both must parse, classified by path, regardless of field number.
        var entries = new List<byte>();
        AppendEntry(entries, fieldNumber: 1, "/userdata/user/2026-07-05_08-46-40-161.mp4.h264_2240x1080", 2278333);
        AppendEntry(entries, fieldNumber: 2, "/userdata/default/default_01.mp4.h264_2240x1080", 3790601);
        AppendEntry(entries, fieldNumber: 2, "/userdata/default/download_86.mp4.h264_2240x1080", 25036141);
        var top = new List<byte>();
        WriteTag(top, 503, 2); WriteVarint(top, (ulong)entries.Count); top.AddRange(entries);

        var parsed = TryxMediaList.ParseMediaEntries(top.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Count);
        var custom = Assert.Single(parsed, e => e.IsCustom);
        Assert.Equal("2026-07-05_08-46-40-161.mp4.h264_2240x1080", custom.Name);
        Assert.Equal(2278333L, custom.SizeBytes);
        Assert.Equal(2, parsed.Count(e => !e.IsCustom));
    }

    [Fact]
    public void ParseMediaEntries_returns_null_when_not_a_media_list()
    {
        Assert.Null(TryxMediaList.ParseMediaEntries(Encoding.UTF8.GetBytes("some heartbeat ack payload")));
        Assert.Null(TryxMediaList.ParseMediaEntries(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseMediaEntries_does_not_throw_on_a_corrupt_length()
    {
        // Same corrupt-length shape as ParseMediaUsedBytes_does_not_throw_on_a_corrupt_length;
        // this must not throw in the drain thread either.
        var marker = Encoding.UTF8.GetBytes("/userdata/default/x");
        var inner = new List<byte>();
        WriteTag(inner, 2, 2);
        inner.AddRange(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 });
        var b = new List<byte>();
        WriteTag(b, 1, 2); WriteVarint(b, (ulong)marker.Length); b.AddRange(marker);
        WriteTag(b, 503, 2); WriteVarint(b, (ulong)inner.Count); b.AddRange(inner);

        Assert.Null(TryxMediaList.ParseMediaEntries(b.ToArray()));
    }

    // Encodes the panel's frame: top-level f1{f1:1}, empty f2, then f503 { repeated f2 {
    // f1:path, f2:ext, f3:sizeBytes, f4:1 } } - the shape ParseMediaUsedBytes walks.
    [Fact]
    public void ParseFilePullResponse_reads_status_session_offset_size_and_data()
    {
        var body = new List<byte>();
        WriteTag(body, 2, 2); WriteVarint(body, 5); body.AddRange(Encoding.UTF8.GetBytes("a.mp4"));
        WriteTag(body, 3, 0); WriteVarint(body, 7);
        WriteTag(body, 4, 0); WriteVarint(body, 65536);
        WriteTag(body, 5, 0); WriteVarint(body, 715466);
        WriteTag(body, 6, 2); WriteVarint(body, 3); body.AddRange(new byte[] { 1, 2, 3 });
        var payload = new List<byte> { 0x0a, 0x02, 0x08, 0x01, 0x12, 0x00 };
        WriteTag(payload, 805, 2); WriteVarint(payload, (ulong)body.Count); payload.AddRange(body);

        var chunk = TryxMediaList.ParseFilePullResponse(payload.ToArray());

        Assert.NotNull(chunk);
        var c = chunk.Value;
        Assert.Equal((true, 7UL, 65536L, 715466L), (c.Ok, c.SessionId, c.Offset, c.FileSize));
        Assert.Equal(new byte[] { 1, 2, 3 }, c.Data);
    }

    [Fact]
    public void ParseFilePullResponse_reports_a_file_error()
    {
        var payload = new List<byte>();
        WriteTag(payload, 805, 2); WriteVarint(payload, 4);
        WriteTag(payload, 1, 0); WriteVarint(payload, 1);
        WriteTag(payload, 3, 0); WriteVarint(payload, 99);

        var chunk = TryxMediaList.ParseFilePullResponse(payload.ToArray());

        Assert.False(chunk!.Value.Ok);
        Assert.Empty(chunk.Value.Data);
    }

    [Fact]
    public void ParseErrorCode_reads_body_case_not_supported_and_ignores_success()
    {
        // Payloads as captured from the panel: an unsupported command, and a heartbeat reply.
        var unsupported = Convert.FromHexString("0a020801121808021214426f6479436173654e6f74537570706f72746564");
        var success = Convert.FromHexString("0a0208011200");

        Assert.Equal(2, TryxMediaList.ParseErrorCode(unsupported));
        Assert.Null(TryxMediaList.ParseErrorCode(success));
    }

    private static byte[] BuildMediaListBlob(params (string path, long size)[] files)
    {
        var entries = new List<byte>();
        foreach (var (path, size) in files)
        {
            var entry = new List<byte>();
            WriteTag(entry, 1, 2); WriteVarint(entry, (ulong)path.Length); entry.AddRange(Encoding.UTF8.GetBytes(path));
            WriteTag(entry, 2, 2); WriteVarint(entry, 3); entry.AddRange(Encoding.UTF8.GetBytes("mp4"));
            WriteTag(entry, 3, 0); WriteVarint(entry, (ulong)size);
            WriteTag(entry, 4, 0); WriteVarint(entry, 1);
            WriteTag(entries, 2, 2); WriteVarint(entries, (ulong)entry.Count); entries.AddRange(entry);
        }
        var top = new List<byte>();
        WriteTag(top, 1, 2); WriteVarint(top, 2); top.Add(0x08); top.Add(0x01);
        WriteTag(top, 2, 2); WriteVarint(top, 0);
        WriteTag(top, 503, 2); WriteVarint(top, (ulong)entries.Count); top.AddRange(entries);
        return top.ToArray();
    }

    // Appends one file entry { f1:path, f3:size } under the given repeated field number.
    private static void AppendEntry(List<byte> entries, int fieldNumber, string path, long size)
    {
        var entry = new List<byte>();
        WriteTag(entry, 1, 2); WriteVarint(entry, (ulong)path.Length); entry.AddRange(Encoding.UTF8.GetBytes(path));
        WriteTag(entry, 3, 0); WriteVarint(entry, (ulong)size);
        WriteTag(entries, fieldNumber, 2); WriteVarint(entries, (ulong)entry.Count); entries.AddRange(entry);
    }

    private static void WriteTag(List<byte> b, int fieldNumber, int wireType)
        => WriteVarint(b, (ulong)((fieldNumber << 3) | wireType));

    private static void WriteVarint(List<byte> b, ulong v)
    {
        while (v >= 0x80) { b.Add((byte)((v & 0x7f) | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }
}

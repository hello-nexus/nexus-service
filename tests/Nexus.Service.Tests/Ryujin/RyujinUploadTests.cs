using System.Linq;
using Nexus.Service.Peripherals.Ryujin;

namespace Nexus.Service.Tests.Ryujin;

public class RyujinUploadTests
{
    // Frame sizes of the 15 frames in the AVI Armoury Crate uploaded in the echo2 capture.
    private static readonly int[] CaptureFrameSizes =
    {
        75771, 113085, 114115, 113164, 114836, 113529, 113365, 113330, 114611, 113687, 113056, 113777, 113881, 113684, 113588,
    };

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ryujin", name));

    private static byte[] Report(string hex)
    {
        var report = new byte[65];
        Convert.FromHexString(hex.Replace(" ", "")).CopyTo(report, 0);
        return report;
    }

    // ---- AVI structure, against the uploaded file's header and index ----

    [Fact]
    public void Avi_header_and_index_match_the_captured_upload()
    {
        var frames = CaptureFrameSizes.Select(n => Enumerable.Repeat((byte)0xAB, n).ToArray()).ToArray();
        var avi = RyujinMediaAvi.Build(frames);

        Assert.Equal(1673534, avi.Length);
        Assert.Equal(Fixture("ac-upload-header.bin"), avi.AsSpan(0, RyujinMediaAvi.MoviDataOffset).ToArray());
        Assert.Equal(Fixture("ac-upload-idx1.bin"), avi.AsSpan(avi.Length - 248).ToArray());
    }

    [Fact]
    public void Avi_frames_sit_in_00dc_chunks_padded_to_even_length()
    {
        var avi = RyujinMediaAvi.Build(new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5 } });

        int at = RyujinMediaAvi.MoviDataOffset;
        Assert.Equal("00dc", System.Text.Encoding.ASCII.GetString(avi, at, 4));
        Assert.Equal(3u, BitConverter.ToUInt32(avi, at + 4));
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, avi.AsSpan(at + 8, 4).ToArray());
        Assert.Equal("00dc", System.Text.Encoding.ASCII.GetString(avi, at + 12, 4));
        Assert.Equal((uint)(avi.Length - 8), BitConverter.ToUInt32(avi, 4));
    }

    [Fact]
    public void Avi_of_one_frame_counts_one_frame()
    {
        var avi = RyujinMediaAvi.Build(new[] { new byte[] { 9, 9 } });

        Assert.Equal(1u, BitConverter.ToUInt32(avi, 24 + 8 + 16));
        Assert.Equal(1u, BitConverter.ToUInt32(avi, 100 + 8 + 32));
        Assert.Throws<ArgumentException>(() => RyujinMediaAvi.Build(Array.Empty<byte[]>()));
    }

    [Fact]
    public void Jpeg_comment_matches_the_captured_frame_opening()
    {
        // First 21 bytes of the capture's first frame: SOI, COM "Lavc58.134.100".
        var commented = RyujinMediaAvi.WithComment(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43 });

        Assert.Equal(Convert.FromHexString("FFD8FFFE00114C6176633538 2E3133342E31303000".Replace(" ", "")), commented.AsSpan(0, 21).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xDB, 0x00, 0x43 }, commented.AsSpan(21).ToArray());
    }

    // ---- command frames and chunking, against the captured interrupt and bulk transfers ----

    [Fact]
    public void Begin_frames_match_the_captured_upload_commands()
    {
        var begin = RyujinUploadProtocol.EncodeBegin(1673534);

        Assert.Equal(Report("EC 71 01 01"), begin[0]);
        Assert.Equal(Report("EC F1"), begin[1]);
        Assert.Equal(Report("EC 72 01 00 01"), begin[2]);
        Assert.Equal(Report("EC 73 01"), begin[3]);
        Assert.Equal(Report("EC 7F 02 3E 89 19 00"), begin[4]);
        Assert.Equal(Report("EC 73 FF"), RyujinUploadProtocol.EncodeEnd());
    }

    [Fact]
    public void Stored_media_layout_matches_the_captured_select()
    {
        var select = RyujinUploadProtocol.EncodeSelectStoredMedia();

        Assert.Equal(Report("EC 5D 00 01 14 01 00 00 00 01"), select[0]);
        Assert.Equal(Report("EC 51 14 01"), select[1]);
    }

    [Fact]
    public void Chunks_are_4096_bytes_with_a_zero_padded_last_one()
    {
        var file = Enumerable.Range(0, 1673534).Select(i => (byte)(i % 251 + 1)).ToArray();

        var chunks = RyujinUploadProtocol.Chunks(file).ToList();

        Assert.Equal(409, chunks.Count);
        Assert.Equal(409, RyujinUploadProtocol.ChunkCount(file.Length));
        Assert.All(chunks, c => Assert.Equal(4096, c.Length));
        Assert.Equal(1675264, chunks.Sum(c => c.Length));
        Assert.Equal(file, chunks.SelectMany(c => c).Take(file.Length).ToArray());
        Assert.All(chunks.SelectMany(c => c).Skip(file.Length), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("EE 14 00", 0x14)]
    [InlineData("EC EE 13 00", 0x13)]
    [InlineData("EC 19 00 00", 0)]
    [InlineData("EC EE", 0)]
    public void Ack_kind_reads_the_ee_event_with_or_without_the_report_id(string hex, int kind) =>
        Assert.Equal((byte)kind, RyujinUploadProtocol.AckKind(Convert.FromHexString(hex.Replace(" ", ""))));

    // ---- the sequence, against a fake link ----

    private sealed class FakeLink : IRyujinUploadLink
    {
        private readonly Queue<byte[]> _inbound = new();
        public List<string> Events { get; } = new();
        public List<int> Pauses { get; } = new();
        public bool Acks { get; init; } = true;
        public bool FailBulk { get; init; }

        public bool WriteReport(byte[] report)
        {
            Events.Add($"hid {report[1]:X2}{(report[1] is 0x73 or 0x72 ? report[2].ToString("X2") : "")}");
            if (Acks && report[1] == 0x7F)
            {
                _inbound.Enqueue(new byte[] { 0xEC, 0xEE, 0x13 });
            }
            return true;
        }

        public bool WriteChunk(ReadOnlySpan<byte> chunk)
        {
            Events.Add("chunk");
            if (Acks)
            {
                _inbound.Enqueue(new byte[] { 0xEC, 0xEE, 0x14 });
            }
            return !FailBulk;
        }

        public int ReadReport(Span<byte> buffer, int timeoutMs)
        {
            if (_inbound.Count == 0)
            {
                return 0;
            }
            var report = _inbound.Dequeue();
            report.CopyTo(buffer);
            return report.Length;
        }

        public void Pause(int ms) => Pauses.Add(ms);
    }

    private static (RyujinUploader Uploader, List<string> Log) Build(FakeLink link)
    {
        var log = new List<string>();
        return (new RyujinUploader(link, log.Add, log.Add) { AckTimeoutMs = 30, EndWaitMs = 30 }, log);
    }

    [Fact]
    public void Acked_upload_sends_each_chunk_as_soon_as_the_ack_arrives_and_never_pauses_between_chunks()
    {
        var link = new FakeLink();
        var (uploader, log) = Build(link);
        var progress = new List<int>();

        var result = uploader.Upload(new byte[10000], (sent, _) => progress.Add(sent), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(3, result.Chunks);
        Assert.Equal(new[] { "hid 71", "hid F1", "hid 7201", "hid 7301", "hid 7F", "chunk", "chunk", "chunk", "hid 73FF", "hid DC", "hid 99" }, link.Events);
        // Only the capture's four command gaps; the erase acknowledgement replaces the last one.
        Assert.Equal(new[] { 27, 140, 110, 520 }, link.Pauses);
        Assert.Equal(new[] { 1, 2, 3 }, progress);
        Assert.Contains(log, l => l.StartsWith("[ryujin-upload] done mode=fast bytes=10000 chunks=3 ") && l.Contains("ack_ms_min_avg_max=") && l.Contains("acks=3"));
    }

    [Fact]
    public void Missing_acks_retry_the_upload_paced_like_armoury_crate_and_log_it()
    {
        var link = new FakeLink { Acks = false };
        var (uploader, log) = Build(link);

        var result = uploader.Upload(new byte[4096 * 5], null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("ok after paced fallback", result.Message);
        Assert.Equal(2, link.Events.Count(e => e == "hid 71"));
        Assert.Contains(log, l => l.Contains("failed mode=fast") && l.Contains("no chunk ack"));
        Assert.Contains(log, l => l.StartsWith("[ryujin-upload] done mode=paced"));
        var chunkPauses = link.Pauses.Where(p => p is 80 or 2300).ToList();
        Assert.Equal(new[] { 80, 80, 80, 2300, 80 }, chunkPauses);
    }

    [Fact]
    public void A_failing_bulk_write_fails_both_attempts()
    {
        var link = new FakeLink { FailBulk = true };
        var (uploader, log) = Build(link);

        var result = uploader.Upload(new byte[4096], null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(2, log.Count(l => l.Contains("[ryujin-upload] failed")));
    }

    [Fact]
    public void A_keepalive_runs_between_chunks_and_is_logged_once()
    {
        var link = new FakeLink();
        var log = new List<string>();
        int calls = 0;
        var uploader = new RyujinUploader(link, log.Add, log.Add) { AckTimeoutMs = 30, EndWaitMs = 30, KeepaliveMs = 0, Keepalive = () => calls++ };

        var result = uploader.Upload(new byte[4096 * 3], null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(3, calls);
        Assert.Single(log, l => l.Contains("keepalive poll during the upload"));
        Assert.Contains(log, l => l.Contains("keepalives=3"));
    }

    [Fact]
    public void Turning_control_off_aborts_the_upload_without_a_paced_retry()
    {
        var link = new FakeLink();
        var log = new List<string>();
        int checks = 0;
        var uploader = new RyujinUploader(link, log.Add, log.Add) { AckTimeoutMs = 30, EndWaitMs = 30, KeepGoing = () => ++checks < 2 };

        var result = uploader.Upload(new byte[4096 * 4], null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(RyujinUploader.AbortedMessage, result.Message);
        Assert.Equal(1, link.Events.Count(e => e == "hid 71"));
        Assert.Equal(1, link.Events.Count(e => e == "chunk"));
    }

    // ---- encoder, against the real ffmpeg and turbojpeg when this machine has them ----

    [Fact]
    public async Task Encoder_turns_a_gif_into_an_avi_of_640x480_frames()
    {
        if (Nexus.Service.Platform.FfmpegResolver.Path is not { } ffmpeg)
        {
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "ryujin-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var gif = Path.Combine(dir, "in.gif");
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-y", "-f", "lavfi", "-i", "testsrc=d=1:r=10:s=200x100", gif })
            {
                psi.ArgumentList.Add(a);
            }
            using (var p = System.Diagnostics.Process.Start(psi)!)
            {
                await p.WaitForExitAsync();
            }

            var result = await RyujinMediaEncoder.EncodeAsync(File.ReadAllBytes(gif), ".gif", CancellationToken.None);

            if (result.Error == "The JPEG encoder is unavailable.")
            {
                return;
            }
            Assert.True(result.Ok, result.Error);
            Assert.InRange(result.Frames, 2, RyujinMediaEncoder.MaxFrames);
            Assert.Equal((uint)result.Frames, BitConverter.ToUInt32(result.Avi!, 24 + 8 + 16));
            Assert.Equal(640u, BitConverter.ToUInt32(result.Avi!, 24 + 8 + 32));
            Assert.Equal(480u, BitConverter.ToUInt32(result.Avi!, 24 + 8 + 36));
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xFE }, result.Avi!.AsSpan(RyujinMediaAvi.MoviDataOffset + 8, 4).ToArray());
            Assert.True(result.Avi!.Length <= RyujinMediaEncoder.MaxAviBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

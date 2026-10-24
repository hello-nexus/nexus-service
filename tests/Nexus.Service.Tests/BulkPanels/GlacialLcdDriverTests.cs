using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>The streamed-panel path, against the complete custom-image upload in the third capture.</summary>
public class GlacialLcdDriverTests
{
    private static byte[] Report(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty));
        var report = new byte[GlacialLcdProtocol.ReportBytes];
        bytes.CopyTo(report, 0);
        return report;
    }

    private static byte[] Frame(int noiseRows = 0, int seed = 1)
    {
        var frame = new byte[GlacialLcdProtocol.PanelWidth * GlacialLcdProtocol.PanelHeight * 4];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = (byte)(i >> 10);
            frame[i + 1] = 0x80;
            frame[i + 2] = 0x20;
            frame[i + 3] = 0xFF;
        }
        if (noiseRows > 0)
        {
            new Random(seed).NextBytes(frame.AsSpan(0, noiseRows * GlacialLcdProtocol.PanelWidth * 4));
        }
        return frame;
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return condition();
    }

    private sealed class FakeClock
    {
        private long _now = 1_000_000;

        public long Now => Interlocked.Read(ref _now);

        public void Advance(long ms) => Interlocked.Add(ref _now, ms);
    }

    private static GlacialLcdPanelDriver Fast(GlacialLcdLink? link = null, FakeClock? clock = null, int paceScale = 0, int settleMs = 0, int backoffMs = 10, bool streaming = true, int disconnectWaitMs = 0)
    {
        clock ??= new FakeClock();
        link ??= new GlacialLcdLink();
        if (streaming)
        {
            link.StreamOpened(new object());
        }
        return new GlacialLcdPanelDriver(link, paceScale, settleMs, 600_000, backoffMs, 10, () => clock.Now, disconnectWaitMs);
    }

    [Fact]
    public void A_frame_uploads_as_the_captured_sequence_ending_with_the_custom_picture_select()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        Assert.Equal((720, 1280), driver.Connect(pipe, hid));

        Assert.True(driver.SendFrame(pipe, hid, Frame()));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        var writes = hid.Writes;
        Assert.Equal(Report("EC DC"), writes[0]);
        Assert.Equal(new[] { Report("EC 71 01 01"), Report("EC F1"), Report("EC 72 01 01 01"), Report("EC 73 01") }, writes.Skip(1).Take(4));
        var announce = writes[5];
        int size = announce[3] | (announce[4] << 8) | (announce[5] << 16);
        Assert.Equal(new byte[] { 0xEC, 0x7F, 0x02 }, announce.Take(3));
        Assert.Equal(new[] { Report("EC 73 FF"), Report("EC DC"), Report("EC 51 11 01 01") }, writes.Skip(6));

        var chunks = pipe.Writes;
        Assert.Equal((size + GlacialLcdProtocol.ChunkBytes - 1) / GlacialLcdProtocol.ChunkBytes, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(GlacialLcdProtocol.ChunkBytes, c.Length));
        var jpeg = chunks.SelectMany(c => c).ToArray();
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 }, jpeg.Take(10));
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg.Skip(size - 2).Take(2));
        Assert.All(jpeg.Skip(size), b => Assert.Equal(0, b));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void The_encoded_picture_has_the_captured_dimensions_and_quantization_tables()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        var jpeg = pipe.Writes.SelectMany(c => c).ToArray();
        // The captured picture's first luminance row of the quantization table, and its frame header: 1280 high, 720 wide, 3 components.
        // The standard luminance table scaled for the upload quality, first row in zigzag order.
        var standard = new[] { 16, 11, 12, 14, 12, 10, 16, 14, 13, 14 };
        int scale = 200 - 2 * GlacialLcdProtocol.StreamJpegQuality;
        Assert.Equal(standard.Select(v => (byte)Math.Max(1, (v * scale + 50) / 100)), jpeg.Skip(25).Take(10));
        int at = IndexOf(jpeg, new byte[] { 0xFF, 0xC0 });
        Assert.Equal((1280, 720, 3), ((jpeg[at + 5] << 8) | jpeg[at + 6], (jpeg[at + 7] << 8) | jpeg[at + 8], (int)jpeg[at + 9]));
        driver.Disconnect(pipe, hid);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }
        return -1;
    }

    [Fact]
    public void A_changed_frame_follows_the_last_upload_at_once_and_an_identical_one_is_skipped()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        driver.SendFrame(pipe, hid, Frame());
        int looks = driver.Ticks + 2;
        Assert.True(WaitUntil(() => driver.Ticks >= looks));
        Assert.Equal(1, driver.UploadAttempts);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 8));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_picture_the_panel_already_holds_is_not_uploaded_again_after_a_reconnect()
    {
        var memory = new FakeMemory();
        var first = Fast();
        first.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        first.Connect(pipe, hid);
        first.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => first.UploadsCompleted == 1));
        // The remembered picture is written when the stream stops, not per upload.
        Assert.Empty(memory.Hashes);
        first.Disconnect(pipe, hid);
        Assert.Single(memory.Hashes);

        var second = Fast();
        second.BindMemory(memory);
        var pipe2 = new FakePipe();
        var hid2 = new FakeHid();
        second.Connect(pipe2, hid2);
        second.SendFrame(pipe2, hid2, Frame());
        int looks = second.Ticks + 2;
        Assert.True(WaitUntil(() => second.Ticks >= looks));

        Assert.Equal(0, second.UploadAttempts);
        Assert.Empty(pipe2.Writes);
        // The panel is known to hold it, so only the show step goes out.
        Assert.True(WaitUntil(() => hid2.Writes.Any(w => w.AsSpan().SequenceEqual(Report("EC 51 11 01 01")))));
        second.Disconnect(pipe2, hid2);
    }

    [Fact]
    public void A_failed_upload_after_the_flush_forgets_the_remembered_picture_and_backs_off()
    {
        var clock = new FakeClock();
        var memory = new FakeMemory();
        var driver = Fast(clock: clock, backoffMs: 800);
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        clock.Advance(GlacialLcdPanelDriver.PersistAfterMs);
        Assert.True(WaitUntil(() => memory.Hashes.Count == 1));

        pipe.FailWrites = true;
        driver.SendFrame(pipe, hid, Frame(noiseRows: 2));
        Assert.True(WaitUntil(() => driver.UploadAttempts == 2 && !driver.IsUploading));
        // The failure came after the flush, so nothing is assumed to be on display.
        Assert.Empty(memory.Hashes);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 3));
        int looks = driver.Ticks + 2;
        Assert.True(WaitUntil(() => driver.Ticks >= looks));
        Assert.Equal(2, driver.UploadAttempts);

        pipe.FailWrites = false;
        clock.Advance(800);
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));
        driver.Disconnect(pipe, hid);
        Assert.Single(memory.Hashes);
    }

    [Fact]
    public void Disconnect_during_an_upload_returns_promptly_and_leaves_no_finish_step()
    {
        var driver = Fast(paceScale: 100);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.Disconnect(pipe, hid);

        Assert.True(watch.ElapsedMilliseconds < 2000);
        Assert.False(driver.IsUploading);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x73 && w[2] == 0xFF);
    }

    [Fact]
    public void The_native_worker_cannot_enter_while_an_upload_runs_and_a_cancel_waits_for_its_last_step()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link, paceScale: 3);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));

        bool entered = true;
        var probe = new Thread(() =>
        {
            entered = Monitor.TryEnter(link.Sync);
            if (entered)
            {
                Monitor.Exit(link.Sync);
            }
        });
        probe.Start();
        probe.Join();
        Assert.False(entered);

        // A mode switch or a dark display arriving now does not cut the sequence short.
        link.StreamWanted = false;
        link.Dark = true;
        link.RequestCancel();
        Assert.True(WaitUntil(() => !driver.IsUploading, 20_000));
        Assert.Equal(1, driver.UploadsCompleted);
        var writes = hid.Writes;
        Assert.Contains(writes, w => w[1] == 0x73 && w[2] == 0xFF);
        Assert.Equal(0xDC, writes[^2][1]);
        Assert.Equal(0x51, writes[^1][1]);
        driver.Disconnect(pipe, hid);
    }

    private static void Looked(GlacialLcdPanelDriver driver)
    {
        int looks = driver.Ticks + 2;
        Assert.True(WaitUntil(() => driver.Ticks >= looks));
    }

    [Fact]
    public void Nothing_uploads_until_a_stream_is_open()
    {
        var driver = Fast(streaming: false);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Looked(driver);

        Assert.Equal(0, driver.UploadAttempts);
        Assert.Empty(pipe.Writes);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void With_the_nexus_panel_off_nothing_uploads_and_no_flush_is_sent()
    {
        var link = new GlacialLcdLink { NexusPanelEnabled = false };
        var driver = Fast(link);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Looked(driver);

        Assert.Equal(0, driver.UploadAttempts);
        Assert.Empty(pipe.Writes);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0xF1);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_dark_display_or_a_waiting_native_edit_holds_the_upload()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        link.Dark = true;
        driver.SendFrame(pipe, hid, Frame());
        Looked(driver);
        Assert.Equal(0, driver.UploadAttempts);

        link.Dark = false;
        link.NativePending = true;
        Looked(driver);
        Assert.Equal(0, driver.UploadAttempts);

        link.NativePending = false;
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_cancel_that_sent_no_bulk_data_keeps_the_remembered_picture()
    {
        var link = new GlacialLcdLink();
        var memory = new FakeMemory();
        memory.Hashes["asus-glacial-lcd"] = "ABC";
        var driver = Fast(link);
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        link.RequestCancel();
        driver.SendFrame(pipe, hid, Frame());
        Looked(driver);

        Assert.Equal("ABC", memory.Hashes["asus-glacial-lcd"]);
        Assert.Empty(pipe.Writes);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0xF1);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_cancel_after_the_flush_does_not_cut_the_sequence_short()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link);
        var pipe = new FakePipe();
        var hid = new FakeHid { OnWrite = w => { if (w[1] == 0xF1) { link.RequestCancel(); } } };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.NotEmpty(pipe.Writes);
        Assert.Contains(hid.Writes, w => w[1] == 0x73 && w[2] == 0xFF);
        Assert.Equal(0x51, hid.Writes[^1][1]);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_write_failing_after_the_flush_forgets_the_remembered_picture()
    {
        var memory = new FakeMemory();
        memory.Hashes["asus-glacial-lcd"] = "ABC";
        var driver = Fast();
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        hid.OnWrite = w => { if (w[1] == 0xF1) { pipe.FailWrites = true; } };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading && memory.Hashes.IsEmpty));

        Assert.Empty(memory.Hashes);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_cancel_during_the_opening_query_does_not_cut_the_sequence_short()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link);
        var pipe = new FakePipe();
        var hid = new FakeHid { OnWrite = w => { if (w[1] == 0xDC) { link.RequestCancel(); } } };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.Contains(hid.Writes, w => w[1] == 0x73 && w[2] == 0xFF);
        driver.Disconnect(pipe, hid);
    }

    [Theory]
    [InlineData("9:HASH")]
    [InlineData(":HASH")]
    [InlineData("1:")]
    [InlineData("x:HASH")]
    [InlineData("")]
    public void A_remembered_value_this_build_could_not_have_written_means_nothing_is_remembered(string value)
    {
        var memory = new FakeMemory();
        memory.Hashes["asus-glacial-lcd"] = value;
        var driver = Fast();
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        // A remembered picture would be shown at once; an unusable value must not be.
        int looks = driver.Ticks + 3;
        Assert.True(WaitUntil(() => driver.Ticks >= looks));
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x51);

        driver.SendFrame(pipe, hid, Frame());

        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        // Nothing remembered means the default slot 2 is taken as on display, so the first upload writes slot 1.
        Assert.Equal(1, hid.Writes.First(w => w[1] == 0x72)[4]);
        Assert.Equal(1, hid.Writes.First(w => w[1] == 0x51)[4]);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_stopped_worker_forgets_a_flushed_picture_when_nothing_was_stored_since()
    {
        var memory = new FakeMemory();
        memory.Hashes["asus-glacial-lcd"] = "1:KEEP";
        var driver = Fast();
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        // The link goes down right after the flush: a stopped worker, with nothing stored since.
        var hid = new FakeHid();
        hid.OnWrite = w =>
        {
            if (w[1] == 0xF1)
            {
                driver.Disconnect(pipe, hid);
            }
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading));
        Assert.True(WaitUntil(() => memory.Hashes.Count == 0));
    }

    [Fact]
    public void A_user_who_left_the_nexus_panel_mode_stops_the_upload_before_the_session_closes()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        link.StreamWanted = false;
        driver.SendFrame(pipe, hid, Frame());
        int looks = driver.Ticks + 2;
        Assert.True(WaitUntil(() => driver.Ticks >= looks));

        Assert.Equal(0, driver.UploadAttempts);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Closing_a_stream_cancels_a_running_upload_and_opening_one_clears_the_cancel()
    {
        var link = new GlacialLcdLink();
        var stream = new object();
        link.StreamOpened(stream);

        link.StreamClosed(stream);
        Assert.True(link.CancelRequested);

        link.StreamOpened(new object());
        Assert.False(link.CancelRequested);
    }

    [Fact]
    public void A_newer_frame_drops_an_upload_only_before_its_first_command()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        int reads = 0;
        var hid = new FakeHid();
        hid.OnRead = _ =>
        {
            // The first read of the first upload is its start: the newer frame arrives before any command went out.
            if (Interlocked.Increment(ref reads) == 1)
            {
                driver.SendFrame(pipe, hid, Frame(noiseRows: 100, seed: 9));
            }
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.Equal(2, driver.UploadAttempts);
        Assert.Equal(1, hid.Writes.Count(w => w[1] == 0xDC && hid.Writes.IndexOf(w) < hid.Writes.FindIndex(x => x[1] == 0xF1)));
        Assert.Equal(1, hid.Writes.Count(w => w[1] == 0xF1));
        Assert.Equal(1, hid.Writes.Count(w => w[1] == 0x73 && w[2] == 0xFF));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Once_the_flush_is_sent_a_newer_frame_never_cuts_the_upload_short()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        pipe.OnWrite = () =>
        {
            if (pipe.Writes.Count == 1)
            {
                driver.SendFrame(pipe, hid, Frame(noiseRows: 100, seed: 9));
            }
        };

        driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));

        // The first upload's end report comes before the second upload's flush.
        var kinds = hid.Writes.Select(w => w[1] == 0xF1 ? "F1" : w[1] == 0x73 && w[2] == 0xFF ? "end" : "").Where(k => k.Length > 0).ToList();
        Assert.Equal(new[] { "F1", "end", "F1", "end" }, kinds);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Pictures_keep_landing_while_the_content_changes_all_the_time()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        using var stop = new CancellationTokenSource();
        var feeder = new Thread(() =>
        {
            int seed = 0;
            while (!stop.IsCancellationRequested)
            {
                driver.SendFrame(pipe, hid, Frame(noiseRows: 20, seed: ++seed));
                Thread.Sleep(2);
            }
        });
        feeder.Start();

        bool landed = WaitUntil(() => driver.UploadsCompleted >= 3);
        stop.Cancel();
        feeder.Join();

        Assert.True(landed);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_one_per_pc_panel_is_stored_under_its_handler_id_whatever_its_usb_serial_says()
    {
        var driver = Fast();
        var hub = new BulkPanelHub(driver);
        var discovery = new Nexus.Service.Panel.Streams.BulkPanelDiscovery(hub, withheld: () => true);
        var pipe = new FakePipe();
        var hid = new FakeHid { SerialOverride = "AsusTek Computer Inc." };
        Assert.True(hub.Attach(pipe, hid));

        Assert.Equal("asus-glacial-lcd", discovery.WithheldSerial);
        Assert.Empty(discovery.Discover());
        hub.Detach();
    }

    [Fact]
    public void Entries_stored_under_earlier_serial_keys_are_dropped_on_connect()
    {
        var memory = new FakeMemory();
        memory.Hashes["365233833333"] = "OLD";
        memory.Hashes["asus-glacial-lcd"] = "KEEP";
        var driver = Fast();
        driver.BindMemory(memory);

        driver.Connect(new FakePipe(), new FakeHid());

        Assert.Equal(new[] { "asus-glacial-lcd" }, memory.Hashes.Keys);
        driver.Disconnect(new FakePipe(), new FakeHid());
    }

    [Fact]
    public void The_remembered_picture_is_written_by_persist_now_under_the_fixed_key()
    {
        var memory = new FakeMemory();
        var driver = Fast();
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        Assert.Empty(memory.Hashes);

        driver.PersistNow();

        Assert.Equal(new[] { "asus-glacial-lcd" }, memory.Hashes.Keys);
        driver.Disconnect(pipe, hid);
    }

    private static long TimedUpload(GlacialLcdPanelDriver driver, FakePipe pipe, FakeHid hid)
    {
        driver.Connect(pipe, hid);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        watch.Stop();
        driver.Disconnect(pipe, hid);
        return watch.ElapsedMilliseconds;
    }

    [Fact]
    public void With_the_pace_at_zero_nothing_is_paced_and_a_picture_goes_out_in_well_under_a_second()
    {
        var link = new GlacialLcdLink();
        link.StreamOpened(new object());
        var driver = new GlacialLcdPanelDriver(link, paceScalePercent: 0, settleMs: 0);

        long ms = TimedUpload(driver, new FakePipe(), new FakeHid());

        Assert.True(ms < 1500, ms.ToString());
    }

    [Fact]
    public void The_pace_setting_brings_the_captured_waits_back_and_is_read_per_upload()
    {
        var link = new GlacialLcdLink();
        link.StreamOpened(new object());
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { PacePercent = 10 });
        var driver = new GlacialLcdPanelDriver(link, settleMs: 0);
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        long paced = watch.ElapsedMilliseconds;

        tuning.Apply(new GlacialLcdTuningChange { PacePercent = 0 });
        watch.Restart();
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));
        long unpaced = watch.ElapsedMilliseconds;
        driver.Disconnect(pipe, hid);

        Assert.True(paced >= 700, paced.ToString());
        Assert.True(unpaced < paced / 2, $"{unpaced} vs {paced}");
    }

    [Fact]
    public void The_panels_reports_are_read_after_each_step_and_logged_with_their_bytes()
    {
        var driver = Fast();
        var lines = new List<string>();
        driver.LogSink = line => { lock (lines) { lines.Add(line); } };
        var pipe = new FakePipe();
        var hid = new FakeHid { Echo = true };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe, hid);

        List<string> seen;
        lock (lines) { seen = lines.ToList(); }
        Assert.Contains(seen, l => l.Contains("after DC:") && l.EndsWith("EC5C00000032000000321400000000" + "32", StringComparison.Ordinal));
        Assert.Contains(seen, l => l.Contains("after prelude F1:") && l.EndsWith("ECF1", StringComparison.Ordinal));
        Assert.Contains(seen, l => l.Contains("after end 73FF:") && l.EndsWith("EC73", StringComparison.Ordinal));
        Assert.Contains(seen, l => l.Contains("after show 51:") && l.EndsWith("EC51", StringComparison.Ordinal));
        Assert.Contains(seen, l => l.Contains("picture uploaded") && l.Contains("encode") && l.Contains("ms"));
    }

    private static (GlacialLcdPanelDriver Driver, FakePipe Pipe, FakeHid Hid, List<string> Lines) LoggingRig()
    {
        var driver = Fast();
        var lines = new List<string>();
        driver.LogSink = line => { lock (lines) { lines.Add(line); } };
        var pipe = new FakePipe();
        var hid = new FakeHid { Echo = true };
        driver.Connect(pipe, hid);
        return (driver, pipe, hid, lines);
    }

    private static int ReplyLines(List<string> lines)
    {
        lock (lines) { return lines.Count(l => l.Contains("reply +", StringComparison.Ordinal)); }
    }

    private static void Upload(GlacialLcdPanelDriver driver, FakePipe pipe, FakeHid hid, int seed)
    {
        int before = driver.UploadsCompleted;
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: seed));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == before + 1));
    }

    [Fact]
    public void Reply_logging_stops_after_the_first_uploads_unless_a_report_changes_and_starts_again_on_a_connect()
    {
        var (driver, pipe, hid, lines) = LoggingRig();
        for (int i = 1; i <= 3; i++)
        {
            Upload(driver, pipe, hid, i);
        }
        int afterThree = ReplyLines(lines);
        Assert.True(afterThree > 0);

        // Same answers every time: nothing more is logged.
        Upload(driver, pipe, hid, 4);
        Upload(driver, pipe, hid, 5);
        Assert.Equal(afterThree, ReplyLines(lines));

        // A different state report is logged once, at both query steps.
        hid.StateReport = Report("EC 5C 00 00 00 28 00 00 00 28 14 00 00 00 00 28");
        Upload(driver, pipe, hid, 6);
        Assert.Equal(afterThree + 2, ReplyLines(lines));
        driver.Disconnect(pipe, hid);

        // A new connect logs the first uploads in full again.
        var pipe2 = new FakePipe();
        var hid2 = new FakeHid { Echo = true };
        driver.Connect(pipe2, hid2);
        Upload(driver, pipe2, hid2, 7);
        Assert.True(ReplyLines(lines) > afterThree + 2);
        driver.Disconnect(pipe2, hid2);
    }

    [Fact]
    public void Input_left_over_from_earlier_traffic_is_not_taken_for_a_reply()
    {
        var (driver, pipe, hid, lines) = LoggingRig();
        hid.Enqueue(Report("EC 99 AA"));

        Upload(driver, pipe, hid, 1);
        driver.Disconnect(pipe, hid);

        lock (lines)
        {
            Assert.DoesNotContain(lines, l => l.Contains("EC99AA", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_cancel_during_the_wait_after_the_end_report_still_ends_with_the_query_and_the_show_step()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link);
        var pipe = new FakePipe();
        int slices = 0;
        var hid = new FakeHid
        {
            OnRead = timeoutMs =>
            {
                if (timeoutMs == 50 && Interlocked.Increment(ref slices) == 1)
                {
                    link.RequestCancel();
                }
            },
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.Equal(0xDC, hid.Writes[^2][1]);
        Assert.Equal(0x51, hid.Writes[^1][1]);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Reads_that_fail_do_not_fail_the_upload()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid { ReadsFail = true };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());

        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        // A read that reports the device gone ends each wait at once: the two state queries and the end report.
        Assert.True(hid.ReadTimeouts.Count(t => t == 50) <= 3);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_backoff_earned_on_one_link_does_not_carry_over_to_the_next_connect()
    {
        var driver = Fast(backoffMs: 600_000);
        var pipe = new FakePipe { FailWrites = true };
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading));
        driver.Disconnect(pipe, hid);

        var pipe2 = new FakePipe();
        driver.Connect(pipe2, hid);
        driver.SendFrame(pipe2, hid, Frame(noiseRows: 3));

        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe2, hid);
    }

    [Fact]
    public void Uploads_alternate_between_two_slots_and_each_new_picture_is_shown_from_the_slot_it_was_written_to()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        for (int i = 1; i <= 3; i++)
        {
            driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: i));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == i));
        }

        var targets = hid.Writes.Where(w => w[1] == 0x72 && w[3] == 0x01).Select(w => w[4]).ToArray();
        var shows = hid.Writes.Where(w => w[1] == 0x51).Select(w => w[4]).ToArray();
        // The first upload goes to the slot other than the default one on display.
        Assert.Equal(new byte[] { 1, 2, 1 }, targets);
        Assert.Equal(targets, shows);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void The_slot_on_display_is_remembered_with_the_hash_and_an_old_bare_hash_means_the_default_slot()
    {
        var memory = new FakeMemory();
        var driver = Fast();
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.PersistNow();

        Assert.StartsWith("1:", memory.Hashes["asus-glacial-lcd"], StringComparison.Ordinal);
        driver.Disconnect(pipe, hid);

        // A value from a build that always wrote slot 2: the next upload goes to the other slot.
        var old = new FakeMemory();
        old.Hashes["asus-glacial-lcd"] = "BARE";
        var next = Fast();
        next.BindMemory(old);
        var pipe2 = new FakePipe();
        var hid2 = new FakeHid();
        next.Connect(pipe2, hid2);
        next.SendFrame(pipe2, hid2, Frame(noiseRows: 5));
        Assert.True(WaitUntil(() => next.UploadsCompleted == 1));
        Assert.Equal(1, hid2.Writes.First(w => w[1] == 0x72)[4]);
        next.Disconnect(pipe2, hid2);
    }

    [Fact]
    public void A_stale_transports_close_cannot_end_a_newer_stream()
    {
        var link = new GlacialLcdLink();
        var old = new object();
        var current = new object();
        link.StreamOpened(old);
        link.StreamOpened(current);

        link.StreamClosed(old);
        Assert.True(link.Streaming);

        link.StreamClosed(current);
        Assert.False(link.Streaming);
    }

    [Fact]
    public void The_stream_flag_follows_the_stream_open_and_close_hooks()
    {
        var link = new GlacialLcdLink();
        int changes = 0;
        link.Changed += () => changes++;
        var driver = Fast(link, streaming: false);

        var stream = new object();
        driver.StreamOpened(stream);
        Assert.True(link.Streaming);
        Assert.True(link.ShowPending);
        driver.StreamClosed(stream);

        Assert.False(link.Streaming);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Identity_and_stream_defaults()
    {
        var driver = new GlacialLcdPanelDriver(new GlacialLcdLink());
        Assert.Equal(GlacialLcdWorker.HandlerId, driver.HandlerId);
        Assert.Equal(new[] { 0x1D93 }, driver.ProductIds);
        Assert.Equal(0x02, driver.WritePipeId);
        Assert.Equal(0x82, driver.ReadPipeId);
        Assert.True(driver.NeedsHidChannel);
        Assert.True(driver.SingleUnitPerPc);
        Assert.True(driver.HidOpensForInput);
        Assert.False(((IBulkPanelDriver)driver).SupportsBrightness);
        Assert.True(driver.MultiInstance);
        Assert.Equal("lcd-square", driver.Surface);
    }

    private static GlacialLcdTuning NewTuning(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "glacial-tune-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var tuning = new GlacialLcdTuning(new Nexus.Service.Tests.TestableConfigStore(Path.Combine(dir, "settings.json")));
        // The fake panel echoes opcodes, not the progress reports; tests of the reply path turn it on and script the reports.
        tuning.Apply(new GlacialLcdTuningChange { PacePercent = 0, ReplyDriven = false });
        return tuning;
    }

    private static int SentJpegBytes(FakeHid hid)
    {
        var announce = hid.Writes.Last(w => w[1] == 0x7F);
        return announce[3] | (announce[4] << 8) | (announce[5] << 16);
    }

    [Fact]
    public void A_panel_that_stays_silent_stalls_the_upload_before_anything_is_flushed_and_resume_clears_it()
    {
        var tuning = NewTuning(out _);
        var driver = Fast();
        driver.StallReplyMs = 100;
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid { Echo = false };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => tuning.Stalled));
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0xF1);
        Assert.Equal(0, driver.UploadsCompleted);
        var stats = tuning.Stats();
        Assert.Equal(1, stats.UploadsFailed);
        Assert.Contains("DC", stats.LastError);
        Assert.NotNull(stats.LastErrorAt);
        int attempts = driver.UploadAttempts;
        Looked(driver);
        Assert.Equal(attempts, driver.UploadAttempts);

        hid.Echo = true;
        tuning.Resume();
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        Assert.False(tuning.Stalled);
        Assert.Equal(1, tuning.Stats().UploadsOk);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_stall_after_the_transfer_forgets_the_picture_and_stops_uploads()
    {
        var tuning = NewTuning(out _);
        var driver = Fast();
        driver.StallReplyMs = 100;
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        hid.OnWrite = w =>
        {
            if (w[1] == 0x73 && w[2] == 0xFF)
            {
                hid.Echo = false;
                hid.Drain();
            }
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => tuning.Stalled));
        Assert.Equal(0, driver.UploadsCompleted);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x51);
        Assert.Contains("after the transfer", tuning.Stats().LastError);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void The_chunk_size_knob_sets_the_bulk_transfer_size()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ChunkKb = 8, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        int size = SentJpegBytes(hid);
        Assert.Equal((size + 8191) / 8192, pipe.Writes.Count);
        Assert.All(pipe.Writes.Take(pipe.Writes.Count - 1), w => Assert.Equal(8192, w.Length));
        // The last transfer is padded to the next 4 KB boundary only, so the total matches the 4 KB layout.
        Assert.Equal((size + 4095) / 4096 * 4096, pipe.Writes.Sum(w => w.Length));
        driver.Disconnect(pipe, hid);
    }

    [Theory]
    [InlineData(false, new[] { 2, 2 })]
    [InlineData(true, new[] { 1, 2 })]
    public void The_double_buffer_knob_picks_one_slot_or_two(bool doubleBuffer, int[] expected)
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { DoubleBuffer = doubleBuffer, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 1));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 2));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));

        var slots = hid.Writes.Where(w => w[1] == 0x72 && w[3] == 0x01).Select(w => (int)w[4]).ToArray();
        Assert.Equal(expected, slots);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void The_max_fps_knob_spaces_the_start_of_uploads()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { MaxFps = 0.5, PacePercent = 0 });
        var clock = new FakeClock();
        var driver = Fast(clock: clock);
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 1));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 2));
        clock.Advance(500);
        Looked(driver);
        Assert.Equal(1, driver.UploadAttempts);

        clock.Advance(2000);
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));
        driver.Disconnect(pipe, hid);
    }

    /// <summary>The progress reports a panel sends during an upload: begin, stage, one per transfer, then commit.</summary>
    private static void ScriptAcks(FakeHid hid, FakePipe pipe)
    {
        hid.OnWrite = w =>
        {
            if (w[1] == 0x71)
            {
                hid.Enqueue(Report("EE 12"));
            }
            else if (w[1] == 0x73 && w[2] == 0x01)
            {
                hid.Enqueue(Report("EE 13 00 01"));
            }
            else if (w[1] == 0x73 && w[2] == 0xFF)
            {
                hid.Enqueue(Report("EE 13 00 FF"));
            }
        };
        pipe.OnWrite = () => hid.Enqueue(Report("EE 14 00 00 10"));
    }

    [Fact]
    public void The_reply_knob_ends_each_wait_on_the_panels_matching_report_not_the_timer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacial-ack-" + Guid.NewGuid().ToString("N")[..8]);
        GlacialLcdLog.UseDirectory(dir);
        try
        {
            var tuning = NewTuning(out _);
            tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 100 });
            var driver = Fast();
            driver.BindTuning(tuning);
            var pipe = new FakePipe();
            var hid = new FakeHid();
            ScriptAcks(hid, pipe);
            driver.Connect(pipe, hid);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == 1, 30_000));

            // Timers alone would take well over this at the captured pace.
            Assert.True(watch.ElapsedMilliseconds < 4000, watch.ElapsedMilliseconds.ToString());
            driver.Disconnect(pipe, hid);
            GlacialLcdLog.Flush();
            string text = File.ReadAllText(Path.Combine(dir, GlacialLcdLog.FileName));
            Assert.Contains("ACK prelude 71 replied", text);
            Assert.Contains("ACK prelude 73 replied", text);
            Assert.Contains("ACK chunk 1/", text);
            Assert.Contains("ACK end 73FF replied", text);
            Assert.DoesNotContain("TIMEOUT", text);
            var stats = tuning.Stats();
            Assert.Equal(0, stats.Acks.Timeouts);
            Assert.True(stats.Acks.Replies >= 4);
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void A_larger_packet_size_gets_one_chunk_reply_per_transfer_and_no_timeout()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacial-ack-" + Guid.NewGuid().ToString("N")[..8]);
        GlacialLcdLog.UseDirectory(dir);
        try
        {
            var tuning = NewTuning(out _);
            tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 100, ChunkKb = 8 });
            var driver = Fast();
            driver.BindTuning(tuning);
            var pipe = new FakePipe();
            var hid = new FakeHid();
            // The panel sends a single report per transfer, not one per 4 KB inside it.
            ScriptAcks(hid, pipe);
            driver.Connect(pipe, hid);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == 1, 30_000));

            Assert.True(watch.ElapsedMilliseconds < 4000, watch.ElapsedMilliseconds.ToString());
            driver.Disconnect(pipe, hid);
            GlacialLcdLog.Flush();
            Assert.DoesNotContain("TIMEOUT", File.ReadAllText(Path.Combine(dir, GlacialLcdLog.FileName)));
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void A_busy_stage_reply_is_asked_again_before_any_chunk_is_sent()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        ScriptAcks(hid, pipe);
        var script = hid.OnWrite;
        int stages = 0;
        hid.OnWrite = w =>
        {
            if (w[1] == 0x73 && w[2] == 0x01 && Interlocked.Increment(ref stages) == 1)
            {
                hid.Enqueue(Report("EE 13 10 01"));
                return;
            }
            script!(w);
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 4));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1, 30_000));

        var writes = hid.Writes;
        Assert.Equal(2, writes.Count(w => w[1] == 0x73 && w[2] == 0x01));
        Assert.Equal(0, tuning.Stats().UploadsFailed);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_panel_that_stays_busy_ends_the_upload_cleanly_with_no_chunk_streamed()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        hid.OnWrite = w =>
        {
            if (w[1] == 0x71)
            {
                hid.Enqueue(Report("EE 12"));
            }
            else if (w[1] == 0x73 && w[2] == 0x01)
            {
                hid.Enqueue(Report("EE 13 10 01"));
            }
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 4));
        Assert.True(WaitUntil(() => tuning.Stats().UploadsFailed >= 1, 30_000));

        Assert.Empty(pipe.Writes);
        Assert.Equal(0, driver.UploadsCompleted);
        var writes = hid.Writes;
        Assert.Equal(1 + GlacialLcdProtocol.BusyRetries, writes.Take(writes.FindIndex(w => w[1] == 0x72 && w[2] == 0x01 && w[3] == 0x00) + 1).Count(w => w[1] == 0x73 && w[2] == 0x01));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_reply_that_does_not_match_the_step_does_not_end_the_wait()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        // Only a stage-shaped report answers the begin report: the wait must run out, not end on it.
        hid.OnWrite = w =>
        {
            if (w[1] == 0x71)
            {
                hid.Enqueue(Report("EE 13 00 00"));
            }
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1, 30_000));

        Assert.True(tuning.Stats().Acks.Timeouts >= 1);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_silent_panel_falls_back_to_timers_after_the_first_misses_and_is_not_waited_on_for_the_next_uploads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacial-ack-" + Guid.NewGuid().ToString("N")[..8]);
        GlacialLcdLog.UseDirectory(dir);
        try
        {
            var tuning = NewTuning(out _);
            tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
            var driver = Fast();
            driver.BindTuning(tuning);
            var pipe = new FakePipe();
            var hid = new FakeHid();
            driver.Connect(pipe, hid);

            driver.SendFrame(pipe, hid, Frame(noiseRows: 8, seed: 1));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == 1, 30_000));
            var second = System.Diagnostics.Stopwatch.StartNew();
            driver.SendFrame(pipe, hid, Frame(noiseRows: 8, seed: 2));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == 2, 30_000));

            // The second upload ran on timers: none of the ack limits was spent.
            Assert.True(second.ElapsedMilliseconds < 1500, second.ElapsedMilliseconds.ToString());
            driver.Disconnect(pipe, hid);
            GlacialLcdLog.Flush();
            string text = File.ReadAllText(Path.Combine(dir, GlacialLcdLog.FileName));
            Assert.Contains("TIMEOUT", text);
            Assert.Contains("timers for the rest of the upload", text);
            Assert.Contains("replies not awaited for silent steps", text);
            Assert.Equal(2, tuning.Stats().Acks.Timeouts);
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void A_silent_panel_is_asked_again_on_every_eighth_upload_and_waits_only_there()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        for (int i = 1; i <= 7; i++)
        {
            driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: i));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == i, 30_000));
        }
        // Only the first upload spent its two prelude waits; the next six ran on timers.
        Assert.Equal(2, tuning.Stats().Acks.Timeouts);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 8));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 8, 30_000));
        // The probe asks each step on its own: two prelude waits, one chunk wait, one commit wait.
        Assert.Equal(6, tuning.Stats().Acks.Timeouts);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_panel_that_answers_the_prelude_but_not_the_chunks_stops_being_waited_on_at_the_chunks_only()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        ScriptAcks(hid, pipe);
        pipe.OnWrite = null;
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 1));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1, 30_000));
        int after = tuning.Stats().Acks.Timeouts;
        Assert.Equal(1, after);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 2));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2, 30_000));

        // The second upload still got its prelude and commit replies and skipped the chunk waits.
        Assert.Equal(after, tuning.Stats().Acks.Timeouts);
        Assert.True(tuning.Stats().Acks.LastUploadReplies >= 3);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_reply_driven_finish_waits_out_the_captured_settle_when_no_commit_report_comes()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        ScriptAcks(hid, pipe);
        var script = hid.OnWrite;
        hid.OnWrite = w =>
        {
            if (!(w[1] == 0x73 && w[2] == 0xFF))
            {
                script!(w);
            }
        };
        driver.Connect(pipe, hid);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1, 30_000));

        Assert.True(watch.ElapsedMilliseconds >= GlacialLcdProtocol.FinishSettleMs - 100, watch.ElapsedMilliseconds.ToString());
        driver.Disconnect(pipe, hid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_close_write_mode_report_follows_the_end_report_before_the_last_state_query_when_the_knob_is_on(bool on)
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { CloseWriteMode = on });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe, hid);

        var writes = hid.Writes;
        int end = writes.FindIndex(w => w[1] == 0x73 && w[2] == 0xFF);
        int show = writes.FindLastIndex(w => w[1] == 0x51);
        var close = writes.FindIndex(w => w[1] == 0x72 && w[2] == 0x01 && w[3] == 0x00);
        if (on)
        {
            Assert.InRange(close, end + 1, show - 1);
            Assert.Equal(0xDC, writes[close + 1][1]);
        }
        else
        {
            Assert.Equal(-1, close);
        }
    }

    private static void ScriptRawAcks(FakeHid hid)
    {
        hid.OnWrite = w =>
        {
            if (w[1] == 0x7F && w[2] == 0x03)
            {
                hid.Enqueue(Report("EC 7F 00"));
            }
        };
    }

    /// <summary>Waits for the condition while moving the driver's clock on, since its frame interval and backoff run on that clock.</summary>
    private static bool WaitUntilTicking(FakeClock clock, Func<bool> condition, int advanceMs = 50, int timeoutMs = 20_000) =>
        WaitUntil(() =>
        {
            clock.Advance(advanceMs);
            return condition();
        }, timeoutMs);

    private static GlacialLcdPanelDriver RawDriver(FakeClock clock, out GlacialLcdTuning tuning, out FakePipe pipe, out FakeHid hid, bool ack = true)
    {
        tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { RawStream = true });
        var driver = Fast(clock: clock);
        driver.BindTuning(tuning);
        pipe = new FakePipe();
        // The echo would answer the announce too; the scripted report is the only accepting reply.
        hid = new FakeHid { Echo = false };
        if (ack)
        {
            ScriptRawAcks(hid);
        }
        driver.Connect(pipe, hid);
        return driver;
    }

    [Fact]
    public void Raw_streaming_selects_the_stream_mode_announces_each_frame_and_writes_it_whole_with_no_jpeg_or_flash_reports()
    {
        var clock = new FakeClock();
        var driver = RawDriver(clock, out var tuning, out var pipe, out var hid);
        var frame = Frame(noiseRows: 4);

        driver.SendFrame(pipe, hid, frame);
        Assert.True(WaitUntilTicking(clock, () => pipe.Writes.Count == 1));

        var writes = hid.Writes;
        Assert.Equal(0x51, writes[0][1]);
        Assert.Equal(0x20, writes[0][2]);
        var announce = writes.Single(w => w[1] == 0x7F);
        Assert.Equal(0x03, announce[2]);
        Assert.Equal(GlacialLcdProtocol.RawFrameBytes, announce[3] | (announce[4] << 8) | (announce[5] << 16) | (announce[6] << 24));
        Assert.Equal(frame, pipe.Writes.Single());
        Assert.DoesNotContain(writes, w => w[1] is 0x71 or 0xF1 or 0x72 or 0x73);
        Assert.True(WaitUntil(() => tuning.Stats().RawFramesOk == 1));
        Assert.True(tuning.Stats().RawActive);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_raw_announce_refused_as_not_ready_is_asked_again_before_it_counts_as_a_failure()
    {
        var clock = new FakeClock();
        var driver = RawDriver(clock, out var tuning, out var pipe, out var hid);
        int announces = 0;
        hid.OnWrite = w =>
        {
            if (w[1] == 0x7F && w[2] == 0x03)
            {
                hid.Enqueue(Report(Interlocked.Increment(ref announces) == 1 ? "EC 7F 02" : "EC 7F 00"));
            }
        };

        driver.SendFrame(pipe, hid, Frame(noiseRows: 2));
        Assert.True(WaitUntilTicking(clock, () => pipe.Writes.Count == 1));

        Assert.Equal(2, announces);
        Assert.Equal(0, tuning.Stats().RawFramesFailed);
        Assert.True(tuning.Current().RawStream);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Raw_frames_wait_out_the_half_second_interval_before_the_next_one()
    {
        var clock = new FakeClock();
        var driver = RawDriver(clock, out _, out var pipe, out var hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 2, seed: 1));
        Assert.True(WaitUntilTicking(clock, () => pipe.Writes.Count == 1, advanceMs: 10));
        long sentAt = clock.Now;

        driver.SendFrame(pipe, hid, Frame(noiseRows: 2, seed: 2));
        Thread.Sleep(400);
        Assert.Single(pipe.Writes);

        clock.Advance(GlacialLcdPanelDriver.RawMinFrameIntervalMs);
        Assert.True(WaitUntilTicking(clock, () => pipe.Writes.Count == 2, advanceMs: 10));
        Assert.True(clock.Now - sentAt >= GlacialLcdPanelDriver.RawMinFrameIntervalMs);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_raw_frame_the_panel_does_not_accept_is_skipped_and_three_in_a_row_switch_the_knob_off()
    {
        var clock = new FakeClock();
        var driver = RawDriver(clock, out var tuning, out var pipe, out var hid, ack: false);

        driver.SendFrame(pipe, hid, Frame());

        Assert.True(WaitUntilTicking(clock, () => !tuning.Current().RawStream, advanceMs: 200, timeoutMs: 60_000));
        Assert.Empty(pipe.Writes);
        Assert.Equal(3, tuning.Stats().RawFramesFailed);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Switching_raw_streaming_off_restores_a_wallpaper_mode_and_the_normal_path_uploads_again()
    {
        var clock = new FakeClock();
        var driver = RawDriver(clock, out var tuning, out var pipe, out var hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4));
        Assert.True(WaitUntilTicking(clock, () => pipe.Writes.Count == 1));

        tuning.Apply(new GlacialLcdTuningChange { RawStream = false });

        Assert.True(WaitUntilTicking(clock, () => !tuning.Stats().RawActive));
        var writes = hid.Writes;
        int exitIndex = writes.FindLastIndex(w => w[1] == 0x51 && w[2] == 0x11 && w[3] == 0x00 && w[4] == 0x00);
        Assert.True(exitIndex > writes.FindIndex(w => w[1] == 0x7F), "wallpaper mode must follow the last raw frame");
        // The panel is back on the normal path: the next frame is a stored-picture upload.
        hid.Echo = true;
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 9));
        Assert.True(WaitUntilTicking(clock, () => hid.Writes.Any(w => w[1] == 0x71) && driver.UploadsCompleted >= 2));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public async Task A_shutdown_during_a_raw_frame_waits_for_the_panel_to_be_put_back()
    {
        var clock = new FakeClock();
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { RawStream = true });
        var driver = Fast(clock: clock, disconnectWaitMs: 10_000);
        driver.BindTuning(tuning);
        var gate = new ManualResetEventSlim(false);
        int writing = 0;
        var pipe = new FakePipe
        {
            OnWrite = () =>
            {
                Interlocked.Exchange(ref writing, 1);
                gate.Wait(10_000);
            },
        };
        var hid = new FakeHid { Echo = false };
        ScriptRawAcks(hid);
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 2));
        Assert.True(WaitUntilTicking(clock, () => Volatile.Read(ref writing) == 1));

        var detach = Task.Run(driver.PrepareDetach);
        await Task.Yield();
        Assert.False(detach.IsCompleted, "the wait must not end while the frame is still being written");
        gate.Set();

        await detach.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains(hid.Writes, w => w[1] == 0x51 && w[2] == 0x11 && w[3] == 0x00 && w[4] == 0x00);
        Assert.False(tuning.Stats().RawActive);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Turning_raw_streaming_on_again_starts_the_failure_count_afresh()
    {
        var clock = new FakeClock();
        var driver = RawDriver(clock, out var tuning, out var pipe, out var hid, ack: false);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 2, seed: 1));
        Assert.True(WaitUntilTicking(clock, () => tuning.Stats().RawFramesFailed >= 2, advanceMs: 200, timeoutMs: 60_000));
        Assert.True(tuning.Current().RawStream);

        tuning.Apply(new GlacialLcdTuningChange { RawStream = false });
        Assert.True(WaitUntilTicking(clock, () => !tuning.Stats().RawActive, advanceMs: 200));
        tuning.Apply(new GlacialLcdTuningChange { RawStream = true });
        driver.SendFrame(pipe, hid, Frame(noiseRows: 2, seed: 2));
        int before = tuning.Stats().RawFramesFailed;
        Assert.True(WaitUntilTicking(clock, () => tuning.Stats().RawFramesFailed >= before + 2, advanceMs: 200, timeoutMs: 60_000));

        // Four failures in all, but only two since the knob came back on: still on.
        Assert.True(tuning.Current().RawStream);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_shutdown_that_begins_while_raw_streaming_is_being_entered_leaves_the_panel_alone()
    {
        var clock = new FakeClock();
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { RawStream = true });
        var driver = Fast(clock: clock);
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid { Echo = false };
        int fired = 0;
        // The entry drains input before the mode select; the shutdown begins at that first read.
        hid.OnRead = _ =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                driver.BeginShutdown();
            }
        };
        driver.Connect(pipe, hid);

        Assert.True(WaitUntilTicking(clock, () => Volatile.Read(ref fired) == 1));
        int ticks = driver.Ticks;
        Assert.True(WaitUntilTicking(clock, () => driver.Ticks >= ticks + 3));

        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x51 && w[2] == 0x20);
        Assert.True(driver.WaitIdle(TimeSpan.FromSeconds(1)));
        Assert.False(tuning.Stats().RawActive);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Turning_raw_streaming_off_and_on_between_two_passes_still_restarts_the_failure_count()
    {
        var tuning = NewTuning(out _);
        int before = tuning.RawEnables;
        tuning.Apply(new GlacialLcdTuningChange { RawStream = false });
        tuning.Apply(new GlacialLcdTuningChange { RawStream = true });
        Assert.Equal(before + 1, tuning.RawEnables);
    }

    [Fact]
    public void An_in_place_update_rewrites_the_slot_on_display_and_sends_no_show_and_logs_what_follows_the_end_report()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacial-inplace-" + Guid.NewGuid().ToString("N")[..8]);
        GlacialLcdLog.UseDirectory(dir);
        try
        {
            var tuning = NewTuning(out _);
            tuning.Apply(new GlacialLcdTuningChange { UpdateInPlace = true, DoubleBuffer = true, PacePercent = 0 });
            var driver = Fast();
            driver.BindTuning(tuning);
            var pipe = new FakePipe();
            var hid = new FakeHid();
            driver.Connect(pipe, hid);

            // Nothing is showing yet: the first upload alternates and shows as usual.
            driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 1));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
            int firstSlot = hid.Writes.Where(w => w[1] == 0x72 && w[3] == 0x01).Select(w => (int)w[4]).Single();
            int shows = hid.Writes.Count(w => w[1] == 0x51);
            Assert.Equal(1, shows);
            int writesBefore = hid.Writes.Count;
            hid.OnWrite = w =>
            {
                if (w[1] == 0x73 && w[2] == 0xFF)
                {
                    hid.Enqueue(Report("EE 20 00 01"));
                }
            };

            driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: 2));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));

            var second = hid.Writes.Skip(writesBefore).ToList();
            Assert.Equal(firstSlot, second.Where(w => w[1] == 0x72 && w[3] == 0x01).Select(w => (int)w[4]).Single());
            Assert.DoesNotContain(second, w => w[1] == 0x51);
            int end = second.FindIndex(w => w[1] == 0x73 && w[2] == 0xFF);
            Assert.DoesNotContain(second.Skip(end + 1), w => w[1] == 0xDC);
            driver.Disconnect(pipe, hid);
            GlacialLcdLog.Flush();
            string text = File.ReadAllText(Path.Combine(dir, GlacialLcdLog.FileName));
            Assert.Contains("EE200001", text);
            Assert.Contains("updated in place, no show sent", text);
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void Without_the_in_place_probe_the_picture_is_shown_after_every_upload()
    {
        var tuning = NewTuning(out _);
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        for (int i = 1; i <= 2; i++)
        {
            driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: i));
            Assert.True(WaitUntil(() => driver.UploadsCompleted == i));
        }
        Assert.Equal(2, hid.Writes.Count(w => w[1] == 0x51));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void The_raw_probe_queries_the_panel_first_forces_every_pixel_opaque_logs_what_it_drains_and_lowers_the_frame_floor()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacial-rawprobe-" + Guid.NewGuid().ToString("N")[..8]);
        GlacialLcdLog.UseDirectory(dir);
        try
        {
            var clock = new FakeClock();
            var tuning = NewTuning(out _);
            tuning.Apply(new GlacialLcdTuningChange { RawStream = true, RawProbe = true });
            var driver = Fast(clock: clock);
            driver.BindTuning(tuning);
            var pipe = new FakePipe();
            var hid = new FakeHid { Echo = false };
            hid.OnWrite = w =>
            {
                if (w[1] == 0xDC)
                {
                    hid.Enqueue(Report("EC 5C 00"));
                }
                else if (w[1] == 0x7F && w[2] == 0x03)
                {
                    hid.Enqueue(Report("EC 7F 00"));
                }
            };
            driver.Connect(pipe, hid);
            Assert.True(WaitUntilTicking(clock, () => hid.Writes.Any(w => w[1] == 0x51 && w[2] == 0x20), advanceMs: 5));
            // Let the entry finish, then let the panel volunteer a report between frames.
            int settled = driver.Ticks;
            Assert.True(WaitUntilTicking(clock, () => driver.Ticks >= settled + 3, advanceMs: 5));
            hid.Enqueue(Report("EE 20 00"));
            int seen = driver.Ticks;
            Assert.True(WaitUntilTicking(clock, () => driver.Ticks >= seen + 3, advanceMs: 5));

            var frame = Frame(noiseRows: 2);
            for (int i = 3; i < frame.Length; i += 4)
            {
                frame[i] = 0x00;
            }
            driver.SendFrame(pipe, hid, frame);
            Assert.True(WaitUntilTicking(clock, () => pipe.Writes.Count == 1, advanceMs: 5));

            var writes = hid.Writes;
            Assert.True(writes.FindIndex(w => w[1] == 0xDC) < writes.FindIndex(w => w[1] == 0x51 && w[2] == 0x20));
            var sent = pipe.Writes.Single();
            for (int i = 3; i < sent.Length; i += 4)
            {
                Assert.Equal(0xFF, sent[i]);
            }

            // Past the lowered floor but short of the usual one.
            driver.SendFrame(pipe, hid, Frame(noiseRows: 2, seed: 5));
            Thread.Sleep(300);
            Assert.Single(pipe.Writes);
            clock.Advance(GlacialLcdPanelDriver.RawProbeMinFrameIntervalMs + 10);
            Assert.True(WaitUntil(() => pipe.Writes.Count == 2));
            driver.Disconnect(pipe, hid);

            GlacialLcdLog.Flush();
            string text = File.ReadAllText(Path.Combine(dir, GlacialLcdLog.FileName));
            Assert.Contains("RX drained between frames: EE20", text);
            Assert.Contains("state query before the mode select answered", text);
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void Leaving_the_nexus_panel_ends_the_raw_stream_without_overriding_the_mode_the_native_path_sets()
    {
        var clock = new FakeClock();
        var link = new GlacialLcdLink();
        var driver = Fast(link, clock);
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { RawStream = true });
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid { Echo = false };
        ScriptRawAcks(hid);
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 2));
        Assert.True(WaitUntilTicking(clock, () => pipe.Writes.Count == 1));
        int before = hid.Writes.Count;

        link.StreamWanted = false;

        Assert.True(WaitUntilTicking(clock, () => !tuning.Stats().RawActive));
        Assert.Equal(before, hid.Writes.Count);
        driver.Disconnect(pipe, hid);
    }

    private static (int Width, int Height) JpegSize(FakePipe pipe)
    {
        var data = pipe.Writes.SelectMany(w => w).ToArray();
        for (int i = 0; i + 9 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xC0)
            {
                return ((data[i + 7] << 8) | data[i + 8], (data[i + 5] << 8) | data[i + 6]);
            }
        }
        return (0, 0);
    }

    [Theory]
    [InlineData("send", 50, 360, 640)]
    [InlineData("upscale", 50, 720, 1280)]
    [InlineData("send", 100, 720, 1280)]
    public void The_scale_knob_sends_a_smaller_picture_or_a_smoothed_full_size_one(string mode, int percent, int width, int height)
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ScaleMode = mode, ScalePercent = percent, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 50));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.Equal((width, height), JpegSize(pipe));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void The_quality_knob_changes_the_picture_size_and_the_stats_follow_the_uploads()
    {
        var tuning = NewTuning(out _);
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        tuning.Apply(new GlacialLcdTuningChange { JpegQuality = 90, PacePercent = 0 });
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200, seed: 3));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        int high = SentJpegBytes(hid);
        tuning.Apply(new GlacialLcdTuningChange { JpegQuality = 30 });
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200, seed: 3).Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));
        int low = SentJpegBytes(hid);

        Assert.True(low < high, $"{low} vs {high}");
        var stats = tuning.Stats();
        Assert.Equal(2, stats.UploadsOk);
        Assert.Equal(low, stats.LastFrameBytes);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Every_upload_is_logged_with_its_steps_and_every_reply()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacial-log-" + Guid.NewGuid().ToString("N")[..8]);
        GlacialLcdLog.UseDirectory(dir);
        try
        {
            var tuning = NewTuning(out _);
            tuning.Apply(new GlacialLcdTuningChange { PacePercent = 0 });
            var driver = Fast();
            driver.BindTuning(tuning);
            var pipe = new FakePipe();
            var hid = new FakeHid();
            driver.Connect(pipe, hid);
            for (int i = 1; i <= 5; i++)
            {
                driver.SendFrame(pipe, hid, Frame(noiseRows: 4, seed: i));
                Assert.True(WaitUntil(() => driver.UploadsCompleted == i));
            }
            driver.Disconnect(pipe, hid);

            GlacialLcdLog.Flush();
            string text = File.ReadAllText(Path.Combine(dir, GlacialLcdLog.FileName));
            Assert.Contains("settings id=", text);
            Assert.Contains("start settings=", text);
            foreach (string step in new[] { "TX +", "DC", "prelude 71", "prelude F1", "prelude 72", "prelude 73", "prelude 7F", "chunk 1/", "end 73FF", "DC before show", "show 51" })
            {
                Assert.Contains(step, text);
            }
            // Replies are logged for the fifth upload too, not only the first few.
            Assert.Contains("u5 RX", text);
            Assert.Contains("RX +", text);
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void A_detach_waits_for_the_upload_in_flight_outside_any_caller_lock_and_stops_new_ones()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { PacePercent = 3 });
        var driver = Fast(disconnectWaitMs: 60_000);
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200, seed: 1));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));

        driver.PrepareDetach();

        // It returned only once the sequence was through the end report and the show step.
        Assert.False(driver.IsUploading);
        Assert.Equal(0x51, hid.Writes[^1][1]);
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200, seed: 2));
        Looked(driver);
        Assert.Equal(1, driver.UploadAttempts);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Shutdown_stops_new_uploads_at_once_and_the_state_write_does_not_wait_for_the_one_in_flight()
    {
        var memory = new FakeMemory();
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { PacePercent = 3 });
        var driver = Fast(disconnectWaitMs: 60_000);
        driver.BindMemory(memory);
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200, seed: 1));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));

        driver.BeginShutdown();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.PersistState();
        Assert.True(watch.ElapsedMilliseconds < 500, watch.ElapsedMilliseconds.ToString());
        Assert.True(driver.IsUploading);

        Assert.True(driver.WaitIdle(TimeSpan.FromSeconds(30)));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.SendFrame(pipe, hid, Frame(noiseRows: 200, seed: 2));
        Looked(driver);
        Assert.Equal(1, driver.UploadAttempts);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_wait_that_runs_out_reports_it()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        Assert.True(driver.WaitIdle(TimeSpan.FromMilliseconds(10)));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_missed_state_reply_is_asked_for_again_before_the_panel_is_called_stalled()
    {
        var tuning = NewTuning(out _);
        var driver = Fast();
        driver.StallReplyMs = 80;
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        int queries = 0;
        var hid = new FakeHid();
        hid.OnWrite = w =>
        {
            if (w[1] == 0xDC && Interlocked.Increment(ref queries) == 1)
            {
                hid.Drain();
            }
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.False(tuning.Stalled);
        Assert.True(hid.Writes.Count(w => w[1] == 0xDC) >= 3);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_stalled_panel_is_probed_after_a_backoff_and_uploads_resume_on_their_own()
    {
        var tuning = NewTuning(out _);
        var clock = new FakeClock();
        var driver = Fast(clock: clock);
        driver.StallReplyMs = 60;
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid { Echo = false };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => tuning.Stalled));
        int queries = hid.Writes.Count(w => w[1] == 0xDC);
        Assert.Equal(3, queries);

        // Still inside the backoff: no probe.
        clock.Advance(10_000);
        Looked(driver);
        Assert.Equal(queries, hid.Writes.Count(w => w[1] == 0xDC));

        hid.Echo = true;
        clock.Advance(25_000);
        Assert.True(WaitUntil(() => !tuning.Stalled));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Chunk_pacing_scales_with_the_transfer_size_so_the_total_time_does_not_depend_on_it()
    {
        var small = NewTuning(out _);
        small.Apply(new GlacialLcdTuningChange { PacePercent = 100, ChunkKb = 4 });
        var big = NewTuning(out _);
        big.Apply(new GlacialLcdTuningChange { PacePercent = 100, ChunkKb = 16 });
        var cfgSmall = small.Current();
        var cfgBig = big.Current();

        int estimateSmall = GlacialLcdPanelDriver.EstimateUploadMs(80_000, cfgSmall);
        int estimateBig = GlacialLcdPanelDriver.EstimateUploadMs(80_000, cfgBig);

        // Same bytes, same pace: within one chunk gap of each other.
        Assert.InRange(Math.Abs(estimateSmall - estimateBig), 0, 2100 * 4);
    }

    [Fact]
    public void A_reply_driven_finish_keeps_its_floor_when_the_pace_is_zero_and_no_commit_report_comes()
    {
        var tuning = NewTuning(out _);
        tuning.Apply(new GlacialLcdTuningChange { ReplyDriven = true, PacePercent = 0 });
        var driver = Fast();
        driver.BindTuning(tuning);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        hid.OnWrite = w =>
        {
            if (w[1] == 0x73 && w[2] == 0xFF)
            {
                hid.Drain();
            }
        };
        driver.Connect(pipe, hid);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.True(watch.ElapsedMilliseconds >= GlacialLcdPanelDriver.FinishReplyWaitMs - 50, watch.ElapsedMilliseconds.ToString());
        driver.Disconnect(pipe, hid);
    }

    private static (Nexus.Service.Panel.PanelDeviceRegistry Registry, Nexus.Service.Panel.Streams.StreamedPanelCoordinator Coordinator, GlacialLcdLink Link, BulkPanelHub Hub) PanelRig(bool override_)
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacial-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var store = new Nexus.Service.Tests.TestableConfigStore(Path.Combine(dir, "settings.json"));
        // As an install that ran an earlier build with the gate off: the saved gate is false, the mode is the Nexus panel.
        store.Update(s =>
        {
            s.Devices.GlacialLcdNexusPanel = false;
            s.Devices.GlacialLcd.UserEdited = true;
            s.Devices.GlacialLcd.ModeChosen = true;
            s.Devices.GlacialLcd.Mode = Nexus.Service.Persistence.GlacialLcdSettings.ModePanel;
        });
        var link = new GlacialLcdLink { NexusPanelEnabled = override_ };
        var driver = new GlacialLcdPanelDriver(link, 0, 0);
        var hub = new BulkPanelHub(driver);
        Assert.True(hub.Attach(new FakePipe(), new FakeHid()));
        var discovery = new Nexus.Service.Panel.Streams.BulkPanelDiscovery(
            hub, null, GlacialLcdNexusPanelGate.Withheld(link, store), listedWhileWithheld: true);
        var registry = new Nexus.Service.Panel.PanelDeviceRegistry(store);
        var gate = new Nexus.Service.Devices.DeviceControlGate(store);
        gate.SetEnabled(GlacialLcdWorker.HandlerId, true);
        var coordinator = new Nexus.Service.Panel.Streams.StreamedPanelCoordinator(
            new[] { discovery }, new Nexus.Service.Panel.Streams.StreamedPanelStore(Path.Combine(dir, "streams.json")), registry, gate, notifyOverlay: null);
        return (registry, coordinator, link, hub);
    }

    [Fact]
    public async System.Threading.Tasks.Task With_the_saved_gate_off_but_the_override_on_the_nexus_panel_mode_creates_the_panel_record_and_opens_the_stream()
    {
        var (registry, coordinator, link, hub) = PanelRig(override_: true);
        try
        {
            coordinator.TickOnce();

            var record = Assert.Single(registry.List(), r => r.Capabilities?.Family == GlacialLcdWorker.HandlerId);
            Assert.Contains(record.Id, coordinator.LivePanelDeviceIds());
            Assert.True(link.Streaming);
        }
        finally
        {
            await coordinator.StopAsync(default);
            coordinator.Dispose();
            hub.Detach();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task With_the_gate_off_the_nexus_panel_mode_creates_no_panel_record_and_opens_no_stream()
    {
        var (registry, coordinator, link, hub) = PanelRig(override_: false);
        try
        {
            coordinator.TickOnce();

            Assert.DoesNotContain(registry.List(), r => r.Capabilities?.Family == GlacialLcdWorker.HandlerId);
            Assert.False(link.Streaming);
        }
        finally
        {
            await coordinator.StopAsync(default);
            coordinator.Dispose();
            hub.Detach();
        }
    }

    private sealed class FakeMemory : ILcdUploadMemory
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Hashes { get; } = new();

        public string? Load(string key) => Hashes.TryGetValue(key, out var hash) ? hash : null;

        public void Save(string key, string hash) => Hashes[key] = hash;

        public void Clear(string key) => Hashes.TryRemove(key, out _);

        public void ClearOthers(string keep)
        {
            foreach (var other in Hashes.Keys.Where(k => k != keep).ToList())
            {
                Hashes.TryRemove(other, out _);
            }
        }
    }

    private sealed class FakePipe : IBulkUsbPipe
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _writes = new();

        public List<byte[]> Writes
        {
            get { lock (_lock) { return _writes.ToList(); } }
        }

        public bool FailWrites { get; set; }

        /// <summary>Runs after each transfer is recorded.</summary>
        public Action? OnWrite { get; set; }

        public bool Write(ReadOnlySpan<byte> data)
        {
            if (FailWrites)
            {
                return false;
            }
            lock (_lock)
            {
                _writes.Add(data.ToArray());
            }
            OnWrite?.Invoke();
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class FakeHid : IHidDevice
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _writes = new();

        public List<byte[]> Writes
        {
            get { lock (_lock) { return new List<byte[]>(_writes); } }
        }

        /// <summary>Runs after each report is recorded, so a test can act at an exact point of the sequence.</summary>
        public Action<byte[]>? OnWrite { get; set; }

        /// <summary>The panel answers a state query with a state report and every other report with its header.</summary>
        public bool Echo { get; set; } = true;

        /// <summary>Replaces the state report an echoing panel sends for a query.</summary>
        public byte[]? StateReport { get; set; }

        /// <summary>Input waiting to be read, ahead of anything the panel answers.</summary>
        public void Enqueue(byte[] report)
        {
            lock (_lock)
            {
                _input.Enqueue(report);
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Timeouts the driver asked reads to wait, in order.</summary>
        public List<int> ReadTimeouts { get; } = new();

        /// <summary>A read result that models a vanished device.</summary>
        public bool ReadsFail { get; set; }

        /// <summary>Called at the start of every read with the timeout asked, outside the fake's lock.</summary>
        public Action<int>? OnRead { get; set; }

        private readonly Queue<byte[]> _input = new();

        public void Drain()
        {
            lock (_lock)
            {
                _input.Clear();
            }
        }

        public int VendorId => 0x0B05;
        public int ProductId => GlacialLcdProtocol.ProductId;
        public string Path => "/dev/fake-glacial";
        public string? SerialOverride { get; set; }
        public string? Serial => SerialOverride ?? "FAKE";
        public int UsagePage => 0xFF72;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            lock (_lock)
            {
                _writes.Add(copy);
                if (Echo)
                {
                    var answer = new byte[GlacialLcdProtocol.ReportBytes];
                    if (copy[1] == 0xDC)
                    {
                        // A state report: id, the state opcode, then the settings fields.
                        var state = StateReport ?? Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32");
                        state.CopyTo(answer, 0);
                    }
                    else
                    {
                        answer[0] = copy[0];
                        answer[1] = copy[1];
                    }
                    _input.Enqueue(answer);
                    Monitor.PulseAll(_lock);
                }
            }
            OnWrite?.Invoke(copy);
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            OnRead?.Invoke(timeoutMs);
            lock (_lock)
            {
                ReadTimeouts.Add(timeoutMs);
                if (ReadsFail)
                {
                    return -1;
                }
                if (_input.Count == 0 && timeoutMs > 0)
                {
                    // Waits for input like a timed read, so a long single read shows up as a long wait.
                    Monitor.Wait(_lock, timeoutMs);
                }
                if (_input.Count == 0)
                {
                    return 0;
                }
                var report = _input.Dequeue();
                report.CopyTo(buffer);
                return report.Length;
            }
        }
        public void Dispose() { }
    }
}

using System;
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

    private static GlacialLcdPanelDriver Fast(GlacialLcdLink? link = null, FakeClock? clock = null, int paceScale = 0, int settleMs = 0, int backoffMs = 10, bool streaming = true)
    {
        clock ??= new FakeClock();
        link ??= new GlacialLcdLink();
        if (streaming)
        {
            link.StreamOpened(new object());
        }
        return new GlacialLcdPanelDriver(link, paceScale, settleMs, 600_000, backoffMs, 10, () => clock.Now);
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
    public void The_native_worker_cannot_enter_while_an_upload_runs_and_a_cancel_ends_it()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link, paceScale: 100);
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

        link.RequestCancel();
        Assert.True(WaitUntil(() => !driver.IsUploading, 4000));
        Assert.Equal(0, driver.UploadsCompleted);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x73 && w[2] == 0xFF);
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
    public void A_cancel_after_the_flush_forgets_the_remembered_picture_because_the_flush_may_have_cleared_it()
    {
        var link = new GlacialLcdLink();
        var memory = new FakeMemory();
        memory.Hashes["asus-glacial-lcd"] = "ABC";
        var driver = Fast(link);
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid { OnWrite = w => { if (w[1] == 0xF1) { link.RequestCancel(); } } };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading && memory.Hashes.IsEmpty));

        Assert.Empty(memory.Hashes);
        Assert.Empty(pipe.Writes);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_cancel_before_the_flush_keeps_the_remembered_picture()
    {
        var link = new GlacialLcdLink();
        var memory = new FakeMemory();
        memory.Hashes["asus-glacial-lcd"] = "ABC";
        var driver = Fast(link);
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid { OnWrite = w => { if (w[1] == 0xDC) { link.RequestCancel(); } } };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading));

        Assert.Equal("ABC", memory.Hashes["asus-glacial-lcd"]);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0xF1);
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
    public void A_newer_frame_drops_an_upload_only_before_the_flush()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        int queries = 0;
        var hid = new FakeHid();
        hid.OnWrite = w =>
        {
            // The first upload's opening query is when the newer frame arrives.
            if (w[1] == 0xDC && ++queries == 1)
            {
                driver.SendFrame(pipe, hid, Frame(noiseRows: 100, seed: 9));
            }
        };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        Assert.Equal(2, driver.UploadAttempts);
        // The dropped attempt sent no flush; the one that ran sent one flush and finished.
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
    public void The_default_pace_comes_from_the_resolver_and_is_stored_as_unset()
    {
        Assert.Equal(4, new GlacialLcdPanelDriver(new GlacialLcdLink()).ConfiguredPaceScalePercent);
        // Stored as unset, so a later change of the default is not held back by a value saved today.
        var fresh = new Nexus.Service.Persistence.DevicesSettings();
        Assert.Equal(0, fresh.GlacialLcdUploadPacePercent);
        Assert.Equal(
            Nexus.Service.Persistence.DevicesSettings.DefaultGlacialLcdUploadPacePercent,
            Nexus.Service.Persistence.DevicesSettings.ResolveGlacialLcdUploadPace(fresh.GlacialLcdUploadPacePercent));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-5, 4)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(5000, 1000)]
    public void A_stored_pace_of_zero_counts_as_unset_and_the_top_is_capped(int stored, int expected)
    {
        Assert.Equal(expected, Nexus.Service.Persistence.DevicesSettings.ResolveGlacialLcdUploadPace(stored));
    }

    [Fact]
    public void A_settings_file_without_the_key_gets_the_default_and_one_with_zero_resolves_to_it()
    {
        var absent = System.Text.Json.JsonSerializer.Deserialize(
            "{\"devices\":{}}", Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        var zero = System.Text.Json.JsonSerializer.Deserialize(
            "{\"devices\":{\"glacialLcdUploadPacePercent\":0}}", Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;

        Assert.Equal(0, absent.Devices.GlacialLcdUploadPacePercent);
        Assert.Equal(4, Nexus.Service.Persistence.DevicesSettings.ResolveGlacialLcdUploadPace(absent.Devices.GlacialLcdUploadPacePercent));
        Assert.Equal(4, Nexus.Service.Persistence.DevicesSettings.ResolveGlacialLcdUploadPace(zero.Devices.GlacialLcdUploadPacePercent));
    }

    [Fact]
    public void Values_stored_by_earlier_builds_under_the_old_key_are_ignored_and_the_new_key_can_restore_an_earlier_pace()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize(
            "{\"devices\":{\"glacialLcdPaceScalePercent\":19}}", Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        var restored = System.Text.Json.JsonSerializer.Deserialize(
            "{\"devices\":{\"glacialLcdUploadPacePercent\":19}}", Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;

        Assert.Equal(0, old.Devices.GlacialLcdUploadPacePercent);
        Assert.Equal(4, Nexus.Service.Persistence.DevicesSettings.ResolveGlacialLcdUploadPace(old.Devices.GlacialLcdUploadPacePercent));
        Assert.Equal(19, Nexus.Service.Persistence.DevicesSettings.ResolveGlacialLcdUploadPace(restored.Devices.GlacialLcdUploadPacePercent));
        Assert.Equal(100, Nexus.Service.Persistence.DevicesSettings.ResolveGlacialLcdUploadPace(100));
    }

    [Fact]
    public void The_hidden_pace_setting_brings_the_captured_waits_back_and_is_read_per_upload()
    {
        var link = new GlacialLcdLink();
        link.StreamOpened(new object());
        int percent = 10;
        var driver = new GlacialLcdPanelDriver(link, settleMs: 0);
        driver.BindPaceScale(() => percent);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        long paced = watch.ElapsedMilliseconds;

        percent = 0;
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
    public void A_cancel_during_the_wait_after_the_end_report_stops_the_upload_before_the_show_step()
    {
        var link = new GlacialLcdLink();
        var driver = Fast(link);
        var pipe = new FakePipe();
        int slices = 0;
        // The cancel lands inside the first timed read of the wait, so the outcome does not depend on timing.
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
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading));

        // One long read would never see a 50 ms slice, so the cancel would not fire and the show step would go out.
        Assert.Equal(0, driver.UploadsCompleted);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x51);
        Assert.DoesNotContain(500, hid.ReadTimeouts);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_silent_panel_costs_only_short_waits_and_never_fails_the_upload()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe, hid);

        var timeouts = hid.ReadTimeouts;
        Assert.All(timeouts, t => Assert.InRange(t, 0, 50));
        // The wait after the end report is a run of short slices, not one long read (how many fit depends on the timer).
        Assert.True(timeouts.Count(t => t == 50) >= 2);
        // Transfers are polled, never waited on: one zero-timeout read per transfer, plus the drain at the start.
        Assert.Equal(pipe.Writes.Count + 1, timeouts.Count(t => t == 0));
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
        // A read that reports the device gone ends the wait after the end report at once.
        Assert.True(hid.ReadTimeouts.Count(t => t == 50) <= 1);
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

        var targets = hid.Writes.Where(w => w[1] == 0x72).Select(w => w[4]).ToArray();
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
        public bool Echo { get; set; }

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

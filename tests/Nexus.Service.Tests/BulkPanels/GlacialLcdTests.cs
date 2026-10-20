using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    [InlineData(50, false, false, "EC 5C 01 00 00 32 00 00 00 32 00 00 00 00 00 32")]
    [InlineData(50, true, false, "EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32")]
    [InlineData(30, true, false, "EC 5C 01 00 00 1E 00 00 00 1E 14 00 00 00 00 1E")]
    [InlineData(70, true, false, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46")]
    [InlineData(70, true, true, "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46 14")]
    public void Settings_write_matches_the_captured_brightness_display_and_sleep_fields(int percent, bool displayOn, bool keepOn, string expected)
    {
        Assert.Equal(Report(expected), GlacialLcdProtocol.EncodeSettings(percent, displayOn, keepOn));
    }

    [Fact]
    public void Settings_brightness_is_clamped_to_a_percent()
    {
        Assert.Equal(100, GlacialLcdProtocol.EncodeSettings(250, false, false)[5]);
        Assert.Equal(0, GlacialLcdProtocol.EncodeSettings(-5, false, false)[5]);
    }

    [Fact]
    public void State_reply_parses_the_fields_the_settings_write_sets()
    {
        var reply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32");
        var state = GlacialLcdProtocol.ParseState(reply);
        Assert.Equal((50, true, false), state);
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

    private static byte[] Frame(int noiseRows = 0, int seed = 1)
    {
        var frame = new byte[GlacialLcdProtocol.Width * GlacialLcdProtocol.Height * 4];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = (byte)(i >> 10);
            frame[i + 1] = 0x80;
            frame[i + 2] = 0x20;
            frame[i + 3] = 0xFF;
        }
        if (noiseRows > 0)
        {
            new Random(seed).NextBytes(frame.AsSpan(0, noiseRows * GlacialLcdProtocol.Width * 4));
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

    /// <summary>Waits until the worker has taken at least two more looks, so "nothing happened" is a statement about its decision.</summary>
    private static void WorkerLooksTwice(GlacialLcdPanelDriver driver)
    {
        int target = driver.Ticks + 2;
        Assert.True(WaitUntil(() => driver.Ticks >= target));
    }

    private sealed class FakeClock
    {
        private long _now = 1_000_000;

        public long Now => Interlocked.Read(ref _now);

        public void Advance(long ms) => Interlocked.Add(ref _now, ms);
    }

    private static GlacialLcdPanelDriver Fast(
        FakeClock? clock = null, int chunkMs = 0, int refreshMs = 600_000, int settleMs = 0, int maxSettleMs = 600_000,
        int backoffMs = 10, int cancelRetryMs = 600_000)
    {
        clock ??= new FakeClock();
        return new(chunkMs, refreshMs, settleMs, maxSettleMs, backoffMs, cancelRetryMs, () => clock.Now);
    }

    [Fact]
    public void Connect_sends_the_vendor_opening_and_reports_the_jpeg_size()
    {
        var driver = Fast();
        var hid = new FakeHid();

        var geometry = driver.Connect(new FakePipe(), hid);

        Assert.Equal((GlacialLcdProtocol.Width, GlacialLcdProtocol.Height), geometry);
        Assert.Equal(new[] { GlacialLcdProtocol.EncodeHello(), GlacialLcdProtocol.EncodeQuery() }, hid.Writes);
        driver.Disconnect(new FakePipe(), hid);
        Assert.Null(Fast().Connect(new FakePipe(), null));
    }

    [Fact]
    public void A_frame_uploads_as_the_captured_prelude_then_announce_then_4k_chunks_of_one_jpeg()
    {
        var driver = Fast();
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        int opening = hid.Writes.Count;

        Assert.True(driver.SendFrame(pipe, hid, Frame()));
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        var prelude = GlacialLcdProtocol.EncodeCustomImagePrelude();
        var writes = hid.Writes.Skip(opening).ToList();
        Assert.Equal(GlacialLcdProtocol.EncodeQuery(), writes[0]);
        Assert.Equal(prelude, writes.Skip(1).Take(4));
        var announce = writes[5];
        int size = announce[3] | (announce[4] << 8) | (announce[5] << 16);
        Assert.Equal(Report("EC 7F 02").Take(3), announce.Take(3));
        var jpeg = pipe.Writes.SelectMany(w => w).ToArray();
        Assert.Equal(size, jpeg.Length);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, jpeg.Take(3));
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg.TakeLast(2));
        Assert.All(pipe.Writes.SkipLast(1), w => Assert.Equal(GlacialLcdProtocol.ChunkBytes, w.Length));
        Assert.InRange(pipe.Writes[^1].Length, 1, GlacialLcdProtocol.ChunkBytes);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_frame_arriving_inside_the_cooldown_is_flushed_when_it_ends()
    {
        var clock = new FakeClock();
        var driver = Fast(clock, refreshMs: 700);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.SendFrame(pipe, hid, Frame(noiseRows: 8));
        WorkerLooksTwice(driver);
        Assert.Equal(1, driver.UploadsCompleted);

        // No further SendFrame: the driver flushes the pending frame by itself.
        clock.Advance(700);
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_frame_is_held_until_the_content_settles()
    {
        var clock = new FakeClock();
        var driver = Fast(clock, settleMs: 900);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        clock.Advance(899);
        WorkerLooksTwice(driver);
        Assert.Equal(0, driver.UploadAttempts);

        clock.Advance(1);
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Only_the_first_upload_after_a_connect_may_skip_the_settle_wait()
    {
        var clock = new FakeClock();
        var driver = Fast(clock, refreshMs: 100, settleMs: 600_000, maxSettleMs: 500);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        clock.Advance(500);
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));

        // A page that never settles is not pushed again on a timer.
        driver.SendFrame(pipe, hid, Frame(noiseRows: 4));
        clock.Advance(100_000);
        WorkerLooksTwice(driver);
        Assert.Equal(1, driver.UploadAttempts);
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
        first.Disconnect(pipe, hid);
        Assert.Single(memory.Hashes);

        var second = Fast();
        second.BindMemory(memory);
        var pipe2 = new FakePipe();
        var hid2 = new FakeHid();
        second.Connect(pipe2, hid2);
        second.SendFrame(pipe2, hid2, Frame());
        WorkerLooksTwice(second);

        Assert.Equal(0, second.UploadAttempts);
        Assert.Empty(pipe2.Writes);
        second.Disconnect(pipe2, hid2);
    }

    [Fact]
    public void A_failed_upload_forgets_the_remembered_picture_and_backs_off()
    {
        var clock = new FakeClock();
        var memory = new FakeMemory();
        var driver = Fast(clock, refreshMs: 100, backoffMs: 800);
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        Assert.Single(memory.Hashes);

        pipe.FailWrites = true;
        clock.Advance(100);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 2));
        Assert.True(WaitUntil(() => driver.UploadAttempts == 2));
        // The panel may hold half a picture, so nothing is remembered.
        Assert.Empty(memory.Hashes);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 3));
        WorkerLooksTwice(driver);
        Assert.Equal(2, driver.UploadAttempts);

        pipe.FailWrites = false;
        clock.Advance(800);
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 2));
        Assert.Single(memory.Hashes);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void The_cooldown_survives_a_reconnect()
    {
        var clock = new FakeClock();
        var driver = Fast(clock);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        driver.Disconnect(pipe, hid);

        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 5));
        WorkerLooksTwice(driver);

        Assert.Equal(1, driver.UploadAttempts);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Disconnect_during_an_upload_returns_promptly_and_a_new_connect_works()
    {
        var driver = Fast(chunkMs: 600_000);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.Disconnect(pipe, hid);

        Assert.True(watch.ElapsedMilliseconds < 2000);
        Assert.False(driver.IsUploading);
        // A stopped worker is never revived by the next connect: a fresh one takes over.
        Assert.NotNull(driver.Connect(pipe, hid));
        Assert.True(driver.SendFrame(pipe, hid, Frame()));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Brightness_during_an_upload_waits_for_it_and_then_applies()
    {
        var driver = Fast(chunkMs: 40);
        var pipe = new FakePipe();
        var hid = new FakeHid { StateReply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32 14") };
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 60));
        Assert.True(WaitUntil(() => driver.IsUploading));
        int before = hid.Writes.Count;

        Assert.True(driver.SetBrightness(pipe, hid, 70));
        Assert.Equal(before, hid.Writes.Count);

        Assert.True(WaitUntil(() => driver.UploadsCompleted == 1));
        Assert.True(WaitUntil(() => hid.Writes.Any(w => w.AsSpan().SequenceEqual(GlacialLcdProtocol.EncodeSettings(70, true, true)))));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_deferred_brightness_that_fails_is_retried_by_the_driver_until_applied()
    {
        var clock = new FakeClock();
        var driver = Fast(clock, chunkMs: 40);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 60));
        Assert.True(WaitUntil(() => driver.IsUploading));
        Assert.True(driver.SetBrightness(pipe, hid, 0));

        // The panel stays silent after the upload, so the first apply fails.
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading));
        WorkerLooksTwice(driver);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0x5C);

        hid.StateReply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32");
        clock.Advance(10_000);
        Assert.True(WaitUntil(() => hid.Writes.Any(w => w.AsSpan().SequenceEqual(GlacialLcdProtocol.EncodeSettings(0, true, false)))));
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_pending_brightness_survives_a_disconnect_and_is_applied_by_the_next_worker()
    {
        var clock = new FakeClock();
        var driver = Fast(clock, chunkMs: 600_000);
        var pipe = new FakePipe();
        var silent = new FakeHid();
        driver.Connect(pipe, silent);
        driver.SendFrame(pipe, silent, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));
        Assert.True(driver.SetBrightness(pipe, silent, 0));
        Assert.True(WaitUntil(() => !driver.IsUploading, 2000));
        driver.Disconnect(pipe, silent);

        var answering = new FakeHid { StateReply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32") };
        driver.Connect(pipe, answering);
        clock.Advance(10_000);

        Assert.True(WaitUntil(() => answering.Writes.Any(w => w.AsSpan().SequenceEqual(GlacialLcdProtocol.EncodeSettings(0, true, false))), 5000));
        driver.Disconnect(pipe, answering);
    }

    [Fact]
    public void A_disconnect_that_cancels_an_upload_adds_neither_cooldown_nor_backoff()
    {
        var clock = new FakeClock();
        var driver = Fast(clock, chunkMs: 600_000, backoffMs: 600_000, cancelRetryMs: 600_000);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));
        driver.Disconnect(pipe, hid);

        var pipe2 = new FakePipe();
        driver.Connect(pipe2, hid);
        driver.SendFrame(pipe2, hid, Frame(noiseRows: 100));

        // No clock advance: the next upload starts at once on the fresh worker.
        Assert.True(WaitUntil(() => pipe2.Writes.Count >= 1, 5000));
        driver.Disconnect(pipe2, hid);
    }

    [Fact]
    public void Brightness_zero_cancels_the_upload_and_applies_at_once()
    {
        var driver = Fast(chunkMs: 600_000);
        var pipe = new FakePipe();
        var hid = new FakeHid { StateReply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32") };
        driver.Connect(pipe, hid);
        driver.SendFrame(pipe, hid, Frame(noiseRows: 100));
        Assert.True(WaitUntil(() => pipe.Writes.Count >= 1));

        Assert.True(driver.SetBrightness(pipe, hid, 0));

        Assert.True(WaitUntil(() => !driver.IsUploading, 2000));
        Assert.True(WaitUntil(() => hid.Writes.Any(w => w.AsSpan().SequenceEqual(GlacialLcdProtocol.EncodeSettings(0, true, false))), 2000));
        Assert.Equal(0, driver.UploadsCompleted);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void Brightness_uses_only_a_fresh_state_reply()
    {
        var driver = Fast();
        var hid = new FakeHid { StateReply = Report("EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32 14") };
        // An older reply left in the queue must not be mistaken for the answer.
        hid.Input.Enqueue(Report("EC 5C 00 00 00 0A 00 00 00 0A 11 00 00 00 00 0A"));

        Assert.True(driver.SetBrightness(new FakePipe(), hid, 70));

        Assert.Equal(
            new[] { GlacialLcdProtocol.EncodeQuery(), GlacialLcdProtocol.EncodeSettings(70, true, true) },
            hid.Writes);
    }

    [Fact]
    public void Brightness_without_a_state_reply_writes_no_settings_and_never_reads_without_a_timeout()
    {
        var clock = new FakeClock();
        var driver = Fast(clock);
        var hid = new FakeHid();
        hid.Input.Enqueue(Report("EC 5C 00 00 00 0A 00 00 00 0A 11 00 00 00 00 0A"));

        Assert.False(driver.SetBrightness(new FakePipe(), hid, 30));

        Assert.Equal(new[] { GlacialLcdProtocol.EncodeQuery() }, hid.Writes);
        // A read that waits forever would wedge the hub on a handle that ignores timeouts.
        Assert.All(hid.ReadTimeouts, t => Assert.True(t >= 0));

        // Inside the retry pause the panel is left alone.
        Assert.False(driver.SetBrightness(new FakePipe(), hid, 30));
        Assert.Single(hid.Writes);
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
        Assert.True(driver.HidOpensForInput);
        Assert.True(driver.SupportsBrightness);
        Assert.Equal(0, ((IBulkPanelDriver)driver).KeepaliveMs);
        Assert.Equal(30 * 60 * 1000, GlacialLcdPanelDriver.DefaultRefreshIntervalMs);
    }

    private sealed class FakeMemory : ILcdUploadMemory
    {
        public Dictionary<string, string> Hashes { get; } = new();

        public string? Load(string key) => Hashes.TryGetValue(key, out var hash) ? hash : null;

        public void Save(string key, string hash) => Hashes[key] = hash;

        public void Clear(string key) => Hashes.Remove(key);
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

        /// <summary>Input reports waiting to be read.</summary>
        public Queue<byte[]> Input { get; } = new();

        /// <summary>Queued as input each time the state query is written; null models a panel that stays silent.</summary>
        public byte[]? StateReply { get; set; }

        public int VendorId => 0x0B05;
        public int ProductId => GlacialLcdProtocol.ProductId;
        public string Path => "/dev/fake-glacial";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF72;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            lock (_lock)
            {
                _writes.Add(report.ToArray());
                if (report[1] == 0xDC && StateReply is { } reply)
                {
                    Input.Enqueue(reply);
                }
            }
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;

        public List<int> ReadTimeouts { get; } = new();

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            lock (_lock)
            {
                ReadTimeouts.Add(timeoutMs);
                if (Input.Count == 0)
                {
                    return 0;
                }
                var reply = Input.Dequeue();
                reply.CopyTo(buffer);
                return reply.Length;
            }
        }

        public void Dispose() { }
    }
}

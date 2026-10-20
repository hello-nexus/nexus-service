using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        Assert.Equal(new[] { Report("EC 71 01 01"), Report("EC F1"), Report("EC 72 01 01 02"), Report("EC 73 01") }, writes.Skip(1).Take(4));
        var announce = writes[5];
        int size = announce[3] | (announce[4] << 8) | (announce[5] << 16);
        Assert.Equal(new byte[] { 0xEC, 0x7F, 0x02 }, announce.Take(3));
        Assert.Equal(new[] { Report("EC 73 FF"), Report("EC DC"), Report("EC 51 11 01 02") }, writes.Skip(6));

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
        Assert.Equal(new byte[] { 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07 }, jpeg.Skip(25).Take(10));
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
        Assert.True(WaitUntil(() => hid2.Writes.Any(w => w.AsSpan().SequenceEqual(Report("EC 51 11 01 02")))));
        second.Disconnect(pipe2, hid2);
    }

    [Fact]
    public void A_failed_upload_forgets_the_remembered_picture_and_backs_off()
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
        Assert.True(WaitUntil(() => driver.UploadAttempts == 2));
        Assert.True(WaitUntil(() => memory.Hashes.Count == 0));
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
        memory.Hashes["FAKE"] = "ABC";
        var driver = Fast(link);
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid();
        driver.Connect(pipe, hid);

        link.RequestCancel();
        driver.SendFrame(pipe, hid, Frame());
        Looked(driver);

        Assert.Equal("ABC", memory.Hashes["FAKE"]);
        Assert.Empty(pipe.Writes);
        Assert.DoesNotContain(hid.Writes, w => w[1] == 0xF1);
        driver.Disconnect(pipe, hid);
    }

    [Fact]
    public void A_cancel_between_the_flush_and_the_first_transfer_still_forgets_the_remembered_picture()
    {
        var link = new GlacialLcdLink();
        var memory = new FakeMemory();
        memory.Hashes["FAKE"] = "ABC";
        var driver = Fast(link);
        driver.BindMemory(memory);
        var pipe = new FakePipe();
        var hid = new FakeHid { OnWrite = w => { if (w[1] == 0xF1) { link.RequestCancel(); } } };
        driver.Connect(pipe, hid);

        driver.SendFrame(pipe, hid, Frame());
        Assert.True(WaitUntil(() => driver.UploadAttempts == 1 && !driver.IsUploading));

        Assert.Empty(memory.Hashes);
        Assert.Empty(pipe.Writes);
        driver.Disconnect(pipe, hid);
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
        Assert.True(driver.HidOpensForInput);
        Assert.False(((IBulkPanelDriver)driver).SupportsBrightness);
        Assert.True(driver.MultiInstance);
        Assert.Equal("lcd-square", driver.Surface);
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

        /// <summary>Runs after each report is recorded, so a test can act at an exact point of the sequence.</summary>
        public Action<byte[]>? OnWrite { get; set; }

        public int VendorId => 0x0B05;
        public int ProductId => GlacialLcdProtocol.ProductId;
        public string Path => "/dev/fake-glacial";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF72;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            lock (_lock)
            {
                _writes.Add(copy);
            }
            OnWrite?.Invoke(copy);
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}

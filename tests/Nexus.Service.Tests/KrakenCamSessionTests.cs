using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>The 2023 Kraken (0x300E) follows the vendor app's session init and backlight layout; every other model does not.</summary>
public class KrakenCamSessionTests
{
    private sealed class Rig
    {
        public readonly KrakenHub Hub;
        public readonly Hid Device = new();
        public readonly Lcd Bulk = new();
        public long Now = 1_000_000;
        public readonly List<(int Ms, int WritesBefore)> Delays = new();

        public Rig(int productId)
        {
            var model = KrakenModel.Find(productId)!;
            Hub = new KrakenHub(new Factory(Bulk)) { Clock = () => Now, CamSenderThread = false };
            Hub.FadeRunner.AutoRun = false;
            Hub.DelayMs = ms => Delays.Add((ms, Device.Writes.Count));
            Device.BulkCount = () => Bulk.Writes.Count;
            Hub.Attach(Device, model, KrakenProtocol.ReportLength);
            Assert.True(Hub.Connect());
        }

        // On the 2023 Kraken a pushed frame only becomes pending; the sender puts it on the wire, stepped by hand here.
        public bool Push(byte[] frame)
        {
            var taken = Hub.PushStreamFrame(frame);
            return taken && (!Hub.IsCamSessionModel || Hub.SendCamSlot());
        }

        public byte[] Frame()
        {
            var f = new byte[Hub.LcdFrameBytes];
            Array.Fill(f, (byte)7);
            return f;
        }

        public byte[] FrameOf(byte value)
        {
            var f = new byte[Hub.LcdFrameBytes];
            Array.Fill(f, value);
            return f;
        }

        // Each frame is two bulk writes (header, then pixels); the pixel write is the odd one out.
        public byte[][] SentFrames() => Bulk.Writes.Where((_, i) => i % 2 == 1).ToArray();

        public string[] Ids() => Device.Writes.Select(w => $"{w[0]:X2}{w[1]:X2}").ToArray();

        public int Count(string id) => Ids().Count(i => i == id);

        // Opens the stream, then silences the fake so only injected reports reach the hub.
        public void OpenStream(int brightness = 80)
        {
            Assert.True(Hub.SetLcdBacklight(brightness, 0));
            Assert.True(Push(Frame()));
            Device.Echo = false;
            Device.Writes.Clear();
            Delays.Clear();
        }

        // Forgets what the opening frame did, so a test sees only its own frame's waits and writes.
        public void ForgetOpeningFrame()
        {
            Bulk.Writes.Clear();
            Device.Delivered.Clear();
            Hub.CamReplies.Take(System.Diagnostics.Stopwatch.Frequency);
        }

        public bool PollWithReport(double liquidC = 31.5)
        {
            Device.Push(Status(liquidC));
            return Hub.Poll();
        }

        public static byte[] Status(double liquidC)
        {
            var r = new byte[KrakenProtocol.ReportLength];
            r[0] = 0x75;
            r[1] = 0x02;
            r[15] = (byte)liquidC;
            r[16] = (byte)Math.Round((liquidC - Math.Floor(liquidC)) * 10);
            return r;
        }
    }

    [Fact]
    public void Connect_on_the_2023_Kraken_sets_the_vendor_update_interval()
    {
        var rig = new Rig(0x300E);
        var report = rig.Device.Writes.Single(w => w[0] == 0x70 && w[1] == 0x02);
        Assert.Equal(new byte[] { 0x70, 0x02, 0x01, 0xB8, 0x0B }, report[..5]);
    }

    [Fact]
    public void The_first_stream_frame_on_the_2023_Kraken_opens_the_session_in_the_vendor_order()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        Assert.True(rig.Push(rig.Frame()));

        Assert.Equal(
            new[] { "1002", "7002", "7401", "3604", "3001", "3603", "3002", "3002", "3601", "3602" }, rig.Ids());
        Assert.Equal(new byte[] { 0x70, 0x02, 0x01, 0xB8, 0x0B }, rig.Device.Writes[1][..5]);
        var start = rig.Device.Writes[8];
        Assert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x01, 0x06 }, start[..5]);
    }

    [Fact]
    public void Session_open_goes_dark_first_then_restores_the_real_backlight_after_the_delay()
    {
        var rig = new Rig(0x300E);
        Assert.True(rig.Hub.SetLcdBacklight(80, 2));
        rig.Device.Writes.Clear();

        Assert.True(rig.Push(rig.Frame()));

        Assert.Equal(
            new byte[] { 0x30, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E },
            rig.Device.Writes[6][..9]);
        Assert.Equal(
            new byte[] { 0x30, 0x02, 0x01, 80, 0x00, 0x00, 0x00, 0x02, 0x1E },
            rig.Device.Writes[7][..9]);
        var delay = Assert.Single(rig.Delays);
        Assert.Equal(70, delay.Ms);
        Assert.Equal(7, delay.WritesBefore);
    }

    [Fact]
    public void No_status_request_is_sent_while_the_stream_is_open_and_reports_keep_arriving()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();

        for (int i = 0; i < 20; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.PollWithReport());
        }

        Assert.Equal(0, rig.Count("7401"));
        Assert.Equal(31.5, rig.Hub.Snapshot.LiquidTempC, 1);
    }

    [Fact]
    public void A_status_request_is_the_fallback_once_no_report_was_read_for_three_seconds()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        rig.Device.Push(Rig.Status(30));
        Assert.True(rig.Hub.Poll());

        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        Assert.Equal(0, rig.Count("7401"));

        rig.Now += 1000;
        rig.Device.Echo = true;
        Assert.True(rig.Hub.Poll());
        Assert.Equal(1, rig.Count("7401"));

        // The fallback's reply counts as a report, so the next window starts over.
        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        rig.Now += 1000;
        Assert.True(rig.Hub.Poll());
        Assert.Equal(1, rig.Count("7401"));
    }

    [Fact]
    public void The_fallback_is_logged_at_most_once_a_minute()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        const string Marker = "while streaming; polling once";
        int Logged() => ServiceLog.RecentLines().Split('\n').Count(l => l.Contains(Marker));
        var before = Logged();

        for (int i = 0; i < 30; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.Hub.Poll());
        }

        Assert.True(rig.Count("7401") >= 10);
        Assert.InRange(Logged() - before, 0, 1);
    }

    [Fact]
    public void A_keepalive_goes_out_every_28_seconds_while_the_stream_is_open()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();

        for (int i = 0; i < 27; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.PollWithReport());
        }
        Assert.Equal(0, rig.Count("1002"));

        rig.Now += 1000;
        Assert.True(rig.PollWithReport());
        Assert.Equal(1, rig.Count("1002"));

        for (int i = 0; i < 28; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.PollWithReport());
        }
        Assert.Equal(2, rig.Count("1002"));
    }

    [Fact]
    public void Before_a_stream_opens_the_2023_Kraken_polls_status_as_always_and_sends_no_keepalive()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        for (int i = 0; i < 40; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.Hub.Poll());
        }

        Assert.Equal(40, rig.Count("7401"));
        Assert.Equal(0, rig.Count("1002"));
    }

    [Theory]
    [InlineData(0x3014)]
    [InlineData(0x3012)]
    [InlineData(0x300C)]
    [InlineData(0x3008)]
    public void Other_models_keep_polling_and_never_send_the_keepalive_or_session_extras(int productId)
    {
        var rig = new Rig(productId);
        rig.Device.Writes.Clear();
        rig.Hub.PushStreamFrame(rig.Frame());
        var open = rig.Ids();
        Assert.DoesNotContain("1002", open);
        Assert.DoesNotContain("7002", open);

        rig.Device.Writes.Clear();
        for (int i = 0; i < 40; i++)
        {
            rig.Now += 1000;
            Assert.True(rig.Hub.Poll());
        }

        Assert.Equal(40, rig.Count("7401"));
        Assert.Equal(0, rig.Count("1002"));
        Assert.True(rig.Hub.StatusFresh);
    }

    [Fact]
    public void Temperature_is_withheld_from_cooling_once_reports_stop_for_too_long()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        var cooling = new KrakenCoolingProvider(rig.Hub);
        rig.Now += 1000;
        Assert.True(rig.PollWithReport(40));
        Assert.Equal(40f, cooling.ReadTemperature("nzxt-kraken:liquid"));
        Assert.Single(cooling.GetTemperatureSources());

        rig.Now += 9000;
        Assert.True(rig.Hub.StatusFresh);
        rig.Now += 2000;

        Assert.False(rig.Hub.StatusFresh);
        Assert.Null(cooling.ReadTemperature("nzxt-kraken:liquid"));
        Assert.Empty(cooling.GetTemperatureSources());

        rig.Device.Push(Rig.Status(41));
        Assert.True(rig.Hub.Poll());
        Assert.Equal(41f, cooling.ReadTemperature("nzxt-kraken:liquid"));
    }

    [Fact]
    public void The_thirty_second_summary_counts_frames_written_skipped_and_reports_read()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        var frame = rig.Frame();
        for (int i = 0; i < 5; i++)
        {
            rig.Now += 100;
            Assert.True(rig.Push(frame));
        }
        Assert.True(rig.PollWithReport());
        rig.Now += 31_000;
        Assert.True(rig.PollWithReport());

        var line = ServiceLog.RecentLines().Split('\n')
            .Last(l => l.Contains("[nzxt-kraken] 30 s:"));
        Assert.Contains("frames written", line);
        Assert.Contains("skipped as unchanged", line);
        Assert.Contains("status reports read", line);
        Assert.Contains("start-to-start ms min", line);
        Assert.Contains("new frames sent", line);
        Assert.Contains("repeated", line);
    }

    [Fact]
    public void Later_frames_do_not_reopen_the_session()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        for (int i = 0; i < 20; i++)
        {
            rig.Now += 40;
            var frame = rig.Frame();
            frame[0] = (byte)i;
            Assert.True(rig.Push(frame));
        }

        Assert.Single(rig.Ids(), "3604");
        Assert.Single(rig.Ids(), "3001");
        Assert.Single(rig.Ids(), "3603");
        Assert.Single(rig.Ids(), "7401");
        Assert.Single(rig.Ids(), "1002");
        Assert.Single(rig.Ids(), "7002");
    }

    [Fact]
    public void A_pushed_frame_waits_for_the_sender_instead_of_going_out_on_arrival()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));

        Assert.Empty(rig.Bulk.Writes);
        Assert.Empty(rig.Device.Writes);
    }

    [Fact]
    public void A_newer_frame_replaces_the_pending_one_and_nothing_queues()
    {
        var rig = new Rig(0x300E);
        Assert.True(rig.Hub.PushStreamFrame(rig.FrameOf(8)));
        Assert.True(rig.Hub.PushStreamFrame(rig.FrameOf(16)));
        Assert.True(rig.Hub.PushStreamFrame(rig.FrameOf(248)));

        Assert.True(rig.Hub.SendCamSlot());

        var sent = Assert.Single(rig.SentFrames());
        // Every byte of the source was 248, so the packed pixel is the top 5, 6 and 5 bits of it, high byte first.
        Assert.Equal(new byte[] { 0xFF, 0xDF }, sent[..2]);
    }

    [Fact]
    public void With_nothing_new_the_last_frame_goes_out_again_every_slot()
    {
        var rig = new Rig(0x300E);
        Assert.True(rig.Hub.PushStreamFrame(rig.FrameOf(100)));

        for (int i = 0; i < 4; i++)
        {
            Assert.True(rig.Hub.SendCamSlot());
        }

        var sent = rig.SentFrames();
        Assert.Equal(4, sent.Length);
        Assert.All(sent, f => Assert.Equal(sent[0], f));
        Assert.Equal(4, rig.Count("3601"));
    }

    [Fact]
    public void An_identical_frame_is_not_deduplicated_on_the_cadenced_wire()
    {
        var rig = new Rig(0x300E);
        var frame = rig.FrameOf(60);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(rig.Push(frame));
        }

        Assert.Equal(3, rig.SentFrames().Length);
    }

    [Fact]
    public void No_slot_before_the_first_frame_sends_anything()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.SendCamSlot());

        Assert.Empty(rig.Bulk.Writes);
        Assert.Empty(rig.Device.Writes);
    }

    [Fact]
    public void The_sender_sends_nothing_while_the_lock_holds_the_display_dark()
    {
        var rig = new Rig(0x300E);
        var lockState = new Nexus.Service.Lighting.LcdLockState();
        rig.Hub.BindLockSleep(lockState, () => true);
        Assert.True(rig.Hub.SetLcdBacklight(80, 0));
        Assert.True(rig.Push(rig.FrameOf(10)));
        var before = rig.Bulk.Writes.Count;

        lockState.Set(true);
        Assert.True(rig.Hub.LockDimmed);
        Assert.True(rig.Hub.PushStreamFrame(rig.FrameOf(20)));
        Assert.True(rig.Hub.SendCamSlot());

        Assert.Equal(before, rig.Bulk.Writes.Count);
    }

    // A cadence whose slots open when the test releases the gate, so the real sender thread runs deterministically.
    private sealed class GatedCadence
    {
        public readonly System.Threading.SemaphoreSlim Gate = new(0);
        private long _tick;

        public Func<System.Threading.CancellationToken, KrakenCadence> Factory => ct => new KrakenCadence(
            () => System.Threading.Interlocked.Add(ref _tick, KrakenCadence.PeriodUs),
            1_000_000,
            _ =>
            {
                try
                {
                    Gate.Wait(ct);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            });
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting for the sender");
            System.Threading.Thread.Sleep(2);
        }
    }

    [Fact]
    public void Closing_the_stream_stops_the_sender_thread_and_no_more_frames_go_out()
    {
        var rig = new Rig(0x300E);
        var gated = new GatedCadence();
        rig.Hub.CamSenderThread = true;
        rig.Hub.CadenceFactory = gated.Factory;

        Assert.True(rig.Hub.PushStreamFrame(rig.FrameOf(30)));
        Assert.True(rig.Hub.CamSenderRunning);
        gated.Gate.Release(2);
        WaitFor(() => rig.SentFrames().Length >= 2);

        rig.Hub.ReleaseStream();

        Assert.False(rig.Hub.CamSenderRunning);
        var sent = rig.Bulk.Writes.Count;
        gated.Gate.Release(5);
        System.Threading.Thread.Sleep(50);
        Assert.Equal(sent, rig.Bulk.Writes.Count);
    }

    [Fact]
    public void A_disconnect_stops_the_sender_thread()
    {
        var rig = new Rig(0x300E);
        var gated = new GatedCadence();
        rig.Hub.CamSenderThread = true;
        rig.Hub.CadenceFactory = gated.Factory;
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));
        Assert.True(rig.Hub.CamSenderRunning);

        rig.Hub.Detach();

        Assert.False(rig.Hub.CamSenderRunning);
        gated.Gate.Release(3);
        System.Threading.Thread.Sleep(50);
        Assert.Empty(rig.Bulk.Writes);
    }

    [Fact]
    public void The_sender_thread_exits_once_frames_stop_arriving_and_a_later_frame_restarts_it()
    {
        var rig = new Rig(0x300E);
        var gated = new GatedCadence();
        rig.Hub.CamSenderThread = true;
        rig.Hub.CadenceFactory = gated.Factory;
        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));
        rig.Now += 11_000;

        gated.Gate.Release(1);
        WaitFor(() => !rig.Hub.CamSenderRunning);

        Assert.True(rig.Hub.PushStreamFrame(rig.Frame()));
        Assert.True(rig.Hub.CamSenderRunning);
        rig.Hub.ReleaseStream();
    }

    [Fact]
    public void The_Plus_V2_still_sends_on_arrival_and_skips_an_unchanged_frame()
    {
        var rig = new Rig(0x3014);
        var frame = rig.FrameOf(60);

        Assert.True(rig.Hub.PushStreamFrame(frame));
        Assert.Single(rig.SentFrames());
        Assert.True(rig.Hub.PushStreamFrame(frame));

        Assert.Single(rig.SentFrames());
        Assert.False(rig.Hub.CamSenderRunning);
    }

    private static byte[] FrameWithBrightFirstRow(Rig rig)
    {
        var f = rig.FrameOf(10);
        Array.Fill(f, (byte)200, 0, rig.Hub.LcdWidth * 4);
        return f;
    }

    [Fact]
    public void The_stopgap_copies_row_one_over_row_zero_for_the_2023_Kraken_stream()
    {
        var rig = new Rig(0x300E);
        var transport = new Nexus.Service.Panel.Streams.KrakenStreamTransport(rig.Hub, "s");

        transport.Write(FrameWithBrightFirstRow(rig));
        Assert.True(rig.Hub.SendCamSlot());

        var sent = Assert.Single(rig.SentFrames());
        Assert.Equal(new byte[] { 0x08, 0x41 }, sent[..2]);
    }

    [Fact]
    public void The_stopgap_leaves_every_other_model_s_frame_alone()
    {
        var rig = new Rig(0x3014);
        var transport = new Nexus.Service.Panel.Streams.KrakenStreamTransport(rig.Hub, "s");

        transport.Write(FrameWithBrightFirstRow(rig));

        var sent = Assert.Single(rig.SentFrames());
        Assert.NotEqual(new byte[] { 0x08, 0x41 }, sent[..2]);
    }

    [Fact]
    public void The_Plus_V2_session_is_unchanged()
    {
        var rig = new Rig(0x3014);
        rig.Device.Writes.Clear();

        Assert.True(rig.Push(rig.Frame()));

        Assert.Equal(new[] { "3604", "3603", "3601", "3602" }, rig.Ids());
    }

    [Fact]
    public void The_2023_Kraken_backlight_uses_the_vendor_layout_and_keeps_rotation_in_byte_7()
    {
        var rig = new Rig(0x300E);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.SetLcdBacklight(80, 0));
        var report = rig.Device.Writes.Last(w => w[0] == 0x30 && w[1] == 0x02);
        Assert.Equal(new byte[] { 0x30, 0x02, 0x01, 80, 0x00, 0x00, 0x00, 0x00, 0x1E }, report[..9]);

        Assert.True(rig.Hub.SetLcdBacklight(80, 2));
        report = rig.Device.Writes.Last(w => w[0] == 0x30 && w[1] == 0x02);
        Assert.Equal(2, report[7]);
        Assert.Equal(0x1E, report[8]);
    }

    [Theory]
    [InlineData(0x3014)]
    [InlineData(0x3012)]
    [InlineData(0x300C)]
    [InlineData(0x3008)]
    public void Other_models_keep_the_original_backlight_layout(int productId)
    {
        var rig = new Rig(productId);
        rig.Device.Writes.Clear();

        Assert.True(rig.Hub.SetLcdBacklight(80, 2));

        var report = rig.Device.Writes.Last(w => w[0] == 0x30 && w[1] == 0x02);
        Assert.Equal(KrakenProtocol.EncodeSetBacklight(80, 2), report);
        Assert.Equal(0x01, report[6]);
        Assert.Equal(0x00, report[8]);
    }

    [Fact]
    public void The_default_backlight_encoding_is_byte_identical_to_before()
    {
        var report = KrakenProtocol.EncodeSetBacklight(50, 3);
        var expected = new byte[KrakenProtocol.ReportLength];
        expected[0] = 0x30;
        expected[1] = 0x02;
        expected[2] = 0x01;
        expected[3] = 50;
        expected[6] = 0x01;
        expected[7] = 3;
        Assert.Equal(expected, report);
    }

    [Fact]
    public void Only_the_2023_Kraken_follows_the_vendor_session()
    {
        Assert.Equal(new[] { 0x300E }, KrakenModel.All.Where(m => m.CamSessionInit).Select(m => m.ProductId));
    }

    private static byte[] TransferReply(byte sub)
    {
        var r = new byte[KrakenProtocol.ReportLength];
        r[0] = 0x37;
        r[1] = sub;
        r[14] = KrakenProtocol.AckOk;
        return r;
    }

    // The cooler answers start after a few empty polls, as a real one does within a slot. It never
    // answers end. readsAtEnd gets the device's read count at the moment the end command goes out.
    private static void AnswerStart(Rig rig, Action<int>? readsAtEnd = null, int emptyReads = 2)
    {
        rig.Device.OnWrite = w =>
        {
            if (w[0] == 0x36 && w[1] == 0x01)
            {
                rig.Device.PushAfter(TransferReply(0x01), emptyReads);
            }
            else if (w[0] == 0x36 && w[1] == 0x02)
            {
                readsAtEnd?.Invoke(rig.Device.Reads);
            }
        };
    }

    [Fact]
    public void The_bulk_does_not_start_until_the_start_reply_arrives_and_the_end_path_reads_nothing()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        rig.ForgetOpeningFrame();
        int readsAtEnd = -1;
        AnswerStart(rig, r => readsAtEnd = r);

        Assert.True(rig.Push(rig.Frame()));

        var start = Assert.Single(rig.Device.Delivered, d => d.Id == 0x37 && d.Sub == 0x01);
        Assert.Equal(0, start.BulkWrites);
        Assert.Single(rig.SentFrames());
        Assert.Equal(new[] { "3601", "3602" }, rig.Ids());
        Assert.True(readsAtEnd >= 0);
        Assert.Equal(readsAtEnd, rig.Device.Reads);
        var startStats = rig.Hub.CamReplies.Take(System.Diagnostics.Stopwatch.Frequency);
        Assert.Equal((1, 0), (startStats!.Value.Replies, startStats.Value.Misses));
    }

    [Fact]
    public void A_missing_reply_times_out_carries_on_and_counts_a_miss_without_resetting_the_stream()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        rig.ForgetOpeningFrame();

        Assert.True(rig.Push(rig.Frame()));
        Assert.True(rig.Push(rig.Frame()));

        Assert.Equal(2, rig.SentFrames().Length);
        Assert.Equal(new[] { "3601", "3602", "3601", "3602" }, rig.Ids());
        var start = rig.Hub.CamReplies.Take(System.Diagnostics.Stopwatch.Frequency)!.Value;
        Assert.Equal((0, 2), (start.Replies, start.Misses));
    }

    [Fact]
    public void A_reply_left_from_an_earlier_frame_does_not_satisfy_the_wait()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        rig.ForgetOpeningFrame();
        rig.Device.Push(TransferReply(0x01));

        Assert.True(rig.Push(rig.Frame()));

        var start = rig.Hub.CamReplies.Take(System.Diagnostics.Stopwatch.Frequency)!.Value;
        Assert.Equal((0, 1), (start.Replies, start.Misses));
    }

    [Fact]
    public void A_status_report_queued_before_the_frame_is_folded_not_dropped_by_the_drain()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        rig.ForgetOpeningFrame();
        var before = rig.Hub.StatusReportsRead;
        rig.Device.Push(Rig.Status(33.5));

        Assert.True(rig.Push(rig.Frame()));

        Assert.Equal(before + 1, rig.Hub.StatusReportsRead);
        Assert.Equal(33.5, rig.Hub.Snapshot.LiquidTempC, 1);
    }

    [Fact]
    public void Status_reports_read_while_waiting_for_the_start_reply_are_folded_and_counted_and_the_reply_still_lands()
    {
        var rig = new Rig(0x300E);
        rig.OpenStream();
        rig.ForgetOpeningFrame();
        var before = rig.Hub.StatusReportsRead;
        rig.Device.OnWrite = w =>
        {
            if (w[0] == 0x36 && w[1] == 0x01)
            {
                rig.Device.Push(Rig.Status(36.5));
                rig.Device.Push(TransferReply(0x01));
            }
        };

        Assert.True(rig.Push(rig.Frame()));

        Assert.Equal(before + 1, rig.Hub.StatusReportsRead);
        Assert.Equal(36.5, rig.Hub.Snapshot.LiquidTempC, 1);
        var start = rig.Hub.CamReplies.Take(System.Diagnostics.Stopwatch.Frequency)!.Value;
        Assert.Equal((1, 0), (start.Replies, start.Misses));
        Assert.Equal(0, rig.Device.Delivered.First(d => d.Id == 0x37 && d.Sub == 0x01).BulkWrites);
    }

    [Fact]
    public void The_Plus_V2_sends_a_frame_without_waiting_on_any_reply()
    {
        var rig = new Rig(0x3014);
        rig.OpenStream();
        rig.ForgetOpeningFrame();
        var reads = rig.Device.Reads;

        Assert.True(rig.Push(rig.FrameOf(9)));

        Assert.Equal(reads, rig.Device.Reads);
        Assert.Equal(new[] { "3601", "3602" }, rig.Ids());
        Assert.Null(rig.Hub.CamReplies.Take(System.Diagnostics.Stopwatch.Frequency));
    }

    private sealed class Hid : IHidDevice
    {
        private readonly Queue<(byte[] Report, int EmptyReadsFirst)> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public bool Echo { get; set; } = true;
        public int Reads { get; private set; }
        public Func<int> BulkCount { get; set; } = () => 0;

        // Called after every write that is not echoed, so a test can script what the cooler answers.
        public Action<byte[]>? OnWrite { get; set; }

        // Each report handed to the hub, with how many bulk writes had gone out by then.
        public List<(byte Id, byte Sub, int BulkWrites)> Delivered { get; } = new();

        private int _emptyReads;

        public void Push(byte[] report) => _replies.Enqueue((report, 0));

        // The report becomes readable only after the hub has polled an empty queue this many times.
        public void PushAfter(byte[] report, int emptyReads) => _replies.Enqueue((report, emptyReads));

        public int VendorId => 0x1E71;
        public int ProductId => 0x300E;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            if (!Echo)
            {
                OnWrite?.Invoke(copy);
                return true;
            }
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(copy[0] + 1);
            reply[1] = copy[1];
            reply[14] = KrakenProtocol.AckOk;
            if (copy[0] == 0x10)
            {
                reply[0x11] = 1;
                reply[0x12] = 0;
            }
            _replies.Enqueue((reply, 0));
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            Reads++;
            if (_replies.Count == 0) return 0;
            if (_replies.Peek().EmptyReadsFirst > _emptyReads)
            {
                _emptyReads++;
                return 0;
            }
            _emptyReads = 0;
            var r = _replies.Dequeue().Report;
            Delivered.Add((r[0], r[1], BulkCount()));
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private sealed class Lcd : IKrakenLcdTransport
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Writes = new();

        public bool Write(ReadOnlySpan<byte> data)
        {
            Writes.Enqueue(data.ToArray());
            return true;
        }

        public void Dispose() { }
    }

    private sealed class Factory : IKrakenLcdTransportFactory
    {
        private readonly IKrakenLcdTransport _lcd;
        public Factory(IKrakenLcdTransport lcd) { _lcd = lcd; }
        public IKrakenLcdTransport? Open(string? serial) => _lcd;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Pins the stream's bucket rotation. Ping-pong between two buckets rewrites the bucket
/// that just left the screen while the panel is still reading it, which shows as a band
/// of decode garbage across the bottom rows (bench-measured on the Elite V2). Three
/// buckets give every bucket a full frame off before it is reused.
/// </summary>
public class KrakenStreamBucketTests
{
    // No lighting channels, so Connect skips the accessory table and the fake only has to
    // ack what it is asked. The bucket row is the 2023 Kraken panel with the direct stream
    // switched off; the shipped 0x300E row streams direct, like the Plus V2.
    private static readonly KrakenModel BucketStreamNoRgb = KrakenModel.Find(0x300E)! with { Rgb565DirectStream = false };
    private static readonly KrakenModel EliteNoRgb = KrakenModel.Find(0x300C)!;
    private static readonly KrakenModel PlusV2 = KrakenModel.Find(0x3014)!;
    private static readonly KrakenModel Z3 = KrakenModel.Find(0x3008)!;

    [Fact]
    public void Stream_rotates_three_buckets_and_never_reuses_the_one_just_displayed()
    {
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, BucketStreamNoRgb, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());
        Assert.True(hub.HasLcd);

        var frame = new byte[BucketStreamNoRgb.LcdFrameBytes];
        for (int i = 0; i < 7; i++)
        {
            Assert.True(hub.PushStreamFrame(frame), $"push {i}");
        }

        var setups = hid.Writes.Where(w => w[0] == 0x32 && w[1] == 0x01).Select(w => (Index: w[2], StartPage: w[4] | (w[5] << 8))).ToList();
        var pages = KrakenProtocol.PagesFor(BucketStreamNoRgb.LcdFrameBytes);
        Assert.Equal(new[] { (0, 0), (1, pages), (2, 2 * pages) }, setups.Select(s => ((int)s.Index, s.StartPage)));

        var starts = hid.Writes.Where(w => w[0] == 0x36 && w[1] == 0x01).Select(w => (int)w[2]).ToList();
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2, 0 }, starts);

        var activations = hid.Writes
            .Where(w => w[0] == 0x38 && w[1] == 0x01 && w[2] == (byte)KrakenDisplayMode.Bucket)
            .Select(w => (int)w[3]).ToList();
        Assert.Equal(starts, activations);

        // The bucket written by push k is not the one push k-2 put on screen, which is
        // exactly what ping-pong would do.
        for (int k = 2; k < starts.Count; k++)
        {
            Assert.NotEqual(activations[k - 2], starts[k]);
        }
        Assert.Equal(7, lcd.Frames);
    }

    [Fact]
    public void Elite_2023_streams_raw_bgr_frames_without_touching_buckets()
    {
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, EliteNoRgb, 64);
        Assert.True(hub.Connect());

        var frame = new byte[EliteNoRgb.LcdFrameBytes];
        for (int i = 0; i < 3; i++)
        {
            Assert.True(hub.PushStreamFrame(frame), $"push {i}");
        }

        Assert.DoesNotContain(hid.Writes, w => w[0] == 0x32 && w[1] == 0x01);
        Assert.DoesNotContain(hid.Writes, w => w[0] == 0x38 && w[1] == 0x01 && w[2] == (byte)KrakenDisplayMode.Bucket);
        var starts = hid.Writes.Where(w => w[0] == 0x36 && w[1] == 0x01).ToList();
        Assert.Equal(3, starts.Count);
        Assert.All(starts, s => Assert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x01, 0x09 }, s[..5]));
        Assert.Equal(3, hid.Writes.Count(w => w[0] == 0x36 && w[1] == 0x02));
        // The one-time entry runs once per stream, not per frame.
        Assert.Single(hid.Writes, w => w[0] == 0x36 && w[1] == 0x03);

        int rawBytes = EliteNoRgb.LcdWidth * EliteNoRgb.LcdHeight * 3;
        Assert.Equal(3, lcd.Frames);
        Assert.All(lcd.Headers, h =>
        {
            Assert.Equal(KrakenProtocol.BulkFormatBgr888, h[12]);
            Assert.Equal(rawBytes, BitConverter.ToInt32(h, 16));
        });
        Assert.Equal(3 * rawBytes, lcd.ChunkSizes.Sum());
        // The direct-path transfer size divides a full frame evenly.
        Assert.All(lcd.ChunkSizes, n => Assert.Equal(245_760, n));
        Assert.Equal(KrakenDisplayMode.Liquid, hub.Snapshot.DisplayMode);
    }

    [Fact]
    public void Plus_v2_streams_rgb565_high_byte_first_without_touching_buckets()
    {
        var hid = new AckingHidDevice { FirmwareMajor = 1 };
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, PlusV2, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());
        hid.Writes.Clear();

        var frame = new byte[PlusV2.LcdFrameBytes];
        for (int i = 0; i < frame.Length; i += 4)
        {
            // BGRA pure red: RGB565 0xF800.
            frame[i + 2] = 0xFF;
            frame[i + 3] = 0xFF;
        }
        Assert.True(hub.PushStreamFrame(frame));

        Assert.DoesNotContain(hid.Writes, w => w[0] == 0x32 || (w[0] == 0x38 && w[2] == (byte)KrakenDisplayMode.Bucket));
        Assert.Equal(new byte[] { 0x36, 0x04 }, hid.Writes[0][..2]);
        Assert.Equal(new byte[] { 0x36, 0x03 }, hid.Writes[1][..2]);
        Assert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x01, 0x06 }, hid.Writes[2][..5]);
        Assert.Equal(new byte[] { 0x36, 0x02 }, hid.Writes[3][..2]);
        Assert.Equal(4, hid.Writes.Count);
        Assert.Equal(512, hid.Writes[2].Length);

        var header = Assert.Single(lcd.Headers);
        Assert.Equal(
            new byte[] { 0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10, 0x06, 0, 0, 0, 0x00, 0xC2, 0x01, 0x00 },
            header);
        Assert.Equal(new[] { 115200 }, lcd.ChunkSizes);
        Assert.Equal(0xF8, lcd.LastPayloadFirstBytes[0]);
        Assert.Equal(0x00, lcd.LastPayloadFirstBytes[1]);
    }

    [Fact]
    public void Plus_v2_holds_an_unchanged_frame_until_the_idle_refresh_then_resends_the_cached_bytes()
    {
        long now = 1_000;
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd)) { Clock = () => now };
        hub.Attach(hid, PlusV2, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());

        var frame = new byte[PlusV2.LcdFrameBytes];
        Assert.True(hub.PushStreamFrame(frame));
        now += 100;
        Assert.True(hub.PushStreamFrame(frame));
        hub.RefreshIdleFrame();
        Assert.Equal(1, lcd.Frames);

        now += 1_000;
        hub.RefreshIdleFrame();
        Assert.Equal(2, lcd.Frames);

        frame[0] = 0x80;
        now += 10;
        Assert.True(hub.PushStreamFrame(frame));
        Assert.Equal(3, lcd.Frames);

        hub.ReleaseStream();
        now += 5_000;
        hub.RefreshIdleFrame();
        Assert.Equal(3, lcd.Frames);
    }

    [Fact]
    public void Plus_v2_still_image_goes_through_the_direct_transfer_and_is_refreshed()
    {
        long now = 1_000;
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd)) { Clock = () => now };
        hub.Attach(hid, PlusV2, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());
        hid.Writes.Clear();

        Assert.True(hub.UploadLcdImage(new byte[PlusV2.LcdFrameBytes]));
        Assert.DoesNotContain(hid.Writes, w => w[0] == 0x32);
        Assert.Equal(1, lcd.Frames);

        now += 1_100;
        hub.RefreshIdleFrame();
        Assert.Equal(2, lcd.Frames);
    }

    [Fact]
    public void Plus_v2_init_uses_its_telemetry_interval_and_skips_the_lighting_query()
    {
        var hid = new AckingHidDevice();
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, PlusV2, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());

        Assert.Contains(hid.Writes, w => w[0] == 0x70 && w[1] == 0x02 && w[2] == 0x01 && w[3] == 0xB8 && w[4] == 0x0B);
        Assert.DoesNotContain(hid.Writes, w => w[0] == 0x20 && w[1] == 0x03);
    }

    [Fact]
    public void Elite_2023_update_interval_and_start_bytes_are_unchanged()
    {
        var hid = new AckingHidDevice();
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, EliteNoRgb, 64);
        Assert.True(hub.Connect());

        Assert.Contains(hid.Writes, w => w[0] == 0x70 && w[1] == 0x02 && w[4] == 0x01);
        Assert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x01, 0x09 }, KrakenProtocol.EncodeStartDirectTransfer()[..5]);
    }

    [Fact]
    public void Direct_stream_reenters_after_a_mode_change()
    {
        var hid = new AckingHidDevice();
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, EliteNoRgb, 64);
        Assert.True(hub.Connect());
        var frame = new byte[EliteNoRgb.LcdFrameBytes];

        Assert.True(hub.PushStreamFrame(frame));
        Assert.True(hub.PushStreamFrame(frame));
        Assert.True(hub.SetDisplayMode(KrakenDisplayMode.Blank));
        Assert.True(hub.PushStreamFrame(frame));

        Assert.Equal(2, hid.Writes.Count(w => w[0] == 0x36 && w[1] == 0x03));
    }

    [Fact]
    public void Elite_2023_on_firmware_1_keeps_the_bucket_stream()
    {
        var hid = new AckingHidDevice { FirmwareMajor = 1 };
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, EliteNoRgb, 64);
        Assert.True(hub.Connect());

        Assert.True(hub.PushStreamFrame(new byte[EliteNoRgb.LcdFrameBytes]));

        Assert.Contains(hid.Writes, w => w[0] == 0x32 && w[1] == 0x01);
        Assert.DoesNotContain(hid.Writes, w => w[0] == 0x36 && w[1] == 0x01 && w[4] == KrakenProtocol.BulkFormatBgr888);
    }

    [Fact]
    public void Bucket_models_still_reject_a_non_ack_bucket_setup_reply()
    {
        var hid = new AckingHidDevice { SetupReplyByte = 0x05 };
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, BucketStreamNoRgb, 64);
        Assert.True(hub.Connect());

        Assert.False(hub.UploadLcdImage(new byte[BucketStreamNoRgb.LcdFrameBytes]));
    }

    [Fact]
    public void Elite_2023_still_upload_accepts_a_non_ack_bucket_setup_reply()
    {
        // Firmware 2.x is reported to answer a successful bucket setup with 0x05.
        var hid = new AckingHidDevice { SetupReplyByte = 0x05 };
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, EliteNoRgb, 64);
        Assert.True(hub.Connect());

        Assert.True(hub.UploadLcdImage(new byte[EliteNoRgb.LcdFrameBytes]));
        Assert.Equal(new byte[] { 0x36, 0x03 }, hid.Writes.First(w => w[0] == 0x36)[..2]);
        Assert.Equal(1, lcd.Frames);
    }

    [Fact]
    public void Z3_panel_is_offered_as_a_fifteen_fps_stream()
    {
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(new AckingHidDevice(), Z3, 64);
        Assert.True(hub.Connect());
        Assert.True(hub.HasLcd);

        var info = Assert.Single(new KrakenPanelDiscovery(hub).Discover());
        Assert.Equal(15, info.Profile.Fps);
        Assert.Equal(StreamCodec.RawBgra, info.Profile.Codec);
    }

    [Fact]
    public void Z3_stream_deletes_and_recreates_each_bucket_across_all_sixteen()
    {
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, Z3, 64);
        Assert.True(hub.Connect());
        hid.Writes.Clear();

        var frame = new byte[Z3.LcdFrameBytes];
        for (int i = 0; i < 18; i++)
        {
            Assert.True(hub.PushStreamFrame(frame), $"push {i}");
        }

        // Entry once: cancel, release, clear all sixteen.
        var lcdCommands = hid.Writes.Where(w => w[0] is 0x32 or 0x36 or 0x38).Select(w => w[..9]).ToList();
        Assert.Single(lcdCommands, w => w[0] == 0x36 && w[1] == 0x03);
        Assert.Equal(new byte[] { 0x36, 0x03 }, lcdCommands[0][..2]);
        Assert.Equal(new byte[] { 0x38, 0x01, 0x02, 0x00 }, lcdCommands[1][..4]);
        for (int b = 0; b < 16; b++)
        {
            Assert.Equal(new byte[] { 0x32, 0x02, (byte)b }, lcdCommands[2 + b][..3]);
        }

        // Then per frame: delete twice, setup at bucket * 400 pages, start, end, show.
        var perFrame = lcdCommands.Skip(18).ToList();
        Assert.Equal(18 * 6, perFrame.Count);
        for (int f = 0; f < 18; f++)
        {
            int b = f % 16;
            int start = b * 400;
            var cmds = perFrame.Skip(f * 6).Take(6).ToList();
            Assert.Equal(new byte[] { 0x32, 0x02, (byte)b }, cmds[0][..3]);
            Assert.Equal(new byte[] { 0x32, 0x02, (byte)b }, cmds[1][..3]);
            Assert.Equal(new byte[] { 0x32, 0x01, (byte)b, (byte)(b + 1), (byte)(start & 0xFF), (byte)(start >> 8), 0x90, 0x01, 0x01 }, cmds[2]);
            Assert.Equal(new byte[] { 0x36, 0x01, (byte)b }, cmds[3][..3]);
            Assert.Equal(new byte[] { 0x36, 0x02 }, cmds[4][..2]);
            Assert.Equal(new byte[] { 0x38, 0x01, 0x04, (byte)b }, cmds[5][..4]);
        }

        Assert.Equal(18, lcd.Frames);
        Assert.All(lcd.Headers, h =>
        {
            Assert.Equal(KrakenProtocol.BulkFormatRgba8888, h[12]);
            Assert.Equal(Z3.LcdFrameBytes, BitConverter.ToInt32(h, 16));
        });
        Assert.Equal(KrakenDisplayMode.Bucket, hub.Snapshot.DisplayMode);
    }

    [Fact]
    public void Z3_stream_slows_retries_only_after_repeated_rejections()
    {
        var hid = new AckingHidDevice { SetupReplyByte = 0x05 };
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, Z3, 64);
        Assert.True(hub.Connect());
        long now = 10_000;
        hub.Clock = () => now;
        var frame = new byte[Z3.LcdFrameBytes];
        hid.Writes.Clear();

        for (int i = 0; i < 3; i++)
        {
            Assert.False(hub.PushStreamFrame(frame));
        }
        Assert.Equal(3, hid.Writes.Count(w => w[0] == 0x32 && w[1] == 0x01));
        hid.SetupReplyByte = KrakenProtocol.AckOk;
        hid.Writes.Clear();
        now += 999;
        Assert.False(hub.PushStreamFrame(frame));
        Assert.Empty(hid.Writes);

        now += 1;
        Assert.True(hub.PushStreamFrame(frame));
    }

    [Fact]
    public void Z3_stream_reenters_after_a_still_upload()
    {
        var hid = new AckingHidDevice();
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, Z3, 64);
        Assert.True(hub.Connect());
        var frame = new byte[Z3.LcdFrameBytes];

        Assert.True(hub.PushStreamFrame(frame));
        Assert.True(hub.UploadLcdImage(new byte[Z3.LcdFrameBytes]));
        hid.Writes.Clear();
        Assert.True(hub.PushStreamFrame(frame));

        // The still wiped the table, so the stream starts over from bucket 0.
        Assert.Single(hid.Writes, w => w[0] == 0x36 && w[1] == 0x03);
        Assert.Equal(0, hid.Writes.Single(w => w[0] == 0x32 && w[1] == 0x01)[2]);
    }

    [Fact]
    public void Z3_still_upload_cancels_then_clears_every_bucket()
    {
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, Z3, 64);
        Assert.True(hub.Connect());
        hid.Writes.Clear();

        Assert.True(hub.UploadLcdImage(new byte[Z3.LcdFrameBytes]));

        var lcdCommands = hid.Writes.Where(w => w[0] is 0x32 or 0x36 or 0x38).ToList();
        Assert.Equal(new byte[] { 0x36, 0x03 }, lcdCommands[0][..2]);
        Assert.Equal(new byte[] { 0x38, 0x01, 0x02, 0x00 }, lcdCommands[1][..4]);
        Assert.Equal(16, lcdCommands.Count(w => w[0] == 0x32 && w[1] == 0x02));
        // Pages cover the pixels plus the bulk header.
        var setup = Assert.Single(lcdCommands, w => w[0] == 0x32 && w[1] == 0x01);
        Assert.Equal(new byte[] { 0x32, 0x01, 0x00, 0x01, 0x00, 0x00, 0x91, 0x01, 0x01 }, setup[..9]);
        Assert.Equal(new byte[] { 0x38, 0x01, 0x04, 0x00 }, lcdCommands[^1][..4]);
        var header = Assert.Single(lcd.Headers);
        Assert.Equal(KrakenProtocol.BulkFormatRgba8888, header[12]);
        Assert.Equal(Z3.LcdFrameBytes, BitConverter.ToInt32(header, 16));
    }

    [Fact]
    public void Z3_gif_upload_sends_the_file_whole_as_format_01()
    {
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, Z3, 64);
        Assert.True(hub.Connect());
        var gif = new byte[3000];
        var unrotated = new byte[2900];

        Assert.Equal(KrakenGifUpload.Shown, hub.UploadLcdGif(gif, unrotated, hub.Snapshot.LcdOrientationQuarterTurns));

        var header = Assert.Single(lcd.Headers);
        Assert.Equal(KrakenProtocol.BulkFormatGif, header[12]);
        Assert.Equal(gif.Length, BitConverter.ToInt32(header, 16));
        Assert.Equal(gif.Length, lcd.ChunkSizes.Sum());
        var setup = Assert.Single(hid.Writes, w => w[0] == 0x32 && w[1] == 0x01);
        Assert.Equal(KrakenProtocol.PagesFor(gif.Length), setup[6] | (setup[7] << 8));
        Assert.Same(unrotated, hub.ShownLcdGif);
    }

    [Fact]
    public void Rotation_after_a_gif_does_not_bring_back_the_earlier_still()
    {
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(new AckingHidDevice(), Z3, 64);
        Assert.True(hub.Connect());
        Assert.True(hub.UploadLcdImage(new byte[Z3.LcdFrameBytes]));
        Assert.Equal(KrakenGifUpload.Shown, hub.UploadLcdGif(new byte[100], new byte[100], hub.Snapshot.LcdOrientationQuarterTurns));

        Assert.True(hub.SetLcdBacklight(100, (hub.Snapshot.LcdOrientationQuarterTurns + 1) & 3));

        Assert.Equal(2, lcd.Frames);
    }

    [Fact]
    public void A_still_upload_forgets_the_gif()
    {
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(new AckingHidDevice(), Z3, 64);
        Assert.True(hub.Connect());
        Assert.Equal(KrakenGifUpload.Shown, hub.UploadLcdGif(new byte[100], new byte[100], hub.Snapshot.LcdOrientationQuarterTurns));

        Assert.True(hub.UploadLcdImage(new byte[Z3.LcdFrameBytes]));

        Assert.Null(hub.ShownLcdGif);
    }

    [Fact]
    public void A_gif_rotated_for_an_old_orientation_is_stale()
    {
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(new AckingHidDevice(), Z3, 64);
        Assert.True(hub.Connect());

        var result = hub.UploadLcdGif(new byte[100], new byte[100], hub.Snapshot.LcdOrientationQuarterTurns + 1);

        Assert.Equal(KrakenGifUpload.Stale, result);
        Assert.Equal(0, lcd.Frames);
    }

    [Fact]
    public void A_rotation_redo_does_not_replace_a_newer_gif()
    {
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(new AckingHidDevice(), Z3, 64);
        Assert.True(hub.Connect());
        int turns = hub.Snapshot.LcdOrientationQuarterTurns;
        var older = new byte[100];
        var newer = new byte[100];
        Assert.Equal(KrakenGifUpload.Shown, hub.UploadLcdGif(new byte[100], older, turns));
        Assert.Equal(KrakenGifUpload.Shown, hub.UploadLcdGif(new byte[100], newer, turns));

        var result = hub.UploadLcdGif(new byte[100], older, turns, onlyIfShown: true);

        Assert.Equal(KrakenGifUpload.Stale, result);
        Assert.Equal(2, lcd.Frames);
        Assert.Same(newer, hub.ShownLcdGif);
    }

    [Fact]
    public void Gif_upload_is_refused_where_the_pump_cannot_play_one()
    {
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(new AckingHidDevice(), BucketStreamNoRgb, 64);
        Assert.True(hub.Connect());

        Assert.Equal(KrakenGifUpload.Failed, hub.UploadLcdGif(new byte[100], new byte[100], hub.Snapshot.LcdOrientationQuarterTurns));
        Assert.Equal(0, lcd.Frames);
    }

    [Fact]
    public void Bucket_model_still_upload_sends_no_cancel()
    {
        var hid = new AckingHidDevice();
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(hid, BucketStreamNoRgb, 64);
        Assert.True(hub.Connect());

        Assert.True(hub.UploadLcdImage(new byte[BucketStreamNoRgb.LcdFrameBytes]));

        Assert.DoesNotContain(hid.Writes, w => w[0] == 0x36 && w[1] == 0x03);
    }

    [Fact]
    public void Discovered_panel_carries_the_cooler_device_id_as_family()
    {
        // The web merges a streamed panel into the curated device row its family names.
        var hub = new KrakenHub(new FixedLcdFactory(new RecordingLcdTransport()));
        hub.Attach(new AckingHidDevice(), EliteNoRgb, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());

        var info = Assert.Single(new KrakenPanelDiscovery(hub).Discover());

        Assert.Equal(KrakenHub.DeviceId, info.Profile.Family);
        Assert.Equal(KrakenHub.DeviceId, info.Profile.BuildCapabilities().Family);
    }

    /// <summary>Answers every command with its ack report: id+1, same sub-command, [14]=1.</summary>
    private sealed class AckingHidDevice : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x1E71;
        public int ProductId => 0x300C;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public byte SetupReplyByte { get; set; } = KrakenProtocol.AckOk;
        public byte FirmwareMajor { get; init; } = 2;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(copy[0] + 1);
            reply[1] = copy[1];
            reply[14] = copy[0] == 0x32 && copy[1] == 0x01 ? SetupReplyByte : KrakenProtocol.AckOk;
            if (copy[0] == 0x10)
            {
                reply[0x11] = FirmwareMajor;
                reply[0x12] = 1;
            }
            _replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var r = _replies.Dequeue();
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private sealed class RecordingLcdTransport : IKrakenLcdTransport
    {
        // A header write starts a frame; the pixel chunks that follow belong to it.
        public int Frames { get; private set; }
        public List<byte[]> Headers { get; } = new();
        public List<int> ChunkSizes { get; } = new();
        public byte[] LastPayloadFirstBytes { get; private set; } = Array.Empty<byte>();
        public bool Write(ReadOnlySpan<byte> data)
        {
            if (data.Length == 20)
            {
                Frames++;
                Headers.Add(data.ToArray());
            }
            else
            {
                ChunkSizes.Add(data.Length);
                LastPayloadFirstBytes = data[..Math.Min(2, data.Length)].ToArray();
            }
            return true;
        }
        public void Dispose() { }
    }

    private sealed class FixedLcdFactory : IKrakenLcdTransportFactory
    {
        private readonly IKrakenLcdTransport _lcd;
        public FixedLcdFactory(IKrakenLcdTransport lcd) { _lcd = lcd; }
        public IKrakenLcdTransport? Open(string? serial) => _lcd;
    }
}

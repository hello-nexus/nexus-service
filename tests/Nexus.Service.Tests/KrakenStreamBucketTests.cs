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
    // ack what it is asked. The 2023 Kraken streams through buckets; the 2023 Elite does not.
    private static readonly KrakenModel BucketStreamNoRgb = KrakenModel.Find(0x300E)!;
    private static readonly KrakenModel EliteNoRgb = KrakenModel.Find(0x300C)!;

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
        // CAM's one-time entry runs once per stream, not per frame.
        Assert.Single(hid.Writes, w => w[0] == 0x36 && w[1] == 0x03);

        int rawBytes = EliteNoRgb.LcdWidth * EliteNoRgb.LcdHeight * 3;
        Assert.Equal(3, lcd.Frames);
        Assert.All(lcd.Headers, h =>
        {
            Assert.Equal(KrakenProtocol.BulkFormatBgr888, h[12]);
            Assert.Equal(rawBytes, BitConverter.ToInt32(h, 16));
        });
        Assert.Equal(3 * rawBytes, lcd.ChunkSizes.Sum());
        // CAM's transfer size, which divides a full frame evenly.
        Assert.All(lcd.ChunkSizes, n => Assert.Equal(245_760, n));
        Assert.Equal(KrakenDisplayMode.Liquid, hub.Snapshot.DisplayMode);
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

        public byte SetupReplyByte { get; init; } = KrakenProtocol.AckOk;
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

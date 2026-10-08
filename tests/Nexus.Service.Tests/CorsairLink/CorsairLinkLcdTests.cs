using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkLcdTests
{
    // -------------------------------------------------------------------------
    // Chunk-header tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ChunkBuilder_header_bytes_correct()
    {
        var buf = new byte[8];
        CorsairLinkLcd.FillChunkHeader(buf, chunkIndex: 3, dataLen: 512, isLast: false);

        Assert.Equal(0x02, buf[0]);
        Assert.Equal(0x05, buf[1]);
        Assert.Equal(0x01, buf[2]);
        Assert.Equal(0x00, buf[3]); // not last
        Assert.Equal(0x03, buf[4]); // chunkIndex 3
        Assert.Equal(0x00, buf[5]);
        Assert.Equal(0x00, buf[6]); // 512 & 0xFF
        Assert.Equal(0x02, buf[7]); // 512 >> 8
    }

    [Theory]
    [InlineData(true, 0x01)]
    [InlineData(false, 0x00)]
    public void ChunkBuilder_last_chunk_sets_flag(bool isLast, byte expected)
    {
        var buf = new byte[8];
        CorsairLinkLcd.FillChunkHeader(buf, chunkIndex: 0, dataLen: 100, isLast: isLast);
        Assert.Equal(expected, buf[3]);
    }

    [Fact]
    public void ChunkBuilder_exact_multiple_capped_to_1015()
    {
        // A 1016-byte remaining payload must be capped so the firmware latch fires.
        var size = CorsairLinkLcd.ComputeChunkSize(offset: 0, totalLength: 1016, isLast: true);
        Assert.Equal(1015, size);
    }

    [Fact]
    public void ChunkBuilder_partial_last_returned_as_is()
    {
        // 2000 - 1016 = 984, which is below MaxPayloadPerChunk, so no cap.
        var size = CorsairLinkLcd.ComputeChunkSize(offset: 1016, totalLength: 2000, isLast: true);
        Assert.Equal(984, size);
    }

    [Fact]
    public void ChunkBuilder_chunk_index_wraps_at_256()
    {
        var buf = new byte[8];

        CorsairLinkLcd.FillChunkHeader(buf, chunkIndex: 256, dataLen: 10, isLast: false);
        Assert.Equal(0x00, buf[4]); // 256 & 0xFF = 0

        CorsairLinkLcd.FillChunkHeader(buf, chunkIndex: 257, dataLen: 10, isLast: false);
        Assert.Equal(0x01, buf[4]); // 257 & 0xFF = 1
    }

    [Theory]
    [InlineData(1000, 0xE8, 0x03)]
    [InlineData(1015, 0xF7, 0x03)]
    [InlineData(1, 0x01, 0x00)]
    public void ChunkBuilder_payload_length_field_matches_data(int dataLen, byte expectedLo, byte expectedHi)
    {
        var buf = new byte[8];
        CorsairLinkLcd.FillChunkHeader(buf, chunkIndex: 0, dataLen: dataLen, isLast: true);
        Assert.Equal(expectedLo, buf[6]);
        Assert.Equal(expectedHi, buf[7]);
    }

    // -------------------------------------------------------------------------
    // Feature report layout tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void FeatureReport_brightness_layout(byte value)
    {
        var report = CorsairLinkLcd.BuildBrightnessReport(value);
        Assert.Equal(4, report.Length);
        Assert.Equal(0x03, report[0]);
        Assert.Equal(0x0b, report[1]);
        Assert.Equal(value, report[2]);
        Assert.Equal(0x01, report[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FeatureReport_rotation_layout(byte value)
    {
        var report = CorsairLinkLcd.BuildRotationReport(value);
        Assert.Equal(4, report.Length);
        Assert.Equal(0x03, report[0]);
        Assert.Equal(0x0c, report[1]);
        Assert.Equal(value, report[2]);
        Assert.Equal(0x01, report[3]);
    }

    [Fact]
    public void SendFrame_emits_1024_byte_frame_with_report_id_0x02()
    {
        var (lcd, device) = AttachLcd();
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });

        var frame = Assert.Single(device.Writes);
        Assert.Equal(1024, frame.Length);    // not 1025; no leading null report-id byte
        Assert.Equal(0x02, frame[0]);         // HID report id
        Assert.Equal(0x05, frame[1]);
        Assert.Equal(0x01, frame[2]);
        Assert.Equal(0x01, frame[3]);         // single chunk is the last chunk
        Assert.Equal(0x00, frame[4]);         // chunk index 0
        Assert.Equal(0x00, frame[5]);
        Assert.Equal(0x03, frame[6]);         // length 3, LE lo
        Assert.Equal(0x00, frame[7]);         // LE hi
        Assert.Equal(0xFF, frame[8]);         // payload at offset 8
        Assert.Equal(0xD8, frame[9]);
        Assert.Equal(0xFF, frame[10]);
    }

    [Fact]
    public void SendFrame_chunks_large_jpeg_with_indices_and_last_flag()
    {
        var (lcd, device) = AttachLcd();
        var jpeg = new byte[1020]; // > 1016 -> two chunks (1016 + 4)
        for (var i = 0; i < jpeg.Length; i++) jpeg[i] = (byte)(i & 0xFF);
        lcd.SendFrame(jpeg);

        Assert.Equal(2, device.Writes.Count);
        var c0 = device.Writes[0];
        Assert.Equal(1024, c0.Length);
        Assert.Equal(0x02, c0[0]);
        Assert.Equal(0x00, c0[3]); // not last
        Assert.Equal(0x00, c0[4]); // index 0
        Assert.Equal(0xF8, c0[6]); // 1016 LE lo
        Assert.Equal(0x03, c0[7]); // 1016 LE hi
        var c1 = device.Writes[1];
        Assert.Equal(0x01, c1[3]); // last
        Assert.Equal(0x01, c1[4]); // index 1
        Assert.Equal(0x04, c1[6]); // 4 LE lo
        Assert.Equal(0x00, c1[7]);
    }

    [Fact]
    public void Detach_sends_shutdown_sequence_via_production_path()
    {
        var (lcd, device) = AttachLcd();
        // Only glass Nexus actually painted is handed back; a frame is what claims it.
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });
        lcd.Detach();

        Assert.Equal(3, device.Features.Count);
        Assert.Equal(new byte[] { 0x03, 0x1e, 0x01, 0x01 }, device.Features[0]);
        Assert.Equal(new byte[] { 0x03, 0x1d, 0x00, 0x01 }, device.Features[1]);
        Assert.Equal(new byte[] { 0x03, 0x0b, 0x64, 0x01 }, device.Features[2]);
        Assert.False(lcd.HasDevice);
    }

    [Fact]
    public void Detach_without_hand_back_leaves_the_screen_to_the_other_program()
    {
        var (lcd, device) = AttachLcd();
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });

        lcd.Detach(handBack: false);

        Assert.Empty(device.Features);
        Assert.False(lcd.HasDevice);
    }

    // -------------------------------------------------------------------------
    // LCD HID discovery test
    // -------------------------------------------------------------------------

    [Fact]
    public void LcdDiscovery_matches_by_serial_and_output_report_size()
    {
        const string targetSerial = "ABCDEF123456";
        var chain = new CorsairLinkDevice[]
        {
            new() { Channel = 1, Type = 6, Serial = targetSerial, Name = "LCD AIO" },
        };

        // Literal 1024 (not the SUT const) so a wrong const is caught; the 1025 decoy
        // carries the right serial and would be opened if the size filter were 1025.
        var wrongSerial = new HidDeviceInfo
        {
            VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid,
            Path = "/dev/wrong-serial", Serial = "DIFFERENT_SERIAL", OutputReportByteLength = 1024,
        };
        var wrongSize = new HidDeviceInfo
        {
            VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid,
            Path = "/dev/wrong-size", Serial = targetSerial, OutputReportByteLength = 1025,
        };
        var correct = new HidDeviceInfo
        {
            VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid,
            Path = "/dev/correct", Serial = targetSerial, OutputReportByteLength = 1024,
        };

        var device = new RecordingHidDevice();
        var hid = new RecordingHidEnumerator(new[] { wrongSerial, wrongSize, correct }, device);
        var lcd = new CorsairLinkLcd();
        lcd.DiscoverAndAttach(chain, hid);

        Assert.True(lcd.HasDevice);
        Assert.Equal("/dev/correct", hid.LastOpenedPath);
        Assert.Empty(device.Features); // OLH sends no power-on during init

        lcd.Detach();
        Assert.False(lcd.HasDevice);
    }

    // -------------------------------------------------------------------------
    // Media library meta roundtrip
    // -------------------------------------------------------------------------

    [Fact]
    public void LcdMediaLibrary_meta_roundtrip()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"nexus-lcd-test-{Guid.NewGuid()}");
        try
        {
            var lib = new CorsairLinkLcdMediaLibrary(tmpDir);
            var item = new LcdMediaItem
            {
                Id = "test-item",
                Name = "test.jpg",
                Type = "static",
                Frames = 1,
                Delays = Array.Empty<int>(),
                ImportedAtUnixMs = 1_234_567_890_000L,
            };

            // Write a dummy frame file so ListItems considers the entry complete.
            var itemDir = Path.Combine(tmpDir, item.Id);
            Directory.CreateDirectory(itemDir);
            File.WriteAllBytes(Path.Combine(itemDir, "frame-0000.jpg"), Array.Empty<byte>());
            lib.SaveMeta(item);

            var items = lib.ListItems();
            Assert.Single(items);
            Assert.Equal("test-item", items[0].Id);
            Assert.Equal("test.jpg", items[0].Name);
            Assert.Equal("static", items[0].Type);
            Assert.Equal(1, items[0].Frames);
            Assert.Equal(1_234_567_890_000L, items[0].ImportedAtUnixMs);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // GIF frame delay parsing
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseGifFrameDelays_extracts_centiseconds_as_ms()
    {
        // Two GCE blocks: delay = 5cs (50ms) and delay = 10cs (100ms).
        var gif = new byte[]
        {
            // GCE 1: delay = 5 centiseconds
            0x21, 0xF9, 0x04, 0x00, 0x05, 0x00, 0x00, 0x00,
            // GCE 2: delay = 10 centiseconds
            0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,
        };

        var delays = CorsairLinkLcdMediaLibrary.ParseGifFrameDelays(gif);

        Assert.Equal(2, delays.Length);
        Assert.Equal(50, delays[0]);
        Assert.Equal(100, delays[1]);
    }

    [Fact]
    public void ParseGifFrameDelays_minimum_10ms()
    {
        // delay = 0 centiseconds -> minimum 10ms.
        var gif = new byte[]
        {
            0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        var delays = CorsairLinkLcdMediaLibrary.ParseGifFrameDelays(gif);

        Assert.Single(delays);
        Assert.Equal(10, delays[0]);
    }

    // -------------------------------------------------------------------------
    // Worker frame streaming
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LcdWorker_still_uses_single_frame()
    {
        var sent = new List<byte[]>();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF };
        var image = new LcdImageData
        {
            Id = "still",
            Frames = new[] { new LcdFrame { JpegBytes = jpeg, DelayMs = 0 } },
        };

        using var cts = new CancellationTokenSource();
        await CorsairLinkLcdWorker.StreamImageAsync(
            image,
            mem => sent.Add(mem.ToArray()),
            cts.Token);

        Assert.Single(sent);
        Assert.Equal(jpeg, sent[0]);
    }

    [Fact]
    public async Task LcdWorker_gif_cycles_all_frames()
    {
        var sent = new List<byte[]>();
        var frames = new[]
        {
            new LcdFrame { JpegBytes = new byte[] { 0xAA }, DelayMs = 100 },
            new LcdFrame { JpegBytes = new byte[] { 0xBB }, DelayMs = 200 },
            new LcdFrame { JpegBytes = new byte[] { 0xCC }, DelayMs = 50 },
        };
        var image = new LcdImageData { Id = "anim", Frames = frames };

        using var cts = new CancellationTokenSource();
        await CorsairLinkLcdWorker.StreamImageAsync(
            image,
            mem => sent.Add(mem.ToArray()),
            cts.Token);

        Assert.Equal(3, sent.Count);
        Assert.Equal(new byte[] { 0xAA }, sent[0]);
        Assert.Equal(new byte[] { 0xBB }, sent[1]);
        Assert.Equal(new byte[] { 0xCC }, sent[2]);
    }

    private static (CorsairLinkLcd lcd, RecordingHidDevice device) AttachLcd()
    {
        var device = new RecordingHidDevice();
        var info = new HidDeviceInfo
        {
            VendorId = CorsairLinkLcd.LcdVendorId,
            ProductId = CorsairLinkLcd.AioPid,
            Path = "/dev/hid-lcd",
            Serial = "S1",
            OutputReportByteLength = 1024,
        };
        var hid = new RecordingHidEnumerator(new[] { info }, device);
        var lcd = new CorsairLinkLcd();
        lcd.DiscoverAndAttach(
            new CorsairLinkDevice[] { new() { Channel = 1, Type = 6, Serial = "S1" } }, hid);
        return (lcd, device);
    }

    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

    /// <summary>Records SetFeature reports and Write frames for wire-level assertions.</summary>
    private sealed class RecordingHidDevice : IHidDevice
    {
        public List<byte[]> Features { get; } = new();
        public List<byte[]> Writes { get; } = new();

        public int VendorId => 0x1B1C;
        public int ProductId => CorsairLinkLcd.AioPid;
        public string Path => "/dev/fake";
        public string? Serial => null;
        public int UsagePage => 0;
        public int Usage => 0;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            Features.Add(report.ToArray());
            return true;
        }

        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            return true;
        }

        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class RecordingHidEnumerator : IHidEnumerator
    {
        private readonly IReadOnlyList<HidDeviceInfo> _infos;
        private readonly RecordingHidDevice _device;
        public string? LastOpenedPath { get; private set; }

        public RecordingHidEnumerator(IReadOnlyList<HidDeviceInfo> infos, RecordingHidDevice device)
        {
            _infos = infos;
            _device = device;
        }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
        {
            var result = new List<HidDeviceInfo>();
            foreach (var info in _infos)
            {
                if (info.VendorId == vendorId && info.ProductId == productId)
                {
                    result.Add(info);
                }
            }
            return result;
        }

        public IReadOnlyList<HidDeviceInfo> FindAll() => _infos;

        public IHidDevice? Open(string path, bool forInput = false)
        {
            LastOpenedPath = path;
            return _device;
        }
    }
}

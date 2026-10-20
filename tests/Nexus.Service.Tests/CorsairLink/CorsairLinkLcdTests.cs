using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Host mode on before the first frame, then the hand-back to the coolant screen.
        Assert.Equal(4, device.Features.Count);
        Assert.Equal(new byte[] { 0x03, 0x1d, 0x01, 0x01 }, device.Features[0]);
        Assert.Equal(new byte[] { 0x03, 0x1e, 0x01, 0x01 }, device.Features[1]);
        Assert.Equal(new byte[] { 0x03, 0x1d, 0x00, 0x01 }, device.Features[2]);
        Assert.Equal(new byte[] { 0x03, 0x0b, 0x64, 0x01 }, device.Features[3]);
        Assert.False(lcd.HasDevice);
    }

    [Fact]
    public void ReleaseForShutdown_hands_back_and_refuses_a_late_reattach()
    {
        var (lcd, device) = AttachLcd();
        lcd.ShowsStoredImage = true;
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });

        lcd.ReleaseForShutdown(handBack: true);

        Assert.Equal(new byte[] { 0x03, 0x1e, 0x00, 0x01 }, device.Features[1]);
        Assert.Equal(new byte[] { 0x03, 0x1d, 0x00, 0x01 }, device.Features[2]);
        var hid = new RecordingHidEnumerator(new[]
        {
            new HidDeviceInfo { VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid, Path = "/dev/hid-lcd", Serial = "S1", OutputReportByteLength = 1024 },
        }, device);
        lcd.DiscoverAndAttach(new CorsairLinkDevice[] { new() { Channel = 1, Type = 6, Serial = "S1" } }, hid);
        Assert.False(lcd.HasDevice);
    }

    [Fact]
    public void Detach_hands_back_to_the_stored_image_when_one_is_selected()
    {
        var (lcd, device) = AttachLcd();
        lcd.ShowsStoredImage = true;
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });
        lcd.Detach();

        Assert.Equal(new byte[] { 0x03, 0x1e, 0x00, 0x01 }, device.Features[1]);
        Assert.Equal(new byte[] { 0x03, 0x1d, 0x00, 0x01 }, device.Features[2]);
    }

    [Fact]
    public void Host_mode_is_entered_once_and_again_after_a_screen_select()
    {
        var (lcd, device) = AttachLcd();
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });
        Assert.Single(device.Features);

        Assert.True(lcd.SelectHandBackScreen(storedImage: true));

        // iCUE's select sequence, then host mode back on because Nexus paints the glass.
        Assert.Equal(
            new[] { "031d0101", "031d0101", "031b0001", "031e0001", "031d0001", "031d0101" },
            device.Features.Select(f => Convert.ToHexString(f).ToLowerInvariant()));
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });
        Assert.Equal(6, device.Features.Count);
    }

    [Fact]
    public void A_hand_back_without_resuming_leaves_host_mode_off_and_asks_for_no_repaint()
    {
        var (lcd, device) = AttachLcd();
        var owner = new object();
        lcd.ClaimStream(owner);
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });

        Assert.True(lcd.SelectHandBackScreen(storedImage: false, resumePainting: false));

        Assert.Equal("031d0001", Convert.ToHexString(device.Features[^1]).ToLowerInvariant());
        Assert.False(lcd.TakeRepaintRequest(owner));
    }

    [Fact]
    public void A_screen_select_asks_the_owning_stream_to_repaint_once()
    {
        var (lcd, _) = AttachLcd();
        var owner = new object();
        lcd.ClaimStream(owner);
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });
        Assert.False(lcd.TakeRepaintRequest(owner));

        lcd.SelectHandBackScreen(storedImage: true);

        Assert.False(lcd.TakeRepaintRequest(new object()));
        Assert.True(lcd.TakeRepaintRequest(owner));
        Assert.False(lcd.TakeRepaintRequest(owner));
    }

    [Fact]
    public void A_run_of_failed_writes_drops_the_screen_so_the_next_discovery_reopens_it()
    {
        var (lcd, device) = AttachLcd();
        Assert.True(lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF }));

        // One miss keeps the handle; a run of them drops it.
        device.Gone = true;
        Assert.False(lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.True(lcd.HasDevice);
        Assert.False(lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.False(lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.False(lcd.HasDevice);

        // Re-enumerated: attach again, and the first frame turns host mode back on.
        device.Gone = false;
        var hid = new RecordingHidEnumerator(new[]
        {
            new HidDeviceInfo { VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid, Path = "/dev/hid-lcd", Serial = "S1", OutputReportByteLength = 1024 },
        }, device);
        lcd.DiscoverAndAttach(new CorsairLinkDevice[] { new() { Channel = 1, Type = 6, Serial = "S1" } }, hid);
        Assert.True(lcd.HasDevice);
        device.Features.Clear();
        Assert.True(lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.Equal(new[] { new byte[] { 0x03, 0x1d, 0x01, 0x01 } }, device.Features);
    }

    [Fact]
    public void Detach_without_hand_back_leaves_the_screen_to_the_other_program()
    {
        var (lcd, device) = AttachLcd();
        lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });

        lcd.Detach(handBack: false);

        Assert.Equal(new[] { new byte[] { 0x03, 0x1d, 0x01, 0x01 } }, device.Features);
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

    [Fact]
    public void LcdDiscovery_takes_the_sole_lcd_when_serials_differ()
    {
        // TITAN RX LCD on the Y70: chain serial and USB serial never match.
        var chain = new CorsairLinkDevice[]
        {
            new() { Channel = 13, Type = 6, Serial = "4E0CF5679521C109ED49AD3D" },
        };
        var lcdHid = new HidDeviceInfo
        {
            VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid,
            Path = "/dev/titan-lcd", Serial = "A9IHT509000CJJ", OutputReportByteLength = 1024,
        };

        var hid = new RecordingHidEnumerator(new[] { lcdHid }, new RecordingHidDevice());
        var lcd = new CorsairLinkLcd();
        lcd.DiscoverAndAttach(chain, hid);

        Assert.True(lcd.HasDevice);
        Assert.Equal("/dev/titan-lcd", hid.LastOpenedPath);
    }

    [Fact]
    public void LcdDiscovery_does_not_guess_between_two_unmatched_lcds()
    {
        var chain = new CorsairLinkDevice[] { new() { Channel = 13, Type = 6, Serial = "CHAIN" } };
        HidDeviceInfo Lcd(string path, string serial) => new()
        {
            VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid,
            Path = path, Serial = serial, OutputReportByteLength = 1024,
        };

        var hid = new RecordingHidEnumerator(new[] { Lcd("/dev/a", "A"), Lcd("/dev/b", "B") }, new RecordingHidDevice());
        var lcd = new CorsairLinkLcd();
        lcd.DiscoverAndAttach(chain, hid);

        Assert.False(lcd.HasDevice);
        Assert.Null(hid.LastOpenedPath);
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
        public bool Gone { get; set; }

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
            if (Gone) return false;
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

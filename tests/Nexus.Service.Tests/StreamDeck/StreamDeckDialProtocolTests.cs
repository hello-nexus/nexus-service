using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Byte vectors for the expanded gen2 family (dials, strips, info screen,
/// rings, touch keys). Sources: Elgato HID docs, node-elgato-stream-deck
/// (Studio, Galleon), and real reports captured from Elgato 7.6.0 driving the
/// bench Plus (bench capture 2026-10-08). These verify the builders and
/// decoders, not the device.
/// </summary>
public class StreamDeckDialProtocolTests
{
    private static readonly StreamDeckModel Plus = StreamDeckModels.ByProductId(0x0084)!;
    private static readonly StreamDeckModel PlusXl = StreamDeckModels.ByProductId(0x00c6)!;
    private static readonly StreamDeckModel Neo = StreamDeckModels.ByProductId(0x009a)!;
    private static readonly StreamDeckModel Studio = StreamDeckModels.ByProductId(0x00aa)!;
    private static readonly StreamDeckModel Galleon = StreamDeckModels.ByProductId(0x2b18)!;

    private static byte[] Hex(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    private static byte[] Padded(string hex, int length)
    {
        var bytes = new byte[length];
        Hex(hex).CopyTo(bytes, 0);
        return bytes;
    }

    // ── Model rows ──

    [Theory]
    [InlineData(0x0084, 8, 2, 4, 120, 4, "none", "none")]
    [InlineData(0x00c6, 36, 4, 9, 112, 6, "rot90Ccw", "rot90Ccw")]
    [InlineData(0x009a, 8, 2, 4, 96, 0, "flipBoth", "flipBoth")]
    [InlineData(0x00aa, 32, 2, 16, 112, 2, "none", "none")]
    [InlineData(0x2b18, 12, 4, 3, 160, 2, "none", "none")]
    public void ExpandedRows_MatchTheDocumentedLayout(
        int pid, int keys, int rows, int columns, int keyPx, int encoders, string transform, string screenTransform)
    {
        var model = StreamDeckModels.ByProductId(pid)!;
        Assert.Equal(keys, model.KeyCount);
        Assert.Equal(rows * columns, model.KeyCount);
        Assert.Equal(rows, model.Rows);
        Assert.Equal(columns, model.Columns);
        Assert.Equal(keyPx, model.KeyPixelSize);
        Assert.Equal(encoders, model.Encoders);
        Assert.Equal(transform, model.Transform);
        Assert.Equal(screenTransform, model.ScreenTransform);
        Assert.True(model.HasExpandedInput);
        Assert.Equal(512, model.InputReportBufferLength);
        Assert.False(model.Verified);
    }

    [Fact]
    public void ExpandedRows_ScreensTouchKeysAndPlacement()
    {
        Assert.Equal(new StreamDeckScreen(800, 100, StreamDeckScreenKind.TouchStrip), Plus.Screen);
        Assert.Equal(new StreamDeckScreen(1200, 100, StreamDeckScreenKind.TouchStrip), PlusXl.Screen);
        Assert.Equal(new StreamDeckScreen(248, 58, StreamDeckScreenKind.InfoScreen), Neo.Screen);
        Assert.Equal(new StreamDeckScreen(720, 384, StreamDeckScreenKind.DialScreen), Galleon.Screen);
        Assert.Null(Studio.Screen);
        Assert.Equal(2, Neo.TouchKeys);
        Assert.Equal(StreamDeckDialPlacement.Below, Plus.DialPlacement);
        Assert.Equal(StreamDeckDialPlacement.Above, Galleon.DialPlacement);
        Assert.Equal(StreamDeckDialPlacement.Sides, Studio.DialPlacement);
        Assert.Equal(StreamDeckDialPlacement.None, Neo.DialPlacement);
        Assert.Equal(144, Studio.KeyWidth);
        Assert.Equal(112, Studio.KeyHeight);
        Assert.Equal(24, Studio.EncoderRingLeds);
        Assert.Equal(4, Galleon.EncoderRingLeds);
        Assert.Equal(0x1B1C, Galleon.VendorId);
        Assert.Equal(0x0FD9, Plus.VendorId);
    }

    [Fact]
    public void ButtonOnlyModels_KeepTheirSmallInputBuffer()
    {
        var xl = StreamDeckModels.ByProductId(0x006c)!;
        Assert.False(xl.HasExpandedInput);
        Assert.Equal(4 + xl.KeyCount, xl.InputReportBufferLength);
    }

    [Fact]
    public void Galleon_OnlyAcceptsTheStreamDeckCollectionOnInterfaceZero()
    {
        HidDeviceInfo Info(int page, int usage, string path) => new() { VendorId = 0x1B1C, ProductId = 0x2b18, UsagePage = page, Usage = usage, Path = path };

        Assert.True(Galleon.AcceptsCollection(Info(0x0C, 0x01, @"\\?\hid#vid_1b1c&pid_2b18&mi_00#7&abc")));
        Assert.False(Galleon.AcceptsCollection(Info(0x0C, 0x01, @"\\?\hid#vid_1b1c&pid_2b18&mi_02#7&abc")));
        Assert.False(Galleon.AcceptsCollection(Info(0x01, 0x06, @"\\?\hid#vid_1b1c&pid_2b18&mi_00#7&abc")));
        Assert.True(Plus.AcceptsCollection(Info(0x01, 0x06, "any")));
    }

    [Theory]
    [InlineData("iokit:1000766e2")]
    [InlineData("/dev/hidraw4")]
    public void Galleon_OffWindows_NeedsTheStreamDeckUsagePageBecausePathsCarryNoInterface(string path)
    {
        HidDeviceInfo Info(int page, int usage) => new() { VendorId = 0x1B1C, ProductId = 0x2b18, UsagePage = page, Usage = usage, Path = path };

        Assert.True(Galleon.AcceptsCollection(Info(0x0C, 0x01)));
        Assert.False(Galleon.AcceptsCollection(Info(0xFF42, 0x01)));
        Assert.False(Galleon.AcceptsCollection(Info(0x01, 0x06)));
        Assert.False(Galleon.AcceptsCollection(Info(0x0C, 0x02)));
    }

    [Fact]
    public void ShortDialAndTouchReportsAreIgnored()
    {
        Assert.Null(StreamDeckProtocol.DecodeInput(Hex("01 03"), Plus));
        Assert.Null(StreamDeckProtocol.DecodeInput(Hex("01 03 05 00"), Plus));
        Assert.Null(StreamDeckProtocol.DecodeInput(Hex("01 02 0e 00 03"), Plus));
    }

    // ── Input vectors (bench capture 2026-10-08) ──

    [Fact]
    public void Decode_RotateDialThreePlusOne()
    {
        var input = StreamDeckProtocol.DecodeInput(Hex("01 03 05 00 01 00 00 01 00"), Plus)!;

        Assert.Equal(StreamDeckInputKind.DialRotate, input.Kind);
        Assert.Equal(new[] { 0, 0, 1, 0 }, input.DialTicks);
    }

    [Fact]
    public void Decode_RotateCarriesSignedAccumulatedTicks()
    {
        var input = StreamDeckProtocol.DecodeInput(Hex("01 03 05 00 01 f6 0a 00 00"), Plus)!;

        Assert.Equal(new[] { -10, 10, 0, 0 }, input.DialTicks);
    }

    [Fact]
    public void Decode_DialOneDown()
    {
        var input = StreamDeckProtocol.DecodeInput(Hex("01 03 05 00 00 01 00 00 00"), Plus)!;

        Assert.Equal(StreamDeckInputKind.DialPress, input.Kind);
        Assert.Equal(new[] { true, false, false, false }, input.DialDown);
    }

    [Fact]
    public void Decode_Tap()
    {
        var input = StreamDeckProtocol.DecodeInput(Hex("01 02 0e 00 01 01 67 00 32 00 00 00 00 00 00 00 00 00"), Plus)!;

        Assert.Equal(StreamDeckInputKind.Touch, input.Kind);
        Assert.Equal(StreamDeckTouchKind.Tap, input.TouchKind);
        Assert.Equal((103, 50), (input.X, input.Y));
    }

    [Fact]
    public void Decode_LongTouch()
    {
        var input = StreamDeckProtocol.DecodeInput(Hex("01 02 0e 00 02 01 26 01 40 00 00 00 00 00 00 00 00 00"), Plus)!;

        Assert.Equal(StreamDeckTouchKind.Long, input.TouchKind);
        Assert.Equal((294, 64), (input.X, input.Y));
    }

    [Fact]
    public void Decode_FlickCarriesStartAndEnd()
    {
        var input = StreamDeckProtocol.DecodeInput(Hex("01 02 0e 00 03 00 17 02 4b 00 e5 01 40 00 00 00 00 00"), Plus)!;

        Assert.Equal(StreamDeckTouchKind.Flick, input.TouchKind);
        Assert.Equal((535, 75), (input.X, input.Y));
        Assert.Equal((485, 64), (input.X2, input.Y2));
    }

    [Fact]
    public void Decode_KeyReportOnPlusIsAKeySnapshot()
    {
        var report = new byte[4 + Plus.KeyCount];
        report[1] = 0x00;
        report[4 + 5] = 1;

        var input = StreamDeckProtocol.DecodeInput(report, Plus)!;

        Assert.Equal(StreamDeckInputKind.Keys, input.Kind);
        Assert.True(input.Keys[5]);
        Assert.Empty(input.TouchKeys);
    }

    [Fact]
    public void Decode_NeoAppendsTwoTouchKeysAfterTheLcdKeys()
    {
        var report = new byte[4 + Neo.KeyCount + Neo.TouchKeys];
        report[4 + 8 + 1] = 1;

        var input = StreamDeckProtocol.DecodeInput(report, Neo)!;

        Assert.Equal(8, input.Keys.Length);
        Assert.Equal(new[] { false, true }, input.TouchKeys);
    }

    [Fact]
    public void Decode_NfcAndUnknownReportsAreIgnored()
    {
        Assert.Null(StreamDeckProtocol.DecodeInput(Hex("01 04 03 00 41 42 43"), Studio));
        Assert.Null(StreamDeckProtocol.DecodeInput(Hex("01 09 00 00"), Plus));
    }

    [Fact]
    public void Decode_DialAndTouchReportsAreIgnoredOnModelsWithoutThem()
    {
        Assert.Null(StreamDeckProtocol.DecodeInput(Hex("01 03 05 00 01 01 00 00 00"), Neo));
        Assert.Null(StreamDeckProtocol.DecodeInput(Hex("01 02 0e 00 01 01 67 00 32 00 00 00 00 00"), Studio));
    }

    [Fact]
    public void Decode_ButtonOnlyModelsTreatEveryReportAsKeys()
    {
        var xl = StreamDeckModels.ByProductId(0x006c)!;
        var report = new byte[4 + xl.KeyCount];
        report[1] = 0x03;
        report[4] = 1;

        var input = StreamDeckProtocol.DecodeInput(report, xl)!;

        Assert.Equal(StreamDeckInputKind.Keys, input.Kind);
        Assert.True(input.Keys[0]);
    }

    // ── Region image pages (bench capture 2026-10-08) ──

    [Fact]
    public void RegionPages_FullStrip_MatchCapturedHeaders()
    {
        var jpeg = new byte[22999];
        var pages = StreamDeckProtocol.BuildRegionImagePages(jpeg, 0, 0, 800, 100);

        Assert.Equal(23, pages.Count);
        Assert.All(pages, p => Assert.Equal(1024, p.Length));
        Assert.Equal(Hex("02 0c 00 00 00 00 20 03 64 00 00 00 00 f0 03 00"), pages[0][..16]);
        Assert.Equal(Hex("02 0c 00 00 00 00 20 03 64 00 00 15 00 f0 03 00"), pages[21][..16]);
        Assert.Equal(Hex("02 0c 00 00 00 00 20 03 64 00 01 16 00 37 03 00"), pages[22][..16]);
    }

    [Fact]
    public void RegionPages_IconSubRect_MatchCapturedHeaders()
    {
        var jpeg = new byte[2062];
        var pages = StreamDeckProtocol.BuildRegionImagePages(jpeg, 416, 40, 48, 48);

        Assert.Equal(3, pages.Count);
        Assert.Equal(Hex("02 0c a0 01 28 00 30 00 30 00 00 00 00 f0 03 00"), pages[0][..16]);
        Assert.Equal(Hex("02 0c a0 01 28 00 30 00 30 00 00 01 00 f0 03 00"), pages[1][..16]);
        Assert.Equal(Hex("02 0c a0 01 28 00 30 00 30 00 01 02 00 2e 00 00"), pages[2][..16]);
    }

    [Fact]
    public void RegionPages_CarryPayloadAfterTheSixteenByteHeader()
    {
        var jpeg = Enumerable.Range(0, 1500).Select(i => (byte)(i % 251)).ToArray();
        var pages = StreamDeckProtocol.BuildRegionImagePages(jpeg, 0, 0, 200, 100);

        Assert.Equal(jpeg[..1008], pages[0][16..1024]);
        Assert.Equal(jpeg[1008..], pages[1][16..(16 + 492)]);
    }

    [Fact]
    public void NeoInfoScreenPages_UseTheEightByteZeroBHeader()
    {
        var jpeg = new byte[2100];
        var pages = StreamDeckProtocol.BuildNeoInfoScreenPages(jpeg);

        Assert.Equal(3, pages.Count);
        Assert.Equal(Hex("02 0b 00 00 f8 03 00 00"), pages[0][..8]);
        Assert.Equal(Hex("02 0b 00 01 44 00 02 00"), pages[2][..8]);
    }

    // ── Feature reports (bench capture 2026-10-08 + docs) ──

    [Fact]
    public void FillScreenBlack_MatchesTheCapturedInitReport()
    {
        Assert.Equal(Padded("03 05 00 00 00", 32), StreamDeckProtocol.BuildGen2FillScreenFeature(0, 0, 0));
    }

    [Fact]
    public void SleepOff_MatchesTheCapturedInitReport()
    {
        Assert.Equal(Padded("03 0d 00 00 00 00", 32), StreamDeckProtocol.BuildGen2SleepDurationFeature(0));
    }

    [Fact]
    public void SleepDuration_IsInt32LittleEndian()
    {
        Assert.Equal(Padded("03 0d 2c 01 00 00", 32), StreamDeckProtocol.BuildGen2SleepDurationFeature(300));
    }

    [Fact]
    public void FillKey_AddressesNeoTouchKeysAtEightAndNine()
    {
        Assert.Equal(Padded("03 06 08 ff 80 00", 32), StreamDeckProtocol.BuildGen2FillKeyFeature(8, 0xff, 0x80, 0));
        Assert.Equal(Padded("03 06 09 01 02 03", 32), StreamDeckProtocol.BuildGen2FillKeyFeature(9, 1, 2, 3));
    }

    [Fact]
    public void GalleonPixelAndKeepAlive_MatchTheNodeBytes()
    {
        Assert.Equal(Padded("03 24 0c ff 80 40", 32), StreamDeckProtocol.BuildGalleonRingPixelFeature(12, 255, 128, 64));
        Assert.Equal(Padded("03 27", 32), StreamDeckProtocol.BuildGalleonKeepAliveFeature());
    }

    // ── Rings ──

    private static byte[] RampRing(int leds) =>
        Enumerable.Range(0, leds).SelectMany(i => new[] { (byte)(i + 1), (byte)0, (byte)0 }).ToArray();

    [Fact]
    public void StudioRing_DialZeroIsUnrotated_DialOneRotatesTwelveLeds()
    {
        var ring = RampRing(24);

        var d0 = StreamDeckProtocol.BuildStudioRingReport(Studio, 0, ring);
        var d1 = StreamDeckProtocol.BuildStudioRingReport(Studio, 1, ring);

        Assert.Equal(1024, d0.Length);
        Assert.Equal(new byte[] { 0x02, 0x0f, 0 }, d0[..3]);
        Assert.Equal(ring, d0[3..(3 + 72)]);
        Assert.Equal(new byte[] { 0x02, 0x0f, 1 }, d1[..3]);
        Assert.Equal(13, d1[3]); // LED 12 of the visual order lands first
        Assert.Equal(1, d1[3 + 12 * 3]);
    }

    [Fact]
    public void StudioCenterLed_IsDialThenRgb()
    {
        var report = StreamDeckProtocol.BuildStudioCenterLedReport(1, 10, 20, 30);

        Assert.Equal(1024, report.Length);
        Assert.Equal(new byte[] { 0x02, 0x10, 1, 10, 20, 30 }, report[..6]);
    }

    [Fact]
    public void GalleonRing_SendsOneFeatureReportPerLedWithTheNodeIndexAndRotation()
    {
        var ring = RampRing(4);

        var d0 = StreamDeckProtocol.BuildGalleonRingFeatures(Galleon, 0, ring);
        var d1 = StreamDeckProtocol.BuildGalleonRingFeatures(Galleon, 1, ring);

        Assert.Equal(4, d0.Count);
        // dial 0: base index (1 - 0) * 4, ring rotated left by 3
        Assert.Equal(new byte[] { 0x03, 0x24, 4, 4, 0, 0 }, d0[0][..6]);
        Assert.Equal(new byte[] { 0x03, 0x24, 5, 1, 0, 0 }, d0[1][..6]);
        // dial 1: base index 0, ring rotated left by 1
        Assert.Equal(new byte[] { 0x03, 0x24, 0, 2, 0, 0 }, d1[0][..6]);
        Assert.Equal(new byte[] { 0x03, 0x24, 3, 1, 0, 0 }, d1[3][..6]);
    }

    // ── Surface against a mock device ──

    private static (MockStreamDeckHidDevice dev, HidStreamDeckSurface surface) Connect(StreamDeckModel model, int featureLen = 32)
    {
        var dev = new MockStreamDeckHidDevice { ProductId = model.ProductId, VendorId = model.VendorId };
        var surface = new HidStreamDeckSurface(new FakeStreamDeckHidEnumerator { DeviceToOpen = dev }, model);
        using var ready = new ManualResetEventSlim();
        surface.Ready += ready.Set;
        Assert.True(surface.Connect(new HidDeviceInfo
        {
            VendorId = model.VendorId, ProductId = model.ProductId, Path = "p", Serial = "S1",
            FeatureReportByteLength = featureLen,
        }));
        if (model.OpenSettleMs > 0)
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(3)));
        }
        return (dev, surface);
    }

    [Fact]
    public void Connect_ExpandedModel_SendsFillBlackThenSleepOff()
    {
        var (dev, _) = Connect(Plus);

        var writes = dev.FeatureWrites.Where(w => w[1] is 0x05 or 0x0d).ToList();
        Assert.Equal(2, writes.Count);
        Assert.Equal(Padded("03 05 00 00 00", 32), writes[0]);
        Assert.Equal(Padded("03 0d 00 00 00 00", 32), writes[1]);
    }

    [Fact]
    public void Connect_ButtonOnlyModel_SendsNoInit()
    {
        var (dev, _) = Connect(StreamDeckModels.ByProductId(0x006c)!);

        Assert.DoesNotContain(dev.FeatureWrites, w => w[1] is 0x05 or 0x0d);
    }

    [Fact]
    public void SetScreenRegion_WritesCapturedPagesAndRejectsOutOfBounds()
    {
        var (dev, surface) = Connect(Plus);

        Assert.True(surface.SetScreenRegion(0, 0, 200, 100, new byte[1500]));
        Assert.Equal(2, dev.OutputWrites.Count);
        Assert.Equal(Hex("02 0c 00 00 00 00 c8 00 64 00 00 00 00 f0 03 00"), dev.OutputWrites[0][..16]);

        Assert.False(surface.SetScreenRegion(700, 0, 200, 100, new byte[10]));
        Assert.False(surface.SetScreenRegion(0, 0, 0, 100, new byte[10]));
        Assert.False(surface.SetInfoScreen(new byte[10]));
    }

    [Fact]
    public void SetScreenRegion_ButtonOnlyModelIsRefused()
    {
        var (_, surface) = Connect(StreamDeckModels.ByProductId(0x006c)!);

        Assert.False(surface.SetScreenRegion(0, 0, 10, 10, new byte[10]));
    }

    [Fact]
    public void SetInfoScreen_NeoOnly()
    {
        var (dev, surface) = Connect(Neo);

        Assert.True(surface.SetInfoScreen(new byte[100]));
        Assert.Equal(Hex("02 0b 00 01 64 00 00 00"), dev.OutputWrites[0][..8]);
    }

    [Fact]
    public void FillKey_RangeIncludesNeoTouchKeysOnly()
    {
        var (dev, surface) = Connect(Neo);

        Assert.True(surface.FillKey(9, 1, 2, 3));
        Assert.False(surface.FillKey(10, 1, 2, 3));
        Assert.Equal(Padded("03 06 09 01 02 03", 32), dev.FeatureWrites.Last());
    }

    [Fact]
    public void SetRing_Studio_WritesOneOutputReport_Galleon_WritesFeaturePerLed()
    {
        var (studioDev, studio) = Connect(Studio);
        Assert.True(studio.SetRing(0, RampRing(24)));
        Assert.Single(studioDev.OutputWrites);
        Assert.False(studio.SetRing(2, RampRing(24)));
        Assert.True(studio.SetCenterLed(1, 1, 2, 3));

        var (galleonDev, galleon) = Connect(Galleon);
        galleonDev.FeatureWrites.Clear();
        Assert.True(galleon.SetRing(1, RampRing(4)));
        Assert.Equal(4, galleonDev.FeatureWrites.Count(w => w[1] == 0x24));
        Assert.False(galleon.SetCenterLed(0, 1, 2, 3));
    }

    [Fact]
    public void Galleon_RefusesWritesUntilTheSettleDelayHasPassed_WithoutBlockingConnect()
    {
        var dev = new MockStreamDeckHidDevice { ProductId = Galleon.ProductId, VendorId = Galleon.VendorId };
        var surface = new HidStreamDeckSurface(new FakeStreamDeckHidEnumerator { DeviceToOpen = dev }, Galleon);
        using var ready = new ManualResetEventSlim();
        surface.Ready += ready.Set;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(surface.Connect(new HidDeviceInfo { VendorId = Galleon.VendorId, ProductId = Galleon.ProductId, Path = "p", Serial = "S" }));
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < Galleon.OpenSettleMs);
        Assert.False(surface.SetBrightness(50));
        Assert.Empty(dev.FeatureWrites);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(surface.SetBrightness(50));
        Assert.Contains(dev.FeatureWrites, w => w[1] == 0x05 && w[2] == 0);
    }

    [Fact]
    public async Task Galleon_AFailedPingKeepsPinging_UntilTheFailureThresholdDropsTheHandle()
    {
        var (dev, surface) = Connect(Galleon);
        for (var i = 0; i < 100 && dev.FeatureWrites.Count(w => w[1] == 0x27) < 1; i++)
        {
            await Task.Delay(20);
        }

        dev.FailNextFeatureWrite = true;
        var before = dev.FeatureWrites.Count(w => w[1] == 0x27);
        for (var i = 0; i < 150 && dev.FeatureWrites.Count(w => w[1] == 0x27) < before + 2; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(dev.FeatureWrites.Count(w => w[1] == 0x27) >= before + 2, "pings continue after one failure");
        Assert.True(surface.IsConnected);
    }

    [Fact]
    public async Task Galleon_PingsKeepAliveUntilDisconnected()
    {
        var (dev, surface) = Connect(Galleon);

        for (var i = 0; i < 100 && dev.FeatureWrites.Count(w => w[1] == 0x27) < 2; i++)
        {
            await Task.Delay(20);
        }
        Assert.True(dev.FeatureWrites.Count(w => w[1] == 0x27) >= 2);

        surface.Disconnect();
        var after = dev.FeatureWrites.Count(w => w[1] == 0x27);
        await Task.Delay(700);
        Assert.Equal(after, dev.FeatureWrites.Count(w => w[1] == 0x27));
    }

    [Fact]
    public void ReadInput_Plus_DecodesADialReportFromA512ByteBuffer()
    {
        var (dev, surface) = Connect(Plus);
        dev.PendingReads.Enqueue(Hex("01 03 05 00 01 00 00 01 00"));

        var input = surface.ReadInput(10);

        Assert.NotNull(input);
        Assert.Equal(StreamDeckInputKind.DialRotate, input!.Kind);
        Assert.Equal(1, input.DialTicks[2]);
    }
}

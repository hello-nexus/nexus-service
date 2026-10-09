using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Records HID exchanges so tests can assert the exact bytes HidStreamDeckSurface
/// sends/reads. PendingReads is a ConcurrentQueue (not a plain Queue) because
/// StreamDeckConnectionWorkerTests now drives this device from a live
/// StreamDeckInputReader background thread concurrently with the test thread
/// enqueuing reports. Read polls in short increments up to timeoutMs instead
/// of returning 0 instantly, so that background reader idles between polls
/// rather than busy-spinning for the lifetime of the test process.
/// </summary>
internal sealed class MockStreamDeckHidDevice : IHidDevice
{
    private const int PollIntervalMs = 5;

    public List<byte[]> FeatureWrites { get; } = new();
    public List<byte[]> OutputWrites { get; } = new();
    public ConcurrentQueue<byte[]> PendingReads { get; } = new();
    public Func<byte[], byte[]>? FeatureReplyBuilder { get; set; }
    public bool FailNextFeatureWrite { get; set; }
    /// <summary>When set and true for a feature report, the write is rejected every time.</summary>
    public Func<byte[], bool>? RejectFeature { get; set; }
    public bool FailNextOutputWrite { get; set; }
    public bool FailNextRead { get; set; }
    public bool Disposed { get; private set; }

    public int VendorId { get; set; } = 0x0FD9;
    public int ProductId { get; set; } = 0x0063;
    public string Path { get; set; } = "mock-path";
    public string? Serial { get; set; } = "MOCKSERIAL1";
    public int UsagePage { get; set; } = 0x0C;
    public int Usage { get; set; } = 0x01;

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        if (FailNextFeatureWrite) { FailNextFeatureWrite = false; return false; }
        if (RejectFeature?.Invoke(report.ToArray()) == true) { return false; }
        FeatureWrites.Add(report.ToArray());
        return true;
    }

    public bool GetFeature(Span<byte> buffer)
    {
        var reply = FeatureReplyBuilder?.Invoke(buffer.ToArray()) ?? new byte[buffer.Length];
        reply.AsSpan(0, Math.Min(reply.Length, buffer.Length)).CopyTo(buffer);
        return true;
    }

    public bool Write(ReadOnlySpan<byte> report)
    {
        if (FailNextOutputWrite) { FailNextOutputWrite = false; return false; }
        OutputWrites.Add(report.ToArray());
        return true;
    }

    public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
    public bool GetInputReport(Span<byte> buffer) => false;

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (FailNextRead) return -1;
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 0);
        while (true)
        {
            if (PendingReads.TryDequeue(out var next))
            {
                var n = Math.Min(next.Length, buffer.Length);
                next.AsSpan(0, n).CopyTo(buffer);
                return n;
            }
            if (Environment.TickCount64 >= deadline)
            {
                return 0;
            }
            Thread.Sleep(PollIntervalMs);
        }
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeStreamDeckHidEnumerator : IHidEnumerator
{
    public IHidDevice? DeviceToOpen { get; set; }
    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => Array.Empty<HidDeviceInfo>();
    public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();
    public IHidDevice? Open(string path, bool forInput = false) => DeviceToOpen;
}

/// <summary>
/// Exercises HidStreamDeckSurface (the real production surface) against a
/// mock IHidDevice, verifying it sends the exact StreamDeckProtocol byte
/// layouts and applies each model's key-index remap before touching the wire.
/// </summary>
public class HidStreamDeckSurfaceTests
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;
    private static readonly StreamDeckModel Original = StreamDeckModels.ByProductId(0x0060)!;

    private static (MockStreamDeckHidDevice dev, HidStreamDeckSurface surface) Connect(StreamDeckModel model, string serial = "A00DA431130Y9Y")
    {
        var dev = new MockStreamDeckHidDevice { Serial = serial, ProductId = model.ProductId };
        var hid = new FakeStreamDeckHidEnumerator { DeviceToOpen = dev };
        var surface = new HidStreamDeckSurface(hid, model);
        var info = new HidDeviceInfo { VendorId = StreamDeckModels.VendorId, ProductId = model.ProductId, Path = "mock-path", Serial = serial };
        Assert.True(surface.Connect(info));
        return (dev, surface);
    }

    /// <summary>An exact-length, arbitrary-content BMP wire image for a BMP model, matching StreamDeckModel.IsValidWireImageLength.</summary>
    private static byte[] BmpImage(StreamDeckModel model) =>
        Enumerable.Range(0, 54 + model.KeyPixelSize * model.KeyPixelSize * 3).Select(i => (byte)(i % 256)).ToArray();

    [Fact]
    public void Connect_PopulatesSerialFromHidDeviceInfo()
    {
        var (_, surface) = Connect(Mini);
        Assert.Equal("A00DA431130Y9Y", surface.Serial);
        Assert.True(surface.IsConnected);
    }

    [Fact]
    public void Connect_ReadsFirmwareVersionViaFeature0x04()
    {
        var dev = new MockStreamDeckHidDevice
        {
            FeatureReplyBuilder = req =>
            {
                var reply = new byte[32];
                req.CopyTo(reply, 0);
                System.Text.Encoding.ASCII.GetBytes("1.00.006").CopyTo(reply, 5);
                return reply;
            },
        };
        var hid = new FakeStreamDeckHidEnumerator { DeviceToOpen = dev };
        var surface = new HidStreamDeckSurface(hid, Mini);

        surface.Connect(new HidDeviceInfo { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId, Path = "p", Serial = "s" });

        Assert.Equal("1.00.006", surface.FirmwareVersion);
    }

    [Fact]
    public void SetBrightness_SendsBenchConfirmedFeatureBytes()
    {
        var (dev, surface) = Connect(Mini);

        Assert.True(surface.SetBrightness(42));

        Assert.Single(dev.FeatureWrites);
        Assert.Equal(StreamDeckProtocol.BuildBrightnessFeature(42), dev.FeatureWrites[0]);
    }

    [Fact]
    public void Reset_SendsResetFeatureBytes()
    {
        var (dev, surface) = Connect(Mini);

        Assert.True(surface.Reset());

        Assert.Equal(StreamDeckProtocol.BuildResetFeature(), dev.FeatureWrites[0]);
    }

    [Fact]
    public void SetKeyImage_Mini_PushesPagesMatchingProtocolBuilder()
    {
        var (dev, surface) = Connect(Mini);
        var image = BmpImage(Mini);

        Assert.True(surface.SetKeyImage(2, image));

        var expected = StreamDeckProtocol.BuildImagePages(image, rawKeyIndex: 2, Mini);
        Assert.Equal(expected.Count, dev.OutputWrites.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], dev.OutputWrites[i]);
        }
    }

    [Fact]
    public void SetKeyImage_Original_AppliesRightToLeftRemapBeforeWire()
    {
        var (dev, surface) = Connect(Original);

        Assert.True(surface.SetKeyImage(0, BmpImage(Original)));

        // Canonical key 0 (row 0 leftmost) maps to raw hardware index 4
        // (row 0 rightmost) for the Original, so the wire byte is 4+1=5.
        Assert.Equal(5, dev.OutputWrites[0][5]);
    }

    [Fact]
    public void SetKeyImage_IndexOutOfRange_Fails()
    {
        var (_, surface) = Connect(Mini);
        Assert.False(surface.SetKeyImage(Mini.KeyCount, new byte[] { 1 }));
        Assert.False(surface.SetKeyImage(-1, new byte[] { 1 }));
    }

    [Fact]
    public void ClearKey_Mini_PushesTheBlankBmpConstant()
    {
        var (dev, surface) = Connect(Mini);

        Assert.True(surface.ClearKey(0));

        var expected = StreamDeckProtocol.BuildImagePages(StreamDeckProtocol.BuildBlankBmp(80), rawKeyIndex: 0, Mini);
        Assert.Equal(expected.Count, dev.OutputWrites.Count);
        Assert.Equal(expected[0], dev.OutputWrites[0]);
    }

    [Fact]
    public void ClearKey_Original_NoBlankConstantAvailable_ReturnsFalse()
    {
        var (_, surface) = Connect(Original);
        Assert.False(surface.ClearKey(0));
    }

    [Fact]
    public void ReadInput_DecodesQueuedReport()
    {
        var (dev, surface) = Connect(Mini);
        dev.PendingReads.Enqueue(new byte[] { 0x01, 0, 1, 0, 0, 0, 0 });

        var states = surface.ReadInput(10)?.Keys;

        Assert.Equal(new[] { false, true, false, false, false, false }, states);
    }

    [Fact]
    public void ReadInput_IdleReturnsNull_AndStaysConnected()
    {
        var (_, surface) = Connect(Mini);

        Assert.Null(surface.ReadInput(10));
        Assert.True(surface.IsConnected);
    }

    [Fact]
    public void ReadInput_DeviceGone_DisconnectsAndDisposesHandle()
    {
        var (dev, surface) = Connect(Mini);
        dev.FailNextRead = true;

        Assert.Null(surface.ReadInput(10));

        Assert.False(surface.IsConnected);
        Assert.True(dev.Disposed);
    }

    [Fact]
    public void SetBrightness_AfterFiveConsecutiveFailures_DropsInterface()
    {
        var (dev, surface) = Connect(Mini);

        for (var i = 0; i < 4; i++)
        {
            dev.FailNextFeatureWrite = true;
            Assert.False(surface.SetBrightness(50));
            Assert.True(surface.IsConnected); // below threshold, interface stays
        }

        dev.FailNextFeatureWrite = true;
        Assert.False(surface.SetBrightness(50));

        Assert.False(surface.IsConnected);
    }

    // ── Gen2 (XL, Pedal) ──

    private static readonly StreamDeckModel Xl = StreamDeckModels.ByProductId(0x006c)!;
    private static readonly StreamDeckModel Pedal = StreamDeckModels.ByProductId(0x0086)!;

    [Fact]
    public void SetBrightness_Xl_SendsGen2FeatureBytes()
    {
        var (dev, surface) = Connect(Xl);

        Assert.True(surface.SetBrightness(42));

        Assert.Equal(StreamDeckProtocol.BuildGen2BrightnessFeature(42), dev.FeatureWrites[0]);
    }

    [Fact]
    public void Reset_Xl_SendsGen2ResetBytes()
    {
        var (dev, surface) = Connect(Xl);

        Assert.True(surface.Reset());

        Assert.Equal(StreamDeckProtocol.BuildGen2ResetFeature(), dev.FeatureWrites[0]);
    }

    [Fact]
    public void SetKeyImage_Xl_PushesPagesMatchingTheGen2Builder()
    {
        var (dev, surface) = Connect(Xl);
        var image = Enumerable.Range(0, 2000).Select(i => (byte)(i % 256)).ToArray();

        Assert.True(surface.SetKeyImage(5, image));

        var expected = StreamDeckProtocol.BuildGen2ImagePages(image, rawKeyIndex: 5, Xl);
        Assert.Equal(expected.Count, dev.OutputWrites.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], dev.OutputWrites[i]);
        }
    }

    [Fact]
    public void ReadInput_Xl_DecodesAGen2Report()
    {
        var (dev, surface) = Connect(Xl);
        var report = new byte[4 + Xl.KeyCount];
        report[4 + 9] = 1;
        dev.PendingReads.Enqueue(report);

        var states = surface.ReadInput(10)?.Keys;

        Assert.NotNull(states);
        Assert.True(states![9]);
    }

    [Fact]
    public void Connect_Xl_ReadsFirmwareViaGen2OffsetSix()
    {
        var dev = new MockStreamDeckHidDevice
        {
            ProductId = Xl.ProductId,
            FeatureReplyBuilder = req =>
            {
                var reply = new byte[32];
                req.CopyTo(reply, 0);
                System.Text.Encoding.ASCII.GetBytes("1.02.007").CopyTo(reply, StreamDeckProtocol.Gen2FirmwareStringOffset);
                return reply;
            },
        };
        var hid = new FakeStreamDeckHidEnumerator { DeviceToOpen = dev };
        var surface = new HidStreamDeckSurface(hid, Xl);

        surface.Connect(new HidDeviceInfo { VendorId = StreamDeckModels.VendorId, ProductId = Xl.ProductId, Path = "p", Serial = "s" });

        Assert.Equal("1.02.007", surface.FirmwareVersion);
    }

    [Fact]
    public void Pedal_HasNoScreen_SetBrightnessResetAndSetKeyImageAllNoOpWithoutTouchingTheDevice()
    {
        var (dev, surface) = Connect(Pedal);

        Assert.False(surface.SetBrightness(50));
        Assert.False(surface.Reset());
        Assert.False(surface.SetKeyImage(0, new byte[] { 1, 2, 3 }));
        Assert.False(surface.ClearKey(0));

        Assert.Empty(dev.FeatureWrites);
        Assert.Empty(dev.OutputWrites);
    }

    [Fact]
    public void Pedal_ReadInput_StillDecodesKeyPresses()
    {
        var (dev, surface) = Connect(Pedal);
        var report = new byte[4 + Pedal.KeyCount];
        report[4 + 2] = 1;
        dev.PendingReads.Enqueue(report);

        var states = surface.ReadInput(10)?.Keys;

        Assert.Equal(new[] { false, false, true }, states);
    }
}

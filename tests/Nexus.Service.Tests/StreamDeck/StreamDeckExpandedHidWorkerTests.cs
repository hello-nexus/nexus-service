using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Xunit;
using static Nexus.Service.Tests.StreamDeck.DeckTestHelpers;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Records every Open path and serves a mock device for paths it knows, so a test can prove which HID collections the worker touched.</summary>
internal sealed class RecordingWorkerHidEnumerator : IHidEnumerator
{
    public List<HidDeviceInfo> Infos { get; } = new();
    public Dictionary<string, IHidDevice> Devices { get; } = new();
    public List<string> OpenedPaths { get; } = new();

    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
        Infos.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();

    public IReadOnlyList<HidDeviceInfo> FindAll() => Infos;

    public IHidDevice? Open(string path, bool forInput = false)
    {
        lock (OpenedPaths) { OpenedPaths.Add(path); }
        return Devices.TryGetValue(path, out var device) ? device : null;
    }
}

/// <summary>The worker against the real HID surface and input reader over mock devices: model matching, the dial report path, and the shutdown path.</summary>
public sealed class StreamDeckExpandedHidWorkerTests : IDisposable
{
    private readonly InMemoryConfigStore _store = new();
    private readonly FakeDeckActionExecutor _executor = new();
    private readonly FakeDialValues _values = new();
    private StreamDeckConnectionWorker? _worker;

    public void Dispose() => _worker?.Dispose();

    private (StreamDeckConnectionWorker Worker, RecordingWorkerHidEnumerator Hid) Build(
        StreamDeckModel model, string path, string serial, MockStreamDeckHidDevice device, UsbDeviceEntry usb, string? configJson = null)
    {
        var hid = new RecordingWorkerHidEnumerator();
        hid.Infos.Add(new HidDeviceInfo
        {
            VendorId = model.VendorId, ProductId = model.ProductId, Path = path, Serial = serial,
            UsagePage = 0xFF00, Usage = model.HidUsage == 0 ? 0x01 : model.HidUsage, FeatureReportByteLength = 32,
        });
        hid.Devices[path] = device;
        var gate = new DeviceControlGate(_store);
        gate.SetEnabled("streamdeck", true);
        _worker = new StreamDeckConnectionWorker(
            hid, new HardwarePresence(new FixedUsbEnumerator(usb)), gate, _store, _executor, NewTestKeyRenderer(),
            new MultiplexHub(), new FakeSensorProvider(), dialValues: _values);
        if (configJson is not null)
        {
            var config = JsonSerializer.Deserialize(configJson, AppJsonContext.Default.DeckConfig)!;
            _store.Update(s => s.StreamDeck.Decks[serial] = new PhysicalDeckSettings { LegacyDeck = config });
            _store.Update(s => ActivateLegacyDeck(s, serial, model.Columns, model.Rows));
        }
        return (_worker, hid);
    }

    private static UsbDeviceEntry Usb(StreamDeckModel model) => new() { VendorId = model.VendorId, ProductId = model.ProductId };

    [Fact]
    public void Plus_ConnectInitSendsFillBlackAndSleepOff_ThenPushesTheStripAndKeys()
    {
        var plus = StreamDeckModels.ByProductId(0x0084)!;
        var device = new MockStreamDeckHidDevice { ProductId = plus.ProductId };
        var (worker, _) = Build(plus, "plus-path", "PLUS1", device, Usb(plus), "{\"pages\":[{\"slots\":[{\"action\":{\"type\":\"openUrl\",\"url\":\"https://x\"}}]}]}");

        worker.Tick();

        Assert.Single(worker.Surfaces);
        Assert.Contains(device.FeatureWrites, w => w[1] == 0x05 && w[2] == 0 && w[3] == 0 && w[4] == 0);
        Assert.Contains(device.FeatureWrites, w => w[1] == 0x0d);
        Assert.Contains(device.OutputWrites, w => w[0] == 0x02 && w[1] == 0x07 && w[2] == 0);
        var strip = device.OutputWrites.Where(w => w[1] == 0x0c).ToList();
        Assert.NotEmpty(strip);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x20, 0x03, 0x64, 0 }, strip[0][2..10]);
    }

    [Fact]
    public void Plus_DialReportsReachTheWorkerThroughTheInputReader()
    {
        var plus = StreamDeckModels.ByProductId(0x0084)!;
        var device = new MockStreamDeckHidDevice { ProductId = plus.ProductId };
        var (worker, _) = Build(plus, "plus-path", "PLUS1", device, Usb(plus),
            "{\"pages\":[{\"slots\":[],\"dials\":[{},{},{\"action\":{\"type\":\"volume\",\"deviceId\":\"out\"}}]}]}");
        _values.Set("volume:out", 50);
        worker.Tick();

        // Bench capture 2026-10-08: rotate dial 3 by +1.
        device.PendingReads.Enqueue(new byte[] { 0x01, 0x03, 0x05, 0x00, 0x01, 0x00, 0x00, 0x01, 0x00 });

        Assert.True(SpinWait.SpinUntil(() => _values.Writes.Count > 0, TimeSpan.FromSeconds(3)));
        Assert.Equal(("volume:out", 52d), _values.Writes.First());
    }

    [Fact]
    public void Plus_TouchReportsPageThroughTheInputReader()
    {
        var plus = StreamDeckModels.ByProductId(0x0084)!;
        var device = new MockStreamDeckHidDevice { ProductId = plus.ProductId };
        var (worker, _) = Build(plus, "plus-path", "PLUS1", device, Usb(plus), "{\"pages\":[{\"slots\":[]},{\"slots\":[]}]}");
        worker.Tick();

        // Bench capture 2026-10-08: flick left, 535 to 485.
        device.PendingReads.Enqueue(new byte[] { 0x01, 0x02, 0x0e, 0x00, 0x03, 0x00, 0x17, 0x02, 0x4b, 0x00, 0xe5, 0x01, 0x40, 0x00, 0, 0, 0, 0 });

        Assert.True(SpinWait.SpinUntil(() => worker.GetCurrentPage("PLUS1") == 1, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Galleon_OpensOnlyItsStreamDeckCollection_AndPingsKeepAlive()
    {
        var galleon = StreamDeckModels.ByProductId(0x2b18)!;
        var device = new MockStreamDeckHidDevice { ProductId = galleon.ProductId, VendorId = galleon.VendorId };
        var (worker, hid) = Build(galleon, @"\\?\hid#vid_1b1c&pid_2b18&mi_00#a", "GAL1", device, Usb(galleon));
        hid.Infos.Add(new HidDeviceInfo { VendorId = galleon.VendorId, ProductId = galleon.ProductId, Path = @"\\?\hid#vid_1b1c&pid_2b18&mi_01#b", Usage = 0x06, UsagePage = 0x01 });
        hid.Infos.Add(new HidDeviceInfo { VendorId = galleon.VendorId, ProductId = galleon.ProductId, Path = @"\\?\hid#vid_1b1c&pid_2b18&mi_02#c", Usage = 0x01, UsagePage = 0xFF42 });

        worker.Tick();

        Assert.Single(worker.Surfaces);
        lock (hid.OpenedPaths)
        {
            Assert.All(hid.OpenedPaths, p => Assert.Equal(@"\\?\hid#vid_1b1c&pid_2b18&mi_00#a", p));
        }
        Assert.True(SpinWait.SpinUntil(() => device.FeatureWrites.Count(w => w[1] == 0x27) >= 1, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Studio_ShutdownBlanksTheRingsAndCentreLedsBeforeTheLogo()
    {
        var studio = StreamDeckModels.ByProductId(0x00aa)!;
        var device = new MockStreamDeckHidDevice { ProductId = studio.ProductId };
        var (worker, _) = Build(studio, "studio-path", "STU1", device, Usb(studio),
            "{\"pages\":[{\"slots\":[],\"dials\":[{\"action\":{\"type\":\"volume\",\"deviceId\":\"out\"}}]}]}");
        _values.Set("volume:out", 100);
        worker.Tick();
        Assert.Contains(device.OutputWrites, w => w[1] == 0x0f && w[2] == 0 && w[3] > 0);
        device.OutputWrites.Clear();
        device.FeatureWrites.Clear();

        worker.ResetConnectedSurfacesForShutdown();

        var rings = device.OutputWrites.Where(w => w[1] == 0x0f).ToList();
        Assert.Equal(new[] { (byte)0, (byte)1 }, rings.Select(w => w[2]).ToArray());
        Assert.All(rings, w => Assert.All(w[3..(3 + 72)], b => Assert.Equal(0, b)));
        Assert.Equal(2, device.OutputWrites.Count(w => w[1] == 0x10));
        Assert.Contains(device.FeatureWrites, w => w[1] == 0x02);
    }

    [Fact]
    public void Neo_ShutdownDarkensTheTouchKeyBacklights()
    {
        var neo = StreamDeckModels.ByProductId(0x009a)!;
        var device = new MockStreamDeckHidDevice { ProductId = neo.ProductId };
        var (worker, _) = Build(neo, "neo-path", "NEO1", device, Usb(neo), "{\"pages\":[{\"slots\":[]},{\"slots\":[]}]}");
        worker.Tick();
        Assert.Contains(device.FeatureWrites, w => w[1] == 0x06 && w[2] == 9 && w[3] > 0);
        device.FeatureWrites.Clear();

        worker.ResetConnectedSurfacesForShutdown();

        Assert.Contains(device.FeatureWrites, w => w[1] == 0x06 && w[2] == 8 && w[3] == 0 && w[4] == 0 && w[5] == 0);
        Assert.Contains(device.FeatureWrites, w => w[1] == 0x06 && w[2] == 9 && w[3] == 0 && w[4] == 0 && w[5] == 0);
    }
}

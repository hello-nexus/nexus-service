using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Fps;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Test HID enumerator that opens a caller-supplied device per path.</summary>
internal sealed class FakeWorkerHidEnumerator : IHidEnumerator
{
    public Dictionary<int, List<HidDeviceInfo>> ByProductId { get; } = new();
    public Dictionary<string, IHidDevice> DevicesByPath { get; } = new();

    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
        ByProductId.TryGetValue(productId, out var list) ? list : Array.Empty<HidDeviceInfo>();

    public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();

    public IHidDevice? Open(string path, bool forInput = false) =>
        DevicesByPath.TryGetValue(path, out var dev) ? dev : null;
}

/// <summary>
/// Spy executor: records every dispatch instead of touching real providers.
/// HandleKeyDown fire-and-forgets each dispatch onto the thread pool
/// (Task.Run, never awaited), so two presses within one Tick() can call
/// ExecuteAsync concurrently from different threads - a plain List here would
/// silently drop an entry under that race, so Calls is a ConcurrentQueue.
/// </summary>
internal sealed class FakeDeckActionExecutor : IDeckActionExecutor
{
    public readonly ConcurrentQueue<(DeckAction? Action, string Serial, int KeyIndex, string LatchKey)> Calls = new();
    private readonly Dictionary<string, bool> _latches = new();

    public Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
    {
        Calls.Enqueue((action, serial, keyIndex, latchKey));
        return Task.CompletedTask;
    }

    public bool IsToggleOn(DeckToggleState? state, string latchKey) => _latches.TryGetValue(latchKey, out var v) && v;

    public int OpenAppCount;
    public void OpenApp() => Interlocked.Increment(ref OpenAppCount);
}

/// <summary>Minimal settable ISensorProvider for monitoring-tile push tests.</summary>
internal sealed class FakeSensorProvider : ISensorProvider
{
    public IReadOnlyList<HardwareSensor> CpuSensors { get; set; } = Array.Empty<HardwareSensor>();
    public IReadOnlyList<GpuReadout> Gpus { get; set; } = Array.Empty<GpuReadout>();
    public IReadOnlyList<HardwareSensor> MemorySensors { get; set; } = Array.Empty<HardwareSensor>();
    public IReadOnlyList<HardwareSensor> MotherboardSensors { get; set; } = Array.Empty<HardwareSensor>();
    public IReadOnlyDictionary<string, StorageComponent> StorageComponents { get; set; } = new Dictionary<string, StorageComponent>();
    public SensorExtras Extras { get; set; } = new();
    public int ExtrasCalls { get; private set; }

    public string GetCpuModel() => "TestCPU";
    public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
    public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
    public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
    public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
    public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
    public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
    public string GetMemoryTotalFormatted() => "32 GB";
    public string GetRamBrandModel() => "";
    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => StorageComponents;
    public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
    public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
    public string GetStorageBrandModel() => "";
    public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => MotherboardSensors;
    public string GetMotherboardModel() => "TestMobo";
    public SensorExtras GetSensorExtras()
    {
        ExtrasCalls++;
        return Extras;
    }
    public string GetOsVersion() => "TestOS";
    public void SetPollingRate(int pollingRate) { }
    public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Minimal IFpsProvider recording SetDemand, for the fps monitoring category.</summary>
internal sealed class FakeFpsProvider : IFpsProvider
{
    public HardwareComponent Component { get; set; } = new() { Id = "fps", Name = "FPS" };
    public bool? LastDemand { get; private set; }
    public int DemandCalls { get; private set; }

    public void SetDemand(string source, bool wanted)
    {
        LastDemand = wanted;
        DemandCalls++;
    }

    public HardwareComponent GetComponent() => Component;
    public bool TryReadCurrentFps(out double fps) { fps = 0; return false; }
    public void Dispose() { }
}

public class StreamDeckConnectionWorkerTests
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    private sealed record Fixtures(
        FakeWorkerHidEnumerator Hid,
        HardwarePresence Presence,
        DeviceControlGate Gate,
        InMemoryConfigStore Store,
        FakeDeckActionExecutor Executor,
        StreamDeckImageCache ImageCache,
        MultiplexHub Hub,
        FakeSensorProvider Sensors);

    private static Fixtures NewFixtures(bool devicePresent)
    {
        var hid = new FakeWorkerHidEnumerator();
        var usbEntries = devicePresent
            ? new List<UsbDeviceEntry> { new() { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId } }
            : new List<UsbDeviceEntry>();
        var presence = new HardwarePresence(new FixedUsbEnumerator(usbEntries.ToArray()));
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        // DeviceControlPolicy defaults "streamdeck" off (Elgato's own software
        // is a mapped competitor - see DeviceControlPolicyTests); these tests
        // exercise the worker's own connect/dispatch behavior, so opt in
        // explicitly rather than depending on the brand default.
        gate.SetEnabled("streamdeck", true);
        var imageCache = new StreamDeckImageCache(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-streamdeck-test-" + Guid.NewGuid().ToString("N")));
        return new Fixtures(hid, presence, gate, store, new FakeDeckActionExecutor(), imageCache, new MultiplexHub(), new FakeSensorProvider());
    }

    private static StreamDeckConnectionWorker NewWorker(
        Fixtures f, SimulatedStreamDeckSurface? simulated = null, IFpsProvider? fps = null) =>
        new(f.Hid, f.Presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors, simulated, fps: fps);

    private static void AddMiniDevice(FakeWorkerHidEnumerator hid, string path, string serial)
    {
        hid.ByProductId[Mini.ProductId] = new List<HidDeviceInfo>
        {
            new() { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId, Path = path, Serial = serial },
        };
        hid.DevicesByPath[path] = new MockStreamDeckHidDevice { Serial = serial, ProductId = Mini.ProductId, Path = path };
    }

    [Fact]
    public void Tick_DiscoversAndConnectsAPresentDeck()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.Single(worker.Surfaces);
        Assert.NotNull(worker.FindBySerial("SERIAL-1"));
        Assert.True(worker.FindBySerial("SERIAL-1")!.IsConnected);
    }

    [Fact]
    public void Tick_DiscoversAndConnectsAGen2Deck()
    {
        var xl = StreamDeckModels.ByProductId(0x006c)!;
        var f = NewFixtures(devicePresent: true);
        f.Hid.ByProductId[xl.ProductId] = new List<HidDeviceInfo>
        {
            new() { VendorId = StreamDeckModels.VendorId, ProductId = xl.ProductId, Path = "path-xl", Serial = "XL-SERIAL" },
        };
        f.Hid.DevicesByPath["path-xl"] = new MockStreamDeckHidDevice { Serial = "XL-SERIAL", ProductId = xl.ProductId, Path = "path-xl" };
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.NotNull(worker.FindBySerial("XL-SERIAL"));
        Assert.True(worker.FindBySerial("XL-SERIAL")!.IsConnected);
    }

    [Fact]
    public void Tick_GateDisabled_NeverConnects()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        f.Gate.SetEnabled("streamdeck", false);
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void Tick_NoUsbPresence_SkipsHidScanEntirely()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1"); // hid would find it, but presence says no 0x0FD9 on the bus
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void Tick_DeviceUnplugged_DisposesAndRemovesTheSurface()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var usb = new MutableUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId });
        var presence = new HardwarePresence(usb);
        using var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors);

        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        Assert.Single(worker.Surfaces);

        usb.Devices.Clear();
        f.Hid.ByProductId.Clear();
        worker.Tick();

        Assert.Empty(worker.Surfaces);
        Assert.True(dev.Disposed);
    }

    [Fact]
    public void Tick_DeviceUnplugged_PersistedProductIdSurvivesForTheDisconnectedDecksListing()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var usb = new MutableUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId });
        var presence = new HardwarePresence(usb);
        using var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors);

        worker.Tick();
        Assert.Equal(Mini.ProductId, f.Store.Load().StreamDeck.Decks["SERIAL-1"].ProductId);

        usb.Devices.Clear();
        f.Hid.ByProductId.Clear();
        worker.Tick();

        Assert.Empty(worker.Surfaces);
        Assert.Equal(Mini.ProductId, f.Store.Load().StreamDeck.Decks["SERIAL-1"].ProductId);
    }

    [Fact]
    public void Tick_WithSimulatedSurface_RegistersItOnFirstTick()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        Assert.NotNull(worker.FindBySerial("sim-0001"));
    }

    [Fact]
    public void Tick_WithSimulatedSurface_PumpsPressesAcrossMultipleTicksWithoutError()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();

        Assert.True(worker.FindBySerial("sim-0001")!.IsConnected);
    }

    [Fact]
    public void Tick_GateDisabled_DoesNotDisposeTheSimulatedSurface()
    {
        // The simulated surface is a shared DI singleton the dev routes also
        // hold a reference to; disabling Nexus Control must stop tracking it,
        // not dispose the shared instance out from under those routes.
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();

        Assert.True(simulated.IsConnected);
    }

    [Fact]
    public async Task SimulatedPress_OnRootActionSlot_DispatchesToTheExecutor()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal("sim-0001", call.Serial);
        Assert.Equal(0, call.KeyIndex);
        Assert.Same(action, call.Action);
    }

    [Fact]
    public async Task SimulatedPress_FastDownThenUpWithinOneTick_StillDispatchesExactlyOnce()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        // Both transitions queue before a single Tick drains them, mirroring
        // a press+release faster than the 1s tick (the sub-tick hold that
        // previously got dropped when ReadInput only sampled the latest state).
        simulated.Poke(0, true);
        simulated.Poke(0, false);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal("sim-0001", call.Serial);
        Assert.Equal(0, call.KeyIndex);
        Assert.Same(action, call.Action);
    }

    [Fact]
    public async Task RealSurface_DedicatedReaderDrainsQueuedReportsAndDispatchesEachPress()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var actionA = new DeckAction { Type = "openUrl", Url = "https://a.example.com" };
        var actionB = new DeckAction { Type = "openUrl", Url = "https://b.example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = actionA }, new DeckSlot { Action = actionB } } } } },
        });
        using var worker = NewWorker(f);
        worker.Tick(); // connects the surface and starts its dedicated StreamDeckInputReader

        // Two full press+release cycles queued as 4 separate HID reports. The
        // reader's background thread (not this test's Tick calls) is what
        // drains and decodes them, exactly as a real burst of rapid presses
        // arriving between ticks would.
        var device = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        device.PendingReads.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 }); // key 0 down
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 }); // key 0 up
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 1, 0, 0, 0, 0 }); // key 1 down
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 }); // key 1 up

        for (var i = 0; i < 50 && f.Executor.Calls.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        // Both dispatches are fire-and-forget Task.Run work items (HandleKeyDown
        // never awaits them), so their thread-pool execution order relative to
        // each other isn't guaranteed - assert the set, not indexed positions.
        // Exactly 2 (not 4) confirms the up transitions were diffed out, not dispatched.
        Assert.Equal(2, f.Executor.Calls.Count);
        Assert.Contains(f.Executor.Calls, c => c.KeyIndex == 0 && ReferenceEquals(c.Action, actionA));
        Assert.Contains(f.Executor.Calls, c => c.KeyIndex == 1 && ReferenceEquals(c.Action, actionB));
    }

    [Fact]
    public async Task RealSurface_DedicatedReaderDrivesFolderNavPushAndBack()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var innerAction = new DeckAction { Type = "openUrl", Url = "https://inner.example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Action = innerAction } } } } } } },
            },
        });
        using var worker = NewWorker(f);
        worker.Tick();
        var device = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];

        // Press key 0 (the folder slot) at root - pushes into the folder.
        device.PendingReads.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 });
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 });
        for (var i = 0; i < 50 && worker.GetFolderPath("SERIAL-1").Count == 0; i++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("SERIAL-1"));

        // Inside the folder, key 0 is reserved for Back; the folder's own
        // slot 0 lives at physical key 1.
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 1, 0, 0, 0, 0 });
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 });
        for (var i = 0; i < 50 && f.Executor.Calls.Count < 1; i++)
        {
            await Task.Delay(10);
        }
        var call = Assert.Single(f.Executor.Calls);
        Assert.Same(innerAction, call.Action);
        Assert.Equal(0, call.KeyIndex);

        // Pressing key 0 again (now Back) pops the folder path.
        device.PendingReads.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 });
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 });
        for (var i = 0; i < 50 && worker.GetFolderPath("SERIAL-1").Count > 0; i++)
        {
            await Task.Delay(10);
        }
        Assert.Empty(worker.GetFolderPath("SERIAL-1"));
    }

    [Fact]
    public async Task KeyPress_OnFolderSlot_PushesFolderAndShiftsSubsequentKeysByOne()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var innerAction = new DeckAction { Type = "openUrl", Url = "https://inner.example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Action = innerAction } } } } },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        // Press key 0 (the folder slot) at root - no shift applies at root.
        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();

        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));
        Assert.Empty(f.Executor.Calls);

        // Inside the folder, key 0 is reserved for Back; the folder's own
        // slot 0 lives at physical key 1.
        simulated.Poke(1, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Same(innerAction, call.Action);
        // The dispatch's key index is the logical slot index within the
        // folder's own slot list (0), not the physical key number (1) - the
        // physical key was shifted by the reserved Back key at index 0.
        Assert.Equal(0, call.KeyIndex);
    }

    [Fact]
    public void KeyPress_OnBackKey_PopsTheFolderPath()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));

        simulated.Poke(0, true);
        worker.Tick();

        Assert.Empty(worker.GetFolderPath("sim-0001"));
    }

    [Fact]
    public void FolderView_PushesTheCachedBackBitmapAtKeyZero_FallsBackToClearWhenUncached()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));
        Assert.Null(simulated.PeekKeyImage(0));

        var bytes = new byte[] { 7, 7, 7 };
        var hash = StreamDeckImageCache.Hash(bytes);
        f.ImageCache.Store("sim-0001", hash, bytes);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"].ImageRefs["back/0"] = hash);
        worker.RefreshView("sim-0001");

        Assert.Equal(bytes, simulated.PeekKeyImage(0));
    }

    [Theory]
    [InlineData(0x0086, 1, 3, 3)]  // Pedal
    [InlineData(0x0063, 2, 3, 6)]  // Mini
    [InlineData(0x0090, 2, 3, 6)]  // Mini MK.2
    [InlineData(0x006c, 4, 8, 32)] // XL
    public void SetSimulatedModel_ForAGivenModel_RegistersASurfaceWithMatchingLayout(
        int productId, int rows, int columns, int keyCount)
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        Assert.True(worker.SetSimulatedModel(productId));

        var surface = Assert.Single(worker.Surfaces).Value;
        Assert.Equal(rows, surface.Model.Rows);
        Assert.Equal(columns, surface.Model.Columns);
        Assert.Equal(keyCount, surface.Model.KeyCount);
    }

    [Fact]
    public void SetSimulatedModel_UnknownProductId_ReturnsFalseAndAddsNoSurface()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        Assert.False(worker.SetSimulatedModel(0xDEAD));

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void SetSimulatedModel_ReplacingTheCurrentModel_TearsDownTheOldSurfaceCleanly()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);
        Assert.True(worker.SetSimulatedModel(0x0063)); // Mini
        var miniSerial = Assert.Single(worker.Surfaces).Value.Serial;
        Assert.True(worker.HasSerialState(miniSerial));

        Assert.True(worker.SetSimulatedModel(0x006c)); // XL

        Assert.False(worker.HasSerialState(miniSerial));
        Assert.Null(worker.FindBySerial(miniSerial));
        var xlSurface = Assert.Single(worker.Surfaces).Value;
        Assert.Equal("XL", xlSurface.Model.Name);
    }

    [Fact]
    public void ClearSimulatedModel_RemovesTheSurfaceAndItsState()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);
        Assert.True(worker.SetSimulatedModel(0x0063));
        var serial = Assert.Single(worker.Surfaces).Value.Serial;

        worker.ClearSimulatedModel();

        Assert.Empty(worker.Surfaces);
        Assert.False(worker.HasSerialState(serial));
    }

    [Fact]
    public void ClearSimulatedModel_WithNoSimulatedDeck_IsANoOp()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        worker.ClearSimulatedModel();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public async Task SetSimulatedModel_ThenPoke_DispatchesThroughTheExecutorLikeAnyOtherSurface()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);
        Assert.True(worker.SetSimulatedModel(0x0063)); // Mini
        var serial = Assert.Single(worker.Surfaces).Value.Serial;
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        f.Store.Update(s => s.StreamDeck.Decks[serial] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });

        var simulated = (SimulatedStreamDeckSurface)worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey];
        simulated.Poke(0, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal(serial, call.Serial);
        Assert.Equal(0, call.KeyIndex);
        Assert.Same(action, call.Action);
    }

    /// <summary>
    /// Two-page config for page-nav tests: each page's slot 1 dispatches a
    /// distinguishable openUrl action, so a dispatch after a page change
    /// proves the worker actually resolved the NEW page's view, not just
    /// advanced a counter.
    /// </summary>
    private static PhysicalDeckSettings TwoPageDeckSettings() => new()
    {
        Deck = new DeckConfig
        {
            Pages =
            {
                new DeckPage
                {
                    Slots =
                    {
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } },
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                    },
                },
                new DeckPage
                {
                    Slots =
                    {
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page1.example.com" } },
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "goto", Target = 0 } },
                    },
                },
            },
        },
    };

    [Fact]
    public async Task PageNextAction_AdvancesPageAndDispatchesFromTheNewPagesView()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = TwoPageDeckSettings());
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();

        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));
        Assert.Empty(f.Executor.Calls);

        simulated.Poke(1, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal("https://page1.example.com", call.Action!.Url);
    }

    [Fact]
    public void PagePrevAction_AtFirstPage_ClampsAndStaysAtZero()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = TwoPageDeckSettings());
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(2, true);
        worker.Tick();

        Assert.Equal(0, worker.GetCurrentPage("sim-0001"));
    }

    [Fact]
    public void PageGotoAction_JumpsToTheTargetPageClampedToRange()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var settings = TwoPageDeckSettings();
        // Out-of-range goto target must clamp to the last real page (index 1).
        settings.Deck.Pages[0].Slots[1].Action = new DeckAction { Type = "page", Op = "goto", Target = 99 };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = settings);
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(1, true);
        worker.Tick();
        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));

        simulated.Poke(1, false);
        worker.Tick();
        simulated.Poke(2, true); // page 1's slot 2: goto target=0
        worker.Tick();

        Assert.Equal(0, worker.GetCurrentPage("sim-0001"));
    }

    [Fact]
    public void PageIndicatorSlot_PressDoesNotDispatchToTheExecutor()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "pageIndicator" } } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();

        Assert.Empty(f.Executor.Calls);
    }

    [Fact]
    public void PageAction_InsideAFolder_ResetsFolderPathToRoot()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot
                            {
                                Folder = new DeckFolder
                                {
                                    Slots = { new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } } },
                                },
                            },
                        },
                    },
                    new DeckPage(),
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        // Enter the folder (key 0), then press the folder's own slot 0 (physical
        // key 1, since key 0 is reserved as Back inside the folder) - the "page
        // next" action.
        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));

        simulated.Poke(1, true);
        worker.Tick();

        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));
        Assert.Empty(worker.GetFolderPath("sim-0001"));
    }

    [Fact]
    public void GetCurrentPage_UnknownSerial_ReturnsZero()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        Assert.Equal(0, worker.GetCurrentPage("never-connected"));
    }

    /// <summary>
    /// Physical push-in feedback (Elgato-software parity): key-down pushes a
    /// scaled-and-inset variant of the key's uploaded image immediately, and
    /// key-up restores the exact original bytes.
    /// </summary>
    [Fact]
    public void HandleKeyDown_LeafActionWithUploadedImage_PushesAPressedVariant_KeyUpRestoresTheOriginal()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var original = StreamDeckProtocol.BuildBlankBmp(Mini.KeyPixelSize);
        var hash = StreamDeckImageCache.Hash(original);
        f.ImageCache.Store("sim-0001", hash, original);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } } } } } },
            ImageRefs = { ["0.0/0"] = hash },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.Equal(original, simulated.PeekKeyImage(0));

        simulated.Poke(0, true);
        worker.Tick();
        var pressed = simulated.PeekKeyImage(0);
        Assert.NotNull(pressed);
        Assert.NotEqual(original, pressed);
        // Same wire dimensions (re-encoded at the source's own size), so the
        // difference is pixel content (the background inset), not a resize
        // that would desync from the model's fixed wire image length.
        Assert.Equal(original.Length, pressed!.Length);

        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(original, simulated.PeekKeyImage(0));
    }

    [Fact]
    public void HandleKeyDown_TwoSlotsSharingTheSameUploadedImage_ProduceByteIdenticalPressedVariants()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var original = StreamDeckProtocol.BuildBlankBmp(Mini.KeyPixelSize);
        var hash = StreamDeckImageCache.Hash(original);
        f.ImageCache.Store("sim-0001", hash, original);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://a.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://b.example.com" } },
                        },
                    },
                },
            },
            ImageRefs = { ["0.0/0"] = hash, ["0.1/0"] = hash },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        var pressedA = simulated.PeekKeyImage(0);
        simulated.Poke(0, false);
        worker.Tick();

        simulated.Poke(1, true);
        worker.Tick();
        var pressedB = simulated.PeekKeyImage(1);

        Assert.NotNull(pressedA);
        Assert.Equal(pressedA, pressedB);
    }

    /// <summary>
    /// A monitoring key now gets the same push-in feedback a leaf action
    /// gets, rendered from its own last-pushed tile. Key-up must restore the
    /// original tile immediately (its own explicit SetKeyImage call), not
    /// merely rely on the same tick's later RefreshMonitoringKeys pass -
    /// asserted by requiring two separate SetKeyImage calls on release
    /// (the explicit restore, plus the normal repaint ForceMonitoringKeyRefresh
    /// still triggers), not one.
    /// </summary>
    [Fact]
    public void HandleKeyDown_MonitoringSlot_PushesAPressedVariant_KeyUpRestoresTheOriginalTileImmediately()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var original = simulated.PeekKeyImage(0);
        Assert.NotNull(original);

        simulated.Poke(0, true);
        worker.Tick();
        var pressed = simulated.PeekKeyImage(0);
        Assert.NotNull(pressed);
        Assert.NotEqual(original, pressed);

        var callsBeforeRelease = simulated.SetKeyImageCallCount;
        simulated.Poke(0, false);
        worker.Tick();

        Assert.Equal(2, simulated.SetKeyImageCallCount - callsBeforeRelease);
        Assert.Equal(original, simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// ForceMonitoringKeyRefresh (called from HandleKeyUp) drops only the
    /// tracked hash, not the bytes, precisely so a re-press before the next
    /// tick's repaint still has something to render an inset from. All three
    /// transitions land in one Tick() call (SimulatedStreamDeckSurface drains
    /// its whole queue), so RefreshMonitoringKeys never runs in between to
    /// repopulate the hash on its own.
    /// </summary>
    [Fact]
    public void HandleKeyDown_MonitoringSlot_RepressedBeforeTheNextTick_StillShowsAPressedInset()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var original = simulated.PeekKeyImage(0);

        simulated.Poke(0, true);
        simulated.Poke(0, false);
        simulated.Poke(0, true);
        worker.Tick();

        var pressedAgain = simulated.PeekKeyImage(0);
        Assert.NotNull(pressedAgain);
        Assert.NotEqual(original, pressedAgain);
    }

    /// <summary>
    /// A monitoring key never rendered yet has nothing in
    /// _monitoringLastPushedBytes for HandlePressVisual to render an inset
    /// from - skipped silently, no exception, no SetKeyImage call. The
    /// config is added to the store AFTER the connect tick (bypassing
    /// PushCurrentView/RefreshView, which would otherwise render it inline)
    /// so HandleKeyDown resolves a real monitoring slot whose tile genuinely
    /// has never been painted. RefreshMonitoringKeys, later in the same
    /// tick, would normally paint it first-time, but the key is already
    /// marked held by then, so it skips too - proving the guard, not a race.
    /// </summary>
    [Fact]
    public void HandleKeyDown_MonitoringSlotNeverRendered_SkipsSilentlyWithNoPressedImage()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick(); // connects with no persisted deck config - root view stays empty
        Assert.Null(simulated.PeekKeyImage(0));
        var callsBeforeHold = simulated.SetKeyImageCallCount;

        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } } },
            },
        });

        var exception = Record.Exception(() =>
        {
            simulated.Poke(0, true);
            worker.Tick();
        });

        Assert.Null(exception);
        Assert.Equal(callsBeforeHold, simulated.SetKeyImageCallCount);
        Assert.Null(simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// Image-refs v2: the key is page-qualified, so two pages that each use
    /// slot 0 at their own root resolve their own distinct uploaded image
    /// instead of one page's upload overwriting the other's.
    /// </summary>
    [Fact]
    public void Tick_TwoPagesReuseTheSameSlotIndex_EachPageResolvesItsOwnUploadedImage()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var page0Bytes = new byte[] { 1, 1, 1 };
        var page1Bytes = new byte[] { 2, 2, 2 };
        var page0Hash = StreamDeckImageCache.Hash(page0Bytes);
        var page1Hash = StreamDeckImageCache.Hash(page1Bytes);
        f.ImageCache.Store("sim-0001", page0Hash, page0Bytes);
        f.ImageCache.Store("sim-0001", page1Hash, page1Bytes);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } } } },
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page1.example.com" } } } },
                },
            },
            ImageRefs = { ["0.0/0"] = page0Hash, ["1.0/0"] = page1Hash },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.Equal(page0Bytes, simulated.PeekKeyImage(0));

        Assert.True(worker.SetNav("sim-0001", 1, Array.Empty<int>()));

        Assert.Equal(page1Bytes, simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// A legacy pre-v2 key ("0/0", no leading page segment) is an orphan
    /// under image-refs v2 - ResolveSlotImage only builds page-qualified
    /// keys, so it never resolves; no migration re-keys it.
    /// </summary>
    [Fact]
    public void Tick_LegacyUnqualifiedImageRefKey_NeverResolves()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var bytes = StreamDeckProtocol.BuildBlankBmp(Mini.KeyPixelSize);
        var hash = StreamDeckImageCache.Hash(bytes);
        f.ImageCache.Store("sim-0001", hash, bytes);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } } } } } },
            ImageRefs = { ["0/0"] = hash },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        Assert.Null(simulated.PeekKeyImage(0));
    }

    [Fact]
    public void HandleKeyDown_OnAPageNavKey_MatchesAPureNavRepaintWithNoExtraPressedPush()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = TwoPageDeckSettings());
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        var beforeNav = simulated.SetKeyImageCallCount;
        worker.SetNav("sim-0001", 1, Array.Empty<int>());
        var navOnlyDelta = simulated.SetKeyImageCallCount - beforeNav;
        var navOnlyImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        worker.SetNav("sim-0001", 0, Array.Empty<int>());

        var beforePress = simulated.SetKeyImageCallCount;
        simulated.Poke(0, true); // page 0 slot 0 is "page next" (TwoPageDeckSettings)
        worker.Tick();
        var pressDelta = simulated.SetKeyImageCallCount - beforePress;
        var pressImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));
        Assert.Equal(navOnlyDelta, pressDelta);
        Assert.Equal(navOnlyImages, pressImages);
    }

    [Fact]
    public void HandleKeyDown_OnAFolderSlot_MatchesAPureNavRepaintWithNoExtraPressedPush()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        var beforeNav = simulated.SetKeyImageCallCount;
        worker.SetNav("sim-0001", 0, new[] { 0 });
        var navOnlyDelta = simulated.SetKeyImageCallCount - beforeNav;
        var navOnlyImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        worker.SetNav("sim-0001", 0, Array.Empty<int>());

        var beforePress = simulated.SetKeyImageCallCount;
        simulated.Poke(0, true); // the only root slot: a folder
        worker.Tick();
        var pressDelta = simulated.SetKeyImageCallCount - beforePress;
        var pressImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));
        Assert.Equal(navOnlyDelta, pressDelta);
        Assert.Equal(navOnlyImages, pressImages);
    }

    private static byte[]?[] SnapshotKeyImages(SimulatedStreamDeckSurface surface, int keyCount)
    {
        var snapshot = new byte[]?[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            snapshot[i] = surface.PeekKeyImage(i);
        }
        return snapshot;
    }

    private static bool ImageEquals(byte[]? a, byte[]? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.AsSpan().SequenceEqual(b);
    }

    [Fact]
    public void Tick_MonitoringSlot_RendersAndPushesAValidWireImage_WithoutTouchingImageRefsOrTheCache()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        var bytes = simulated.PeekKeyImage(0);
        Assert.NotNull(bytes);
        Assert.True(Mini.IsValidWireImageLength(bytes!.Length));
        Assert.Empty(f.Store.Load().StreamDeck.Decks["sim-0001"].ImageRefs);
    }

    [Fact]
    public void Tick_MonitoringSlot_UnchangedSensorValue_SkipsTheSecondPush()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        // Connect drives PushCurrentView's two-pass repaint for the one
        // monitoring slot: an empty placeholder, then the real tile.
        worker.Tick();
        Assert.Equal(2, simulated.SetKeyImageCallCount);

        worker.Tick();
        worker.Tick();

        Assert.Equal(2, simulated.SetKeyImageCallCount);
    }

    /// <summary>
    /// The rendered tile for a Temperature sensor differs between Units.MonitoringTempUnit
    /// "c" and "f" for the same reading, proving BuildMonitoringTileInput actually
    /// threads the preference into DeckMonitoringFormat.ResolveValueText's text
    /// (and so into the rendered pixels), not just leaving the service-formatted
    /// Celsius string on the physical key regardless of the app preference.
    /// </summary>
    [Fact]
    public void Tick_MonitoringSlot_TemperatureSensor_RendersDifferentTextForCelsiusVsFahrenheit()
    {
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };

        var celsiusFixtures = NewFixtures(devicePresent: false);
        celsiusFixtures.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Temperature", Value = 65.3f, Units = "°C", Formatted = "65.3 °C", Parent = new SensorParent() },
        };
        var celsiusSurface = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        celsiusFixtures.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        // Both workers stay alive (using var, not a nested block) until the
        // bytes are captured: worker Dispose() resets the surface, which
        // clears its key images, so reading PeekKeyImage after disposal would
        // see a blanked tile instead of the rendered one.
        using var celsiusWorker = NewWorker(celsiusFixtures, celsiusSurface);
        celsiusWorker.Tick();
        var celsiusBytes = celsiusSurface.PeekKeyImage(0);

        var fahrenheitFixtures = NewFixtures(devicePresent: false);
        fahrenheitFixtures.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Temperature", Value = 65.3f, Units = "°C", Formatted = "65.3 °C", Parent = new SensorParent() },
        };
        var fahrenheitSurface = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        fahrenheitFixtures.Store.Update(s =>
        {
            s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
            {
                Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
            };
            s.Units.MonitoringTempUnit = "f";
        });
        using var fahrenheitWorker = NewWorker(fahrenheitFixtures, fahrenheitSurface);
        fahrenheitWorker.Tick();
        var fahrenheitBytes = fahrenheitSurface.PeekKeyImage(0);

        Assert.NotNull(celsiusBytes);
        Assert.NotNull(fahrenheitBytes);
        Assert.False(ImageEquals(celsiusBytes, fahrenheitBytes));
    }

    /// <summary>
    /// A monitoring key that already settled on a hash-skip (no push while
    /// the reading is unchanged) must still repaint the very next tick after
    /// Units.MonitoringTempUnit changes: the rendered ValueText changes even
    /// though the underlying sensor reading did not, so the wire-bytes hash
    /// changes too and the push is not masked by the unchanged-reading skip.
    /// </summary>
    [Fact]
    public void Tick_MonitoringSlot_TempUnitPreferenceChangesMidSession_RepaintsWithoutAValueChange()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Temperature", Value = 65.3f, Units = "°C", Formatted = "65.3 °C", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        worker.Tick();
        worker.Tick();
        Assert.Equal(2, simulated.SetKeyImageCallCount);

        f.Store.Update(s => s.Units.MonitoringTempUnit = "f");
        worker.Tick();

        Assert.True(simulated.SetKeyImageCallCount > 2);
    }

    [Fact]
    public void Tick_MonitoringSlot_UnresolvedSensor_RendersAPlaceholderInsteadOfLeavingTheKeyBlank()
    {
        var f = NewFixtures(devicePresent: false); // no CpuSensors configured: "cpu/missing" never resolves
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        var bytes = simulated.PeekKeyImage(0);
        Assert.NotNull(bytes);
        Assert.True(Mini.IsValidWireImageLength(bytes!.Length));
    }

    [Fact]
    public void Tick_MonitoringSlot_UnresolvedSensor_PlaceholderIsHashSkippedOnRepeatTicks()
    {
        var f = NewFixtures(devicePresent: false);
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        // Connect drives PushCurrentView's two-pass repaint: pass 1's empty
        // placeholder and pass 2's unresolved-sensor render happen to be
        // pixel-identical here, but pass 1 never hash-checks, so both push.
        worker.Tick();
        Assert.Equal(2, simulated.SetKeyImageCallCount);

        worker.Tick();
        worker.Tick();

        Assert.Equal(2, simulated.SetKeyImageCallCount);
    }

    [Fact]
    public void Tick_MonitoringSlot_PassesLabelTextAndFixedScaleThroughToTheRenderedTile()
    {
        HardwareSensor[] Sensors() => new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Clock", Value = 4500f, Formatted = "4500MHz", Parent = new SensorParent() },
        };

        var legacy = NewFixtures(devicePresent: false);
        legacy.Sensors.CpuSensors = Sensors();
        var legacyAction = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" };
        var legacySurface = new SimulatedStreamDeckSurface(Mini, "sim-legacy");
        legacy.Store.Update(s => s.StreamDeck.Decks["sim-legacy"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = legacyAction } } } } },
        });
        using var legacyWorker = NewWorker(legacy, legacySurface);
        legacyWorker.Tick();
        var legacyBytes = legacySurface.PeekKeyImage(0);

        var v3 = NewFixtures(devicePresent: false);
        v3.Sensors.CpuSensors = Sensors();
        var v3Action = new DeckAction
        {
            Type = "monitoring",
            Category = "cpu",
            Sensor = "cpu/core0",
            Style = "line",
            LabelText = "Custom Label",
            Scale = "fixed",
            Min = 0,
            Max = 10000,
        };
        var v3Surface = new SimulatedStreamDeckSurface(Mini, "sim-v3");
        v3.Store.Update(s => s.StreamDeck.Decks["sim-v3"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = v3Action } } } } },
        });
        using var v3Worker = NewWorker(v3, v3Surface);
        v3Worker.Tick();
        var v3Bytes = v3Surface.PeekKeyImage(0);

        Assert.NotNull(legacyBytes);
        Assert.NotNull(v3Bytes);
        Assert.NotEqual(legacyBytes, v3Bytes);
    }

    [Fact]
    public void PushCurrentView_NeverClearsALiveMonitoringKey()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "radial" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.NotNull(simulated.PeekKeyImage(0));

        // RefreshView drives PushCurrentView with viewChanged=false; it must
        // not blank the monitoring key the way a real navigation would.
        worker.RefreshView("sim-0001");

        Assert.NotNull(simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// Regression for the reported flicker bug: editing an unrelated static
    /// key's config (as the deck-config PUT route's debounced saves do on
    /// every keystroke) must not touch a live monitoring tile whose reading
    /// has not changed - no placeholder blank, no redundant HID write.
    /// </summary>
    [Fact]
    public void RefreshView_UnrelatedStaticKeyEdit_LeavesAnUnchangedMonitoringTileAlone()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var original = StreamDeckProtocol.BuildBlankBmp(Mini.KeyPixelSize);
        var hash = StreamDeckImageCache.Hash(original);
        f.ImageCache.Store("sim-0001", hash, original);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            // "number" style renders from ValueText alone, so its wire
                            // bytes stay stable across ticks unless the sensor value changes.
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } },
                        },
                    },
                },
            },
            ImageRefs = { ["0.1/0"] = hash },
        });
        using var worker = NewWorker(f, simulated);
        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        worker.Tick(); // connect - both keys settle
        var beforeMonitoringBytes = simulated.PeekKeyImage(0);
        Assert.NotNull(beforeMonitoringBytes);
        var callsBefore = simulated.SetKeyImageCallCount;
        var captured = CaptureBroadcasts(f.Hub);

        // The unrelated static key's own edit (e.g. a color drag), as the PUT
        // config route would apply, with the monitoring reading unchanged.
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"].Deck.Pages[0].Slots[1].Color = "#ff0000");
        worker.RefreshView("sim-0001");

        // Only the static key's own unconditional pass-1 re-blit writes to
        // HID - the monitoring tile's unchanged pixels never get re-pushed.
        Assert.Equal(callsBefore + 1, simulated.SetKeyImageCallCount);
        Assert.Equal(beforeMonitoringBytes, simulated.PeekKeyImage(0));

        // The broadcast hash was cleared regardless (the editor's own frame
        // cache can be stale after undo/redo), so the tile still re-sends
        // once - never a blank placeholder followed by the real content.
        var tiles = captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles).ToList();
        Assert.Single(tiles);
    }

    /// <summary>
    /// The monitoring key's own config change (e.g. its background color) is
    /// exactly the case a same-view refresh must still repaint - the pixels
    /// actually changed, so PushMonitoringKey's own hash compare pushes it
    /// within the same tick, without needing PushCurrentView's invalidation.
    /// </summary>
    [Fact]
    public void RefreshView_MonitoringKeysOwnConfigChange_StillPushesToHidAndRebroadcastsWithinTheTick()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } } } },
        });
        using var worker = NewWorker(f, simulated);
        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        worker.Tick();
        var beforeBytes = simulated.PeekKeyImage(0);
        Assert.NotNull(beforeBytes);
        var callsBefore = simulated.SetKeyImageCallCount;
        var captured = CaptureBroadcasts(f.Hub);

        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"].Deck.Pages[0].Slots[0].Color = "#00ff00");
        worker.RefreshView("sim-0001");

        Assert.Equal(callsBefore + 1, simulated.SetKeyImageCallCount);
        Assert.NotEqual(beforeBytes, simulated.PeekKeyImage(0));

        var tiles = captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles).ToList();
        Assert.Single(tiles);
    }

    /// <summary>A slot retyped to monitoring by a same-view refresh must show its real tile from the same call, never the departing slot type's stale pixels.</summary>
    [Fact]
    public void RefreshView_SlotBecomesMonitoring_RendersTheRealTileInTheSameCall()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.Null(simulated.PeekKeyImage(0)); // no uploaded image for the leaf slot

        f.Store.Update(s =>
            s.StreamDeck.Decks["sim-0001"].Deck.Pages[0].Slots[0] =
                new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } });
        worker.RefreshView("sim-0001");

        var bytes = simulated.PeekKeyImage(0);
        Assert.NotNull(bytes);
        Assert.True(Mini.IsValidWireImageLength(bytes!.Length));
    }

    /// <summary>A slot retyped away from monitoring by a same-view refresh gets its static image/ClearKey from pass 1, unconditional on viewChanged.</summary>
    [Fact]
    public void RefreshView_SlotStopsBeingMonitoring_ClearsTheKeyFromPassOne()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.NotNull(simulated.PeekKeyImage(0));

        f.Store.Update(s =>
            s.StreamDeck.Decks["sim-0001"].Deck.Pages[0].Slots[0] =
                new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } }); // no ImageRef uploaded
        worker.RefreshView("sim-0001");

        Assert.Null(simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// Weather has no stored slot image and no HID hash gate of its own
    /// (RenderWeatherKeys repaints it unconditionally on its own round-robin
    /// cadence), so pass 1 must not clear it on a same-view refresh - there
    /// is nothing to immediately repaint it until the next Tick(). A real
    /// navigation still flushes it, matching monitoring's placeholder intent.
    /// </summary>
    [Fact]
    public void RefreshView_WeatherKey_LeavesLivePixelsAlone_ButNavigationStillClearsIt()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "weather" } } } } },
            },
        });
        using var worker = NewWorker(f, simulated); // no IWeatherProvider wired - RefreshWeatherKeys never repaints on Tick()
        worker.Tick();
        Assert.Null(simulated.PeekKeyImage(0));

        // Stand in for a real weather tile RefreshWeatherKeys would have
        // already pushed once a provider resolved a snapshot.
        simulated.SetKeyImage(0, new byte[] { 1, 2, 3 });

        worker.RefreshView("sim-0001");
        Assert.Equal(new byte[] { 1, 2, 3 }, simulated.PeekKeyImage(0));

        Assert.True(worker.SetNav("sim-0001", 0, Array.Empty<int>()));
        Assert.Null(simulated.PeekKeyImage(0));
    }

    [Fact]
    public void Tick_MonitoringSlot_RepaintsAfterNavigatingAwayAndBackWithAnUnchangedReading()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page1.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        Assert.NotNull(simulated.PeekKeyImage(0));

        // Page 1's key 0 has no ImageRef, so navigating there clears the
        // physical key the monitoring slot used to own.
        simulated.Poke(1, true);
        worker.Tick();
        simulated.Poke(1, false);
        worker.Tick();
        Assert.Null(simulated.PeekKeyImage(0));

        // Navigate back with the sensor reading unchanged from the first
        // push. A quantized-unchanged reading must not suppress the repaint
        // just because the key was blanked while on page 1 in between.
        simulated.Poke(1, true);
        worker.Tick();
        simulated.Poke(1, false);
        worker.Tick();
        worker.Tick();

        Assert.NotNull(simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// PushCurrentView paints every visible monitoring key immediately on
    /// connect (an uncapped nav-style repaint - see the PushCurrentView
    /// monitoring-slot branch), so the round-robin cap only bites once the
    /// view is already established and a later value change needs every key
    /// re-rendered. "number" style renders from ValueText alone, so its wire
    /// bytes stay stable across ticks unless the sensor value itself changes.
    /// </summary>
    [Fact]
    public void Tick_MoreMonitoringKeysThanTheCap_RoundRobinsAcrossTicks()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 1f, Formatted = "1%", Parent = new SensorParent() },
        };
        // One more monitoring slot than the per-tick push cap.
        var slots = new List<DeckSlot>();
        for (var i = 0; i < Mini.KeyCount - 1; i++)
        {
            slots.Add(new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } });
        }
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        var baseline = SnapshotKeyImages(simulated, slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            Assert.NotNull(baseline[i]);
        }

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 99f, Formatted = "99%", Parent = new SensorParent() },
        };
        worker.Tick();
        var afterOneTick = SnapshotKeyImages(simulated, slots.Count);
        var updated = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            if (!ImageEquals(baseline[i], afterOneTick[i]))
            {
                updated++;
            }
        }
        Assert.Equal(MonitoringPushCapPerTickForTests, updated);

        worker.Tick();
        var afterTwoTicks = SnapshotKeyImages(simulated, slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            Assert.False(ImageEquals(baseline[i], afterTwoTicks[i]));
        }
    }

    /// <summary>Mirrors StreamDeckConnectionWorker's private MonitoringPushCapPerTick, which the test project cannot see directly.</summary>
    private const int MonitoringPushCapPerTickForTests = 4;

    /// <summary>
    /// While a monitoring key is physically held, RefreshMonitoringKeys must
    /// skip pushing it (avoiding a race with the pressed overlay) even though
    /// sampling still runs every tick - the only push during the hold is
    /// HandlePressVisual's own pressed inset, rendered from the key's last
    /// tile (still the pre-hold 10% reading, not the changed 90% one).
    /// Release forces a fresh push regardless of the hash-skip optimization,
    /// so the tile catches up immediately.
    /// </summary>
    [Fact]
    public void MonitoringSlot_HeldDuringATick_OnlyThePressedInsetPushes_ReleaseForcesAFreshOne()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        var initial = simulated.PeekKeyImage(0);
        Assert.NotNull(initial);
        var callsBeforeHold = simulated.SetKeyImageCallCount;

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 90f, Formatted = "90%", Parent = new SensorParent() },
        };
        simulated.Poke(0, true);
        worker.Tick();

        var pressed = simulated.PeekKeyImage(0);
        Assert.NotEqual(initial, pressed);
        Assert.Equal(callsBeforeHold + 1, simulated.SetKeyImageCallCount);

        simulated.Poke(0, false);
        worker.Tick();

        Assert.NotEqual(initial, simulated.PeekKeyImage(0));
        Assert.NotEqual(pressed, simulated.PeekKeyImage(0));
        Assert.True(simulated.SetKeyImageCallCount > callsBeforeHold + 1);
    }

    /// <summary>
    /// Regression for the reported bug: pressing a page-nav key that reveals
    /// a monitoring slot must not leave the previous page's tile up. A single
    /// Tick() both processes the physical press (PumpSimulatedInput mirrors
    /// a real HID input report) and, via that press's own PushCurrentView
    /// call, repaints the newly visible key before this tick's monitoring
    /// pass ever runs.
    /// </summary>
    [Fact]
    public void Tick_NavigatingToAMonitoringSlot_PaintsItInTheSamePushCurrentViewPass()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number", LabelText = "PageZero" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number", LabelText = "PageOne" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        var pageZeroImage = simulated.PeekKeyImage(0);
        Assert.NotNull(pageZeroImage);

        simulated.Poke(1, true);
        worker.Tick();

        var pageOneImage = simulated.PeekKeyImage(0);
        Assert.NotNull(pageOneImage);
        Assert.NotEqual(pageZeroImage, pageOneImage);
    }

    /// <summary>Same nav path, but the landing key's sensor id never resolves - it must still get a placeholder, never the departing page's pixels.</summary>
    [Fact]
    public void Tick_NavigatingToAnUnresolvedMonitoringSlot_PushesAPlaceholderNotStalePixels()
    {
        var f = NewFixtures(devicePresent: false); // no CpuSensors configured: "cpu/missing" never resolves
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Label = "PageZero", Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.Null(simulated.PeekKeyImage(0)); // page 0's slot has no ImageRef

        simulated.Poke(1, true);
        worker.Tick();

        var bytes = simulated.PeekKeyImage(0);
        Assert.NotNull(bytes);
        Assert.True(Mini.IsValidWireImageLength(bytes!.Length));
    }

    /// <summary>
    /// Regression for the reported bug: on a view with more than one
    /// monitoring key, PushCurrentView must push every key's empty
    /// placeholder (pass 1) before any key's real content (pass 2) - so the
    /// whole view goes clean instantly on nav, instead of an earlier key's
    /// expensive sample+render delaying a later key's placeholder while it
    /// still shows the previous view's pixels.
    /// </summary>
    [Fact]
    public void PushCurrentView_MultipleMonitoringKeys_PushesEveryPlaceholderBeforeAnyRealContent()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var slots = new List<DeckSlot>
        {
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
            new() { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } },
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick(); // connect drives one PushCurrentView call

        var monitoringPushes = new List<int>();
        foreach (var key in simulated.SetKeyImageOrder)
        {
            if (key == 0 || key == 2)
            {
                monitoringPushes.Add(key);
            }
        }
        // Both keys' placeholders (pass 1) precede both keys' real content
        // (pass 2) - never key 0's real content ahead of key 2's placeholder.
        Assert.Equal(new[] { 0, 2, 0, 2 }, monitoringPushes);
    }

    /// <summary>
    /// The nav's own PushCurrentView pushes twice for the monitoring key (an
    /// empty placeholder, then the real tile - see PushCurrentView's two-pass
    /// repaint), and that same tick's own RefreshMonitoringKeys pass must not
    /// push a third time for the nav, nor push again on the next tick while
    /// the reading is unchanged.
    /// </summary>
    [Fact]
    public void Tick_NavigatingToAMonitoringSlot_TheFollowingTickDoesNotRepushAnUnchangedTile()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var callsBeforeNav = simulated.SetKeyImageCallCount;

        simulated.Poke(1, true);
        worker.Tick();
        simulated.Poke(1, false);
        worker.Tick();
        var callsAfterNav = simulated.SetKeyImageCallCount;
        Assert.Equal(callsBeforeNav + 2, callsAfterNav);

        worker.Tick();
        Assert.Equal(callsAfterNav, simulated.SetKeyImageCallCount);
    }

    [Fact]
    public void SampleMonitoringHistory_SensorGoesUnresolvedThenReturns_DoesNotAppendZeroTrough()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        worker.Tick();
        worker.Tick();

        var beforeUnresolved = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(beforeUnresolved);
        Assert.All(beforeUnresolved!, v => Assert.Equal(10f, v));
        var sampleCount = beforeUnresolved!.Count;

        // The sensor id disappears (device unplugged, id gone). More ticks
        // than MonitoringHistoryLength so a zero-trough bug would clearly
        // show as both growth and altered values.
        f.Sensors.CpuSensors = Array.Empty<HardwareSensor>();
        for (var i = 0; i < 45; i++)
        {
            worker.Tick();
        }

        var whileUnresolved = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(whileUnresolved);
        Assert.Equal(sampleCount, whileUnresolved!.Count);
        Assert.All(whileUnresolved!, v => Assert.Equal(10f, v));

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 55f, Formatted = "55%", Parent = new SensorParent() },
        };
        worker.Tick();

        var afterResolved = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(afterResolved);
        Assert.Equal(sampleCount + 1, afterResolved!.Count);
        Assert.Equal(55f, afterResolved![^1]);
    }

    [Fact]
    public void SampleMonitoringHistory_NeverResolved_HistoryStaysEmpty()
    {
        var f = NewFixtures(devicePresent: false); // no CpuSensors configured: "cpu/missing" never resolves
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        for (var i = 0; i < 5; i++)
        {
            worker.Tick();
        }

        Assert.Null(worker.MonitoringHistoryForTests("sim-0001", 0, "0"));
    }

    /// <summary>
    /// The deck-config PUT route saves the new config then calls RefreshView
    /// (PushCurrentView) while the deck stays connected - the eviction path
    /// under test must run there, not only on disconnect.
    /// </summary>
    [Fact]
    public void PushCurrentView_ConfigEditRetypesAMonitoringSlot_EvictsItsHistoryAndHash_SurvivingSlotHistoryUntouched()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 7f, Formatted = "7%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        worker.Tick();
        worker.Tick();

        var survivorHistoryBefore = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(survivorHistoryBefore);
        Assert.NotNull(worker.MonitoringHistoryForTests("sim-0001", 0, "1"));
        Assert.True(worker.HasMonitoringHashForTests("sim-0001", 0, "1"));

        // Config edit retypes slot 1 away from monitoring, as the deck-config
        // PUT route's persisted change would.
        f.Store.Update(s =>
        {
            s.StreamDeck.Decks["sim-0001"].Deck.Pages[0].Slots[1] =
                new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } };
        });
        worker.RefreshView("sim-0001");

        Assert.Null(worker.MonitoringHistoryForTests("sim-0001", 0, "1"));
        Assert.False(worker.HasMonitoringHashForTests("sim-0001", 0, "1"));

        // RefreshView's own PushCurrentView call inline-samples every still-
        // visible monitoring slot (see the PushCurrentView monitoring-slot
        // branch), so slot 0 gains exactly one more sample - it must not be
        // reset to a fresh single-sample buffer by the eviction pass.
        var survivorHistoryAfter = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(survivorHistoryAfter);
        Assert.Equal(survivorHistoryBefore!.Count + 1, survivorHistoryAfter!.Count);
        for (var i = 0; i < survivorHistoryBefore.Count; i++)
        {
            Assert.Equal(survivorHistoryBefore[i], survivorHistoryAfter[i]);
        }
    }

    /// <summary>Deleting a page orphans that page's monitoring entries the same way retyping a slot does; ResolveSlot returns null once the page index is out of range.</summary>
    [Fact]
    public void PushCurrentView_ConfigEditDeletesAPage_EvictsThatPagesMonitoringHistory()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 3f, Formatted = "3%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } } } },
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        Assert.True(worker.SetNav("sim-0001", 1, Array.Empty<int>()));
        worker.Tick();
        worker.Tick();
        Assert.NotNull(worker.MonitoringHistoryForTests("sim-0001", 1, "0"));

        // Config edit deletes page 1; the deck's tracked current page (still
        // 1 at this point) will clamp back to 0 once PushCurrentView runs.
        f.Store.Update(s =>
        {
            s.StreamDeck.Decks["sim-0001"].Deck.Pages.RemoveAt(1);
        });
        worker.RefreshView("sim-0001");

        Assert.Null(worker.MonitoringHistoryForTests("sim-0001", 1, "0"));
    }

    /// <summary>
    /// Regression: a config edit that deletes the currently displayed page
    /// moves ClampCurrentPageLocked's resolved page away from the tracked
    /// one even though RefreshView's call site passes viewChanged=false.
    /// PushCurrentView must detect the mismatch and escalate to a real view
    /// change, so the landing view's weather key is cleared and its
    /// monitoring key gets the placeholder-then-real two-pass repaint
    /// instead of only the real content pushed once.
    /// </summary>
    [Fact]
    public void RefreshView_ConfigDeletesTheCurrentlyDisplayedPage_EscalatesToAFullViewChange()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "weather" } },
                        },
                    },
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } } } },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick(); // connect at page 0

        Assert.True(worker.SetNav("sim-0001", 1, Array.Empty<int>()));
        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));

        // Stand in for a live weather tile the deck would already be showing.
        simulated.SetKeyImage(1, new byte[] { 9, 9, 9 });
        var writesBefore = simulated.SetKeyImageOrder.Count;

        // Deletes page 1, the currently displayed page, then a same-view
        // refresh (viewChanged=false at the call site) - as the deck-config
        // PUT route applies an edit.
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"].Deck.Pages.RemoveAt(1));
        worker.RefreshView("sim-0001");

        Assert.Equal(0, worker.GetCurrentPage("sim-0001")); // clamped back onto page 0

        // The weather key on the landing view was cleared, not left showing
        // the departed page's foreign pixels.
        Assert.Null(simulated.PeekKeyImage(1));

        // The monitoring key got the placeholder-then-real two-pass repaint
        // a real view change gets, not a single same-view repaint.
        var monitoringWritesDuringRefresh = simulated.SetKeyImageOrder.Skip(writesBefore).Count(k => k == 0);
        Assert.Equal(2, monitoringWritesDuringRefresh);
    }

    /// <summary>
    /// Regression: a config edit that removes the folder currently being
    /// shown makes ResolveView return null for the tracked folder path, and
    /// PushCurrentView's own fallback resets it to root - a same-view
    /// refresh landing on a different view the same way a deleted page does.
    /// </summary>
    [Fact]
    public void RefreshView_ConfigRemovesTheDisplayedFolder_EscalatesToAFullViewChange()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Action = new DeckAction { Type = "weather" } } } } },
                            new DeckSlot { Action = new DeckAction { Type = "weather" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick(); // connect at root

        Assert.True(worker.SetNav("sim-0001", 0, new[] { 0 })); // into the folder
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));

        // Stand in for the folder's own live weather tile at physical key 1.
        simulated.SetKeyImage(1, new byte[] { 9, 9, 9 });

        // Retypes slot 0 away from a folder - the config edit removes the
        // folder the deck is currently showing - then a same-view refresh.
        f.Store.Update(s =>
            s.StreamDeck.Decks["sim-0001"].Deck.Pages[0].Slots[0] =
                new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } });
        worker.RefreshView("sim-0001");

        Assert.Empty(worker.GetFolderPath("sim-0001"));
        // Root slot 1 is a weather slot too - cleared, not left showing the
        // departed folder's foreign pixels.
        Assert.Null(simulated.PeekKeyImage(1));
    }

    /// <summary>
    /// DisconnectAll (driven here via the feature gate turning off, the same
    /// path service shutdown takes) restores the deck's persisted brightness
    /// and fires a firmware Reset() on every real surface before tearing it
    /// down, so it shows the built-in Elgato boot logo instead of freezing
    /// on its last live frame.
    /// </summary>
    [Fact]
    public void DisconnectAll_RealSurface_RestoresBrightnessAndResetsBeforeTeardown()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { Brightness = 55 });
        using var worker = NewWorker(f);
        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        dev.FeatureWrites.Clear(); // drop the connect-time brightness write

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();

        Assert.Contains(StreamDeckProtocol.BuildBrightnessFeature(55), dev.FeatureWrites);
        Assert.Contains(StreamDeckProtocol.BuildResetFeature(), dev.FeatureWrites);
        Assert.True(dev.Disposed);
    }

    /// <summary>
    /// FastServiceShutdown's real-quit path (SCM stop, /service/stop, tray
    /// shut down) resolves the worker and calls this directly - it must
    /// reset every connected real surface, skip the simulated one, and
    /// leave tracked state intact (unlike DisconnectAll, nothing tears the
    /// surface down since the process is exiting anyway).
    /// </summary>
    [Fact]
    public void ResetConnectedSurfacesForShutdown_ResetsRealSurfaces_SkipsSimulated_LeavesStateTracked()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { Brightness = 55 });
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        dev.FeatureWrites.Clear(); // drop the connect-time brightness write

        worker.ResetConnectedSurfacesForShutdown();

        Assert.Contains(StreamDeckProtocol.BuildBrightnessFeature(55), dev.FeatureWrites);
        Assert.Contains(StreamDeckProtocol.BuildResetFeature(), dev.FeatureWrites);
        Assert.False(dev.Disposed);
        Assert.Equal(0, simulated.ResetCount);
        Assert.True(simulated.IsConnected);
        Assert.Equal(2, worker.Surfaces.Count);
    }

    /// <summary>The simulated deck is a shared DI singleton other routes hold a reference to; DisconnectAll must never reset or dispose it.</summary>
    [Fact]
    public void DisconnectAll_SkipsTheSimulatedSurface_NeverResetsIt()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();

        Assert.True(simulated.IsConnected);
        Assert.Equal(0, simulated.ResetCount);
    }

    /// <summary>A second DisconnectAll pass (e.g. the gate staying off across ticks) finds an already-empty surface set and does nothing further.</summary>
    [Fact]
    public void DisconnectAll_CalledAgainWithNothingConnected_IsANoOp()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        using var worker = NewWorker(f);
        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();
        var writesAfterFirstDisconnect = dev.FeatureWrites.Count;
        Assert.True(dev.Disposed);
        Assert.Empty(worker.Surfaces);

        worker.Tick();

        Assert.Equal(writesAfterFirstDisconnect, dev.FeatureWrites.Count);
        Assert.Empty(worker.Surfaces);
    }

    /// <summary>An unplug (or a dead read handle) removes the surface without ever resetting it - a vanished device cannot be written to.</summary>
    [Fact]
    public void Tick_DeviceUnplugged_DoesNotResetTheSurface()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var usb = new MutableUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId });
        var presence = new HardwarePresence(usb);
        using var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors);

        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        dev.FeatureWrites.Clear();

        usb.Devices.Clear();
        f.Hid.ByProductId.Clear();
        worker.Tick();

        Assert.DoesNotContain(StreamDeckProtocol.BuildResetFeature(), dev.FeatureWrites);
        Assert.True(dev.Disposed);
    }

    private static List<(string Topic, byte[] Payload)> CaptureBroadcasts(MultiplexHub hub)
    {
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        return captured;
    }

    private static JsonElement TileFramePayload((string Topic, byte[] Payload) captured)
    {
        using var doc = JsonDocument.Parse(captured.Payload);
        return doc.RootElement.GetProperty("d").Clone();
    }

    [Fact]
    public void PushMonitoringKey_SubscriberPresent_BroadcastsStreamdeckTilesFrame_ForRootSlot()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var slots = new List<DeckSlot>
        {
            new(), new(), new(),
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);
        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        worker.Tick(); // connect - establishes the baseline hash, uncaptured
        var captured = CaptureBroadcasts(f.Hub);

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 90f, Formatted = "90%", Parent = new SensorParent() },
        };
        worker.Tick();

        var tiles = captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles).ToList();
        var tile = Assert.Single(tiles);
        var d = TileFramePayload(tile);
        Assert.Equal("sim-0001", d.GetProperty("serial").GetString());
        Assert.Equal(0, d.GetProperty("page").GetInt32());
        Assert.Equal("3", d.GetProperty("slotPath").GetString());
        Assert.Equal("image/jpeg", d.GetProperty("mime").GetString());
        var bytes = Convert.FromBase64String(d.GetProperty("data").GetString()!);
        Assert.True(bytes.Length > 2);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    /// <summary>Same contract as the root-slot case, but the visible key lives inside a folder - slotPath must be the folder-slots-array-index dot-chain ("3.2"), never the page-prefixed ImageRefs form ("0.3.2").</summary>
    [Fact]
    public void PushMonitoringKey_SubscriberPresent_BroadcastsStreamdeckTilesFrame_ForFolderNestedSlot()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var folderSlots = new List<DeckSlot>
        {
            new(), new(),
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
        };
        var rootSlots = new List<DeckSlot>
        {
            new(), new(), new(),
            new() { Folder = new DeckFolder { Slots = folderSlots } },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = rootSlots } } },
        });
        using var worker = NewWorker(f, simulated);
        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        worker.Tick(); // connect at root
        Assert.True(worker.SetNav("sim-0001", 0, new[] { 3 })); // into the folder at root slot 3
        var captured = CaptureBroadcasts(f.Hub);

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 90f, Formatted = "90%", Parent = new SensorParent() },
        };
        worker.Tick();

        var tiles = captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles).ToList();
        var tile = Assert.Single(tiles);
        var d = TileFramePayload(tile);
        Assert.Equal(0, d.GetProperty("page").GetInt32());
        Assert.Equal("3.2", d.GetProperty("slotPath").GetString());
    }

    [Fact]
    public void PushMonitoringKey_UnchangedReading_DoesNotRebroadcast()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var slots = new List<DeckSlot> { new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);
        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        worker.Tick(); // connect - establishes the hash, uncaptured
        var captured = CaptureBroadcasts(f.Hub);

        worker.Tick(); // sensor unchanged - same quantized reading, same rendered pixels

        Assert.DoesNotContain(captured, c => c.Topic == PanelTopics.StreamDeckTiles);
    }

    /// <summary>No subscriber means no streamdeckTiles broadcast. The source also gates the JPEG encode itself behind the same TopicHasSubscribers check, but that is not independently observable from this test.</summary>
    [Fact]
    public void PushMonitoringKey_NoSubscribers_DoesNotBroadcast()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var slots = new List<DeckSlot> { new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);
        var captured = CaptureBroadcasts(f.Hub);

        worker.Tick(); // connect
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 90f, Formatted = "90%", Parent = new SensorParent() },
        };
        worker.Tick(); // reading changes, but nobody subscribed to streamdeckTiles

        Assert.DoesNotContain(captured, c => c.Topic == PanelTopics.StreamDeckTiles);
    }

    /// <summary>The topic carries no snapshot provider, so a fresh subscriber only sees frames the worker itself re-broadcasts on the 0-&gt;1 transition; both visible tiles must land within the very next tick even with unchanged readings.</summary>
    [Fact]
    public void StreamdeckTiles_SubscriberJoins_RebroadcastsAllVisibleTilesWithinOneTick()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var slots = new List<DeckSlot>
        {
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" } },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick(); // connect, no subscriber yet - nothing broadcasts
        var captured = CaptureBroadcasts(f.Hub);

        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles); // 0 -> 1 transition
        worker.Tick(); // sensor unchanged - the subscribe-triggered hash clear is what forces this

        var slotPaths = captured
            .Where(c => c.Topic == PanelTopics.StreamDeckTiles)
            .Select(c => TileFramePayload(c).GetProperty("slotPath").GetString())
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "0", "1" }, slotPaths);
    }

    /// <summary>Mirrors the subscriber-join broadcast test above, but on "number"-style tiles (stable wire bytes across ticks) so a zero-HID-write assertion cannot be confused with a legitimate history-driven render change.</summary>
    [Fact]
    public void StreamdeckTiles_SubscriberJoins_RebroadcastsVisibleTilesWithZeroHidWrites()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var slots = new List<DeckSlot>
        {
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick(); // connect, no subscriber yet
        var callsBeforeSubscribe = simulated.SetKeyImageCallCount;
        var captured = CaptureBroadcasts(f.Hub);

        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles); // 0 -> 1 transition
        worker.Tick(); // sensor unchanged - the subscribe-triggered broadcast-hash clear is what forces this

        Assert.Equal(callsBeforeSubscribe, simulated.SetKeyImageCallCount);

        var slotPaths = captured
            .Where(c => c.Topic == PanelTopics.StreamDeckTiles)
            .Select(c => TileFramePayload(c).GetProperty("slotPath").GetString())
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "0", "1" }, slotPaths);
    }

    /// <summary>
    /// A real navigation, unlike a same-view refresh, still invalidates
    /// every monitoring HID hash for the whole serial (not only the newly
    /// visible slot) and still broadcasts the placeholder frame before the
    /// real one.
    /// </summary>
    [Fact]
    public void SetNav_BroadcastsPlaceholderThenReal_AndInvalidatesEveryMonitoringHidHashForTheSerial()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } },
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        worker.Tick(); // connect at page 0
        Assert.True(worker.HasMonitoringHashForTests("sim-0001", 0, "0"));

        var captured = CaptureBroadcasts(f.Hub);
        Assert.True(worker.SetNav("sim-0001", 1, Array.Empty<int>()));

        // Page 0's HID hash is gone too, even though its slot is not visible
        // anymore - a real nav invalidates every monitoring hash for the
        // serial, not only the newly visible slot's.
        Assert.False(worker.HasMonitoringHashForTests("sim-0001", 0, "0"));
        Assert.True(worker.HasMonitoringHashForTests("sim-0001", 1, "0"));

        var tiles = captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles).ToList();
        Assert.Equal(2, tiles.Count); // pass 1's placeholder frame, then pass 2's real content
        Assert.All(tiles, t => Assert.Equal("0", TileFramePayload(t).GetProperty("slotPath").GetString()));
    }

    /// <summary>The reserved Back key (physical key 0 at folder depth &gt; 0) is never a resolvable DeckSlot and is rendered by PushBackKey, not PushMonitoringPlaceholder/PushMonitoringKey - it must never appear as a streamdeckTiles slotPath.</summary>
    [Fact]
    public void StreamdeckTiles_NeverEmitsForTheReservedBackSlot()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var folderSlots = new List<DeckSlot>
        {
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
        };
        var rootSlots = new List<DeckSlot> { new() { Folder = new DeckFolder { Slots = folderSlots } } };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = rootSlots } } },
        });
        using var worker = NewWorker(f, simulated);
        using var sub = f.Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);
        var captured = CaptureBroadcasts(f.Hub);

        worker.Tick(); // connect at root
        Assert.True(worker.SetNav("sim-0001", 0, new[] { 0 })); // into the folder - key 0 becomes Back

        var tiles = captured.Where(c => c.Topic == PanelTopics.StreamDeckTiles).ToList();
        Assert.NotEmpty(tiles); // the folder's own monitoring slot did broadcast
        Assert.All(tiles, t => Assert.NotEqual("back", TileFramePayload(t).GetProperty("slotPath").GetString()));
    }

    private sealed class MutableUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Devices { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => Devices;
    }

    // ── Wider monitoring category set (extras / fps / network) ──

    private static void SeedDeck(Fixtures f, string serial, params DeckAction[] actions)
    {
        var page = new DeckPage();
        foreach (var action in actions)
        {
            page.Slots.Add(new DeckSlot { Action = action });
        }
        f.Store.Update(s => s.StreamDeck.Decks[serial] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { page } },
        });
    }

    private static HardwareComponent ExtrasComponent(string id, params HardwareSensor[] sensors) =>
        new() { Id = id, Name = id, Sensors = new List<HardwareSensor>(sensors) };

    private static HardwareSensor Sensor(string id, string name, string type, float value, string formatted) =>
        new() { Id = id, Name = name, Type = type, Value = value, Formatted = formatted, Parent = new SensorParent() };

    [Fact]
    public void Tick_MonitoringKeyOnAnExtrasCategory_SamplesItFromTheExtrasWalk()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.Extras = new SensorExtras
        {
            MemoryModules = { ExtrasComponent("/memory/dimm/0", Sensor("dimm0/temp", "Temperature", "Temperature", 41f, "41.0 °C")) },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        SeedDeck(f, "sim-0001", new DeckAction
        {
            Type = "monitoring", Category = "memoryModule", Sensor = "dimm0/temp", Style = "number",
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        Assert.Equal(new[] { 41f }, worker.MonitoringHistoryForTests("sim-0001", 0, "0"));
    }

    /// <summary>
    /// GetSensorExtras rebuilds every battery/NIC/DIMM/PSU component per call,
    /// so a tick walks it once for the whole batch of keys - not once per key.
    /// </summary>
    [Fact]
    public void Tick_SeveralExtrasBackedKeys_WalksSensorExtrasOncePerTick()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.Extras = new SensorExtras
        {
            MemoryModules = { ExtrasComponent("/memory/dimm/0", Sensor("dimm0/temp", "Temperature", "Temperature", 41f, "41.0 °C")) },
            Psus = { ExtrasComponent("/psu/0", Sensor("psu/watts", "Power", "Power", 310f, "310 W")) },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        SeedDeck(f, "sim-0001",
            new DeckAction { Type = "monitoring", Category = "memoryModule", Sensor = "dimm0/temp", Style = "number" },
            new DeckAction { Type = "monitoring", Category = "psu", Sensor = "psu/watts", Style = "number" });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        var before = f.Sensors.ExtrasCalls;
        worker.Tick();

        Assert.Equal(1, f.Sensors.ExtrasCalls - before);
    }

    [Fact]
    public void Tick_NoExtrasBackedKey_NeverWalksSensorExtras()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[] { Sensor("cpu/core0", "Core 0", "Load", 42f, "42%") };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        SeedDeck(f, "sim-0001", new DeckAction
        {
            Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number",
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        worker.Tick();

        Assert.Equal(0, f.Sensors.ExtrasCalls);
    }

    [Fact]
    public void Tick_NetworkKey_SamplesTheNicSummedAggregate()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.Extras = new SensorExtras
        {
            Nics =
            {
                ExtrasComponent("eth",
                    Sensor("a", "Download Speed", "Throughput", 1000f, "1000 B/s"),
                    Sensor("b", "Upload Speed", "Throughput", 200f, "200 B/s")),
            },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        SeedDeck(f, "sim-0001", new DeckAction
        {
            Type = "monitoring", Category = "network", Sensor = "Network Total", Style = "number",
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        Assert.Equal(new[] { 1200f }, worker.MonitoringHistoryForTests("sim-0001", 0, "0"));
    }

    /// <summary>
    /// The fps sensors only exist while IFpsProvider capture runs, so the
    /// worker registers demand from the visible keys and releases it as soon
    /// as no key asks for the category any more.
    /// </summary>
    [Fact]
    public void Tick_FpsKey_HoldsCaptureDemandAndReleasesItWhenTheKeyGoesAway()
    {
        var f = NewFixtures(devicePresent: false);
        var fps = new FakeFpsProvider
        {
            Component = new HardwareComponent
            {
                Id = "fps", Name = "FPS",
                Sensors = { Sensor("fps/current", "FPS", "Framerate", 144f, "144") },
            },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        SeedDeck(f, "sim-0001", new DeckAction
        {
            Type = "monitoring", Category = "fps", Sensor = "FPS", Style = "number",
        });
        using var worker = NewWorker(f, simulated, fps);

        worker.Tick();

        Assert.True(fps.LastDemand);
        Assert.Equal(new[] { 144f }, worker.MonitoringHistoryForTests("sim-0001", 0, "0"));

        SeedDeck(f, "sim-0001", new DeckAction { Type = "hotkey", Keys = "ctrl+c" });
        worker.Tick();

        Assert.False(fps.LastDemand);
    }

    [Fact]
    public void Tick_NoFpsKey_ReleasesCaptureDemandEveryTick()
    {
        var f = NewFixtures(devicePresent: false);
        var fps = new FakeFpsProvider();
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        SeedDeck(f, "sim-0001", new DeckAction { Type = "hotkey", Keys = "ctrl+c" });
        using var worker = NewWorker(f, simulated, fps);

        worker.Tick();

        Assert.False(fps.LastDemand);
        Assert.True(fps.DemandCalls > 0);
    }
}

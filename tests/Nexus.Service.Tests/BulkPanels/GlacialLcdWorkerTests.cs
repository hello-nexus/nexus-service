using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.StreamDeck;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class GlacialLcdWorkerTests
{
    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    private sealed class Rig
    {
        public Usb Usb { get; } = new() { Present = true };
        public InMemoryConfigStore Store { get; } = new();
        public RecordingDevice Device { get; } = new();
        public GlacialLcdWorker Worker { get; }
        public GlacialLcdLink Link { get; } = new();

        public Rig(int connectPollMs = 50)
        {
            Store.Update(s => s.Devices.NexusControlEnabled.Add(GlacialLcdWorker.HandlerId));
            Worker = new GlacialLcdWorker(
                new SingleHid(Device), new DeviceControlGate(Store), new HardwarePresence(Usb), Store, new FakeSensorProvider(),
                Link, connectPollMs: connectPollMs);
        }
    }

    [Fact]
    public async Task The_worker_only_reads_until_the_user_changes_something_then_applies_at_once()
    {
        var rig = new Rig();
        await rig.Worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => rig.Worker.IsConnected));
            Assert.True(await Eventually(() => rig.Device.Snapshot().Count >= 2));
            // Whatever else has gone out so far, none of it is a settings or mode write.
            var first = rig.Device.Snapshot();
            Assert.Equal(new[] { GlacialLcdProtocol.EncodeHello(), GlacialLcdProtocol.EncodeQuery() }, first.Take(2));
            Assert.All(first, w => Assert.Contains(w[1], new byte[] { 0x82, 0xDC }));

            rig.Store.Update(s =>
            {
                s.Devices.GlacialLcd.UserEdited = true;
                s.Devices.GlacialLcd.ModeChosen = true;
                s.Devices.GlacialLcd.Mode = GlacialLcdSettings.ModeStatic;
                s.Devices.GlacialLcd.StaticAsset = 4;
            });
            rig.Worker.RequestApply();
            Assert.True(await Eventually(() => rig.Device.Snapshot().Any(w => w.AsSpan().SequenceEqual(GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeStaticAsset, 4)))));
        }
        finally
        {
            await rig.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_request_ends_a_backoff_wait_early()
    {
        // A panel that never takes the opening reports sends the worker into backoff.
        var rig = new Rig(connectPollMs: 60_000);
        rig.Device.FailWrites = true;
        await rig.Worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => rig.Device.Attempts >= 1));
            int attempts = rig.Device.Attempts;
            rig.Device.FailWrites = false;
            rig.Worker.RequestApply();

            Assert.True(await Eventually(() => rig.Worker.IsConnected, 5000));
            Assert.True(rig.Device.Attempts > attempts);
        }
        finally
        {
            await rig.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_stream_driver_gets_an_overlapped_handle_and_other_bulk_panels_do_not()
    {
        var opened = new System.Collections.Concurrent.ConcurrentBag<(string Path, bool ForInput)>();
        var store = new InMemoryConfigStore();
        foreach (IBulkPanelDriver driver in new IBulkPanelDriver[] { new GlacialLcdPanelDriver(new GlacialLcdLink()), new RyujinPanelDriver() })
        {
            store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
            var usb = new AnyUsb { Vendor = driver.VendorId, Product = driver.ProductIds[0] };
            var worker = new BulkPanelConnectionWorker(
                new RecordingEnumerator(driver, opened), new NullBulkUsbPipeFactory(), new BulkPanelHub(driver),
                new DeviceControlGate(store), new HardwarePresence(usb), connectPollMs: 50, presencePollMs: 50);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(await Eventually(() => opened.Any(o => o.Path == driver.HandlerId)));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        Assert.True(opened.First(o => o.Path == GlacialLcdWorker.HandlerId).ForInput);
        Assert.False(opened.First(o => o.Path == "asus-ryujin-lcd").ForInput);
    }

    private sealed class AnyUsb : IUsbEnumerator
    {
        public int Vendor;
        public int Product;

        public List<UsbDeviceEntry> Enumerate() => new() { new() { VendorId = Vendor, ProductId = Product } };
    }

    private sealed class RecordingEnumerator : IHidEnumerator
    {
        private readonly IBulkPanelDriver _driver;
        private readonly System.Collections.Concurrent.ConcurrentBag<(string Path, bool ForInput)> _opened;

        public RecordingEnumerator(IBulkPanelDriver driver, System.Collections.Concurrent.ConcurrentBag<(string Path, bool ForInput)> opened)
        {
            _driver = driver;
            _opened = opened;
        }

        // The handler id stands in for the path so the test can tell the drivers apart.
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            new[] { new HidDeviceInfo { VendorId = vendorId, ProductId = productId, Path = _driver.HandlerId, OutputReportByteLength = 65 } };

        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();

        public IHidDevice? Open(string path, bool forInput = false)
        {
            _opened.Add((path, forInput));
            return null;
        }
    }

    private sealed class Usb : IUsbEnumerator
    {
        public volatile bool Present;

        public List<UsbDeviceEntry> Enumerate() =>
            Present ? new List<UsbDeviceEntry> { new() { VendorId = 0x0B05, ProductId = GlacialLcdProtocol.ProductId } } : new();
    }

    private sealed class SingleHid : IHidEnumerator
    {
        private readonly RecordingDevice _device;

        public SingleHid(RecordingDevice device)
        {
            _device = device;
        }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            new[] { new HidDeviceInfo { VendorId = vendorId, ProductId = productId, Path = "glacial", OutputReportByteLength = 65 } };

        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => _device;
    }

    private sealed class RecordingDevice : IHidDevice
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _writes = new();
        private int _attempts;

        public volatile bool FailWrites;

        public int Attempts => Volatile.Read(ref _attempts);

        public List<byte[]> Snapshot()
        {
            lock (_lock) { return new List<byte[]>(_writes); }
        }

        public int VendorId => 0x0B05;
        public int ProductId => GlacialLcdProtocol.ProductId;
        public string Path => "glacial";
        public string? Serial => null;
        public int UsagePage => 0;
        public int Usage => 0;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Interlocked.Increment(ref _attempts);
            if (FailWrites)
            {
                return false;
            }
            lock (_lock) { _writes.Add(report.ToArray()); }
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    [Fact]
    public void A_suspend_stops_new_uploads_and_waits_for_the_one_in_flight()
    {
        var rig = new Rig();
        bool finished = false;
        // An upload holds the panel's lock for its whole length.
        var upload = new Thread(() =>
        {
            lock (rig.Link.Sync)
            {
                Thread.Sleep(400);
                finished = true;
            }
        });
        upload.Start();
        Thread.Sleep(50);

        rig.Worker.OnHostSuspending(waitMs: 5000);

        Assert.True(rig.Link.Suspended);
        Assert.True(finished);
        upload.Join();

        rig.Worker.OnHostResumed();
        Assert.False(rig.Link.Suspended);
        Assert.True(rig.Link.ShowPending);
    }

    [Fact]
    public void A_suspend_does_not_wait_longer_than_it_is_given()
    {
        var rig = new Rig();
        var release = new ManualResetEventSlim(false);
        var upload = new Thread(() =>
        {
            lock (rig.Link.Sync)
            {
                release.Wait();
            }
        });
        upload.Start();
        Thread.Sleep(50);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        rig.Worker.OnHostSuspending(waitMs: 150);

        Assert.True(watch.ElapsedMilliseconds < 2000);
        release.Set();
        upload.Join();
    }
}

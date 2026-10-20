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

        public Rig(int connectPollMs = 50)
        {
            Store.Update(s => s.Devices.NexusControlEnabled.Add(GlacialLcdWorker.HandlerId));
            Worker = new GlacialLcdWorker(
                new SingleHid(Device), new DeviceControlGate(Store), new HardwarePresence(Usb), Store, new FakeSensorProvider(),
                connectPollMs);
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
}

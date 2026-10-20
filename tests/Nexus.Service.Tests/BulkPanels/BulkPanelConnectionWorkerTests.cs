using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BulkPanelConnectionWorkerTests
{
    [Fact]
    public async Task An_unplugged_panel_is_let_go_and_taken_back_when_it_returns()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), new PipeFactory(), hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 100, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.IsConnected));

            usb.Present = false;
            Assert.True(await Eventually(() => !hub.IsConnected));
            Assert.Equal(1, driver.Disconnects);

            usb.Present = true;
            Assert.True(await Eventually(() => hub.IsConnected));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_glacial_worker_only_reads_until_the_user_changes_something_then_applies_at_once()
    {
        var usb = new SwitchableUsb { Present = true, Vendor = 0x0B05, Product = GlacialLcdProtocol.ProductId };
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(GlacialLcdWorker.HandlerId));
        var hid = new RecordingDevice();
        var worker = new GlacialLcdWorker(
            new SingleHid(hid), new DeviceControlGate(store), new HardwarePresence(usb), store,
            new Nexus.Service.Tests.StreamDeck.FakeSensorProvider(), connectPollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => worker.IsConnected));
            var first = hid.Snapshot();
            Assert.Equal(new[] { GlacialLcdProtocol.EncodeHello(), GlacialLcdProtocol.EncodeQuery() }, first);

            store.Update(s =>
            {
                s.Devices.GlacialLcd.UserEdited = true;
                s.Devices.GlacialLcd.ModeChosen = true;
                s.Devices.GlacialLcd.Mode = GlacialLcdSettings.ModeStatic;
                s.Devices.GlacialLcd.StaticAsset = 4;
            });
            worker.RequestApply();
            Assert.True(await Eventually(() => hid.Snapshot().Any(w => w.AsSpan().SequenceEqual(GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeStaticAsset, 4)))));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_pipe_whose_device_is_gone_is_reopened_while_presence_never_misses()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var pipes = new PipeFactory();
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), pipes, hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 100, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.IsConnected));
            Assert.Single(pipes.Opened);

            Assert.True(pipes.Opened.TryPeek(out var first));
            first!.Gone = true;
            Assert.True(await Eventually(() => pipes.Opened.Count == 2 && hub.IsConnected));
            Assert.Equal(1, driver.Disconnects);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_denied_open_sets_the_hub_flag_and_a_later_successful_open_clears_it()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var pipes = new DenyingPipeFactory { Deny = true };
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), pipes, hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.OpenAccessDenied));
            Assert.False(hub.IsConnected);

            pipes.Deny = false;
            Assert.True(await Eventually(() => hub.IsConnected));
            Assert.False(hub.OpenAccessDenied);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_denied_panel_that_is_unplugged_clears_the_flag()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), new DenyingPipeFactory { Deny = true }, hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.OpenAccessDenied));

            usb.Present = false;
            Assert.True(await Eventually(() => !hub.OpenAccessDenied));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class SwitchableUsb : IUsbEnumerator
    {
        public volatile bool Present;
        public int Vendor = FakeDriver.Vid;
        public int Product = FakeDriver.Pid;

        public List<UsbDeviceEntry> Enumerate() =>
            Present ? new List<UsbDeviceEntry> { new() { VendorId = Vendor, ProductId = Product } } : new();
    }

    private sealed class FakeDriver : IBulkPanelDriver
    {
        public const int Vid = 0x1234;
        public const int Pid = 0x5678;

        public int Disconnects;

        public string HandlerId => "fake-bulk";
        public string Name => "Fake bulk panel";
        public int VendorId => Vid;
        public IReadOnlyList<int> ProductIds { get; } = new[] { Pid };
        public string Surface => "lcd-square";
        public int Fps => 30;
        public byte WritePipeId => 0x01;
        public byte ReadPipeId => 0x81;
        public bool NeedsHidChannel => false;

        public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid) => (320, 320);
        public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra) => true;
        public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid) => Interlocked.Increment(ref Disconnects);
    }

    private sealed class PipeFactory : IBulkUsbPipeFactory
    {
        public readonly ConcurrentQueue<NullPipe> Opened = new();

        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId)
        {
            var pipe = new NullPipe();
            Opened.Enqueue(pipe);
            return pipe;
        }
    }

    private sealed class DenyingPipeFactory : IBulkUsbPipeFactory
    {
        public volatile bool Deny;

        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) =>
            Open(vendorId, productId, writePipeId, readPipeId, out _);

        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId, out bool accessDenied)
        {
            accessDenied = Deny;
            return Deny ? null : new NullPipe();
        }
    }

    private sealed class NullPipe : IBulkUsbPipe
    {
        public volatile bool Gone;

        public bool IsGone => Gone;
        public bool Write(ReadOnlySpan<byte> data) => true;
        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => true;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
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

    private sealed class NoHid : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }
}

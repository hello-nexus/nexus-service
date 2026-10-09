using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
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

        public List<UsbDeviceEntry> Enumerate() =>
            Present ? new List<UsbDeviceEntry> { new() { VendorId = FakeDriver.Vid, ProductId = FakeDriver.Pid } } : new();
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
        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) => new NullPipe();
    }

    private sealed class NullPipe : IBulkUsbPipe
    {
        public bool Write(ReadOnlySpan<byte> data) => true;
        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => true;
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

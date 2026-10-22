using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests.Kraken;

public class KrakenConnectionWorkerClaimTests
{
    private const string HidPath = "/dev/hidraw12";

    [Fact]
    public async Task ConnectThatFailsReleasesTheClaim()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled = new List<string> { KrakenHub.DeviceId });
        var hub = new KrakenHub(null);
        var hid = new SilentHid();
        var usb = new Bus();
        var worker = new KrakenConnectionWorker(
            hid,
            hub,
            new KrakenLightingDeviceProvider(hub, store, new Np50IdentifyTracker()),
            new KrakenCoolingProvider(hub),
            new DeviceControlGate(store),
            new HardwarePresence(usb));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var clock = Stopwatch.StartNew();
            while ((Volatile.Read(ref hid.Opens) == 0 || hub.ClaimedHidPath != null) && clock.ElapsedMilliseconds < 4000)
            {
                await Task.Delay(20);
            }
            Assert.True(hid.Opens >= 1);
            Assert.Null(hub.ClaimedHidPath);
            Assert.Null(hub.ClaimedHwmonName);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class Bus : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new()
        {
            new UsbDeviceEntry { VendorId = KrakenProtocol.VendorId, ProductId = 0x3012, Serial = "S" },
        };
    }

    private sealed class SilentHid : IHidEnumerator
    {
        public int Opens;

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => productId != 0x3012
            ? Array.Empty<HidDeviceInfo>()
            : new[]
            {
                new HidDeviceInfo
                {
                    VendorId = vendorId,
                    ProductId = productId,
                    Path = HidPath,
                    InputReportByteLength = 512,
                    OutputReportByteLength = 512,
                },
            };

        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();

        public IHidDevice? Open(string path, bool forInput = false)
        {
            Interlocked.Increment(ref Opens);
            return new Device(path);
        }
    }

    private sealed class Device : IHidDevice
    {
        public Device(string path) { Path = path; }

        public int VendorId => KrakenProtocol.VendorId;
        public int ProductId => 0x3012;
        public string Path { get; }
        public string? Serial => null;
        public int UsagePage => 0;
        public int Usage => 0;
        public bool SetFeature(ReadOnlySpan<byte> report) => false;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool Write(ReadOnlySpan<byte> report) => true;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}

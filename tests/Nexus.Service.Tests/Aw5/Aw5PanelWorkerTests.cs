using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.Aw5;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Models.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Aw5;

/// <summary>
/// The worker's tick order is load-bearing in ways the panel cannot report: it
/// renders nothing and logs nothing when it is wrong, so only these assertions
/// catch it.
/// </summary>
public class Aw5PanelWorkerTests
{
    private const int Vid = 0x3402;

    private static HidDeviceInfo Panel() => new()
    {
        VendorId = Vid, ProductId = 0x0406, UsagePage = 0xFF01,
        OutputReportByteLength = 64, Path = "lp",
    };

    [Fact]
    public async Task Scans_the_bus_on_the_very_first_tick()
    {
        // The rescan counter is seeded at int.MaxValue so the first tick discovers.
        // Testing it with a pre-increment overflows to int.MinValue and the scan
        // never runs: panels stay dark, no error, nothing in the log.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out _);

        await w.TickAsync(CancellationToken.None);

        Assert.True(hid.Finds > 0);
        Assert.True(hid.Device("lp")!.Features.Count > 0);
    }

    [Fact]
    public async Task Does_not_scan_the_bus_on_every_tick()
    {
        // Discovery walks every HID interface on the box; at the panel's keep-alive
        // rate that would sweep the bus once a second forever, on machines with no
        // AW5 too.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out _);

        for (var i = 0; i < 4; i++) await w.TickAsync(CancellationToken.None);

        Assert.Equal(2, hid.Finds / 1);      // one Find per PID, one scan pass
        Assert.True(hid.Device("lp")!.Features.Count >= 4 * 5, "every tick still writes the panel");
    }

    [Fact]
    public async Task Gated_off_never_touches_the_bus()
    {
        // The gate is checked before the scan, so a device the user switched off
        // costs nothing at all.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out var store, gateOff: true);

        await w.TickAsync(CancellationToken.None);
        await w.TickAsync(CancellationToken.None);

        Assert.Equal(0, hid.Finds);
        Assert.Empty(hid.Opens);
    }

    [Fact]
    public async Task Stands_down_entirely_when_the_vendor_driver_path_is_enabled()
    {
        // Exactly one implementation drives the panel; two writers on one HID is the
        // state the policy exists to prevent.
        var hid = new FakeHid(Panel());
        var hub = new Aw5Hub(hid);
        var w = new Aw5PanelWorker(hub, new Aw5SensorReader(new FakeSensors()),
            new DeviceControlGate(new InMemoryConfigStore()), new DriverExePolicy(enabled: true));

        await w.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await w.StopAsync(CancellationToken.None);

        Assert.Equal(0, hid.Finds);
    }

    private static Aw5PanelWorker Build(FakeHid hid, out InMemoryConfigStore store, bool gateOff = false)
    {
        store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        if (gateOff) gate.SetEnabled(Aw5Handler.HandlerId, false);
        return new Aw5PanelWorker(new Aw5Hub(hid), new Aw5SensorReader(new FakeSensors()),
            gate, new DriverExePolicy(enabled: false));
    }

    private sealed class FakeSensors : ISensorProvider
    {
        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => new[]
        {
            new HardwareSensor { Id = "c/t", Name = "CPU Package", Type = "Temperature", Value = 44 },
            new HardwareSensor { Id = "c/l", Name = "CPU Total", Type = "Load", Value = 25 },
            new HardwareSensor { Id = "c/clk", Name = "Core Average", Type = "Clock", Value = 3600 },
        };
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHid : IHidEnumerator
    {
        private readonly HidDeviceInfo[] _ifaces;
        private readonly Dictionary<string, FakeDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
        public FakeHid(params HidDeviceInfo[] ifaces) { _ifaces = ifaces; }
        public int Finds { get; private set; }
        public List<string> Opens { get; } = new();
        public FakeDevice? Device(string path) => _devices.TryGetValue(path, out var d) ? d : null;

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
        {
            Finds++;
            return _ifaces.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToArray();
        }
        public IReadOnlyList<HidDeviceInfo> FindAll() => _ifaces;
        public IHidDevice? Open(string path, bool forInput = false)
        {
            Opens.Add(path);
            var d = new FakeDevice(path);
            _devices[path] = d;
            return d;
        }

        internal sealed class FakeDevice : IHidDevice
        {
            public FakeDevice(string path) { Path = path; }
            public List<byte[]> Features { get; } = new();
            public int VendorId => Vid;
            public int ProductId => 0x0406;
            public string Path { get; }
            public string? Serial => null;
            public int UsagePage => 0xFF01;
            public int Usage => 1;
            public bool Write(ReadOnlySpan<byte> r) => true;
            public bool SetFeature(ReadOnlySpan<byte> r) { Features.Add(r.ToArray()); return true; }
            public bool GetFeature(Span<byte> b) => throw new NotSupportedException();
            public bool GetInputReport(Span<byte> b) => throw new NotSupportedException();
            public bool SetOutputReport(ReadOnlySpan<byte> r) => throw new NotSupportedException();
            public int Read(Span<byte> b, int t) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}

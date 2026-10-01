using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Nollie;

/// <summary>The poll only looks up HID interfaces when the Nollie devnodes on the bus change (or the periodic recheck is due), and never walks other vendors' devices.</summary>
public class NollieConnectionWorkerTests
{
    private const int Vid = 0x16D5;
    private const int Pid = 0x2A32;

    private readonly NollieHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly Bus _bus = new();
    private readonly CountingHid _hid = new();
    private readonly NollieConnectionWorker _worker;
    private long _now;

    public NollieConnectionWorkerTests()
    {
        var provider = new NollieLightingDeviceProvider(_hub, _store, new Np50IdentifyTracker());
        _worker = new NollieConnectionWorker(_hid, _hub, provider, new DeviceControlGate(_store), _store, new HardwarePresence(_bus), () => _now);
    }

    private NollieLightingDeviceProviderTests.FakeHidDevice Plug(string serial)
    {
        // Presence folds identical units into one entry, as the Windows enumerator does.
        if (_bus.Entries.Count == 0) _bus.Entries.Add(new UsbDeviceEntry { VendorId = Vid, ProductId = Pid, Serial = serial });
        var device = new NollieLightingDeviceProviderTests.FakeHidDevice(Vid, Pid, $"path-{serial}", serial);
        _hid.Devices.Add(device);
        return device;
    }

    [Fact]
    public void Nothing_on_the_bus_looks_nothing_up()
    {
        _worker.Tick();
        _worker.Tick();

        Assert.Equal(0, _hid.FindCalls);
        Assert.Equal(0, _hid.FindAllCalls);
    }

    [Fact]
    public void An_attached_board_is_not_rescanned_until_the_bus_changes()
    {
        Plug("A");
        _worker.Tick();
        Assert.Single(_hub.Controllers);
        Assert.Equal(1, _hid.FindCalls);

        _worker.Tick();
        _worker.Tick();
        Assert.Equal(1, _hid.FindCalls);

        _bus.Entries.Clear();
        _hid.Devices.Clear();
        _worker.Tick();
        Assert.Empty(_hub.Controllers);
        Assert.Equal(0, _hid.FindAllCalls);
    }

    [Fact]
    public void A_hid_interface_that_lands_after_its_devnode_is_retried()
    {
        var device = Plug("LATE");
        _hid.Devices.Clear();
        _worker.Tick();
        Assert.Empty(_hub.Controllers);

        _hid.Devices.Add(device);
        _worker.Tick();
        Assert.Single(_hub.Controllers);

        var calls = _hid.FindCalls;
        _worker.Tick();
        Assert.Equal(calls, _hid.FindCalls);
    }

    [Fact]
    public void Nexus_control_back_on_reattaches_without_a_bus_change()
    {
        Plug("GATE");
        _worker.Tick();
        Assert.Single(_hub.Controllers);

        _store.Update(s => s.Devices.NexusControlDisabled = new List<string> { NollieConnectionWorker.HandlerId });
        _worker.Tick();
        Assert.Empty(_hub.Controllers);

        _store.Update(s => s.Devices.NexusControlDisabled = new List<string>());
        _worker.Tick();
        Assert.Single(_hub.Controllers);
    }

    [Fact]
    public void A_second_identical_board_attaches_on_the_periodic_recheck()
    {
        Plug("ONE");
        _worker.Tick();
        Assert.Single(_hub.Controllers);

        Plug("TWO");
        _worker.Tick();
        Assert.Single(_hub.Controllers);

        _now += NollieConnectionWorker.RecheckMs;
        _worker.Tick();
        Assert.Equal(2, _hub.Controllers.Count);
    }

    [Fact]
    public void An_empty_presence_read_does_not_detach_an_attached_board()
    {
        Plug("KEEP");
        _worker.Tick();
        _bus.Entries.Clear();

        _worker.Tick();
        _now += NollieConnectionWorker.RecheckMs;
        _worker.Tick();

        Assert.Single(_hub.Controllers);
    }

    [Fact]
    public void A_board_failing_writes_is_dropped_then_reattached()
    {
        var device = Plug("FAIL");
        _worker.Tick();
        var controller = Assert.Single(_hub.Controllers);

        device.WriteResult = false;
        for (var i = 0; i < 3; i++) controller.SendChannel(0, new byte[] { 1, 2, 3 });
        _worker.Tick();
        Assert.Empty(_hub.Controllers);

        device.WriteResult = true;
        _worker.Tick();
        Assert.Single(_hub.Controllers);
    }

    private sealed class Bus : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Entries { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => new(Entries);
    }

    private sealed class CountingHid : IHidEnumerator
    {
        public List<NollieLightingDeviceProviderTests.FakeHidDevice> Devices { get; } = new();
        public int FindCalls { get; private set; }
        public int FindAllCalls { get; private set; }

        private static HidDeviceInfo Info(NollieLightingDeviceProviderTests.FakeHidDevice d) => new()
        {
            VendorId = d.VendorId,
            ProductId = d.ProductId,
            Path = d.Path,
            Serial = d.Serial,
            UsagePage = d.UsagePage,
            Usage = d.Usage,
            OutputReportByteLength = NollieProtocol.WideReportSize,
        };

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
        {
            FindCalls++;
            return Devices.Where(d => d.VendorId == vendorId && d.ProductId == productId).Select(Info).ToList();
        }

        public IReadOnlyList<HidDeviceInfo> FindAll()
        {
            FindAllCalls++;
            return Devices.Select(Info).ToList();
        }

        public IHidDevice? Open(string path, bool forInput = false) => Devices.FirstOrDefault(d => d.Path == path);
    }
}

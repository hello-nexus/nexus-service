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

    // A board whose firmware takes standalone settings (the 32CH).
    private NollieLightingDeviceProviderTests.FakeHidDevice PlugStandalone(string serial)
    {
        _bus.Entries.Add(new UsbDeviceEntry { VendorId = NollieProtocol.VendorIdHighChannel, ProductId = 0x4714, Serial = serial });
        var device = new NollieLightingDeviceProviderTests.FakeHidDevice(NollieProtocol.VendorIdHighChannel, 0x4714, $"path-{serial}", serial);
        _hid.Devices.Add(device);
        return device;
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
    public void Nexus_control_off_releases_the_real_board_and_keeps_the_simulated_one()
    {
        Plug("REAL");
        _worker.Tick();
        Assert.True(_worker.AttachSimulated(Vid, Pid));
        Assert.Equal(2, _hub.Controllers.Count);

        _store.Update(s => s.Devices.NexusControlDisabled = new List<string> { NollieConnectionWorker.HandlerId });
        _worker.Tick();
        _worker.Tick();

        var left = Assert.Single(_hub.Controllers);
        Assert.StartsWith(SimulatedNollieDevice.PathPrefix, left.Path, StringComparison.Ordinal);

        _store.Update(s => s.Devices.NexusControlDisabled = new List<string>());
        _worker.Tick();

        Assert.Equal(2, _hub.Controllers.Count);
        Assert.Single(_hub.Controllers, c => c.Path.StartsWith(SimulatedNollieDevice.PathPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void A_simulated_board_can_be_attached_while_nexus_control_is_off_and_survives_ticks()
    {
        _store.Update(s => s.Devices.NexusControlDisabled = new List<string> { NollieConnectionWorker.HandlerId });

        Assert.True(_worker.AttachSimulated(Vid, Pid));
        _worker.Tick();
        _worker.Tick();

        Assert.Single(_hub.Controllers);
        _worker.DetachSimulated();
        Assert.Empty(_hub.Controllers);
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

    private NollieConnectionWorker Gated(InMemoryConfigStore store)
    {
        var provider = new NollieLightingDeviceProvider(_hub, store, new Np50IdentifyTracker());
        return new NollieConnectionWorker(_hid, _hub, provider, new DeviceControlGate(store), store, new HardwarePresence(_bus), () => _now,
            new Nexus.Service.Lifecycle.FeatureGates(store));
    }

    [Fact]
    public void An_undecided_install_sends_the_board_nothing_then_applies_standalone_when_lighting_comes_on()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.FeaturesOnboardingCompleted = false);
        var worker = Gated(store);
        var device = PlugStandalone("EDGE");

        worker.Tick();
        Assert.Single(_hub.Controllers);
        Assert.Empty(device.Writes);

        store.Update(s => s.FeaturesOnboardingCompleted = true);
        worker.Tick();

        Assert.NotEmpty(device.Writes);
    }

    [Fact]
    public void A_board_Nexus_never_drove_gets_no_release_write()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.FeaturesOnboardingCompleted = false);
        var worker = Gated(store);
        var device = PlugStandalone("UNDRIVEN");
        worker.Tick();

        worker.ReleaseAllForShutdown();

        Assert.Empty(device.Writes);
    }

    [Fact]
    public void A_board_driven_while_lighting_was_on_is_still_released_after_lighting_goes_off()
    {
        var store = new InMemoryConfigStore();
        var worker = Gated(store);
        var device = PlugStandalone("DRIVEN");
        worker.Tick();
        device.Writes.Clear();
        store.Update(s => s.Features.Lighting = false);

        worker.ReleaseAllForShutdown();

        Assert.NotEmpty(device.Writes);
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

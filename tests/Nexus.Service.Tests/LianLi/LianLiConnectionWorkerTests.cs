using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLi;

public class LianLiConnectionWorkerTests
{
    private sealed class FakeHid : IHidEnumerator
    {
        public List<HidDeviceInfo> Present { get; } = new();
        public Dictionary<string, HubTransportSpy> Opened { get; } = new();

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            Present.Where(d => d.VendorId == vendorId && d.ProductId == productId).ToList();

        public IReadOnlyList<HidDeviceInfo> FindAll() => Present;

        public IHidDevice? Open(string path, bool forInput = false)
        {
            var spy = new HubTransportSpy { InputReport = new byte[LianLiProtocol.InputReportSize] };
            Opened[path] = spy;
            return spy;
        }
    }

    private sealed class FakeUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new() { new UsbDeviceEntry { VendorId = LianLiProtocol.VendorId, ProductId = LianLiProtocol.ProductId } };
    }

    private static HidDeviceInfo Hub(string serial, string path) => new()
    {
        VendorId = LianLiProtocol.VendorId,
        ProductId = LianLiProtocol.ProductId,
        Serial = serial,
        Path = path,
        UsagePage = LianLiProtocol.VendorUsagePage,
        Usage = LianLiProtocol.VendorUsage,
    };

    private static (LianLiConnectionWorker Worker, LianLiHubSet Hubs) Create(FakeHid hid, InMemoryConfigStore store)
    {
        var hubs = new LianLiHubSet();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled(LianLiHubSet.PrimaryId, true);
        var worker = new LianLiConnectionWorker(
            hid, hubs, new LianLiLightingDeviceProvider(hubs, store, new Np50IdentifyTracker()), new LianLiCoolingProvider(hubs, store),
            gate, new HardwarePresence(new FakeUsb()), store);
        return (worker, hubs);
    }

    [Fact]
    public void Every_present_hub_gets_a_slot_and_its_key_is_pinned()
    {
        var hid = new FakeHid();
        hid.Present.Add(Hub("S1", "p1"));
        hid.Present.Add(Hub("S2", "p2"));
        var store = new InMemoryConfigStore();
        var (worker, hubs) = Create(hid, store);

        worker.Tick(0);

        Assert.Equal("p1", hubs.Hubs[0].AttachedPath);
        Assert.Equal("p2", hubs.Hubs[1].AttachedPath);
        Assert.False(hubs.Hubs[2].IsConnected);
        Assert.Equal(new[] { "sn:S1", "sn:S2" }, store.Load().Devices.LianLiHubKeys);
    }

    [Fact]
    public void A_hub_keeps_its_slot_when_enumeration_order_changes()
    {
        var store = new InMemoryConfigStore();
        var first = new FakeHid();
        first.Present.Add(Hub("S1", "p1"));
        first.Present.Add(Hub("S2", "p2"));
        Create(first, store).Worker.Tick(0);

        var later = new FakeHid();
        later.Present.Add(Hub("S2", "q2"));
        later.Present.Add(Hub("S1", "q1"));
        var (worker, hubs) = Create(later, store);
        worker.Tick(0);

        Assert.Equal("q1", hubs.Hubs[0].AttachedPath);
        Assert.Equal("q2", hubs.Hubs[1].AttachedPath);
    }

    [Fact]
    public void A_second_hub_follows_the_first_hubs_control_choice_until_it_has_its_own()
    {
        var hid = new FakeHid();
        hid.Present.Add(Hub("S1", "p1"));
        hid.Present.Add(Hub("S2", "p2"));
        var store = new InMemoryConfigStore();
        var (worker, hubs) = Create(hid, store);
        new DeviceControlGate(store).SetEnabled("lianli2", false);

        worker.Tick(0);

        Assert.True(hubs.Hubs[0].IsConnected);
        Assert.False(hubs.Hubs[1].IsConnected);
        Assert.True(new DeviceControlGate(new InMemoryConfigStore()).IsEnabled("lianli2") == new DeviceControlGate(new InMemoryConfigStore()).IsEnabled("lianli"));
    }

    [Fact]
    public void A_serial_less_hub_replugged_to_another_port_keeps_its_slot()
    {
        var store = new InMemoryConfigStore();
        var first = new FakeHid();
        first.Present.Add(Hub("", "port-a"));
        Create(first, store).Worker.Tick(0);

        var later = new FakeHid();
        later.Present.Add(Hub("", "port-b"));
        var (worker, hubs) = Create(later, store);
        worker.Tick(0);

        Assert.Equal("port-b", hubs.Hubs[0].AttachedPath);
        Assert.False(hubs.Hubs[1].IsConnected);
        Assert.Equal(new[] { "path:port-b" }, store.Load().Devices.LianLiHubKeys);
    }

    [Fact]
    public void A_new_hub_takes_over_the_slot_of_one_that_is_gone()
    {
        var store = new InMemoryConfigStore();
        var first = new FakeHid();
        first.Present.Add(Hub("S1", "p1"));
        first.Present.Add(Hub("S2", "p2"));
        Create(first, store).Worker.Tick(0);

        var later = new FakeHid();
        later.Present.Add(Hub("S2", "p2"));
        later.Present.Add(Hub("S3", "p3"));
        var (worker, hubs) = Create(later, store);
        worker.Tick(0);

        Assert.Equal("p3", hubs.Hubs[0].AttachedPath);
        Assert.Equal("p2", hubs.Hubs[1].AttachedPath);
        Assert.Equal(new[] { "sn:S3", "sn:S2" }, store.Load().Devices.LianLiHubKeys);
    }

    [Fact]
    public void Hubs_sharing_a_serial_both_attach_keyed_by_path()
    {
        var hid = new FakeHid();
        hid.Present.Add(Hub("SAME", "p1"));
        hid.Present.Add(Hub("SAME", "p2"));
        var store = new InMemoryConfigStore();
        var (worker, hubs) = Create(hid, store);

        worker.Tick(0);

        Assert.Equal("p1", hubs.Hubs[0].AttachedPath);
        Assert.Equal("p2", hubs.Hubs[1].AttachedPath);
        Assert.Equal(new[] { "path:p1", "path:p2" }, store.Load().Devices.LianLiHubKeys);
    }

    [Fact]
    public void A_hub_turned_off_stays_listed_and_reattaches_when_turned_back_on()
    {
        var hid = new FakeHid();
        hid.Present.Add(Hub("S1", "p1"));
        hid.Present.Add(Hub("S2", "p2"));
        var store = new InMemoryConfigStore();
        var (worker, hubs) = Create(hid, store);
        var gate = new DeviceControlGate(store);
        var handler = new Nexus.Service.Devices.Handlers.LianLiHandler(hubs, 1);
        worker.Tick(0);
        Assert.True(hubs.Hubs[1].IsConnected);

        gate.SetEnabled("lianli2", false);
        worker.Tick(2000);
        Assert.False(hubs.Hubs[1].IsConnected);
        Assert.True(handler.IsConnected(Array.Empty<UsbDeviceEntry>()));

        // Well inside the slow scan interval that applies while a hub is attached.
        gate.SetEnabled("lianli2", true);
        worker.Tick(4000);
        Assert.True(hubs.Hubs[1].IsConnected);
    }

    [Fact]
    public void An_unplugged_extra_hub_drops_off_the_device_list()
    {
        var hid = new FakeHid();
        hid.Present.Add(Hub("S1", "p1"));
        hid.Present.Add(Hub("S2", "p2"));
        var store = new InMemoryConfigStore();
        var (worker, hubs) = Create(hid, store);
        var handler = new Nexus.Service.Devices.Handlers.LianLiHandler(hubs, 1);
        worker.Tick(0);

        hid.Opened["p2"].RejectWrites = true;
        for (var t = 1; t <= 3; t++) worker.Tick(t * 2000);

        Assert.False(hubs.Hubs[1].IsConnected);
        Assert.False(handler.IsConnected(Array.Empty<UsbDeviceEntry>()));
    }

    [Fact]
    public void A_hub_with_no_serial_is_keyed_by_its_path()
    {
        Assert.Equal("path:abc", LianLiConnectionWorker.KeyOf(Hub("", "abc")));
        Assert.Equal("sn:X", LianLiConnectionWorker.KeyOf(Hub("X", "abc")));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Ibp;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Ibp;

/// <summary>
/// Worker policy on scripted bus / gate / HID layers: presence and the
/// per-handler gate decide what is allowed, a unit that will not open is
/// retried on a backoff rather than every tick, and the lighting provider is
/// told exactly once per change.
/// </summary>
public class IbpPeripheralConnectionWorkerTests
{
    private sealed class ScriptedUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Bus { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => Bus;
        public void Plug(int pid) => Bus.Add(new UsbDeviceEntry { VendorId = 0x3402, ProductId = pid });
        public void Unplug(int pid) => Bus.RemoveAll(e => e.ProductId == pid);
    }

    private sealed class ScriptedHid : IHidEnumerator
    {
        public List<HidDeviceInfo> Infos { get; } = new();
        public HashSet<string> RefuseOpen { get; } = new();
        public HashSet<string> RefuseWrites { get; } = new();
        public int FindAllCalls { get; private set; }
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => FindAll();
        public IReadOnlyList<HidDeviceInfo> FindAll() { FindAllCalls++; return Infos.ToList(); }
        public IHidDevice? Open(string path, bool forInput = false) =>
            RefuseOpen.Contains(path) ? null : new NullDevice(Infos.First(i => i.Path == path), RefuseWrites.Contains(path));
        public void Add(IbpPeripheralModel model, string serial) => Infos.Add(new HidDeviceInfo
        {
            VendorId = 0x3402, ProductId = model.ProductId, Path = model.Id + "-" + serial, Serial = serial,
            FeatureReportByteLength = model.ReportLength,
        });

        private sealed class NullDevice : IHidDevice
        {
            private readonly HidDeviceInfo _i;
            private readonly bool _refuseWrites;
            public NullDevice(HidDeviceInfo i, bool refuseWrites) { _i = i; _refuseWrites = refuseWrites; }
            public int VendorId => _i.VendorId;
            public int ProductId => _i.ProductId;
            public string Path => _i.Path;
            public string? Serial => _i.Serial;
            public int UsagePage => 0;
            public int Usage => 0;
            public bool SetFeature(ReadOnlySpan<byte> report) => !_refuseWrites;
            public bool GetFeature(Span<byte> buffer) => false;
            public bool GetInputReport(Span<byte> buffer) => false;
            public bool Write(ReadOnlySpan<byte> report) => true;
            public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
            public int Read(Span<byte> buffer, int timeoutMs) => 0;
            public void Dispose() { }
        }
    }

    private sealed class Rig
    {
        public ScriptedUsb Usb { get; } = new();
        public ScriptedHid Hid { get; } = new();
        public InMemoryConfigStore Store { get; } = new();
        public IbpPeripheralHub Hub { get; }
        public Nexus.Service.Lighting.IbpPeripheralLightingDeviceProvider Lighting { get; }
        public int Changes { get; private set; }
        public long Now { get; set; } = 1_000_000;
        public IbpPeripheralConnectionWorker Worker { get; }

        public Rig()
        {
            Hub = new IbpPeripheralHub(Hid);
            Lighting = new Nexus.Service.Lighting.IbpPeripheralLightingDeviceProvider(
                Hub, Store, new Nexus.Service.Lighting.Np50IdentifyTracker());
            Lighting.DevicesChanged += () => Changes++;
            Worker = new IbpPeripheralConnectionWorker(
                Hub, new HardwarePresence(Usb), new DeviceControlGate(Store), Lighting, () => Now);
        }

        public void GateOff(string handlerId) => Store.Update(s => s.Devices.NexusControlDisabled = new List<string> { handlerId });
        public void GateOn() => Store.Update(s => s.Devices.NexusControlDisabled = new List<string>());
    }

    [Fact]
    public void Opens_present_units_and_notifies_once_per_change()
    {
        var rig = new Rig();
        rig.Worker.Tick();
        Assert.Equal(0, rig.Hid.FindAllCalls); // nothing on the bus: no HID walk
        Assert.Equal(0, rig.Changes);

        rig.Usb.Plug(0x0301);
        rig.Hid.Add(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        rig.Worker.Tick();
        Assert.Single(rig.Hub.Attached);
        Assert.Equal(1, rig.Changes);
        rig.Worker.Tick();
        rig.Worker.Tick();
        Assert.Equal(1, rig.Hid.FindAllCalls); // steady state: no re-enumeration
        Assert.Equal(1, rig.Changes);

        rig.Usb.Unplug(0x0301);
        rig.Worker.Tick();
        Assert.Empty(rig.Hub.Attached);
        Assert.Equal(2, rig.Changes);
    }

    [Fact]
    public void Gate_off_releases_only_that_handlers_units()
    {
        var rig = new Rig();
        rig.Usb.Plug(0x0301);
        rig.Usb.Plug(0x0200);
        rig.Hid.Add(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        rig.Hid.Add(IbpPeripheralProtocol.Km7Mouse, "MS1");
        rig.Worker.Tick();
        Assert.Equal(2, rig.Hub.Attached.Count);

        rig.GateOff(IbpPeripheralProtocol.KeyboardHandlerId);
        rig.Worker.Tick();
        var only = Assert.Single(rig.Hub.Attached);
        Assert.Equal(IbpPeripheralKind.Mouse, only.Model.Kind);
        Assert.Equal(2, rig.Changes);

        rig.GateOn();
        rig.Worker.Tick();
        Assert.Equal(2, rig.Hub.Attached.Count);
        Assert.Equal(3, rig.Changes);
    }

    [Fact]
    public void A_unit_that_will_not_open_is_retried_on_the_backoff_not_every_tick()
    {
        var rig = new Rig();
        rig.Usb.Plug(0x0303);
        rig.Hid.Add(IbpPeripheralProtocol.Mk9Keyboard, "MK1");
        rig.Hid.RefuseOpen.Add("mk9-keyboard-MK1");

        rig.Worker.Tick();
        Assert.Equal(1, rig.Hid.FindAllCalls);
        Assert.Empty(rig.Hub.Attached);
        for (var i = 0; i < 5; i++)
        {
            rig.Now += 1000;
            rig.Worker.Tick();
        }
        Assert.Equal(1, rig.Hid.FindAllCalls);

        rig.Now += IbpPeripheralConnectionWorker.OpenRetryMs;
        rig.Worker.Tick();
        Assert.Equal(2, rig.Hid.FindAllCalls);
        Assert.Empty(rig.Hub.Attached);

        // Re-plug resets the backoff: the next tick retries at once.
        rig.Usb.Unplug(0x0303);
        rig.Worker.Tick();
        rig.Usb.Plug(0x0303);
        rig.Hid.RefuseOpen.Clear();
        rig.Worker.Tick();
        Assert.Equal(3, rig.Hid.FindAllCalls);
        Assert.Single(rig.Hub.Attached);
        Assert.Equal(1, rig.Changes);
    }

    [Fact]
    public void A_unit_the_hub_drops_for_write_failures_waits_out_the_backoff_before_reopening()
    {
        var rig = new Rig();
        rig.Usb.Plug(0x0201);
        rig.Hid.Add(IbpPeripheralProtocol.Km10Mouse, "MS1");
        rig.Hid.RefuseWrites.Add("km10-mouse-MS1");
        rig.Worker.Tick();
        var id = Assert.Single(rig.Hub.Attached).DeviceId;
        Assert.Equal(1, rig.Changes);

        for (var i = 0; i < 5; i++) rig.Hub.WriteFrame(id, new Nexus.Service.Peripherals.Hyte.Np50.RgbColor[3]);
        Assert.Empty(rig.Hub.Attached);

        for (var i = 0; i < 5; i++)
        {
            rig.Now += 1000;
            rig.Worker.Tick();
        }
        Assert.Equal(1, rig.Hid.FindAllCalls);
        Assert.Empty(rig.Hub.Attached);
        Assert.Equal(2, rig.Changes); // the drop was reported once

        rig.Now += IbpPeripheralConnectionWorker.OpenRetryMs;
        rig.Worker.Tick();
        Assert.Equal(2, rig.Hid.FindAllCalls);
        Assert.Single(rig.Hub.Attached);
        Assert.Equal(3, rig.Changes);
    }

    [Fact]
    public void A_stuck_unit_does_not_block_a_sibling_from_opening()
    {
        var rig = new Rig();
        rig.Usb.Plug(0x0301);
        rig.Hid.Add(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        rig.Hid.RefuseOpen.Add("km7-keyboard-KB1");
        rig.Worker.Tick();
        Assert.Empty(rig.Hub.Attached);

        rig.Usb.Plug(0x0200);
        rig.Hid.Add(IbpPeripheralProtocol.Km7Mouse, "MS1");
        rig.Now += 1000;
        rig.Worker.Tick(); // mouse is new and allowed: enumerates despite the keyboard's backoff
        Assert.Equal(2, rig.Hid.FindAllCalls);
        var only = Assert.Single(rig.Hub.Attached);
        Assert.Equal(IbpPeripheralKind.Mouse, only.Model.Kind);
    }
}

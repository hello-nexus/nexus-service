using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests.Devices;

/// <summary>The chipset SMBus as a device: one row, bus "smbus", on by default and never auto-adopted.</summary>
public class SmbusDramDeviceTests
{
    [Fact]
    public void Handler_HasNoUsbIdentity_AndNoPage()
    {
        IDeviceHandler h = new SmbusDramHandler();
        Assert.Equal("smbus-dram", h.Id);
        Assert.Empty(h.Identifiers);
        Assert.True(h.SupportsNexusControl);
        Assert.False(h.HasPage);
        Assert.Equal("", h.GetFirmwareVersion());
        Assert.Equal(OperatingSystem.IsWindows(), h.IsConnected(Array.Empty<UsbDeviceEntry>()));
    }

    [Fact]
    public void GetAll_CarriesTheBusAndTheGateOntoTheWire()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), new SmbusDramHandler() },
            new FixedUsbEnumerator(),
            new PluginProviderRegistry(),
            new DeviceControlGate(new InMemoryConfigStore()));

        var items = manager.GetAll();

        var memory = items.Single(i => i.Id == SmbusDramHandler.HandlerId);
        Assert.Equal("smbus", memory.Bus);
        Assert.Equal("memory", memory.Category);
        Assert.True(memory.SupportsNexusControl);
        Assert.False(memory.Experimental);
        Assert.False(memory.HasPage);
        Assert.Equal("icue", memory.ConflictAppId);
        Assert.Equal("usb", items.Single(i => i.Id == "cnvs").Bus);
    }

    [Fact]
    public void Policy_BusFor_IsUsbForEverythingButTheSmbusDevice()
    {
        Assert.Equal("smbus", DeviceControlPolicy.BusFor(SmbusDramHandler.HandlerId));
        Assert.Equal("usb", DeviceControlPolicy.BusFor("cnvs"));
        Assert.Equal("usb", DeviceControlPolicy.BusFor("corsair"));
    }

    [Fact]
    public void Policy_SharedBusDefaultsOn_EvenThoughItNamesACompetingApp()
    {
        Assert.True(DeviceControlPolicy.DefaultOn(SmbusDramHandler.HandlerId));
        Assert.False(DeviceControlPolicy.DefaultOn("corsair"));
        Assert.True(DeviceControlPolicy.DefaultOn("cnvs"));
    }

    [Fact]
    public void Policy_ConflictAppShowsInTheUi_AndTheBusIsNotExperimental()
    {
        Assert.Equal("icue", DeviceControlPolicy.ConflictAppFor(SmbusDramHandler.HandlerId));
        Assert.False(DeviceControlPolicy.IsExperimental(SmbusDramHandler.HandlerId));
    }

    [Fact]
    public void Gate_StartsEnabled_AndTurningItOffSticks()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());

        Assert.True(gate.IsEnabled(SmbusDramHandler.HandlerId));
        gate.SetEnabled(SmbusDramHandler.HandlerId, false);
        Assert.False(gate.IsEnabled(SmbusDramHandler.HandlerId));
    }

    [Fact]
    public void Gate_ExplicitChoice_RaisesChangedInOrder()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        var raised = new List<(string Id, bool Enabled)>();
        gate.Changed += (id, enabled) => raised.Add((id, enabled));

        gate.SetEnabled(SmbusDramHandler.HandlerId, true);
        Assert.True(gate.IsEnabled(SmbusDramHandler.HandlerId));

        gate.SetEnabled(SmbusDramHandler.HandlerId, false);
        Assert.False(gate.IsEnabled(SmbusDramHandler.HandlerId));

        Assert.Equal(new[] { (SmbusDramHandler.HandlerId, true), (SmbusDramHandler.HandlerId, false) }, raised);
    }
}

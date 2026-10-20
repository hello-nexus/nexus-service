using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Plugins;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Devices;

/// <summary>A ROG Astral card under Devices: present once a GPU read found its G-sensor, named after the card, read-only.</summary>
public class AstralHandlerTests
{
    // Only the Level Sense states matter here; the rest are never called.
    private sealed class Sensors : ISensorProvider
    {
        public List<GpuLevelSenseState> States { get; } = new();

        public IReadOnlyList<GpuLevelSenseState> GetGpuLevelSenseStates() => States;

        public IReadOnlyList<GpuReadout> GetGpus() => throw new NotSupportedException();
        public string GetCpuModel() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => throw new NotSupportedException();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => throw new NotSupportedException();
        public IReadOnlyList<string> GetGpuModels() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => throw new NotSupportedException();
        public string GetMemoryTotalFormatted() => throw new NotSupportedException();
        public string GetRamBrandModel() => throw new NotSupportedException();
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => throw new NotSupportedException();
        public IReadOnlyList<string> GetStoragePartitions() => throw new NotSupportedException();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => throw new NotSupportedException();
        public string GetStorageBrandModel() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => throw new NotSupportedException();
        public string GetMotherboardModel() => throw new NotSupportedException();
        public SensorExtras GetSensorExtras() => throw new NotSupportedException();
        public string GetOsVersion() => throw new NotSupportedException();
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly GpuLevelSenseState Astral = new("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", false, 42, 0.2f, -0.1f, 88.5f, 1.2f);

    [Fact]
    public void Handler_HasNoUsbIdentity_NoSwitch_AndAPage()
    {
        IDeviceHandler h = new AstralHandler(new Sensors());
        Assert.Equal("asus-astral", h.Id);
        Assert.Equal("gpu", h.Category);
        Assert.Empty(h.Identifiers);
        Assert.False(h.SupportsNexusControl);
        Assert.True(h.HasPage);
        Assert.Equal("", h.GetFirmwareVersion());
    }

    [Fact]
    public void IsAbsent_UntilAGpuReadFindsTheCard()
    {
        var sensors = new Sensors();
        IDeviceHandler h = new AstralHandler(sensors);
        Assert.False(h.IsConnected(Array.Empty<UsbDeviceEntry>()));
        Assert.Equal("ASUS ROG Astral", h.Name);

        sensors.States.Add(Astral);

        Assert.True(h.IsConnected(Array.Empty<UsbDeviceEntry>()));
        Assert.Equal("ASUS ROG Astral RTX 5090", h.Name);
    }

    [Fact]
    public void GetAll_ListsTheCard_UnderItsName_WithoutAControlSwitch()
    {
        var sensors = new Sensors();
        sensors.States.Add(Astral);
        var manager = new DeviceManager(
            new IDeviceHandler[] { new AstralHandler(sensors) },
            new FixedUsbEnumerator(),
            new PluginProviderRegistry(),
            new DeviceControlGate(new InMemoryConfigStore()));

        var card = manager.GetAll().Single();

        Assert.Equal("asus-astral", card.Id);
        Assert.Equal("ASUS ROG Astral RTX 5090", card.Name);
        Assert.True(card.Connected);
        Assert.True(card.HasPage);
        Assert.False(card.SupportsNexusControl);
        Assert.False(card.Experimental);
        Assert.Null(card.ConflictAppId);
        Assert.Equal("usb", card.Bus);
    }
}

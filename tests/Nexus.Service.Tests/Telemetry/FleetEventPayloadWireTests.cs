using System.Linq;
using System.Text.Json;
using Nexus.Service.Serialization;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

/// <summary>Serializes through the real AppJsonContext, not a hand-written string, so a field rename is caught the moment it breaks the nexus-api contract.</summary>
public class FleetEventPayloadWireTests
{
    [Fact]
    public void Payload_without_specs_omits_the_specs_field()
    {
        var payload = new FleetEventPayload
        {
            InstallId = "abc123",
            Type = TelemetryEvents.Install,
            Version = "3.1.0",
            Os = "win",
            OsVersion = "Windows 11",
            Arch = "x64",
            DeviceType = "desktop",
        };

        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.FleetEventPayload);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("abc123", root.GetProperty("installId").GetString());
        Assert.Equal("install", root.GetProperty("type").GetString());
        Assert.Equal("3.1.0", root.GetProperty("version").GetString());
        Assert.Equal("win", root.GetProperty("os").GetString());
        Assert.Equal("Windows 11", root.GetProperty("osVersion").GetString());
        Assert.Equal("x64", root.GetProperty("arch").GetString());
        Assert.Equal("desktop", root.GetProperty("deviceType").GetString());
        Assert.False(root.TryGetProperty("specs", out _));
    }

    [Fact]
    public void Specs_event_carries_the_coarse_hardware_summary()
    {
        var payload = new FleetEventPayload
        {
            InstallId = "abc123",
            Type = TelemetryEvents.Specs,
            Version = "3.1.0",
            Os = "win",
            OsVersion = "Windows 11",
            Arch = "x64",
            DeviceType = "desktop",
            Specs = new FleetEventSpecs
            {
                Cpu = "AMD Ryzen 9 9950X",
                Gpu = new[] { "NVIDIA RTX 5080" },
                RamBytes = 34359738368,
                Motherboard = "ASUS ROG X870E",
                Devices =
                {
                    new FleetEventDevice { Vid = 0x1532, Pid = 0x0226, Name = "Huntsman Elite", Manufacturer = "Razer" },
                },
            },
        };

        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.FleetEventPayload);
        var specs = JsonDocument.Parse(json).RootElement.GetProperty("specs");

        Assert.Equal("AMD Ryzen 9 9950X", specs.GetProperty("cpu").GetString());
        Assert.Equal("NVIDIA RTX 5080", specs.GetProperty("gpu")[0].GetString());
        Assert.Equal(34359738368, specs.GetProperty("ramBytes").GetInt64());
        Assert.Equal("ASUS ROG X870E", specs.GetProperty("motherboard").GetString());

        // nexus-api keys on the ids and treats the label as a fallback for
        // hardware its catalog does not know; a rename here breaks that.
        var device = specs.GetProperty("devices")[0];
        Assert.Equal(0x1532, device.GetProperty("vid").GetInt32());
        Assert.Equal(0x0226, device.GetProperty("pid").GetInt32());
        Assert.Equal("Huntsman Elite", device.GetProperty("name").GetString());
        Assert.Equal("Razer", device.GetProperty("manufacturer").GetString());
        Assert.False(device.TryGetProperty("serial", out _));
    }

    [Fact]
    public void Specs_event_serializes_ram_modules_and_drives_without_identifying_fields()
    {
        var payload = new FleetEventPayload
        {
            InstallId = "abc123",
            Type = TelemetryEvents.Specs,
            Specs = new FleetEventSpecs
            {
                RamBytes = 34359738368,
                RamModuleCount = 2,
                RamModules = new()
                {
                    new FleetEventRamModule
                    {
                        Manufacturer = "Corsair", ManufacturerId = "029E", PartNumber = "CMK32GX5M2B6000C36",
                        CapacityBytes = 17179869184, ConfiguredSpeedMts = 6000, RatedSpeedMts = 6000, Generation = "DDR5",
                    },
                },
                StorageDrives = new()
                {
                    new FleetEventDrive { Model = "Samsung SSD 990 PRO 2TB", CapacityBytes = 2000398934016, Bus = "nvme", Media = "ssd" },
                },
            },
        };

        var specs = JsonDocument.Parse(JsonSerializer.Serialize(payload, AppJsonContext.Default.FleetEventPayload))
            .RootElement.GetProperty("specs");

        Assert.Equal(34359738368, specs.GetProperty("ramBytes").GetInt64());
        Assert.Equal(2, specs.GetProperty("ramModuleCount").GetInt32());
        var module = specs.GetProperty("ramModules")[0];
        Assert.Equal("Corsair", module.GetProperty("manufacturer").GetString());
        Assert.Equal("029E", module.GetProperty("manufacturerId").GetString());
        Assert.Equal("CMK32GX5M2B6000C36", module.GetProperty("partNumber").GetString());
        Assert.Equal(17179869184, module.GetProperty("capacityBytes").GetInt64());
        Assert.Equal(6000, module.GetProperty("configuredSpeedMts").GetInt32());
        Assert.Equal(6000, module.GetProperty("ratedSpeedMts").GetInt32());
        Assert.Equal("DDR5", module.GetProperty("generation").GetString());
        var drive = specs.GetProperty("storageDrives")[0];
        Assert.Equal("Samsung SSD 990 PRO 2TB", drive.GetProperty("model").GetString());
        Assert.Equal("nvme", drive.GetProperty("bus").GetString());
        Assert.Equal("ssd", drive.GetProperty("media").GetString());
        Assert.False(drive.TryGetProperty("vendor", out _));
        Assert.False(drive.TryGetProperty("serial", out _));
        Assert.False(module.TryGetProperty("serial", out _));
    }

    [Fact]
    public void Specs_event_serializes_gpu_cards_with_hex_subsystem_ids_and_no_serial()
    {
        var payload = new FleetEventPayload
        {
            Type = TelemetryEvents.Specs,
            Specs = new FleetEventSpecs
            {
                Gpu = new[] { "NVIDIA GeForce RTX 3090" },
                GpuCards = new() { FleetEventGpuCard.From(new Nexus.Service.Models.Sensors.GpuCard("NVIDIA GeForce RTX 3090", "MSI", 0x1462, 0x3881)) },
            },
        };

        var specs = JsonDocument.Parse(JsonSerializer.Serialize(payload, AppJsonContext.Default.FleetEventPayload))
            .RootElement.GetProperty("specs");

        Assert.Equal("NVIDIA GeForce RTX 3090", specs.GetProperty("gpu")[0].GetString());
        var card = specs.GetProperty("gpuCards")[0];
        Assert.Equal("NVIDIA GeForce RTX 3090", card.GetProperty("chip").GetString());
        Assert.Equal("MSI", card.GetProperty("maker").GetString());
        Assert.Equal("1462", card.GetProperty("subsystemVendorId").GetString());
        Assert.Equal("3881", card.GetProperty("subsystemDeviceId").GetString());
        Assert.Equal(4, card.EnumerateObject().Count());
    }

    [Fact]
    public void Specs_without_boards_omit_gpu_cards_and_an_unnamed_vendor_omits_the_maker()
    {
        var bare = new FleetEventPayload { Type = TelemetryEvents.Specs, Specs = new FleetEventSpecs { RamBytes = 1 } };
        Assert.False(JsonDocument.Parse(JsonSerializer.Serialize(bare, AppJsonContext.Default.FleetEventPayload))
            .RootElement.GetProperty("specs").TryGetProperty("gpuCards", out _));

        var unnamed = FleetEventGpuCard.From(new Nexus.Service.Models.Sensors.GpuCard("GPU", null, 0x1234, 0x5678));
        var json = JsonSerializer.Serialize(new FleetEventPayload { Specs = new FleetEventSpecs { GpuCards = new() { unnamed } } }, AppJsonContext.Default.FleetEventPayload);
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("specs").GetProperty("gpuCards")[0].TryGetProperty("maker", out _));
    }

    [Fact]
    public void Specs_without_inventory_omit_the_new_fields()
    {
        var payload = new FleetEventPayload { Type = TelemetryEvents.Specs, Specs = new FleetEventSpecs { RamBytes = 1 } };
        var specs = JsonDocument.Parse(JsonSerializer.Serialize(payload, AppJsonContext.Default.FleetEventPayload))
            .RootElement.GetProperty("specs");
        Assert.False(specs.TryGetProperty("ramModules", out _));
        Assert.False(specs.TryGetProperty("ramModuleCount", out _));
        Assert.False(specs.TryGetProperty("storageDrives", out _));
    }
}

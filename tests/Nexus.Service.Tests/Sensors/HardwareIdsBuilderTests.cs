using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class HardwareIdsBuilderTests
{
    private static HardwareInventory Inventory() => new(
        new[]
        {
            new FleetEventRamModule { Manufacturer = "Corsair", ManufacturerId = "029E", PartNumber = "CMK32GX5M2B6000C30", CapacityBytes = 17179869184, ConfiguredSpeedMts = 6000, RatedSpeedMts = 6000, Generation = "DDR5" },
        },
        new[]
        {
            new FleetEventDrive { Vendor = "Samsung", Model = "SSD 990 PRO 2TB", CapacityBytes = 2000398934016, Bus = "nvme", Media = "ssd" },
        });

    [Fact]
    public void Build_CopiesModelIdsOnly()
    {
        var cards = new[] { new GpuCard("NVIDIA GeForce RTX 3090", "ASUS", 0x1043, 0x87D3, 0x10DE, 0x2204) };
        var ids = HardwareIdsBuilder.Build(cards, Inventory(), ("ASUSTeK COMPUTER INC.", "ROG STRIX X670E-E GAMING WIFI"));

        var gpu = Assert.Single(ids.Gpus!);
        Assert.Equal("10de", gpu.Vendor);
        Assert.Equal("2204", gpu.Device);
        Assert.Equal("1043", gpu.SubVendor);
        Assert.Equal("87d3", gpu.SubDevice);
        var ram = Assert.Single(ids.RamModules!);
        Assert.Equal("Corsair", ram.Manufacturer);
        Assert.Equal("CMK32GX5M2B6000C30", ram.PartNumber);
        Assert.Equal(17179869184, ram.CapacityBytes);
        Assert.Equal(6000, ram.ConfiguredSpeedMts);
        var drive = Assert.Single(ids.Drives!);
        Assert.Equal("Samsung", drive.Vendor);
        Assert.Equal("SSD 990 PRO 2TB", drive.Model);
        Assert.Equal("ASUSTeK COMPUTER INC.", ids.Board!.Manufacturer);
        Assert.Equal("ROG STRIX X670E-E GAMING WIFI", ids.Board.Product);
    }

    [Fact]
    public void Build_KeepsGpuOrderAndNullsUnreadIds()
    {
        var cards = new[] { new GpuCard("AMD Radeon(TM) Graphics", null, 0, 0, 0x1002, 0x164E), new GpuCard("NVIDIA GeForce RTX 4090") };
        var ids = HardwareIdsBuilder.Build(cards, HardwareInventory.Empty, null);

        Assert.Equal(2, ids.Gpus!.Count);
        Assert.Equal("1002", ids.Gpus[0].Vendor);
        Assert.Null(ids.Gpus[0].SubVendor);
        Assert.Null(ids.Gpus[1].Vendor);
        Assert.Null(ids.Gpus[1].SubDevice);
    }

    [Fact]
    public void Build_NoSourceLeavesBlocksNull()
    {
        var ids = HardwareIdsBuilder.Build(Array.Empty<GpuCard>(), HardwareInventory.Empty, (null, ""));

        Assert.Null(ids.Gpus);
        Assert.Null(ids.RamModules);
        Assert.Null(ids.Drives);
        Assert.Null(ids.Board);
        Assert.Null(ids.Monitors);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(0xFFFF + 1, null)]
    [InlineData(0x10DE, "10de")]
    [InlineData(0x0A1B, "0a1b")]
    public void Hex_FormatsFourDigitsOrNull(int id, string? expected) =>
        Assert.Equal(expected, HardwareIdsBuilder.Hex(id));

    [Fact]
    public void ParseMonitorIds_TakesModelFieldsAndIgnoresSerialColumns()
    {
        var rows = new JsonArray
        {
            (JsonNode)new JsonObject { ["Name"] = "DELL U2415", ["Mfg"] = "DEL", ["ProductCode"] = "A0B1", ["Serial"] = "7MT0123", ["SerialNumber"] = 123456 },
            (JsonNode)new JsonObject { ["Name"] = "", ["Mfg"] = "", ["ProductCode"] = "" },
        };

        var monitors = SystemSpecsCollector.ParseMonitorIds(rows)!;

        var monitor = Assert.Single(monitors);
        Assert.Equal("DEL", monitor.Manufacturer);
        Assert.Equal("A0B1", monitor.ProductCode);
        Assert.Equal("DELL U2415", monitor.Name);
        Assert.DoesNotContain("7MT0123", JsonSerializer.Serialize(monitor, AppJsonContext.Default.MonitorIdsDto));
    }

    [Fact]
    public void ParseMonitorIds_NoRowsIsNull() => Assert.Null(SystemSpecsCollector.ParseMonitorIds(new JsonArray()));

    [Fact]
    public void EdidProductCode_ReadsTheProductBytesAndNotTheSerial()
    {
        var edid = new byte[128];
        edid[10] = 0xB1;
        edid[11] = 0xA0;
        edid[12] = 0x78;
        edid[13] = 0x56;
        Assert.Equal("A0B1", WindowsSpecsParsers.EdidProductCode(edid));
        Assert.Equal("", WindowsSpecsParsers.EdidProductCode(new byte[4]));
    }

    [Fact]
    public void ParseBaseboard_ReadsManufacturerAndProductNotSerial()
    {
        var table = Smbios(
            Structure(1, new byte[] { 1, 2, 0, 3 }, "SysVendor", "SysProduct", "SYS-SERIAL"),
            Structure(2, new byte[] { 1, 2, 0, 3, 0 }, "ASUSTeK COMPUTER INC.", "ROG STRIX B650-A", "BOARD-SERIAL-9"));

        var board = SmbiosParser.ParseBaseboard(table);

        Assert.Equal(("ASUSTeK COMPUTER INC.", "ROG STRIX B650-A"), board);
    }

    [Fact]
    public void ParseBaseboard_NoType2IsNull()
    {
        Assert.Null(SmbiosParser.ParseBaseboard(Smbios(Structure(1, new byte[] { 1, 2, 0, 3 }, "A", "B", "C"))));
        Assert.Null(SmbiosParser.ParseBaseboard(null));
    }

    // Raw SMBIOS framing: 8 header bytes with the table length at offset 4, then each structure and an end marker.
    private static byte[] Smbios(params byte[][] structures)
    {
        var body = structures.SelectMany(s => s).Concat(new byte[] { 127, 4, 0, 0, 0, 0 }).ToArray();
        var table = new byte[8 + body.Length];
        BitConverter.GetBytes((uint)body.Length).CopyTo(table, 4);
        body.CopyTo(table, 8);
        return table;
    }

    private static byte[] Structure(byte type, byte[] fields, params string[] strings)
    {
        var bytes = new List<byte> { type, (byte)(4 + fields.Length), 0, 0 };
        bytes.AddRange(fields);
        foreach (var s in strings)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(s));
            bytes.Add(0);
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    [Fact]
    public void SpecsJson_NeverCarriesSerialLikeFieldsOrValues()
    {
        var specs = new SystemSpecsResponse
        {
            Oa3ProductKey = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE",
            HardwareIds = HardwareIdsBuilder.Build(
                new[] { new GpuCard("NVIDIA GeForce RTX 3090", "ASUS", 0x1043, 0x87D3, 0x10DE, 0x2204) },
                Inventory(),
                ("ASUSTeK COMPUTER INC.", "ROG STRIX B650-A")),
        };
        specs.HardwareIds.Monitors = new List<MonitorIdsDto> { new() { Manufacturer = "DEL", ProductCode = "A0B1", Name = "DELL U2415" } };

        var json = JsonSerializer.Serialize(specs, AppJsonContext.Default.SystemSpecsResponse);
        var block = JsonNode.Parse(json)!["hardwareIds"]!;

        Assert.NotNull(block["gpus"]);
        Assert.Equal("10de", block["gpus"]![0]!["vendor"]!.GetValue<string>());
        Assert.Equal("A0B1", block["monitors"]![0]!["productCode"]!.GetValue<string>());
        Assert.DoesNotContain("AAAAA-BBBBB", json);
        Assert.DoesNotContain(PropertyNames(block), n => n.Contains("serial", StringComparison.OrdinalIgnoreCase)
            || n.Contains("mac", StringComparison.OrdinalIgnoreCase)
            || n.Contains("key", StringComparison.OrdinalIgnoreCase)
            || n.Contains("host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DtoTypes_DeclareNoSerialLikeProperties()
    {
        var types = new[] { typeof(HardwareIdsDto), typeof(GpuIdsDto), typeof(RamModuleIdsDto), typeof(DriveIdsDto), typeof(BoardIdsDto), typeof(MonitorIdsDto) };
        var names = types.SelectMany(t => t.GetProperties().Select(p => p.Name)).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Serial", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Mac", StringComparison.Ordinal) || n.Contains("Key", StringComparison.Ordinal) || n.Contains("Host", StringComparison.Ordinal));
    }

    private static IEnumerable<string> PropertyNames(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, child) in o)
                {
                    yield return name;
                    if (child is not null)
                        foreach (var nested in PropertyNames(child))
                            yield return nested;
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item is not null)
                        foreach (var nested in PropertyNames(item))
                            yield return nested;
                break;
        }
    }
}

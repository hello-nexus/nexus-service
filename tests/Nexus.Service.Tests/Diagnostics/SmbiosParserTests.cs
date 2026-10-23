using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nexus.Service.Diagnostics.Memory;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class SmbiosParserTests
{
    private const byte MemoryDeviceType = 17;
    private const byte EndOfTableType = 127;
    private const byte MemoryTypeDdr5 = 0x22;

    [Fact]
    public void ParsesTwoDdr5DimmsAndSkipsEmptySlot()
    {
        var table = BuildTable(
            BuildType17(handle: 0x0001, sizeMb: 16384, memType: MemoryTypeDdr5, speedMts: 6000, configuredMts: 6000,
                deviceLocator: "DIMM_A1", manufacturer: "Corsair", partNumber: "CMK32GX5M2B6000"),
            BuildType17(handle: 0x0002, sizeMb: 16384, memType: MemoryTypeDdr5, speedMts: 6000, configuredMts: 4800,
                deviceLocator: "DIMM_A2", manufacturer: "Corsair", partNumber: "CMK32GX5M2B6000"),
            BuildEmptySlotType17(handle: 0x0003, deviceLocator: "DIMM_B1"));

        var (modules, xmp) = SmbiosParser.Parse(table);

        Assert.Equal(2, modules.Count);

        var a1 = modules.Single(m => m.Slot == "DIMM_A1");
        Assert.Equal(16384L * 1024 * 1024, a1.SizeBytes);
        Assert.Equal(6000, a1.MaxSpeedMts);
        Assert.Equal(6000, a1.ConfiguredSpeedMts);
        Assert.Equal("Corsair", a1.Manufacturer);
        Assert.Equal("CMK32GX5M2B6000", a1.PartNumber);

        var a2 = modules.Single(m => m.Slot == "DIMM_A2");
        Assert.Equal(16384L * 1024 * 1024, a2.SizeBytes);
        Assert.Equal(6000, a2.MaxSpeedMts);
        Assert.Equal(4800, a2.ConfiguredSpeedMts);

        // Empty slot (size word 0) must not appear at all.
        Assert.DoesNotContain(modules, m => m.Slot == "DIMM_B1");

        // DIMM_A1 runs above the DDR5 JEDEC base (5600) -> XMP likely, even
        // though DIMM_A2 alone would not qualify (4800 <= 5600).
        Assert.True(xmp);
    }

    [Fact]
    public void AllModulesAtJedecBase_XmpNotLikely()
    {
        var table = BuildTable(
            BuildType17(handle: 1, sizeMb: 16384, memType: MemoryTypeDdr5, speedMts: 5600, configuredMts: 5600,
                deviceLocator: "DIMM_A1", manufacturer: "Kingston", partNumber: "KF556C40"));

        var (modules, xmp) = SmbiosParser.Parse(table);

        Assert.Single(modules);
        Assert.False(xmp);
    }

    [Fact]
    public void NoModules_XmpIsNull()
    {
        var table = BuildTable(BuildEmptySlotType17(handle: 1, deviceLocator: "DIMM_A1"));

        var (modules, xmp) = SmbiosParser.Parse(table);

        Assert.Empty(modules);
        Assert.Null(xmp);
    }

    [Fact]
    public void TruncatedBuffer_NeverThrows()
    {
        var full = BuildTable(BuildType17(1, 16384, MemoryTypeDdr5, 6000, 6000, "DIMM_A1", "Corsair", "PN"));
        var truncated = full[..(full.Length - 5)];

        var (modules, xmp) = SmbiosParser.Parse(truncated);

        // Reaching here without an exception is the point of the test; result
        // content past truncation is best-effort.
        Assert.True(modules.Count >= 0);
    }

    [Fact]
    public void ParseModules_CarriesTheRawMemoryTypeAndSpeedsAlongside()
    {
        var table = BuildTable(
            BuildType17(handle: 0x0001, sizeMb: 16384, memType: MemoryTypeDdr5, speedMts: 6000, configuredMts: 5600,
                deviceLocator: "DIMM_A1", manufacturer: "Corsair", partNumber: "CMK32GX5M2B6000"),
            BuildEmptySlotType17(handle: 0x0002, deviceLocator: "DIMM_B1"));

        var modules = SmbiosParser.ParseModules(table);

        var only = Assert.Single(modules);
        Assert.Equal(MemoryTypeDdr5, only.MemoryType);
        Assert.Equal(6000, only.Info.MaxSpeedMts);
        Assert.Equal(5600, only.Info.ConfiguredSpeedMts);
    }

    [Fact]
    public void ParseModules_NoTable_ReturnsEmpty()
    {
        Assert.Empty(SmbiosParser.ParseModules(null));
    }

    // ── Synthetic SMBIOS table construction ──

    private static byte[] BuildTable(params byte[][] structures)
    {
        using var body = new MemoryStream();
        foreach (var s in structures) body.Write(s, 0, s.Length);

        var eot = new byte[] { EndOfTableType, 4, 0xFF, 0xFF }; // handle unused, no strings -> 00 00 follows
        body.Write(eot, 0, eot.Length);
        body.WriteByte(0);
        body.WriteByte(0);

        var bodyBytes = body.ToArray();

        using var raw = new MemoryStream();
        raw.WriteByte(0); // Used20CallingMethod
        raw.WriteByte(3); // SMBIOSMajorVersion
        raw.WriteByte(4); // SMBIOSMinorVersion
        raw.WriteByte(0); // DmiRevision
        raw.Write(BitConverter.GetBytes((uint)bodyBytes.Length), 0, 4);
        raw.Write(bodyBytes, 0, bodyBytes.Length);
        return raw.ToArray();
    }

    private static byte[] BuildType17(
        ushort handle, int sizeMb, byte memType, ushort speedMts, ushort configuredMts,
        string deviceLocator, string manufacturer, string partNumber)
    {
        const byte length = 0x22; // includes ConfiguredMemorySpeed at 0x20-0x21
        var strings = new List<string>();
        byte StrIndex(string s)
        {
            strings.Add(s);
            return (byte)strings.Count;
        }

        var b = new byte[length];
        b[0] = MemoryDeviceType;
        b[1] = length;
        b[2] = (byte)(handle & 0xFF);
        b[3] = (byte)(handle >> 8);
        WriteU16(b, 0x0C, (ushort)Math.Clamp(sizeMb, 0, 0x7FFE)); // Size (MB), non-extended range
        b[0x0E] = 9; // form factor: DIMM
        b[0x10] = StrIndex(deviceLocator);
        b[0x12] = memType;
        WriteU16(b, 0x15, speedMts);
        b[0x17] = StrIndex(manufacturer);
        b[0x1A] = StrIndex(partNumber);
        WriteU16(b, 0x20, configuredMts);

        return AppendStrings(b, strings);
    }

    [Fact]
    public void ModuleManufacturerIdField_isExposedAsHex()
    {
        const byte length = 0x30;
        var strings = new List<string> { "DIMM_A1", "Unknown", "F5-6000J3038F16G" };
        var b = new byte[length];
        b[0] = MemoryDeviceType;
        b[1] = length;
        WriteU16(b, 0x0C, 16384);
        b[0x10] = 1;
        b[0x12] = MemoryTypeDdr5;
        b[0x17] = 2;
        b[0x1A] = 3;
        b[0x2C] = 0x04; // continuation count
        b[0x2D] = 0xCD; // id byte
        var table = BuildTable(AppendStrings(b, strings));

        var (modules, _) = SmbiosParser.Parse(table);

        Assert.Equal("04CD", modules.Single().ModuleManufacturerId);
    }

    private static byte[] BuildEmptySlotType17(ushort handle, string deviceLocator)
    {
        const byte length = 0x22;
        var strings = new List<string> { deviceLocator };

        var b = new byte[length];
        b[0] = MemoryDeviceType;
        b[1] = length;
        b[2] = (byte)(handle & 0xFF);
        b[3] = (byte)(handle >> 8);
        WriteU16(b, 0x0C, 0); // Size 0 -> empty slot, parser must skip it
        b[0x10] = 1; // device locator string index

        return AppendStrings(b, strings);
    }

    private static void WriteU16(byte[] b, int offset, ushort value)
    {
        b[offset] = (byte)(value & 0xFF);
        b[offset + 1] = (byte)(value >> 8);
    }

    private static byte[] AppendStrings(byte[] formatted, List<string> strings)
    {
        using var ms = new MemoryStream();
        ms.Write(formatted, 0, formatted.Length);
        foreach (var s in strings)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0);
        }
        ms.WriteByte(0); // string-set terminator
        return ms.ToArray();
    }
}

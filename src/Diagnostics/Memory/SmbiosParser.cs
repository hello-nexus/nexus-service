using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Diagnostics.Memory;

/// <summary>Contract-shaped memory module readout (SMBIOS Type 17, one per
/// populated slot; empty slots are skipped entirely).</summary>
public sealed record MemoryModuleInfo(
    string Slot,
    long? SizeBytes,
    int? MaxSpeedMts,
    int? ConfiguredSpeedMts,
    string? Manufacturer,
    string? PartNumber)
{
    /// <summary>SMBIOS Module Manufacturer ID (the Type 17 field dmidecode prints under that name) as the JEDEC continuation-count byte then id byte in hex; null when the table predates the field or leaves it zero.</summary>
    public string? ModuleManufacturerId { get; init; }
}

/// <summary>
/// Pure SMBIOS Type 17 (Memory Device) parser over the raw buffer returned by
/// Win32 GetSystemFirmwareTable('RSMB', ...). No P/Invoke, no OS dependency -
/// kept as a standalone function of byte[] so it is testable with a synthetic
/// table on any platform. See MemoryInfoProvider for the Windows-only caller
/// that supplies the real buffer.
/// </summary>
public static class SmbiosParser
{
    private const byte MemoryDeviceType = 17;
    private const byte EndOfTableType = 127;

    // SMBIOS Type 17 field offsets, relative to the structure start (spec 3.x).
    private const int OffsetSize = 0x0C;
    private const int OffsetDeviceLocator = 0x10;
    private const int OffsetMemoryType = 0x12;
    private const int OffsetSpeed = 0x15;
    private const int OffsetManufacturer = 0x17;
    private const int OffsetPartNumber = 0x1A;
    private const int OffsetExtendedSize = 0x1C;
    private const int OffsetConfiguredSpeed = 0x20;
    private const int OffsetModuleManufacturerId = 0x2C;

    private const ushort SizeWordUnknown = 0xFFFF;
    private const ushort SizeWordExtended = 0x7FFF;

    private const byte MemoryTypeDdr4 = 0x1A;
    private const byte MemoryTypeDdr5 = 0x22;
    private const int JedecBaseDdr4Mts = 3200;
    private const int JedecBaseDdr5Mts = 5600;

    private sealed class ParsedModule
    {
        public MemoryModuleInfo Info = null!;
        public byte? MemoryType;
    }

    /// <summary>Parses every populated Type 17 structure plus an XMP-likely
    /// heuristic across all of them. rawFirmwareTableBuffer is the buffer exactly
    /// as returned by GetSystemFirmwareTable, including its 8-byte RawSMBIOSData
    /// header (Used20CallingMethod/MajorVersion/MinorVersion/DmiRevision/Length).</summary>
    public static (IReadOnlyList<MemoryModuleInfo> Modules, bool? XmpLikelyActive) Parse(byte[]? rawFirmwareTableBuffer)
    {
        var parsed = ParseDevices(rawFirmwareTableBuffer);
        var modules = new List<MemoryModuleInfo>(parsed.Count);
        foreach (var p in parsed) modules.Add(p.Info);
        return (modules, ComputeXmpLikelyActive(parsed));
    }

    private const byte SystemEnclosureType = 3;
    private const int OffsetChassisType = 0x05;
    private const byte ChassisTypeMask = 0x7F;

    /// <summary>The chassis type of the first System Enclosure (Type 3) structure, without its chassis-lock bit; null when absent.</summary>
    internal static int? ParseChassisType(byte[]? rawFirmwareTableBuffer)
    {
        if (rawFirmwareTableBuffer is null || rawFirmwareTableBuffer.Length < 8)
        {
            return null;
        }

        var end = Math.Min(8 + (int)BitConverter.ToUInt32(rawFirmwareTableBuffer, 4), rawFirmwareTableBuffer.Length);
        var offset = 8;
        while (offset + 4 <= end)
        {
            var type = rawFirmwareTableBuffer[offset];
            var length = rawFirmwareTableBuffer[offset + 1];
            if (length < 4 || offset + length > end || type == EndOfTableType)
            {
                return null;
            }

            if (type == SystemEnclosureType)
            {
                return ReadByte(rawFirmwareTableBuffer, offset, length, OffsetChassisType) is { } value ? value & ChassisTypeMask : null;
            }

            offset = SkipStringSet(rawFirmwareTableBuffer, offset + length, end, out _);
        }

        return null;
    }

    /// <summary>Each populated module with its raw SMBIOS memory-type byte (Win32_PhysicalMemory.SMBIOSMemoryType).</summary>
    internal static IReadOnlyList<(MemoryModuleInfo Info, byte? MemoryType)> ParseModules(byte[]? rawFirmwareTableBuffer)
    {
        var result = new List<(MemoryModuleInfo, byte?)>();
        foreach (var p in ParseDevices(rawFirmwareTableBuffer)) result.Add((p.Info, p.MemoryType));
        return result;
    }

    private static List<ParsedModule> ParseDevices(byte[]? rawFirmwareTableBuffer)
    {
        var parsed = new List<ParsedModule>();
        if (rawFirmwareTableBuffer is null || rawFirmwareTableBuffer.Length < 8)
        {
            return parsed;
        }

        uint tableLength = BitConverter.ToUInt32(rawFirmwareTableBuffer, 4);
        int start = 8;
        int end = Math.Min(start + (int)tableLength, rawFirmwareTableBuffer.Length);

        int offset = start;
        while (offset + 4 <= end)
        {
            byte type = rawFirmwareTableBuffer[offset];
            byte length = rawFirmwareTableBuffer[offset + 1];
            if (length < 4) break; // malformed structure header, stop rather than guess

            int formattedEnd = offset + length;
            if (formattedEnd > end) break;

            int next = SkipStringSet(rawFirmwareTableBuffer, formattedEnd, end, out var strings);

            if (type == EndOfTableType)
            {
                break;
            }

            if (type == MemoryDeviceType)
            {
                var module = ParseMemoryDevice(rawFirmwareTableBuffer, offset, length, strings);
                if (module is not null)
                {
                    parsed.Add(module);
                }
            }

            offset = next;
        }

        return parsed;
    }

    // Scans for the double-null (00 00) terminator following a structure's
    // formatted area - the standard SMBIOS algorithm, one byte at a time, which
    // works uniformly whether zero or many strings are present (a structure
    // with no string references is immediately followed by 00 00). Then
    // tokenizes the individual null-terminated strings within that span.
    // Returns the offset just past the double-null terminator.
    private static int SkipStringSet(byte[] buf, int formattedEnd, int end, out List<string> strings)
    {
        strings = new List<string>();
        int p = formattedEnd;
        while (p + 1 < end && !(buf[p] == 0 && buf[p + 1] == 0))
        {
            p++;
        }

        int i = formattedEnd;
        while (i < p)
        {
            int strStart = i;
            while (i < p && buf[i] != 0) i++;
            strings.Add(Encoding.ASCII.GetString(buf, strStart, i - strStart));
            i++; // skip this string's own null terminator
        }

        return Math.Min(p + 2, end);
    }

    private static ParsedModule? ParseMemoryDevice(byte[] b, int offset, byte length, List<string> strings)
    {
        var sizeWord = ReadU16(b, offset, length, OffsetSize);
        if (sizeWord is null or 0) return null; // empty slot

        long? sizeBytes;
        if (sizeWord == SizeWordUnknown)
        {
            sizeBytes = null;
        }
        else if (sizeWord == SizeWordExtended)
        {
            var extendedMb = ReadU32(b, offset, length, OffsetExtendedSize);
            sizeBytes = extendedMb is uint mb ? (long)(mb & 0x7FFFFFFF) * 1024L * 1024L : null;
        }
        else
        {
            ushort raw = sizeWord.Value;
            bool isKb = (raw & 0x8000) != 0;
            int amount = raw & 0x7FFF;
            sizeBytes = isKb ? (long)amount * 1024L : (long)amount * 1024L * 1024L;
        }

        string slot = ReadString(b, offset, length, OffsetDeviceLocator, strings);
        string? manufacturer = NullIfEmpty(ReadString(b, offset, length, OffsetManufacturer, strings));
        string? partNumber = NullIfEmpty(ReadString(b, offset, length, OffsetPartNumber, strings));

        int? maxSpeedMts = ReadU16(b, offset, length, OffsetSpeed) is ushort sp and not 0 and not SizeWordUnknown ? sp : null;

        int? configuredSpeedMts = null;
        if (length > OffsetConfiguredSpeed)
        {
            var cfg = ReadU16(b, offset, length, OffsetConfiguredSpeed);
            if (cfg is ushort c and not 0 and not SizeWordUnknown) configuredSpeedMts = c;
        }

        string? moduleManufacturerId = null;
        if (ReadByte(b, offset, length, OffsetModuleManufacturerId) is byte bankByte
            && ReadByte(b, offset, length, OffsetModuleManufacturerId + 1) is byte idByte
            && (bankByte | idByte) != 0)
        {
            moduleManufacturerId = $"{bankByte:X2}{idByte:X2}";
        }

        byte? memoryType = ReadByte(b, offset, length, OffsetMemoryType);

        return new ParsedModule
        {
            MemoryType = memoryType,
            Info = new MemoryModuleInfo(slot, sizeBytes, maxSpeedMts, configuredSpeedMts, manufacturer, partNumber)
            {
                ModuleManufacturerId = moduleManufacturerId,
            },
        };
    }

    private static bool? ComputeXmpLikelyActive(List<ParsedModule> modules)
    {
        bool anyDeterminable = false;
        foreach (var m in modules)
        {
            var jedecBase = JedecBaseMts(m.MemoryType);
            var configured = m.Info.ConfiguredSpeedMts;
            if (jedecBase is null || configured is null) continue;

            anyDeterminable = true;
            if (configured.Value > jedecBase.Value) return true;
        }
        return anyDeterminable ? false : null;
    }

    private static int? JedecBaseMts(byte? memoryType) => memoryType switch
    {
        MemoryTypeDdr4 => JedecBaseDdr4Mts,
        MemoryTypeDdr5 => JedecBaseDdr5Mts,
        _ => null,
    };

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static byte? ReadByte(byte[] b, int offset, byte length, int fieldOffset)
    {
        if (fieldOffset >= length) return null;
        return b[offset + fieldOffset];
    }

    private static ushort? ReadU16(byte[] b, int offset, byte length, int fieldOffset)
    {
        if (fieldOffset + 1 >= length) return null;
        return (ushort)(b[offset + fieldOffset] | (b[offset + fieldOffset + 1] << 8));
    }

    private static uint? ReadU32(byte[] b, int offset, byte length, int fieldOffset)
    {
        if (fieldOffset + 3 >= length) return null;
        return (uint)(b[offset + fieldOffset]
            | (b[offset + fieldOffset + 1] << 8)
            | (b[offset + fieldOffset + 2] << 16)
            | (b[offset + fieldOffset + 3] << 24));
    }

    private static string ReadString(byte[] b, int offset, byte length, int fieldOffset, List<string> strings)
    {
        var idx = ReadByte(b, offset, length, fieldOffset);
        if (idx is null || idx.Value == 0 || idx.Value > strings.Count) return "";
        return strings[idx.Value - 1].Trim();
    }
}

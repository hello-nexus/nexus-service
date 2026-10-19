using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Sensors;

/// <summary>
/// In-process Windows reads behind the System Specs tab: no child process, no
/// WMI. Emits the JSON shape <see cref="SystemSpecsCollector"/> parses. Every
/// field is read independently, so one failed read leaves the others intact.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSpecsNative
{
    public static JsonObject Collect()
    {
        var root = new JsonObject();
        root["os"] = Safe(ReadOs);
        root["ram"] = Safe(ReadRam) ?? new JsonArray();
        root["disks"] = Safe(ReadDisks) ?? new JsonArray();
        root["monitors"] = Safe(ReadMonitors) ?? new JsonArray();
        root["video"] = Safe(ReadVideo) ?? new JsonArray();
        root["sound"] = Safe(ReadSound) ?? new JsonArray();
        root["net"] = Safe(ReadNet) ?? new JsonArray();
        root["oa3"] = Safe(ReadOa3Key) ?? "";
        return root;
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[system-specs] {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    // ── OS ────────────────────────────────────────────────────────────────

    private static JsonObject? ReadOs()
    {
        var info = new RtlOsVersionInfo { Size = (uint)Marshal.SizeOf<RtlOsVersionInfo>() };
        if (RtlGetVersion(ref info) != 0) return null;

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var productName = key?.GetValue("ProductName") as string ?? "";
        var caption = WindowsSpecsParsers.ComposeOsCaption(productName, (int)info.Build);
        if (caption.Length == 0) return null;
        return new JsonObject
        {
            ["Caption"] = caption,
            ["Version"] = $"{info.Major}.{info.Minor}.{info.Build}",
        };
    }

    // ── RAM (SMBIOS type 17) ──────────────────────────────────────────────

    private static JsonArray ReadRam()
    {
        var rows = new JsonArray();
        foreach (var (info, memoryType) in SmbiosParser.ParseModules(MemoryInfoProvider.ReadRawTable()))
        {
            rows.Add(new JsonObject
            {
                ["Capacity"] = info.SizeBytes ?? 0,
                ["Speed"] = info.MaxSpeedMts ?? 0,
                ["ConfiguredClockSpeed"] = info.ConfiguredSpeedMts ?? 0,
                ["Manufacturer"] = info.Manufacturer ?? "",
                ["PartNumber"] = info.PartNumber ?? "",
                ["SMBIOSMemoryType"] = memoryType ?? 0,
            });
        }
        return rows;
    }

    /// <summary>The first DIMM's manufacturer and part number, for catalog matching.</summary>
    public static (string Manufacturer, string PartNumber) ReadFirstDimm()
    {
        var first = SmbiosParser.ParseModules(MemoryInfoProvider.ReadRawTable()).FirstOrDefault();
        return first.Info is null ? ("", "") : (first.Info.Manufacturer ?? "", first.Info.PartNumber ?? "");
    }

    // ── Disks ─────────────────────────────────────────────────────────────

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint StorageDeviceProperty = 0;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const int MaxPhysicalDrives = 32;

    private sealed record DiskRow(string Vendor, string Product, string FriendlyName, string MediaType, uint BusType, long Size);

    private static JsonArray ReadDisks()
    {
        var rows = new JsonArray();
        foreach (var d in EnumeratePhysicalDisks()
                     .Where(d => d.BusType != 7)
                     .OrderByDescending(d => d.Size))
        {
            rows.Add(new JsonObject
            {
                ["FriendlyName"] = d.FriendlyName,
                ["MediaType"] = d.MediaType,
                ["BusType"] = WindowsSpecsParsers.BusTypeName(d.BusType),
                ["Size"] = d.Size,
            });
        }
        return rows;
    }

    private static List<DiskRow> EnumeratePhysicalDisks()
    {
        var disks = new List<DiskRow>();
        for (var i = 0; i < MaxPhysicalDrives; i++)
        {
            var disk = ReadPhysicalDisk(i);
            if (disk is not null) disks.Add(disk);
        }
        return disks;
    }

    private static DiskRow? ReadPhysicalDisk(int number)
    {
        var path = $@"\\.\PhysicalDrive{number}";
        // Read access is what IOCTL_DISK_GET_LENGTH_INFO demands; the property
        // queries work with no access at all, so fall back to that for the model.
        var handle = CreateFile(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        var readable = handle != InvalidHandle;
        if (!readable)
        {
            handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == InvalidHandle) return null;
        }

        try
        {
            var descriptor = WindowsSpecsParsers.ParseStorageDescriptor(StorageQuery(handle, StorageDeviceProperty, 1024));
            if (descriptor is null) return null;
            var d = descriptor.Value;
            if (d.Removable) return null;

            long size = 0;
            if (readable)
            {
                var lengthBuf = new byte[8];
                if (DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, lengthBuf, 8, out _, IntPtr.Zero))
                {
                    size = BitConverter.ToInt64(lengthBuf, 0);
                }
            }

            var mediaType = "Unspecified";
            var seek = StorageQuery(handle, StorageDeviceSeekPenaltyProperty, 16);
            if (seek is { Length: >= 9 })
            {
                mediaType = seek[8] != 0 ? "HDD" : "SSD";
            }

            return new DiskRow(d.Vendor, d.Product, WindowsSpecsParsers.DiskFriendlyName(d), mediaType, d.BusType, size);
        }
        finally { CloseHandle(handle); }
    }

    private static byte[]? StorageQuery(IntPtr handle, uint propertyId, int outputSize)
    {
        // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (0 = standard), 4 bytes of AdditionalParameters.
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0, 4), propertyId);
        var output = new byte[outputSize];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, (uint)query.Length, output, (uint)output.Length, out var returned, IntPtr.Zero))
        {
            return null;
        }
        return output.AsSpan(0, (int)Math.Min(returned, (uint)output.Length)).ToArray();
    }

    /// <summary>Vendor and product of the disk holding the system drive, for catalog matching.</summary>
    public static (string Vendor, string Product) ReadSystemDiskBrand()
    {
        var root = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrEmpty(root)) root = "C:";
        var volume = CreateFile($@"\\.\{root.TrimEnd('\\')}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (volume == InvalidHandle) return ("", "");

        int number;
        try
        {
            // STORAGE_DEVICE_NUMBER: DeviceType, DeviceNumber, PartitionNumber.
            var buf = new byte[12];
            if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, null, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
            {
                return ("", "");
            }
            number = BitConverter.ToInt32(buf, 4);
        }
        finally { CloseHandle(volume); }

        var disk = ReadPhysicalDisk(number);
        return disk is null ? ("", "") : (disk.Vendor, disk.Product);
    }

    // ── Monitors, resolution ──────────────────────────────────────────────

    private static readonly Guid MonitorClass = new("4d36e96e-e325-11ce-8c3d-00a0c9a2c1ed");
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-8c3d-00a0c9a2c1ed");

    private static JsonArray ReadMonitors()
    {
        var rows = new JsonArray();
        foreach (var device in WindowsDevnodes.EnumeratePresent(MonitorClass))
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{device.InstanceId}\Device Parameters");
            var edid = key?.GetValue("EDID") as byte[];
            rows.Add(new JsonObject
            {
                ["Name"] = WindowsSpecsParsers.EdidMonitorName(edid),
                ["Mfg"] = WindowsSpecsParsers.EdidManufacturer(edid),
            });
        }
        return rows;
    }

    private static JsonArray ReadVideo()
    {
        var rows = new JsonArray();
        var adapter = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref mode)) continue;
            rows.Add(new JsonObject
            {
                ["CurrentHorizontalResolution"] = (int)mode.PelsWidth,
                ["CurrentVerticalResolution"] = (int)mode.PelsHeight,
                ["CurrentRefreshRate"] = (int)mode.DisplayFrequency,
            });
        }
        return rows;
    }

    // ── Sound ─────────────────────────────────────────────────────────────

    private static JsonArray ReadSound()
    {
        var rows = new JsonArray();
        foreach (var device in WindowsDevnodes.EnumeratePresent(MediaClass))
        {
            if (device.ProblemCode != 0) continue;
            rows.Add(new JsonObject { ["Name"] = device.Name });
        }
        return rows;
    }

    // ── Network ───────────────────────────────────────────────────────────

    // MIB_IF_ROW2 offsets (x64, size 1352). HardwareInterface is bit 0 of the
    // flags byte and is what Get-NetAdapter -Physical filters on.
    private const int IfRowSize = 1352;
    private const int IfRowIndexOffset = 8;
    private const int IfRowDescriptionOffset = 542;
    private const int IfRowDescriptionChars = 257;
    private const int IfRowFlagsOffset = 1152;
    private const int IfRowOperStatusOffset = 1156;
    private const int IfRowTransmitSpeedOffset = 1192;
    private const int IfTableRowsOffset = 8;
    private const uint IfOperStatusUp = 1;

    private static unsafe JsonArray ReadNet()
    {
        var rows = new JsonArray();
        if (GetIfTable2(out var table) != 0 || table == IntPtr.Zero) return rows;
        try
        {
            var count = Marshal.ReadInt32(table);
            var adapters = new List<(uint Index, JsonObject Row)>();
            for (var i = 0; i < count; i++)
            {
                var row = new ReadOnlySpan<byte>((byte*)table + IfTableRowsOffset + ((long)i * IfRowSize), IfRowSize);
                if ((row[IfRowFlagsOffset] & 0x01) == 0) continue;

                var description = System.Text.Encoding.Unicode.GetString(row.Slice(IfRowDescriptionOffset, IfRowDescriptionChars * 2));
                var end = description.IndexOf('\0');
                if (end >= 0) description = description[..end];

                var up = BitConverter.ToUInt32(row.Slice(IfRowOperStatusOffset, 4)) == IfOperStatusUp;
                var speed = BitConverter.ToUInt64(row.Slice(IfRowTransmitSpeedOffset, 8));
                adapters.Add((
                    BitConverter.ToUInt32(row.Slice(IfRowIndexOffset, 4)),
                    new JsonObject
                    {
                        ["InterfaceDescription"] = description,
                        ["LinkSpeed"] = WindowsSpecsParsers.FormatLinkSpeed(speed),
                        ["Status"] = up ? "Up" : "Disconnected",
                    }));
            }
            foreach (var adapter in adapters.OrderBy(a => a.Index)) rows.Add(adapter.Row);
        }
        finally { FreeMibTable(table); }
        return rows;
    }

    // ── OA3 key (ACPI MSDM) ───────────────────────────────────────────────

    private const uint AcpiProvider = 0x41435049;
    private const uint MsdmTableId = 0x4D44534D;

    private static string ReadOa3Key()
        => WindowsSpecsParsers.ParseMsdmKey(ReadFirmwareTable(AcpiProvider, MsdmTableId));

    private static byte[]? ReadFirmwareTable(uint provider, uint tableId)
    {
        var size = GetSystemFirmwareTable(provider, tableId, null, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        var written = GetSystemFirmwareTable(provider, tableId, buffer, size);
        return written == 0 || written > buffer.Length ? null : buffer.AsSpan(0, (int)written).ToArray();
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint DisplayDeviceAttachedToDesktop = 0x1;
    private const uint EnumCurrentSettings = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RtlOsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint PlatformId;
        public fixed char CsdVersion[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int RtlGetVersion(ref RtlOsVersionInfo info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint providerSignature, uint tableId, [Out] byte[]? buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFile(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(IntPtr handle, uint code, byte[]? input, uint inputSize, [Out] byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIfTable2(out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, uint modeNum, ref DevMode mode);
}

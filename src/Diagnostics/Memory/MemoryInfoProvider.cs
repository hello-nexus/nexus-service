using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Service.Diagnostics.Memory;

public sealed record MemoryInfoSnapshot(bool Supported, IReadOnlyList<MemoryModuleInfo> Modules, bool? XmpLikelyActive)
{
    public static readonly MemoryInfoSnapshot Unsupported = new(false, Array.Empty<MemoryModuleInfo>(), null);
}

/// <summary>
/// Reads installed-memory info (per-slot size/speed/manufacturer, XMP-likely
/// heuristic) from raw SMBIOS via GetSystemFirmwareTable('RSMB'). Windows-only;
/// self-gates on <see cref="OperatingSystem.IsWindows"/> so callers do not need
/// to guard the call site. Parsing itself lives in <see cref="SmbiosParser"/>,
/// a pure function kept separate for cross-platform unit testing.
/// </summary>
public static partial class MemoryInfoProvider
{
    private const uint ProviderSignatureRsmb = 0x52534D42u; // 'RSMB' as a big-endian-packed DWORD

    public static MemoryInfoSnapshot GetSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return MemoryInfoSnapshot.Unsupported;
        }

        var raw = ReadRawTable();
        var (modules, xmp) = SmbiosParser.Parse(raw);
        return new MemoryInfoSnapshot(true, modules, xmp);
    }

    internal static byte[]? ReadRawTable()
    {
        try
        {
            uint size = GetSystemFirmwareTable(ProviderSignatureRsmb, 0, IntPtr.Zero, 0);
            if (size == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint written = GetSystemFirmwareTable(ProviderSignatureRsmb, 0, buffer, size);
                if (written == 0 || written > size) return null;
                var bytes = new byte[written];
                Marshal.Copy(buffer, bytes, 0, (int)written);
                return bytes;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId, IntPtr buffer, uint bufferSize);
}

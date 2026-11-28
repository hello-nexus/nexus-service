#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Maps each active GDI adapter name (\\.\DISPLAYn) to the monitor number the
/// Windows Settings display page shows. Settings ranks the DISPLAYCONFIG
/// sources globally, not by the \\.\DISPLAYn ordinal - the GDI namespace keeps
/// stale slots for detached outputs, so the two diverge.
/// </summary>
internal static class WindowsDisplayConfig
{
    /// <summary>
    /// gdi device name (\\.\DISPLAYn) -> Settings monitor number (1-based global
    /// source rank). Empty when QueryDisplayConfig is unavailable / fails, so
    /// callers fall back to the GDI ordinal.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> SourceNumbersByGdiName()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return map;
            if (pathCount == 0) return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            // Settings numbers displays globally across adapters; sourceInfo.id
            // is per-adapter (a second adapter - e.g. a USB virtual display -
            // restarts at 0), so id + 1 collides. Rank the distinct sources by
            // adapter first-seen order then source id, 1-based. Clones share a
            // source id and so share a number, matching Settings.
            var adapterRank = new Dictionary<long, int>();
            var sources = new List<(long Adapter, uint SourceId, string Name)>();
            var seen = new HashSet<(long, uint)>();
            for (var i = 0; i < pathCount; i++)
            {
                var src = paths[i].sourceInfo;
                var name = GdiNameForSource(src.adapterId, src.id);
                if (string.IsNullOrEmpty(name)) continue;
                var adapter = ((long)src.adapterId.HighPart << 32) | src.adapterId.LowPart;
                if (!seen.Add((adapter, src.id))) continue;
                if (!adapterRank.ContainsKey(adapter)) adapterRank[adapter] = adapterRank.Count;
                sources.Add((adapter, src.id, name));
            }
            sources.Sort((a, b) =>
            {
                var byAdapter = adapterRank[a.Adapter].CompareTo(adapterRank[b.Adapter]);
                return byAdapter != 0 ? byAdapter : a.SourceId.CompareTo(b.SourceId);
            });
            for (var i = 0; i < sources.Count; i++)
                map[sources[i].Name] = i + 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] display-config numbering failed: {ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// gdi device name (\\.\DISPLAYn) -> target output technology of its active path. Empty when
    /// QueryDisplayConfig fails, so callers treat the technology as unknown.
    /// </summary>
    internal static IReadOnlyDictionary<string, uint> OutputTechnologyByGdiName()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0 || pathCount == 0)
            {
                return map;
            }
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return map;
            }
            for (var i = 0; i < pathCount; i++)
            {
                var name = GdiNameForSource(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
                if (!string.IsNullOrEmpty(name))
                {
                    map[name] = paths[i].targetInfo.outputTechnology;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] output technology lookup failed: {ex.Message}");
        }
        return map;
    }

    private static string GdiNameForSource(LUID adapterId, uint sourceId)
    {
        var query = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId,
            },
        };
        return DisplayConfigGetDeviceInfo(ref query) == 0 ? query.viewGdiDeviceName ?? "" : "";
    }

    // -- P/Invoke -----------------------------------------------------------

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int CCHDEVICENAME = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // 64-byte ABI; the trailing union is opaque here - we never read modes,
    // QueryDisplayConfig only needs a correctly sized buffer.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
}
#endif

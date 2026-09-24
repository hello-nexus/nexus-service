using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Models.Sensors;
#if WINDOWS
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
#endif

namespace Nexus.Service.Sensors;

/// <summary>
/// Enumerates physical GPU adapters via DXGI to recover each adapter's LUID and
/// dedicated VRAM, so a GPU model (from LibreHardwareMonitor) can be matched to
/// the LUID that the PDH "GPU Engine" / "GPU Process Memory" per-process
/// counters tag their instances with. Windows-only; empty elsewhere, in which
/// case per-process GPU data stays attributed to no adapter (combined view).
/// </summary>
internal static class GpuAdapterLuids
{
    /// <param name="UmdVersion">The user-mode driver version DXGI reports, 0
    /// when the adapter has none; a driver update changes it.</param>
    public readonly record struct Adapter(
        string Luid, string Description, uint VendorId, long DedicatedVramMb,
        uint DeviceId = 0, uint SubSysId = 0, uint Revision = 0, long UmdVersion = 0);

#if WINDOWS
    // Microsoft's PCI vendor id, used by the Basic Render Driver (WARP) and
    // indirect/virtual display adapters. Windows sometimes enumerates these
    // WITHOUT the AdapterFlags.Software flag, yet none provide an OpenGL ICD -
    // creating a GL context against one fail-fasts. No physical GPU vendor uses
    // it (NVIDIA 0x10DE, AMD 0x1002, Intel 0x8086).
    private const uint MicrosoftVendorId = 0x1414;
#endif

#if WINDOWS
    // Adapter LUIDs only change on driver reset / hotplug, but Enumerate() is
    // on the 1 Hz metrics-sampler path via Attach(); without a cache that
    // creates and tears down a DXGI factory every second for the process
    // lifetime - churn in the graphics stack a TDR investigation (AMD + HAGS)
    // flagged as an always-on dGPU touch. A failed/empty enumeration is not
    // cached so a transient DXGI failure retries on the next call.
    private const long CacheTtlMs = 60_000;
    private static readonly object CacheLock = new();
    private static IReadOnlyList<Adapter>? _cache;
    private static long _cacheAtMs;
#endif

    public static IReadOnlyList<Adapter> Enumerate()
    {
#if WINDOWS
        lock (CacheLock)
        {
            var now = Environment.TickCount64;
            if (_cache is { Count: > 0 } cached && now - _cacheAtMs < CacheTtlMs)
            {
                return cached;
            }
            var fresh = EnumerateUncached();
            if (fresh.Count > 0)
            {
                _cache = fresh;
                _cacheAtMs = now;
            }
            return fresh;
        }
#else
        return Array.Empty<Adapter>();
#endif
    }

#if WINDOWS
    // IID_IDXGIDevice: the interface CheckInterfaceSupport reports the UMD version for.
    private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static IReadOnlyList<Adapter> EnumerateUncached()
    {
        var list = new List<Adapter>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            uint i = 0;
            while (factory.EnumAdapters1(i, out var adapter).Success)
            {
                try
                {
                    var d = adapter.Description1;
                    // Skip the Microsoft Basic Render Driver (software adapter).
                    if ((d.Flags & AdapterFlags.Software) == 0)
                    {
                        long vramMb = (long)((ulong)d.DedicatedVideoMemory / (1024UL * 1024UL));
                        // Fails on adapters with no D3D user-mode driver; 0 then.
                        adapter.CheckInterfaceSupport(DxgiDeviceIid, out long umd);
                        list.Add(new Adapter(
                            $"{d.Luid.HighPart}:{d.Luid.LowPart}",
                            d.Description ?? "",
                            d.VendorId,
                            vramMb,
                            d.DeviceId,
                            d.SubsystemId,
                            d.Revision,
                            umd));
                    }
                }
                finally { adapter.Dispose(); }
                i++;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gpu-luid] DXGI enumeration failed: {ex.Message}");
        }
        return list;
    }
#endif

    private static readonly IReadOnlySet<string> NoKernelLuids = new HashSet<string>();

    /// <summary>
    /// Attribute each GPU to its DXGI adapter LUID so the client can scope
    /// per-process GPU counters (whose PDH instances carry a luid tag) to the
    /// picked GPU. Exact model-name match first, then vendor + discrete/integrated
    /// class for any leftover; unmatched GPUs keep AdapterLuid="" (combined view).
    /// </summary>
    public static void Attach(List<GpuReadout> gpus, IReadOnlyDictionary<string, string> rawNames)
    {
        if (gpus.Count == 0) return;
        var adapters = Enumerate();
        if (adapters.Count == 0) return;
        // Any indirect-display driver (a remote-desktop or USB/virtual "display",
        // e.g. the "USB Mobile Monitor Virtual Display" seen on this box) renders
        // through the physical GPU and so enumerates through DXGI as a second
        // adapter with the GPU's exact name/vendor/VRAM. A name match alone can
        // bind the GPU to that clone's LUID - which the per-process GPU counters
        // never tag (the clone owns no VRAM), leaving the client's per-GPU process
        // view empty. Only when two adapters share a description do we consult the
        // set of LUIDs the graphics kernel backs with VRAM and prefer one of those;
        // a single-adapter box skips the PDH query and pays nothing.
        var kernelLuids = HasDuplicateDescriptions(adapters) ? KernelAdapterLuids() : NoKernelLuids;
        MatchAdapterLuids(gpus, adapters, rawNames, kernelLuids);
    }

    private static bool HasDuplicateDescriptions(IReadOnlyList<Adapter> adapters)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in adapters)
            if (!seen.Add(a.Description.Trim())) return true;
        return false;
    }

    // The exact match compares each GPU's raw LHM name (rawNames), not its
    // possibly Astral/AIB-enriched display name, against DXGI's description.
    internal static void MatchAdapterLuids(
        List<GpuReadout> gpus,
        IReadOnlyList<Adapter> adapters,
        IReadOnlyDictionary<string, string> rawNames,
        IReadOnlySet<string> kernelLuids)
    {
        var used = new HashSet<string>();
        foreach (var g in gpus)
        {
            var rawName = rawNames.TryGetValue(g.Id, out var n) ? n : g.Name;
            if (PickAdapter(adapters, used, kernelLuids,
                    x => string.Equals(x.Description.Trim(), rawName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } a)
            { g.AdapterLuid = a.Luid; used.Add(a.Luid); }
        }
        foreach (var g in gpus)
        {
            if (g.AdapterLuid.Length > 0) continue;
            if (PickAdapter(adapters, used, kernelLuids,
                    x => VendorMatches(g.Vendor, x.VendorId) && (x.DedicatedVramMb >= 1024) == !g.Integrated) is { } a)
            { g.AdapterLuid = a.Luid; used.Add(a.Luid); }
        }
    }

    // Among unused adapters (non-empty LUID) passing the predicate, the first one
    // the graphics kernel backs with VRAM wins over one it doesn't (the render
    // clone owns no VRAM). Preferring the FIRST backed match keeps the choice
    // positionally stable across polls - two same-model discrete GPUs, both backed,
    // bind to distinct LUIDs in enumeration order rather than swapping by live
    // usage. With no kernel info, the first match wins, preserving prior behavior.
    private static Adapter? PickAdapter(
        IReadOnlyList<Adapter> adapters,
        HashSet<string> used,
        IReadOnlySet<string> kernelLuids,
        Func<Adapter, bool> predicate)
    {
        Adapter? first = null;
        foreach (var x in adapters)
        {
            if (x.Luid.Length == 0 || used.Contains(x.Luid) || !predicate(x)) continue;
            if (kernelLuids.Contains(x.Luid)) return x;
            first ??= x;
        }
        return first;
    }

    private static bool VendorMatches(string vendor, uint vendorId) => vendor switch
    {
        "nvidia" => vendorId == 0x10DE,
        "amd" => vendorId == 0x1002,
        "intel" => vendorId == 0x8086,
        _ => false,
    };

    /// <summary>
    /// True if at least one DXGI adapter can actually instantiate a Direct3D
    /// device. A removed GPU leaves its driver registered, so DXGI keeps
    /// enumerating it as a ghost adapter (no Software flag, real vendor id) -
    /// counting adapters can't tell a ghost from a present card. D3D11CreateDevice
    /// fails on the ghost (hardware absent) and succeeds on a real GPU; without a
    /// real adapter, GLFW context creation fail-fasts inside the orphaned ICD
    /// (0xc0000409), which a managed catch can't intercept.
    ///
    /// Necessary for the GL path, NOT sufficient: a single-GPU box has been seen
    /// passing this and still getting ApiUnavailable from WGL. The GL verdict is
    /// glfwCreateWindow returning null under GlfwErrorGuard. Windows-only.
    /// </summary>
    public static bool HasUsableHardwareGpu()
    {
#if WINDOWS
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            uint i = 0;
            while (factory.EnumAdapters1(i, out var adapter).Success)
            {
                try
                {
                    var d = adapter.Description1;
                    if ((d.Flags & AdapterFlags.Software) == 0 && d.VendorId != MicrosoftVendorId)
                    {
                        try
                        {
                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                                DeviceCreationFlags.None, null!, out var device, out _, out var ctx);
                            ctx?.Dispose();
                            device?.Dispose();
                            if (device is not null)
                                return true;
                        }
                        catch { } // adapter unusable (e.g. ghost of a removed card); try the next
                    }
                }
                finally { adapter.Dispose(); }
                i++;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gpu-probe] D3D11 usability probe failed: {ex.Message}");
        }
        return false;
#else
        return false;
#endif
    }

    /// <summary>
    /// Canonical LUID string ("HighPart:LowPart", decimal) from a PDH GPU-Engine
    /// instance's "luid_0xHIGH_0xLOW" hex pair. "" if not parseable. Matches the
    /// format produced from <see cref="Adapter.Luid"/> so the two compare equal.
    /// </summary>
    public static string LuidFromHex(string highHex, string lowHex)
    {
        if (TryHex(highHex, out var hi) && TryHex(lowHex, out var lo))
            return $"{(int)hi}:{lo}";
        return "";
    }

    private static bool TryHex(string s, out uint v)
    {
        v = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var t = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.AsSpan(2) : s.AsSpan();
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>
    /// Canonical LUID string from a PDH GPU counter instance's
    /// "luid_0xHIGH_0xLOW_..." segment (e.g. "pid_5040_luid_0x00000000_0x000105fc_...").
    /// "" when absent/unparseable. Same "HighPart:LowPart" form <see cref="Enumerate"/>
    /// produces from DXGI, so a PDH-derived LUID compares equal to a DXGI one.
    /// </summary>
    public static string ParseInstanceLuid(string instance)
    {
        const string tag = "luid_";
        var i = instance.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = instance.AsSpan(i + tag.Length);
        var u1 = rest.IndexOf('_');
        if (u1 <= 0) return "";
        var high = rest[..u1].ToString();
        var after = rest[(u1 + 1)..];
        var u2 = after.IndexOf('_');
        var low = (u2 < 0 ? after : after[..u2]).ToString();
        return LuidFromHex(high, low);
    }

    /// <summary>
    /// LUIDs the Windows graphics kernel backs with VRAM, from the instance names
    /// of the PDH "GPU Adapter Memory" counter. This is the LUID space the
    /// per-process "GPU Engine"/"GPU Process Memory" counters tag their instances
    /// with, so a GPU whose DXGI description is duplicated by an indirect-display
    /// render clone (which owns no VRAM and so never appears here) can be bound to
    /// the LUID that actually carries GPU work. Empty on non-Windows or if the
    /// counter is unavailable.
    /// </summary>
    public static IReadOnlySet<string> KernelAdapterLuids()
    {
#if WINDOWS
        var set = new HashSet<string>();
        IntPtr query = IntPtr.Zero;
        try
        {
            if (Pdh.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return set; }
            if (Pdh.PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out var counter) != 0)
                return set;
            if (Pdh.PdhCollectQueryData(query) != 0) return set;
            foreach (var inst in Pdh.EnumInstanceNames(counter))
            {
                var luid = ParseInstanceLuid(inst);
                if (luid.Length > 0) set.Add(luid);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gpu-luid] GPU Adapter Memory enumeration failed: {ex.Message}");
        }
        finally
        {
            if (query != IntPtr.Zero) { try { Pdh.PdhCloseQuery(query); } catch { } }
        }
        return set;
#else
        return new HashSet<string>();
#endif
    }

#if WINDOWS
    private static class Pdh
    {
        private const string Lib = "pdh.dll";
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;
        // PDH_FMT_COUNTERVALUE_ITEM_W (x64): LPWSTR szName(8) + DWORD CStatus(4)
        // + 4 pad + double(8) = 24 bytes.
        private const int ItemSize = 24;

        [DllImport(Lib, CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
        [DllImport(Lib, CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport(Lib)]
        public static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport(Lib)]
        public static extern uint PdhCloseQuery(IntPtr query);
        [DllImport(Lib)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

        // Only instance names are needed (each carries the adapter LUID); the
        // counter value is not read, so an errored instance's undefined value
        // union is never consumed. A per-instance name is populated regardless
        // of CStatus.
        public static IEnumerable<string> EnumInstanceNames(IntPtr counter)
        {
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0)
                yield break;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out var count, buf) != 0)
                    yield break;
                for (var i = 0; i < count; i++)
                {
                    var namePtr = Marshal.ReadIntPtr(buf + i * ItemSize);
                    if (namePtr == IntPtr.Zero) continue;
                    var name = Marshal.PtrToStringUni(namePtr);
                    if (!string.IsNullOrEmpty(name)) yield return name;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
#endif
}

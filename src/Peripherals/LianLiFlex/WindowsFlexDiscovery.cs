#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// SetupAPI discovery for the Flex fans. Enumerates the shared interface GUID and keeps only the
/// Flex receivers and screens (the RF dongle registers the same GUID and is ignored here).
/// </summary>
public sealed partial class WindowsFlexDiscovery : IFlexDiscovery
{
    private static readonly Guid FlexGuid = new(FlexProtocol.InterfaceGuid);

    public IReadOnlyList<FlexUnitPort> Discover() => Snapshot().Units;

    private readonly object _topologyLock = new();
    private string _lastTopology = "";
    private string _lastMisses = "";

    // The fallback pass is cached per instance id and re-run when the shared-GUID path set changes or after this interval.
    private const long FallbackRecheckMs = 10_000;
    private string _fallbackSignature = "";
    private long _fallbackAtMs = long.MinValue;
    private readonly Dictionary<string, (List<string> Paths, string? Miss)> _fallback = new(StringComparer.OrdinalIgnoreCase);

    public FlexDiscoverySnapshot Snapshot()
    {
        var paths = EnumeratePaths();
        var misses = AddPathsOfUnlistedInstances(paths);
        var hubs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string HubOf(string instanceId)
        {
            if (!hubs.TryGetValue(instanceId, out var hub))
            {
                hub = ParentHub(instanceId);
                hubs[instanceId] = hub;
            }
            return hub;
        }
        var snapshot = FlexDiscoveryParser.Snapshot(paths, HubOf);
        var topology = FlexDiscoveryParser.Describe(paths, HubOf, snapshot);
        bool changed;
        lock (_topologyLock)
        {
            changed = !string.Equals(topology, _lastTopology, StringComparison.Ordinal);
            _lastTopology = topology;
        }
        if (changed)
        {
            ServiceLog.Info($"[lianli-flex] discovery {topology}");
        }
        bool missesChanged;
        lock (_topologyLock)
        {
            missesChanged = !string.Equals(misses, _lastMisses, StringComparison.Ordinal);
            _lastMisses = misses;
        }
        if (missesChanged && misses.Length > 0)
        {
            ServiceLog.Warn($"[lianli-flex] present without an openable path: {misses}");
        }
        return snapshot;
    }

    /// <summary>
    /// Present Flex devices whose interface is not under the shared GUID (a device's own descriptor can name another):
    /// their registered GUIDs are read from the instance and the paths added. Returns a description of the instances still without a path.
    /// </summary>
    private string AddPathsOfUnlistedInstances(List<string> paths)
    {
        var signature = string.Join("|", paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var now = Environment.TickCount64;
        lock (_topologyLock)
        {
            if (!string.Equals(signature, _fallbackSignature, StringComparison.Ordinal) || now - _fallbackAtMs >= FallbackRecheckMs)
            {
                RefreshFallbackLocked(paths);
                _fallbackSignature = signature;
                _fallbackAtMs = now;
            }
            var misses = new List<string>();
            foreach (var entry in _fallback.Values)
            {
                foreach (var path in entry.Paths)
                {
                    if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(path);
                    }
                }
                if (entry.Miss is not null)
                {
                    misses.Add(entry.Miss);
                }
            }
            return string.Join("; ", misses);
        }
    }

    private void RefreshFallbackLocked(List<string> paths)
    {
        var present = PresentUsbInstances();
        if (present is null)
        {
            return;
        }
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var parts = raw.Split('#');
            if (parts.Length > 2)
            {
                listed.Add($@"USB\{parts[1]}\{parts[2]}");
            }
        }
        var flex = new List<(string Id, FlexPath Parsed)>();
        foreach (var instanceId in present)
        {
            if (FlexDiscoveryParser.TryParse(instanceId.Replace('\\', '#'), out var parsed)
                && (FlexProtocol.IsReceiverProduct(parsed.Vid, parsed.Pid) || FlexProtocol.IsScreenProduct(parsed.Vid, parsed.Pid)))
            {
                flex.Add((instanceId, parsed));
            }
        }
        foreach (var gone in _fallback.Keys.Where(k => !flex.Exists(f => string.Equals(f.Id, k, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _fallback.Remove(gone);
        }
        foreach (var (instanceId, parsed) in flex)
        {
            // A composite parent has no interface of its own; its MI children are checked on their own.
            var isCompositeParent = !instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                && flex.Exists(f => f.Parsed.Vid == parsed.Vid && f.Parsed.Pid == parsed.Pid && f.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
            if (listed.Contains(instanceId) || isCompositeParent)
            {
                _fallback.Remove(instanceId);
                continue;
            }
            var guids = RegisteredGuids(instanceId, out var inf);
            var found = new List<string>();
            var failed = false;
            foreach (var guid in guids)
            {
                var interfacePaths = InterfacePathsOf(instanceId, guid);
                if (interfacePaths is null)
                {
                    failed = true;
                }
                else
                {
                    found.AddRange(interfacePaths);
                }
            }
            if (failed && _fallback.ContainsKey(instanceId))
            {
                continue;
            }
            _fallback[instanceId] = (found, found.Count == 0 ? $"{instanceId} guids=[{string.Join(",", guids)}] inf={inf}" : null);
        }
    }

    // A failed enumeration returns null so the caller keeps what it knew.
    private static unsafe List<string>? PresentUsbInstances()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (CM_Get_Device_ID_List_Size(out var chars, "USB", CM_GETIDLIST_FILTER_ENUMERATOR) != 0)
            {
                return null;
            }
            if (chars <= 1)
            {
                return new List<string>();
            }
            var buf = new char[chars];
            uint rc;
            fixed (char* b = buf)
            {
                rc = CM_Get_Device_ID_List("USB", b, chars, CM_GETIDLIST_FILTER_ENUMERATOR);
            }
            if (rc == 0)
            {
                return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            if (rc != CR_BUFFER_SMALL)
            {
                return null;
            }
        }
        return null;
    }

    private static List<Guid> RegisteredGuids(string instanceId, out string inf)
    {
        var guids = new List<Guid>();
        inf = "?";
        try
        {
            using var instance = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            using var parameters = instance?.OpenSubKey("Device Parameters");
            foreach (var name in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
            {
                var value = parameters?.GetValue(name);
                var texts = value is string[] many ? many : value is string one ? new[] { one } : Array.Empty<string>();
                foreach (var text in texts)
                {
                    if (Guid.TryParse(text, out var g) && !guids.Contains(g))
                    {
                        guids.Add(g);
                    }
                }
            }
            if (instance?.GetValue("Driver") is string driver)
            {
                using var cls = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}");
                inf = $"{cls?.GetValue("InfPath")}/{cls?.GetValue("ProviderName")}";
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            inf = ex.GetType().Name;
        }
        return guids;
    }

    private static unsafe List<string>? InterfacePathsOf(string instanceId, Guid guid)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var chars, ref guid, instanceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0)
            {
                return null;
            }
            if (chars <= 1)
            {
                return new List<string>();
            }
            var buf = new char[chars];
            uint rc;
            fixed (char* b = buf)
            {
                rc = CM_Get_Device_Interface_List(ref guid, instanceId, b, chars, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            }
            if (rc == 0)
            {
                return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            if (rc != CR_BUFFER_SMALL)
            {
                return null;
            }
        }
        return null;
    }

    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1 | 0x100;
    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static partial uint CM_Get_Device_ID_List_Size(out uint chars, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static unsafe partial uint CM_Get_Device_ID_List(string filter, char* buffer, uint bufferLen, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint chars, ref Guid guid, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static unsafe partial uint CM_Get_Device_Interface_List(ref Guid guid, string deviceId, char* buffer, uint bufferLen, uint flags);

    private static List<string> EnumeratePaths()
    {
        var paths = new List<string>();
        var guid = FlexGuid;
        var devInfo = Slv3WinUsbInterop.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            Slv3WinUsbInterop.DIGCF_PRESENT | Slv3WinUsbInterop.DIGCF_DEVICEINTERFACE);
        if (devInfo == (IntPtr)(-1))
        {
            return paths;
        }
        try
        {
            var idx = 0u;
            var ifaceData = new Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA();
            ifaceData.cbSize = Marshal.SizeOf(ifaceData);
            while (Slv3WinUsbInterop.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, idx, ref ifaceData))
            {
                idx++;
                var path = ReadDevicePath(devInfo, ref ifaceData);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }
        }
        finally
        {
            Slv3WinUsbInterop.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return paths;
    }

    private static string ReadDevicePath(IntPtr devInfo, ref Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0)
        {
            return "";
        }
        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detail, required, out _, IntPtr.Zero))
            {
                return "";
            }
            return Marshal.PtrToStringUni(detail + 4) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Locate_DevNodeW")]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static unsafe partial uint CM_Get_Device_ID(uint devInst, char* buffer, uint bufferLen, uint flags);

    /// <summary>Instance id of the hub a device sits behind; empty when the lookup fails.</summary>
    private static unsafe string ParentHub(string instanceId)
    {
        if (CM_Locate_DevNode(out var node, instanceId, 0) != 0 || CM_Get_Parent(out var parent, node, 0) != 0)
        {
            return "";
        }
        var buf = new char[400];
        fixed (char* p = buf)
        {
            if (CM_Get_Device_ID(parent, p, (uint)buf.Length, 0) != 0)
            {
                return "";
            }
        }
        return new string(buf).TrimEnd('\0');
    }
}
#endif

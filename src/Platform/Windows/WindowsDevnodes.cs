using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Nexus.Service.Sensors;

namespace Nexus.Service.Platform.Windows;

/// <summary>Present devnodes and their Device Manager problem codes through SetupAPI and cfgmgr32, in-process.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDevnodes
{
    internal readonly record struct PresentDevice(string InstanceId, string Name, uint ProblemCode, string DriverKey);

    private const uint DigcfPresent = 0x2;
    private const uint DigcfAllClasses = 0x4;
    private const uint SpdrpDeviceDesc = 0x0;
    private const uint SpdrpFriendlyName = 0xC;
    private const uint SpdrpDriver = 0x9;
    private const uint CrBufferSmall = 0x1A;
    private const uint DevPropTypeString = 0x12;
    private const uint MaxPropertyBytes = 65536;
    private const uint CmLocateDevnodePhantom = 0x1;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Every present devnode of a setup class, or of all classes (optionally under one enumerator such as USB) when none is given.</summary>
    public static List<PresentDevice> EnumeratePresent(Guid? classGuid = null, string? enumerator = null, bool pnpName = false, bool idsOnly = false)
    {
        var devices = new List<PresentDevice>();
        IntPtr set;
        if (classGuid is { } guid)
        {
            set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        }
        else
        {
            var enumeratorPtr = enumerator is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(enumerator);
            try
            {
                set = SetupDiGetClassDevsAll(IntPtr.Zero, enumeratorPtr, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
            }
            finally { if (enumeratorPtr != IntPtr.Zero) Marshal.FreeHGlobal(enumeratorPtr); }
        }
        if (set == InvalidHandle) return devices;

        try
        {
            var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var name = idsOnly ? "" : ReadName(set, ref data, pnpName);

                var idBuffer = new char[512];
                var instanceId = "";
                unsafe
                {
                    fixed (char* idPtr = idBuffer)
                    {
                        if (SetupDiGetDeviceInstanceId(set, ref data, idPtr, (uint)idBuffer.Length, out _))
                        {
                            instanceId = new string(idPtr);
                        }
                    }
                }

                if (idsOnly)
                {
                    devices.Add(new PresentDevice(instanceId, "", 0, ""));
                    continue;
                }

                uint problem = 0;
                if (CM_Get_DevNode_Status(out _, out var p, data.DevInst, 0) == 0) problem = p;
                devices.Add(new PresentDevice(instanceId, name, problem, ReadStringProperty(set, ref data, SpdrpDriver)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    /// <summary>The present USB devnode whose instance id ends in the adb serial, or null.</summary>
    /// <summary>Present USB devices <paramref name="isKnown"/> accepts, each with the USB hubs above it from a cfgmgr32 parent walk; instance ids are hashed.</summary>
    public static List<Nexus.Service.Diagnostics.UsbTopologyDevice> ReadUsbTopology(Func<int, int, bool> isKnown)
    {
        var result = new List<Nexus.Service.Diagnostics.UsbTopologyDevice>();
        var all = EnumeratePresent(enumerator: "USB");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in all)
        {
            names[d.InstanceId] = d.Name;
        }
        foreach (var d in all)
        {
            if (d.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) || !TryParseVidPid(d.InstanceId, out var vid, out var pid) || !isKnown(vid, pid))
            {
                continue;
            }
            var device = new Nexus.Service.Diagnostics.UsbTopologyDevice { Device = TopologyNode(d.InstanceId, d.Name, 0) };
            var depth = 0;
            foreach (var parentId in ParentInstanceIds(d.InstanceId))
            {
                depth++;
                if (!parentId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                device.Parents.Add(TopologyNode(parentId, names.GetValueOrDefault(parentId, ""), depth));
            }
            result.Add(device);
        }
        return result;
    }

    private static Nexus.Service.Diagnostics.UsbTopologyNode TopologyNode(string instanceId, string name, int depth) => new()
    {
        Id = Nexus.Service.Diagnostics.UsbTopology.ShortHash(instanceId),
        VidPid = TryParseVidPid(instanceId, out var vid, out var pid) ? $"{vid:X4}:{pid:X4}" : (instanceId.Split('\\') is { Length: > 1 } parts ? parts[1] : ""),
        Name = name,
        Port = $"depth {depth}",
    };

    private static bool TryParseVidPid(string instanceId, out int vid, out int pid)
    {
        vid = pid = 0;
        var v = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var p = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        return v >= 0 && p >= 0 && instanceId.Length >= p + 8
            && int.TryParse(instanceId.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid)
            && int.TryParse(instanceId.AsSpan(p + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static List<string> ParentInstanceIds(string instanceId)
    {
        var chain = new List<string>();
        if (CM_Locate_DevNode(out var current, instanceId, 0) != 0)
        {
            return chain;
        }
        unsafe
        {
            char* buffer = stackalloc char[260];
            for (var i = 0; i < 16; i++)
            {
                if (CM_Get_Parent(out var parent, current, 0) != 0 || CM_Get_Device_ID(parent, buffer, 260, 0) != 0)
                {
                    break;
                }
                chain.Add(new string(buffer));
                current = parent;
            }
        }
        return chain;
    }

    public static string? FindPresentUsbInstanceId(string serial)
        => WindowsSpecsParsers.PickUsbInstanceId(EnumeratePresent(enumerator: "USB", idsOnly: true).Select(d => d.InstanceId), serial);

    /// <summary>DEVPKEY_Device_ProblemCode equivalent, or null when the devnode cannot be located.</summary>
    public static int? ReadProblemCode(string instanceId)
    {
        if (CM_Locate_DevNode(out var devInst, instanceId, CmLocateDevnodePhantom) != 0) return null;
        return CM_Get_DevNode_Status(out _, out var problem, devInst, 0) == 0 ? (int)problem : null;
    }

    private static string ReadName(IntPtr set, ref SpDevInfoData data, bool pnpName)
    {
        if (pnpName)
        {
            var best = ReadPnpNameProperty(data.DevInst);
            if (best.Length > 0) return best;
        }
        var first = pnpName ? SpdrpFriendlyName : SpdrpDeviceDesc;
        var second = pnpName ? SpdrpDeviceDesc : SpdrpFriendlyName;
        var name = ReadStringProperty(set, ref data, first);
        return name.Length > 0 ? name : ReadStringProperty(set, ref data, second);
    }

    // DEVPKEY_NAME: the name Device Manager and Win32_PnPEntity show.
    private static string ReadPnpNameProperty(uint devInst)
    {
        var key = new DevPropKey { Fmtid = new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), Pid = 10 };
        var buffer = new byte[1024];
        var size = (uint)buffer.Length;
        var result = CM_Get_DevNode_Property(devInst, ref key, out var type, buffer, ref size, 0);
        if (result == CrBufferSmall && size > buffer.Length && size <= MaxPropertyBytes)
        {
            buffer = new byte[size];
            result = CM_Get_DevNode_Property(devInst, ref key, out type, buffer, ref size, 0);
        }
        if (result != 0 || type != DevPropTypeString) return "";
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    private static string ReadStringProperty(IntPtr set, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out var required))
        {
            return "";
        }
        var length = (int)Math.Min(required, (uint)buffer.Length);
        var text = Encoding.Unicode.GetString(buffer, 0, length);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SpDevInfoData data, uint property, out uint type, [Out] byte[]? buffer, uint size, out uint required);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(uint devInst, ref DevPropKey key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevInfoData data, char* buffer, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static unsafe partial uint CM_Get_Device_ID(uint devInst, char* buffer, uint bufferLen, uint flags);
}

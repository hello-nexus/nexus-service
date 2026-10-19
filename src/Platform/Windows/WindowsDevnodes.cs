using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.Service.Platform.Windows;

/// <summary>Present devnodes and their Device Manager problem codes through SetupAPI and cfgmgr32, in-process.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDevnodes
{
    internal readonly record struct PresentDevice(string InstanceId, string Name, uint ProblemCode);

    private const uint DigcfPresent = 0x2;
    private const uint DigcfAllClasses = 0x4;
    private const uint SpdrpDeviceDesc = 0x0;
    private const uint SpdrpFriendlyName = 0xC;
    private const uint CmGetIdListFilterEnumerator = 0x1;
    private const uint CmGetIdListFilterPresent = 0x100;
    private const uint CmLocateDevnodePhantom = 0x1;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Every present devnode of a setup class, or of all classes when none is given.</summary>
    public static List<PresentDevice> EnumeratePresent(Guid? classGuid = null)
    {
        var devices = new List<PresentDevice>();
        IntPtr set;
        if (classGuid is { } guid)
        {
            set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        }
        else
        {
            set = SetupDiGetClassDevsAll(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        }
        if (set == InvalidHandle) return devices;

        try
        {
            var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var name = ReadStringProperty(set, ref data, SpdrpFriendlyName);
                if (name.Length == 0) name = ReadStringProperty(set, ref data, SpdrpDeviceDesc);

                var idBuffer = new char[512];
                var instanceId = "";
                if (SetupDiGetDeviceInstanceId(set, ref data, idBuffer, (uint)idBuffer.Length, out _))
                {
                    var end = Array.IndexOf(idBuffer, '\0');
                    instanceId = new string(idBuffer, 0, end >= 0 ? end : idBuffer.Length);
                }

                uint problem = 0;
                if (CM_Get_DevNode_Status(out _, out var p, data.DevInst, 0) == 0) problem = p;
                devices.Add(new PresentDevice(instanceId, name, problem));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    /// <summary>The present USB devnode whose instance id ends in the adb serial, or null.</summary>
    public static string? FindPresentUsbInstanceId(string serial)
    {
        if (CM_Get_Device_ID_List_Size(out var length, "USB", CmGetIdListFilterEnumerator | CmGetIdListFilterPresent) != 0
            || length == 0)
        {
            return null;
        }
        var buffer = new char[length];
        if (CM_Get_Device_ID_List("USB", buffer, length, CmGetIdListFilterEnumerator | CmGetIdListFilterPresent) != 0)
        {
            return null;
        }
        return PickUsbInstanceId(SplitMultiSz(buffer), serial);
    }

    /// <summary>DEVPKEY_Device_ProblemCode equivalent, or null when the devnode cannot be located.</summary>
    public static int? ReadProblemCode(string instanceId)
    {
        if (CM_Locate_DevNode(out var devInst, instanceId, CmLocateDevnodePhantom) != 0) return null;
        return CM_Get_DevNode_Status(out _, out var problem, devInst, 0) == 0 ? (int)problem : null;
    }

    /// <summary>The first <c>USB\*\serial</c> id; the case-insensitive suffix match keeps a serial from matching another devnode.</summary>
    internal static string? PickUsbInstanceId(IEnumerable<string> instanceIds, string serial)
    {
        var suffix = "\\" + serial;
        foreach (var id in instanceIds)
        {
            if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                && id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && id.Length > "USB\\".Length + suffix.Length - 1)
            {
                return id;
            }
        }
        return null;
    }

    internal static List<string> SplitMultiSz(char[] buffer)
    {
        var items = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break;
            items.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return items;
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

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevInfoData data, [Out] char[] buffer, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, uint length, uint flags);
}

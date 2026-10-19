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
    private const uint CmLocateDevnodePhantom = 0x1;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Every present devnode of a setup class, or of all classes (optionally under one enumerator such as USB) when none is given.</summary>
    public static List<PresentDevice> EnumeratePresent(Guid? classGuid = null, string? enumerator = null, bool friendlyNameFirst = false, bool idsOnly = false)
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
                var name = idsOnly ? "" : ReadName(set, ref data, friendlyNameFirst);

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
    public static string? FindPresentUsbInstanceId(string serial)
        => WindowsSpecsParsers.PickUsbInstanceId(EnumeratePresent(enumerator: "USB", idsOnly: true).Select(d => d.InstanceId), serial);

    /// <summary>DEVPKEY_Device_ProblemCode equivalent, or null when the devnode cannot be located.</summary>
    public static int? ReadProblemCode(string instanceId)
    {
        if (CM_Locate_DevNode(out var devInst, instanceId, CmLocateDevnodePhantom) != 0) return null;
        return CM_Get_DevNode_Status(out _, out var problem, devInst, 0) == 0 ? (int)problem : null;
    }

    // Win32_PnPEntity.Name prefers the friendly name; the sound list is verified against the description first.
    private static string ReadName(IntPtr set, ref SpDevInfoData data, bool friendlyFirst)
    {
        var first = friendlyFirst ? SpdrpFriendlyName : SpdrpDeviceDesc;
        var second = friendlyFirst ? SpdrpDeviceDesc : SpdrpFriendlyName;
        var name = ReadStringProperty(set, ref data, first);
        return name.Length > 0 ? name : ReadStringProperty(set, ref data, second);
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
    private static unsafe partial bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevInfoData data, char* buffer, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);
}

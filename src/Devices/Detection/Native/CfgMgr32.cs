#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexus.Service.Devices.Detection.Native;

/// <summary>
/// P/Invoke declarations for CfgMgr32 (cfgmgr32.dll): present-device
/// enumeration, devnode property reads, and PnP device-interface change
/// notifications. AOT-friendly via LibraryImport.
/// </summary>
internal static partial class CfgMgr32
{
    internal const uint CR_SUCCESS = 0;

    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;

    // Devnode-parentage walk depth bound for HasUsbAncestor: a HID collection
    // sits at most a few hops below its USB functional device or the root,
    // this only guards against an unexpected devnode-tree cycle.
    private const int MaxAncestorWalkHops = 16;

    // Sibling-walk bound for GetUsbHubSiblingInstanceIds: a hub's child list
    // is a handful of ports, this only guards against an unexpected cycle.
    private const int MaxHubChildWalkHops = 32;

    // devpropdef.h: DEVPROP_TYPE_STRING
    private const uint DevPropTypeString = 0x00000012;

    internal const int CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    internal const int CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    internal const int CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;

    /// <summary>usbiodef.h GUID_DEVINTERFACE_USB_DEVICE - hub-attached USB devices.</summary>
    internal static readonly Guid UsbDeviceInterfaceClass = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid Fmtid;
        public uint Pid;

        public DEVPROPKEY(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k, uint pid)
        {
            Fmtid = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            Pid = pid;
        }
    }

    // devpkey.h
    internal static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 2);
    internal static readonly DEVPROPKEY DEVPKEY_Device_Class =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 9);
    internal static readonly DEVPROPKEY DEVPKEY_Device_Manufacturer =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 13);
    internal static readonly DEVPROPKEY DEVPKEY_Device_LocationInfo =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 15);
    internal static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc =
        new(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2, 4);
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath =
        new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6, 5);
    internal static readonly DEVPROPKEY DEVPKEY_Device_InstanceId =
        new(0x78c34fc8, 0x104a, 0x4aca, 0x9e, 0xa4, 0x52, 0x4d, 0x52, 0x99, 0x6e, 0x57, 256);

    /// <summary>
    /// cfgmgr32.h CM_NOTIFY_FILTER: 16-byte header + 400-byte union
    /// (WCHAR InstanceId[MAX_DEVICE_ID_LEN=200] is the largest member).
    /// Only the DeviceInterface.ClassGuid union member is used here.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static partial uint CM_Get_Device_ID_List_Size(out uint pulLen, string pszFilter, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static unsafe partial uint CM_Get_Device_ID_List(string pszFilter, char* buffer, uint bufferLen, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Locate_DevNodeW")]
    internal static partial uint CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static unsafe partial uint CM_Get_DevNode_Property(
        uint dnDevInst, in DEVPROPKEY propertyKey, out uint propertyType,
        byte* propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    private static unsafe partial uint CM_Get_Device_Interface_Property(
        string pszDeviceInterface, in DEVPROPKEY propertyKey, out uint propertyType,
        byte* propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Register_Notification(
        in CM_NOTIFY_FILTER pFilter, IntPtr pContext, IntPtr pCallback, out IntPtr pNotifyContext);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Unregister_Notification(IntPtr notifyContext);

    internal static uint LocateDevNode(string instanceId, out uint devInst)
        => CM_Locate_DevNode(out devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);

    /// <summary>
    /// Instance ids of currently-attached devices under the given enumerator
    /// (e.g. "USB"). The list can change between the size query and the fetch,
    /// so the fetch retries with a re-queried size. Throws on API failure so
    /// callers can tell "enumeration broken" from "bus empty" - a false empty
    /// would read as every device absent and gate off the heartbeat workers.
    /// </summary>
    internal static unsafe List<string> GetPresentDeviceIds(string enumerator)
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        uint lastCr = CR_SUCCESS;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            lastCr = CM_Get_Device_ID_List_Size(out var len, enumerator, flags);
            if (lastCr != CR_SUCCESS)
            {
                continue;
            }
            if (len == 0)
            {
                return new List<string>();
            }
            // Slack absorbs devices arriving between the size query and the fetch.
            var buffer = new char[len + 1024];
            fixed (char* p = buffer)
            {
                lastCr = CM_Get_Device_ID_List(enumerator, p, (uint)buffer.Length, flags);
                if (lastCr == CR_SUCCESS)
                {
                    return SplitMultiSz(buffer);
                }
            }
        }
        throw new InvalidOperationException($"CM_Get_Device_ID_List failed (CR=0x{lastCr:X})");
    }

    private static List<string> SplitMultiSz(char[] buffer)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }
            if (i == start)
            {
                break; // double null = end of list
            }
            result.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return result;
    }

    /// <summary>
    /// String devnode property, or "" when absent / not a string / read error.
    /// </summary>
    internal static unsafe string GetStringProperty(uint devInst, in DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_Property(devInst, in key, out _, null, ref size, 0);
        if (size == 0 || size > 64 * 1024)
        {
            return "";
        }
        var buffer = new byte[size];
        uint type;
        uint ret;
        fixed (byte* p = buffer)
        {
            ret = CM_Get_DevNode_Property(devInst, in key, out type, p, ref size, 0);
        }
        if (ret != CR_SUCCESS || type != DevPropTypeString)
        {
            return "";
        }
        // UTF-16, null-terminated.
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length));
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text.Substring(0, nul) : text;
    }

    /// <summary>
    /// True when a device interface's devnode, or an ancestor of it, is
    /// enumerated under "USB". Walks CM_Get_Parent from the interface's own
    /// devnode, reading each ancestor's instance id and checking the
    /// enumerator prefix (the text before the first backslash, e.g. "USB",
    /// "HID", "ACPI"). A laptop-integrated I2C/ACPI digitizer never crosses a
    /// USB ancestor, so it correctly returns false. Any lookup failure
    /// (unresolvable interface, broken walk) also returns false: callers must
    /// treat "unknown" the same as "not USB".
    /// </summary>
    internal static unsafe bool HasUsbAncestor(string deviceInterfacePath)
    {
        var instanceId = GetInterfaceInstanceId(deviceInterfacePath);
        if (instanceId.Length == 0 || LocateDevNode(instanceId, out var devInst) != CR_SUCCESS)
        {
            return false;
        }
        for (var hop = 0; hop < MaxAncestorWalkHops; hop++)
        {
            var currentId = hop == 0 ? instanceId : GetStringProperty(devInst, DEVPKEY_Device_InstanceId);
            if (currentId.Length == 0)
            {
                return false;
            }
            var backslash = currentId.IndexOf('\\');
            var enumerator = backslash > 0 ? currentId[..backslash] : currentId;
            if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (CM_Get_Parent(out var parent, devInst, 0) != CR_SUCCESS)
            {
                return false;
            }
            devInst = parent;
        }
        return false;
    }

    /// <summary>
    /// Instance ids of the devices sharing a USB hub with the device
    /// interface's nearest hub-port ancestor (its siblings, not itself).
    /// Walks CM_Get_Parent from the interface's devnode to the first USB
    /// ancestor whose instance id has no "&amp;MI_" segment (a composite
    /// device's per-interface function nodes carry that segment, the
    /// composite device attached to the hub port itself does not), takes
    /// that node's parent (the hub), then walks CM_Get_Child/CM_Get_Sibling
    /// over the hub's children. Distinguishes descriptor-identical devices on
    /// different physical hubs (e.g. two panels using the same touch
    /// controller chip) by what else shares their internal hub. Empty on any
    /// walk failure - callers must not treat that as "definitely no sibling".
    /// </summary>
    internal static unsafe List<string> GetUsbHubSiblingInstanceIds(string deviceInterfacePath)
    {
        var result = new List<string>();
        var instanceId = GetInterfaceInstanceId(deviceInterfacePath);
        if (instanceId.Length == 0 || LocateDevNode(instanceId, out var devInst) != CR_SUCCESS)
        {
            return result;
        }

        uint hubPortDevInst = 0;
        var foundHubPort = false;
        for (var hop = 0; hop < MaxAncestorWalkHops; hop++)
        {
            var currentId = hop == 0 ? instanceId : GetStringProperty(devInst, DEVPKEY_Device_InstanceId);
            if (currentId.Length == 0)
            {
                return result;
            }
            var backslash = currentId.IndexOf('\\');
            var enumerator = backslash > 0 ? currentId[..backslash] : currentId;
            if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase)
                && currentId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0)
            {
                hubPortDevInst = devInst;
                foundHubPort = true;
                break;
            }
            if (CM_Get_Parent(out var parent, devInst, 0) != CR_SUCCESS)
            {
                return result;
            }
            devInst = parent;
        }
        if (!foundHubPort || CM_Get_Parent(out var hub, hubPortDevInst, 0) != CR_SUCCESS)
        {
            return result;
        }

        if (CM_Get_Child(out var child, hub, 0) != CR_SUCCESS)
        {
            return result;
        }
        for (var hop = 0; hop < MaxHubChildWalkHops; hop++)
        {
            if (child != hubPortDevInst)
            {
                var childId = GetStringProperty(child, DEVPKEY_Device_InstanceId);
                if (childId.Length > 0)
                {
                    result.Add(childId);
                }
            }
            if (CM_Get_Sibling(out var sibling, child, 0) != CR_SUCCESS)
            {
                break;
            }
            child = sibling;
        }
        return result;
    }

    private static unsafe string GetInterfaceInstanceId(string deviceInterfacePath)
    {
        if (string.IsNullOrEmpty(deviceInterfacePath))
        {
            return "";
        }
        uint size = 0;
        CM_Get_Device_Interface_Property(deviceInterfacePath, in DEVPKEY_Device_InstanceId, out _, null, ref size, 0);
        if (size == 0 || size > 64 * 1024)
        {
            return "";
        }
        var buffer = new byte[size];
        uint type;
        uint ret;
        fixed (byte* p = buffer)
        {
            ret = CM_Get_Device_Interface_Property(deviceInterfacePath, in DEVPKEY_Device_InstanceId, out type, p, ref size, 0);
        }
        if (ret != CR_SUCCESS || type != DevPropTypeString)
        {
            return "";
        }
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length));
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text.Substring(0, nul) : text;
    }
}
#endif

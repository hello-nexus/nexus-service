#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>Resolves the instance id of the USB hub or controller a dongle's device path sits behind.</summary>
internal static partial class Slv3UsbParent
{
    // Device paths look like \\?\USB#VID_0416&PID_8040#<serial>#{guid}; the instance id is the first three parts joined by backslashes.
    public static string? Resolve(string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length < 3)
        {
            return null;
        }
        var bus = parts[0][(parts[0].LastIndexOf('\\') + 1)..];
        var instanceId = $"{bus}\\{parts[1]}\\{parts[2]}";
        var parent = ParentOf(instanceId);
        // A composite interface node (&MI_xx) sits under the dongle's own composite device; report the hub above it.
        return parent is not null && parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) ? ParentOf(parent) ?? parent : parent;
    }

    // Devices whose USB paths are worth comparing: the SL wireless dongle halves, the HydroShift II board and its glass.
    private static readonly string[] TopologyTargets = { "VID_0416&PID_8040", "VID_0416&PID_8041", "VID_0416&PID_8051", "VID_1CBE&PID_A068" };

    /// <summary>One line per present target device: its hub chain up to the root, serials left out, so shared hubs or controllers stand out.</summary>
    public static unsafe List<string> DescribeTopology()
    {
        var lines = new List<string>();
        const uint FilterEnumerator = 0x1;
        const uint FilterPresent = 0x100;
        if (CM_Get_Device_ID_List_Size(out var chars, "USB", FilterEnumerator | FilterPresent) != 0 || chars < 2)
        {
            return lines;
        }
        var buffer = new char[chars];
        fixed (char* p = buffer)
        {
            if (CM_Get_Device_ID_List("USB", p, chars, FilterEnumerator | FilterPresent) != 0)
            {
                return lines;
            }
        }
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var target in TopologyTargets)
            {
                if (!id.Contains(target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var chain = new List<string> { WithoutSerial(id) };
                var current = id;
                for (var hop = 0; hop < 8 && ParentOf(current) is { } parent; hop++)
                {
                    chain.Add(WithoutSerial(parent));
                    current = parent;
                }
                lines.Add(string.Join(" > ", chain));
            }
        }
        return lines;
    }

    // The serial is the instance id's last part on a leaf device; a hub's last part is a system-generated path, kept.
    private static string WithoutSerial(string instanceId)
    {
        var parts = instanceId.Split('\\');
        return parts.Length >= 3 && parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase) ? parts[0] + "\\" + parts[1] : instanceId;
    }

    private static unsafe string? ParentOf(string instanceId)
    {
        if (CM_Locate_DevNode(out var node, instanceId, 0) != 0 || CM_Get_Parent(out var parent, node, 0) != 0)
        {
            return null;
        }
        var buf = new char[400];
        fixed (char* p = buf)
        {
            if (CM_Get_Device_ID(parent, p, (uint)buf.Length, 0) != 0)
            {
                return null;
            }
        }
        return new string(buf).TrimEnd('\0');
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Locate_DevNodeW")]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static unsafe partial uint CM_Get_Device_ID_List(string? filter, char* buffer, uint bufferLen, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static unsafe partial uint CM_Get_Device_ID(uint devInst, char* buffer, uint bufferLen, uint flags);
}
#endif

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

    // Devices whose USB paths are worth comparing: the SL wireless dongle halves, the HydroShift II board and its display.
    private static readonly string[] TopologyTargets = { "VID_0416&PID_8040", "VID_0416&PID_8041", "VID_0416&PID_8051", "VID_1CBE&PID_A068" };

    /// <summary>One line per present target device: its hub chain up to the root, serials left out, so shared hubs or controllers stand out.</summary>
    public static unsafe List<string> DescribeTopology()
    {
        var lines = new List<string>();
        const uint FilterEnumerator = 0x1;
        const uint FilterPresent = 0x100;
        const uint BufferSmall = 0x1A;
        char[]? buffer = null;
        // A device may arrive between the size query and the read, which reports a small buffer: ask again once.
        for (var attempt = 0; attempt < 2 && buffer is null; attempt++)
        {
            if (CM_Get_Device_ID_List_Size(out var chars, "USB", FilterEnumerator | FilterPresent) != 0 || chars < 2)
            {
                return lines;
            }
            var candidate = new char[chars];
            uint result;
            fixed (char* p = candidate)
            {
                result = CM_Get_Device_ID_List("USB", p, chars, FilterEnumerator | FilterPresent);
            }
            if (result == 0)
            {
                buffer = candidate;
            }
            else if (result != BufferSmall)
            {
                return lines;
            }
        }
        if (buffer is null)
        {
            return lines;
        }
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var target in TopologyTargets)
            {
                // A composite device lists once per interface (&MI_xx) as well; the parent device is the one line wanted.
                if (!id.Contains(target, StringComparison.OrdinalIgnoreCase) || id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var chain = new List<string> { WithoutSerial(id) };
                var current = id;
                for (var hop = 0; hop < 8 && ParentOf(current) is { } parent; hop++)
                {
                    // Hubs keep their full instance id, so two hubs of one model stay distinguishable.
                    chain.Add(parent);
                    current = parent;
                    // The chain ends at the root hub, plus the one PCI controller above it, so controllers tell apart without the ACPI path.
                    if (parent.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ParentOf(parent) is { } controller)
                        {
                            chain.Add(controller);
                        }
                        break;
                    }
                }
                lines.Add(string.Join(" > ", chain));
            }
        }
        return lines;
    }

    // Only the leaf device's instance id ends in its serial; that part is dropped and everything above it is kept whole.
    private static string WithoutSerial(string instanceId)
    {
        var parts = instanceId.Split('\\');
        return parts.Length >= 3 ? parts[0] + "\\" + parts[1] : instanceId;
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

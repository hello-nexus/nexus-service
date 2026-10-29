#if WINDOWS
using System;
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
        return ParentOf($"{bus}\\{parts[1]}\\{parts[2]}");
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

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static unsafe partial uint CM_Get_Device_ID(uint devInst, char* buffer, uint bufferLen, uint flags);
}
#endif

#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// SetupAPI discovery for the Flex fans. Enumerates the shared interface GUID and keeps only the
/// Flex receivers and screens (the RF dongle registers the same GUID and is ignored here).
/// </summary>
public sealed partial class WindowsFlexDiscovery : IFlexDiscovery
{
    private static readonly Guid FlexGuid = new(FlexProtocol.InterfaceGuid);

    public IReadOnlyList<FlexUnitPort> Discover() => FlexDiscoveryParser.Pair(EnumeratePaths(), ParentHub);

    /// <summary>The screens as the shared screen hub models them.</summary>
    public IReadOnlyList<Slv3LcdPortInfo> DiscoverScreens()
    {
        var result = new List<Slv3LcdPortInfo>();
        foreach (var unit in Discover())
        {
            if (unit.ScreenPath.Length > 0 && unit.ScreenSerial.Length > 0)
            {
                result.Add(new Slv3LcdPortInfo { PortName = unit.ScreenPath, Serial = unit.ScreenSerial, ProductId = unit.ScreenPid });
            }
        }
        return result;
    }

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

#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Qos.Service.Peripherals.Hyte.Np50;

/// <summary>
/// SetupAPI-based discovery for NP50 hubs on Windows. Walks the
/// <c>GUID_DEVCLASS_PORTS</c> device class and filters to hardware ids
/// containing <c>VID_3402&amp;PID_0901</c>. AOT-safe (no COM/WMI / no
/// System.Management dependency); matches the pattern in
/// <see cref="Hid.WindowsHidEnumerator"/>.
/// </summary>
public sealed class WindowsNp50PortDiscovery : INp50PortDiscovery
{
    // {4d36e978-e325-11ce-bfc1-08002be10318}: the COM/serial-port device class.
    private static readonly Guid PortsClassGuid = new("4d36e978-e325-11ce-bfc1-08002be10318");

    private static readonly string VidPidFragment = $"VID_{Np50Protocol.VendorId:X4}&PID_{Np50Protocol.ProductId:X4}";

    public IReadOnlyList<Np50PortInfo> Discover()
    {
        var result = new List<Np50PortInfo>();
        var devInfo = Native.SetupDiGetClassDevs(ref PortsClassGuidLocal(), null, IntPtr.Zero, Native.DIGCF_PRESENT);
        if (devInfo == (IntPtr)(-1)) return result;
        try
        {
            var devData = new Native.SP_DEVINFO_DATA();
            devData.cbSize = Marshal.SizeOf(devData);
            for (uint i = 0; Native.SetupDiEnumDeviceInfo(devInfo, i, ref devData); i++)
            {
                var hardwareId = ReadStringProperty(devInfo, ref devData, Native.SPDRP_HARDWAREID);
                if (string.IsNullOrEmpty(hardwareId)) continue;
                if (hardwareId.IndexOf(VidPidFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var portName = ReadPortName(devInfo, ref devData);
                if (string.IsNullOrEmpty(portName)) continue;

                result.Add(new Np50PortInfo
                {
                    PortName = portName,
                    Serial = ReadInstanceId(devInfo, ref devData),
                });
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return result;
    }

    private static ref Guid PortsClassGuidLocal()
    {
        // SetupDiGetClassDevs takes the guid by ref; can't pass `ref readonly`
        // through a `ref Guid` parameter without a mutable local.
        return ref System.Runtime.CompilerServices.Unsafe.AsRef(in PortsClassGuid);
    }

    private static string ReadStringProperty(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData, uint property)
    {
        var buf = new byte[1024];
        if (!Native.SetupDiGetDeviceRegistryProperty(devInfo, ref devData, property, out _, buf, (uint)buf.Length, out var size))
            return "";
        // REG_SZ / REG_MULTI_SZ — both deserialize the first string fine.
        var raw = Encoding.Unicode.GetString(buf, 0, Math.Max(0, (int)size - 2));
        var nul = raw.IndexOf('\0');
        return nul >= 0 ? raw.Substring(0, nul) : raw;
    }

    private static string ReadPortName(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData)
    {
        // The friendly COM port name is stored under
        // HKLM\System\CurrentControlSet\Enum\<devicePath>\Device Parameters\PortName.
        // SetupDiOpenDevRegKey hands back that subkey directly.
        var handle = Native.SetupDiOpenDevRegKey(devInfo, ref devData,
            Native.DICS_FLAG_GLOBAL, 0, Native.DIREG_DEV, Native.KEY_READ);
        if (handle == IntPtr.Zero || handle == (IntPtr)(-1)) return "";
        try
        {
            using var key = RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(handle, ownsHandle: false));
            return key.GetValue("PortName") as string ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Native.RegCloseKey(handle);
        }
    }

    /// <summary>
    /// Pull the device instance id (e.g. "USB\VID_3402&amp;PID_0901\205B338B3232")
    /// and extract the trailing segment as the device serial. SetupDiGetDeviceInstanceId
    /// is the only API call that surfaces the full instance id including the
    /// serial; SPDRP_HARDWAREID stops short of it.
    /// </summary>
    private static string ReadInstanceId(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData)
    {
        var buf = new char[256];
        if (!Native.SetupDiGetDeviceInstanceId(devInfo, ref devData, buf, (uint)buf.Length, out var needed))
            return "";
        var instanceId = new string(buf, 0, Math.Max(0, (int)needed - 1)); // strip the trailing NUL
        // Instance id shape: "USB\VID_xxxx&PID_yyyy\SERIAL". Take everything
        // after the last backslash as the serial.
        var lastSlash = instanceId.LastIndexOf('\\');
        if (lastSlash < 0 || lastSlash == instanceId.Length - 1) return instanceId;
        return instanceId.Substring(lastSlash + 1);
    }

    // ── P/Invoke ──

    private static class Native
    {
        public const uint DIGCF_PRESENT = 0x02;
        public const uint SPDRP_HARDWAREID = 0x01;
        public const uint SPDRP_DEVICEDESC = 0x00;
        public const uint DICS_FLAG_GLOBAL = 0x01;
        public const uint DIREG_DEV = 0x01;
        public const uint KEY_READ = 0x20019;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiOpenDevRegKey(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint scope, uint hwProfile, uint keyType, uint samDesired);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegCloseKey(IntPtr hKey);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupDiGetDeviceInstanceIdW")]
        public static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            [Out] char[] deviceInstanceId,
            uint deviceInstanceIdSize,
            out uint requiredSize);
    }
}
#endif

#if WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// SetupAPI-based discovery for HYTE Y70 Touch display controllers on Windows.
/// Mirrors the Q-series/MiniHub discoveries but matches every Y70 serial PID and maps
/// each to its firmware variant. The PID is parsed out of the hardware id so a
/// single pass covers Touch (0C00) / Infinite (0C01) / Truly (0C02) / GT (0C06).
/// </summary>
public sealed class WindowsY70DisplayPortDiscovery : IY70DisplayPortDiscovery
{
    private static readonly Guid PortsClassGuid = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private static readonly string VidFragment = $"VID_{Y70DisplayProtocol.VendorId:X4}&PID_";

    public IReadOnlyList<Y70DisplayPort> Discover()
    {
        var result = new List<Y70DisplayPort>();
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

                var variant = VariantFromHardwareId(hardwareId);
                if (string.IsNullOrEmpty(variant)) continue;

                var portName = ReadPortName(devInfo, ref devData);
                if (string.IsNullOrEmpty(portName)) continue;

                result.Add(new Y70DisplayPort
                {
                    PortName = portName,
                    Serial = ReadInstanceId(devInfo, ref devData),
                    Variant = variant,
                });
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return result;
    }

    /// <summary>Pull the PID out of "…VID_3402&amp;PID_0C01…" and map it to a Y70 variant, or "" if not a Y70.</summary>
    private static string VariantFromHardwareId(string hardwareId)
    {
        var idx = hardwareId.IndexOf(VidFragment, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var pidStart = idx + VidFragment.Length;
        if (pidStart + 4 > hardwareId.Length) return "";
        var pidHex = hardwareId.Substring(pidStart, 4);
        if (!int.TryParse(pidHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid)) return "";
        return Y70DisplayProtocol.VariantForProductId(pid);
    }

    private static ref Guid PortsClassGuidLocal()
        => ref System.Runtime.CompilerServices.Unsafe.AsRef(in PortsClassGuid);

    private static string ReadStringProperty(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData, uint property)
    {
        var buf = new byte[1024];
        if (!Native.SetupDiGetDeviceRegistryProperty(devInfo, ref devData, property, out _, buf, (uint)buf.Length, out var size))
            return "";
        var raw = Encoding.Unicode.GetString(buf, 0, Math.Max(0, (int)size - 2));
        var nul = raw.IndexOf('\0');
        return nul >= 0 ? raw.Substring(0, nul) : raw;
    }

    private static string ReadPortName(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData)
    {
        var handle = Native.SetupDiOpenDevRegKey(devInfo, ref devData,
            Native.DICS_FLAG_GLOBAL, 0, Native.DIREG_DEV, Native.KEY_READ);
        if (handle == IntPtr.Zero || handle == (IntPtr)(-1)) return "";
        try
        {
            using var key = RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(handle, ownsHandle: false));
            return key.GetValue("PortName") as string ?? "";
        }
        catch { return ""; }
        finally { Native.RegCloseKey(handle); }
    }

    private static string ReadInstanceId(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData)
    {
        var buf = new char[256];
        if (!Native.SetupDiGetDeviceInstanceId(devInfo, ref devData, buf, (uint)buf.Length, out var needed))
            return "";
        var instanceId = new string(buf, 0, Math.Max(0, (int)needed - 1));
        var lastSlash = instanceId.LastIndexOf('\\');
        if (lastSlash < 0 || lastSlash == instanceId.Length - 1) return instanceId;
        return instanceId.Substring(lastSlash + 1);
    }

    private static class Native
    {
        public const uint DIGCF_PRESENT = 0x02;
        public const uint SPDRP_HARDWAREID = 0x01;
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
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            uint scope, uint hwProfile, uint keyType, uint samDesired);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegCloseKey(IntPtr hKey);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            [Out] char[] deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);
    }
}
#endif

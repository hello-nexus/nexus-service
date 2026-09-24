#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Windows HID enumeration and I/O via SetupAPI + hid.dll P/Invoke. Narrow
/// surface tailored to the first-party device protocols' exchanges.
/// </summary>
public sealed class WindowsHidEnumerator : IHidEnumerator
{
    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
        EnumerateDevices(attrs => attrs.VendorID == (ushort)vendorId && attrs.ProductID == (ushort)productId);

    public IReadOnlyList<HidDeviceInfo> FindAll() => EnumerateDevices(null);

    private static List<HidDeviceInfo> EnumerateDevices(Func<Native.HIDD_ATTRIBUTES, bool>? attrsFilter)
    {
        var result = new List<HidDeviceInfo>();
        Native.HidD_GetHidGuid(out var hidGuid);

        var devInfo = Native.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (devInfo == (IntPtr)(-1))
        {
            return result;
        }

        try
        {
            var idx = 0u;
            var ifaceData = new Native.SP_DEVICE_INTERFACE_DATA();
            ifaceData.cbSize = Marshal.SizeOf(ifaceData);

            while (Native.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, idx, ref ifaceData))
            {
                idx++;

                Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0)
                {
                    continue;
                }

                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // First 4 bytes are the size prefix; layout differs 32 vs 64 bit.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);

                    if (!Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detail, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var devicePath = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (string.IsNullOrEmpty(devicePath))
                    {
                        continue;
                    }

                    var info = TryReadDeviceInfo(devicePath, attrsFilter);
                    if (info is not null)
                    {
                        result.Add(info);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return result;
    }

    /// <summary>
    /// Opens the path query-only to read attributes, then the caller's filter, then
    /// preparsed data for report sizes (skipped when the filter rejects the device).
    /// </summary>
    private static HidDeviceInfo? TryReadDeviceInfo(string devicePath, Func<Native.HIDD_ATTRIBUTES, bool>? attrsFilter)
    {
        var handle = Native.CreateFile(devicePath,
            0, // query only
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            0,
            IntPtr.Zero);
        if (handle == (IntPtr)(-1))
        {
            return null;
        }

        try
        {
            var attrs = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            if (!Native.HidD_GetAttributes(handle, ref attrs))
            {
                return null;
            }

            if (attrsFilter is not null && !attrsFilter(attrs))
            {
                return null;
            }

            var serial = TryGetSerial(handle);
            var (usagePage, usage, inLen, outLen, featLen) = TryGetCaps(handle);

            return new HidDeviceInfo
            {
                VendorId = attrs.VendorID,
                ProductId = attrs.ProductID,
                Path = devicePath,
                Serial = serial,
                UsagePage = usagePage,
                Usage = usage,
                InputReportByteLength = inLen,
                OutputReportByteLength = outLen,
                FeatureReportByteLength = featLen,
            };
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>hidapi's own default queue depth for an input handle (hid.c HidD_SetNumInputBuffers call).</summary>
    private const uint InputBufferDepth = 64;

    public IHidDevice? Open(string path, bool forInput = false)
    {
        // Try progressively less restrictive access if another process (e.g. vendor
        // apps like Razer Synapse, G HUB) holds exclusive access. We log the mode
        // we ended up with because reduced access silently breaks SetFeature later.
        // forInput opens for overlapped I/O so WindowsHidDevice.Read can honor its
        // timeout; the feature-report write path stays synchronous.
        var flags = forInput ? Native.FILE_FLAG_OVERLAPPED : 0u;
        IntPtr handle = (IntPtr)(-1);
        (uint access, string label)[] accessModes =
        {
            (Native.GENERIC_READ | Native.GENERIC_WRITE, "RW"),
            (Native.GENERIC_WRITE, "W"),
            (Native.GENERIC_READ, "R"),
            (0, "NONE"),
        };
        string modeUsed = "FAIL";
        foreach (var (access, label) in accessModes)
        {
            handle = Native.CreateFile(path,
                access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                flags,
                IntPtr.Zero);
            if (handle != (IntPtr)(-1))
            {
                modeUsed = label;
                break;
            }
        }

        if (handle == (IntPtr)(-1))
        {
            return null;
        }

        ServiceLog.Info($"[hid] opened {path.Substring(System.Math.Max(0, path.Length - 60))} with access={modeUsed}");

        if (forInput)
        {
            // Grows the driver's queued-input-report depth past its small
            // default, so a burst of presses isn't dropped while nothing was
            // reading; hidapi (hid.c) calls this on every input open.
            Native.HidD_SetNumInputBuffers(handle, InputBufferDepth);
        }

        var attrs = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(handle, ref attrs))
        {
            Native.CloseHandle(handle);
            return null;
        }

        var serial = TryGetSerial(handle);
        var (usagePage, usage, inLen, outLen, featLen) = TryGetCaps(handle);

        return new WindowsHidDevice(handle, path, attrs.VendorID, attrs.ProductID, serial, usagePage, usage,
            inLen, outLen, featLen, forInput);
    }

    private static string? TryGetSerial(IntPtr handle)
    {
        var buf = new byte[254];
        if (!Native.HidD_GetSerialNumberString(handle, buf, (uint)buf.Length))
        {
            return null;
        }
        // UTF-16 string, null-terminated
        var chars = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(chars) ? null : chars;
    }

    private static (int UsagePage, int Usage, int In, int Out, int Feat) TryGetCaps(IntPtr handle)
    {
        if (!Native.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return (0, 0, 0, 0, 0);
        }
        try
        {
            var caps = new Native.HIDP_CAPS();
            if (Native.HidP_GetCaps(preparsed, ref caps) != Native.HIDP_STATUS_SUCCESS)
            {
                return (0, 0, 0, 0, 0);
            }
            return (caps.UsagePage, caps.Usage, caps.InputReportByteLength,
                caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }
    }

    internal static class Native
    {
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int HIDP_STATUS_SUCCESS = unchecked((int)0x00110000);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_SetFeature(IntPtr handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_SetOutputReport(IntPtr handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetFeature(IntPtr handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetInputReport(IntPtr handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_SetNumInputBuffers(IntPtr handle, uint numberBuffers);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetSerialNumberString(IntPtr handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData, uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(IntPtr handle, byte[] buffer, uint toWrite, out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(IntPtr handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

        // Overlapped ReadFile: buffer must stay pinned and the OVERLAPPED valid until
        // the operation completes (GetOverlappedResult), so this variant takes raw
        // pointers rather than marshalling a managed array per call.
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern unsafe bool ReadFileOverlapped(IntPtr handle, byte* buffer, uint toRead, IntPtr read, NativeOverlapped* overlapped);

        // Overlapped WriteFile: a FILE_FLAG_OVERLAPPED handle rejects a synchronous
        // WriteFile, so a forInput device must issue its output reports overlapped.
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteFile")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern unsafe bool WriteFileOverlapped(IntPtr handle, byte* buffer, uint toWrite, IntPtr written, NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateEventW")]
        public static extern IntPtr CreateEventW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ResetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIo(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern unsafe bool GetOverlappedResult(IntPtr handle, NativeOverlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
#endif

using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>IOKit HID + CoreFoundation P/Invoke shared by <see cref="MacHidEnumerator"/> and <see cref="MacHidDevice"/>. Plain scalar/pointer signatures, AOT-safe.</summary>
internal static unsafe class MacHidNative
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint KCfStringEncodingUtf8 = 0x08000100;
    public const int KCfNumberSInt32Type = 3;
    public const int KIoReturnSuccess = 0;

    // IOHIDReportType
    public const int ReportTypeInput = 0;
    public const int ReportTypeOutput = 1;
    public const int ReportTypeFeature = 2;

    [DllImport(IOKit)] public static extern IntPtr IOHIDManagerCreate(IntPtr allocator, uint options);
    [DllImport(IOKit)] public static extern void IOHIDManagerSetDeviceMatching(IntPtr manager, IntPtr matching);
    [DllImport(IOKit)] public static extern IntPtr IOHIDManagerCopyDevices(IntPtr manager);

    [DllImport(IOKit)] public static extern IntPtr IOHIDDeviceGetProperty(IntPtr device, IntPtr key);
    [DllImport(IOKit)] public static extern uint IOHIDDeviceGetService(IntPtr device);
    [DllImport(IOKit)] public static extern IntPtr IOHIDDeviceCreate(IntPtr allocator, uint service);
    [DllImport(IOKit)] public static extern int IOHIDDeviceOpen(IntPtr device, uint options);
    [DllImport(IOKit)] public static extern int IOHIDDeviceClose(IntPtr device, uint options);
    [DllImport(IOKit)] public static extern int IOHIDDeviceSetReport(IntPtr device, int type, nint reportId, byte* report, nint length);
    [DllImport(IOKit)] public static extern int IOHIDDeviceGetReport(IntPtr device, int type, nint reportId, byte* report, nint* length);
    [DllImport(IOKit)] public static extern void IOHIDDeviceRegisterInputReportCallback(IntPtr device, byte* report, nint length, IntPtr callback, IntPtr context);
    [DllImport(IOKit)] public static extern void IOHIDDeviceRegisterRemovalCallback(IntPtr device, IntPtr callback, IntPtr context);
    [DllImport(IOKit)] public static extern void IOHIDDeviceScheduleWithRunLoop(IntPtr device, IntPtr runLoop, IntPtr mode);
    [DllImport(IOKit)] public static extern void IOHIDDeviceUnscheduleFromRunLoop(IntPtr device, IntPtr runLoop, IntPtr mode);

    [DllImport(IOKit)] public static extern int IORegistryEntryGetRegistryEntryID(uint entry, ulong* entryId);
    [DllImport(IOKit)] public static extern IntPtr IORegistryEntryIDMatching(ulong entryId);
    [DllImport(IOKit)] public static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(IOKit)] public static extern int IOObjectRelease(uint obj);

    [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] public static extern nint CFSetGetCount(IntPtr set);
    [DllImport(CoreFoundation)] public static extern void CFSetGetValues(IntPtr set, IntPtr* values);
    [DllImport(CoreFoundation)] public static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPStr)] string str, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CFStringGetCString(IntPtr str, byte* buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CFNumberGetValue(IntPtr number, nint type, int* valuePtr);
    [DllImport(CoreFoundation)] public static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CoreFoundation)] public static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] public static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)] public static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] public static extern void CFRunLoopRun();
    [DllImport(CoreFoundation)] public static extern void CFRunLoopStop(IntPtr runLoop);

    /// <summary>Owned CFString for an IOKit property key or run-loop mode; caller releases.</summary>
    public static IntPtr CfString(string s) => CFStringCreateWithCString(IntPtr.Zero, s, KCfStringEncodingUtf8);

    public static int? IntProperty(IntPtr device, string key)
    {
        var cfKey = CfString(key);
        try
        {
            var value = IOHIDDeviceGetProperty(device, cfKey);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFNumberGetTypeID())
            {
                return null;
            }
            int n;
            return CFNumberGetValue(value, KCfNumberSInt32Type, &n) ? n : null;
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    public static string? StringProperty(IntPtr device, string key)
    {
        var cfKey = CfString(key);
        try
        {
            var value = IOHIDDeviceGetProperty(device, cfKey);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
            {
                return null;
            }
            var buffer = stackalloc byte[512];
            if (!CFStringGetCString(value, buffer, 512, KCfStringEncodingUtf8))
            {
                return null;
            }
            var text = Marshal.PtrToStringUTF8((IntPtr)buffer);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    public static HidDeviceInfo ReadInfo(IntPtr device, string path) => new()
    {
        VendorId = IntProperty(device, "VendorID") ?? 0,
        ProductId = IntProperty(device, "ProductID") ?? 0,
        Path = path,
        Serial = StringProperty(device, "SerialNumber"),
        UsagePage = IntProperty(device, "PrimaryUsagePage") ?? 0,
        Usage = IntProperty(device, "PrimaryUsage") ?? 0,
        InputReportByteLength = IntProperty(device, "MaxInputReportSize") ?? 0,
        OutputReportByteLength = IntProperty(device, "MaxOutputReportSize") ?? 0,
        FeatureReportByteLength = IntProperty(device, "MaxFeatureReportSize") ?? 0,
    };
}

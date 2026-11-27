#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection.Native;
using Nexus.Service.Platform;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Invalidates the <see cref="CachingUsbEnumerator"/> on PnP USB device
/// interface arrival/removal (CM_Register_Notification), so hotplug is
/// detected event-driven instead of by cache expiry. On successful
/// registration the cache's fallback TTL is raised - it then only backstops
/// a missed notification. If registration fails the cache keeps its short
/// polling TTL and behavior matches the pre-notification design.
/// </summary>
internal sealed unsafe class UsbDeviceChangeNotifier : IHostedService
{
    private static readonly TimeSpan EventDrivenTtl = TimeSpan.FromSeconds(120);

    // Callback context: the notifier is a process singleton, so a static ref
    // avoids marshalling a GCHandle through the native context pointer.
    private static UsbDeviceChangeNotifier? s_instance;

    private readonly CachingUsbEnumerator _cache;
    private IntPtr _registration;
    private IntPtr _hidRegistration;

    public UsbDeviceChangeNotifier(CachingUsbEnumerator cache) { _cache = cache; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        s_instance = this;
        var filter = new CfgMgr32.CM_NOTIFY_FILTER
        {
            cbSize = (uint)Marshal.SizeOf<CfgMgr32.CM_NOTIFY_FILTER>(),
            FilterType = CfgMgr32.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            ClassGuid = CfgMgr32.UsbDeviceInterfaceClass,
        };
        var ret = CfgMgr32.CM_Register_Notification(
            in filter, IntPtr.Zero,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, uint, uint>)&OnNotification,
            out _registration);
        if (ret == CfgMgr32.CR_SUCCESS)
        {
            _cache.SetTtl(EventDrivenTtl);
            ServiceLog.Info($"[usb-notify] PnP usb-interface notifications registered; cache fallback TTL {EventDrivenTtl.TotalSeconds:F0}s");
        }
        else
        {
            ServiceLog.Warn($"[usb-notify] CM_Register_Notification failed (CR=0x{ret:X}); keeping the polling TTL");
        }
        // A LampArray is a HID collection: it appears after its USB devnode and over Bluetooth never
        // touches the USB bus, so the Dynamic Lighting count is invalidated on HID interface events.
        var hidFilter = filter;
        hidFilter.ClassGuid = CfgMgr32.HidDeviceInterfaceClass;
        var hidRet = CfgMgr32.CM_Register_Notification(
            in hidFilter, IntPtr.Zero,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, uint, uint>)&OnHidNotification,
            out _hidRegistration);
        if (hidRet != CfgMgr32.CR_SUCCESS)
        {
            ServiceLog.Warn($"[usb-notify] HID interface notification failed (CR=0x{hidRet:X}); the Dynamic Lighting device count refreshes on USB changes only");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_registration != IntPtr.Zero)
        {
            CfgMgr32.CM_Unregister_Notification(_registration);
            _registration = IntPtr.Zero;
        }
        if (_hidRegistration != IntPtr.Zero)
        {
            CfgMgr32.CM_Unregister_Notification(_hidRegistration);
            _hidRegistration = IntPtr.Zero;
        }
        s_instance = null;
        return Task.CompletedTask;
    }

    [UnmanagedCallersOnly]
    private static uint OnNotification(IntPtr hNotify, IntPtr context, int action, IntPtr eventData, uint eventDataSize)
    {
        // PnP callbacks must return quickly; Invalidate is O(1) and the next
        // Enumerate caller performs the actual re-scan.
        if (action is CfgMgr32.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL
                   or CfgMgr32.CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL)
        {
            s_instance?._cache.Invalidate();
            var link = ReadSymbolicLink(eventData, eventDataSize);
            var device = link.Length > 0 ? $" {link}" : "";
            ServiceLog.Info(action == CfgMgr32.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL
                ? $"[usb-notify] usb device arrival{device}; enumeration cache invalidated"
                : $"[usb-notify] usb device removal{device}; enumeration cache invalidated");
        }
        return 0; // ERROR_SUCCESS
    }

    [UnmanagedCallersOnly]
    private static uint OnHidNotification(IntPtr hNotify, IntPtr context, int action, IntPtr eventData, uint eventDataSize)
    {
        if (action is CfgMgr32.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL
                   or CfgMgr32.CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL)
        {
            Nexus.Service.Conflicts.WindowsDynamicLighting.InvalidateDeviceCount();
        }
        return 0;
    }

    /// <summary>
    /// CM_NOTIFY_EVENT_DATA layout for a device-interface event: FilterType(4) +
    /// Reserved(4) + ClassGuid(16), then the WCHAR SymbolicLink
    /// (<c>\\?\USB#VID_xxxx&amp;PID_yyyy#serial#{guid}</c>) - the identity of the
    /// device that arrived/left, which attributes the invalidation in field logs.
    /// </summary>
    private const int SymbolicLinkOffsetBytes = 24;

    private static string ReadSymbolicLink(IntPtr eventData, uint eventDataSize)
    {
        if (eventData == IntPtr.Zero || eventDataSize <= SymbolicLinkOffsetBytes + sizeof(char))
            return "";
        try
        {
            var chars = (int)(eventDataSize - SymbolicLinkOffsetBytes) / sizeof(char);
            var text = Marshal.PtrToStringUni(eventData + SymbolicLinkOffsetBytes, chars) ?? "";
            var nul = text.IndexOf('\0');
            return nul >= 0 ? text.Substring(0, nul) : text;
        }
        catch
        {
            return "";
        }
    }
}
#endif

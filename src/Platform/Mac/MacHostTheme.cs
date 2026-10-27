using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// macOS light/dark source for <see cref="HostThemeSync"/>: reads AppleInterfaceStyle
/// and re-reads on AppleInterfaceThemeChangedNotification (distributed). Delivery
/// needs the main thread's run loop, which <see cref="MacAppBootstrap"/> pumps.
/// </summary>
internal static unsafe class MacHostTheme
{
    private static Action<bool>? _onTheme;
    private static IntPtr _observer;

    /// <summary>Reports the current theme immediately, then on every change.</summary>
    public static bool Start(Action<bool> onTheme)
    {
        if (!OperatingSystem.IsMacOS() || _observer != IntPtr.Zero)
        {
            return false;
        }
        try
        {
            _onTheme = onTheme;
            var cls = objc_allocateClassPair(ClassGet("NSObject"), "NexusHostThemeObserver", IntPtr.Zero);
            if (cls == IntPtr.Zero)
            {
                cls = ClassGet("NexusHostThemeObserver");
                if (cls == IntPtr.Zero)
                {
                    throw new InvalidOperationException("NexusHostThemeObserver class allocation failed");
                }
            }
            else
            {
                class_addMethod(cls, Sel("themeChanged:"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ThemeChangedImpl, "v@:@");
                objc_registerClassPair(cls);
            }

            _observer = MsgSend(MsgSend(cls, Sel("alloc")), Sel("init"));
            var center = MsgSend(ClassGet("NSDistributedNotificationCenter"), Sel("defaultCenter"));
            if (_observer == IntPtr.Zero || center == IntPtr.Zero)
            {
                throw new InvalidOperationException("distributed notification center unavailable");
            }
            MsgSend_AddObserver(center, Sel("addObserver:selector:name:object:"),
                _observer, Sel("themeChanged:"), NsString("AppleInterfaceThemeChangedNotification"), IntPtr.Zero);
            Report();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-theme] host theme observer failed: {ex.Message}");
            _observer = IntPtr.Zero;
            return false;
        }
    }

    private static void Report()
    {
        var defaults = MsgSend(ClassGet("NSUserDefaults"), Sel("standardUserDefaults"));
        var style = Utf8(MsgSend(defaults, Sel("stringForKey:"), NsString("AppleInterfaceStyle")));
        _onTheme?.Invoke(HostThemeSync.IsDarkInterfaceStyle(style));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ThemeChangedImpl(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        try
        {
            Report();
            // The notification fires before the preference commits, so a read on
            // the edge can return the previous value; re-read once it settles.
            _ = Task.Delay(400).ContinueWith(_ =>
            {
                try { Report(); } catch { }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-theme] callback failed: {ex.Message}");
        }
    }

    private static string? Utf8(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
        {
            return null;
        }
        var chars = MsgSend(nsString, Sel("UTF8String"));
        return chars == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(chars);
    }

    private static IntPtr NsString(string s)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s + '\0');
        fixed (byte* p = utf8)
        {
            return MsgSend(ClassGet("NSString"), Sel("stringWithUTF8String:"), (IntPtr)p);
        }
    }

    private const string Libobjc = "/usr/lib/libobjc.dylib";

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr ClassGet([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr extraBytes);

    [DllImport(Libobjc, EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Libobjc, EntryPoint = "class_addMethod")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_AddObserver(IntPtr receiver, IntPtr sel, IntPtr observer, IntPtr selector, IntPtr name, IntPtr obj);
}

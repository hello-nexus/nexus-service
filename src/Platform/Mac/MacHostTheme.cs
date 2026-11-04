using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// macOS light/dark source for <see cref="HostThemeSync"/>: KVO on
/// NSApp.effectiveAppearance, which fires after the new value is committed.
/// Call from the main thread after NSApplication exists; delivery needs the
/// run loop <see cref="MacAppBootstrap"/> pumps.
/// </summary>
internal static unsafe class MacHostTheme
{
    private static Action<bool>? _onTheme;
    private static IntPtr _observer;
    private static IntPtr _app;

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
            var pool = objc_autoreleasePoolPush();
            try
            {
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
                    class_addMethod(cls, Sel("observeValueForKeyPath:ofObject:change:context:"),
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&ObserveImpl, "v@:@@@^v");
                    objc_registerClassPair(cls);
                }

                _observer = MsgSend(MsgSend(cls, Sel("alloc")), Sel("init"));
                _app = MsgSend(ClassGet("NSApplication"), Sel("sharedApplication"));
                if (_observer == IntPtr.Zero || _app == IntPtr.Zero)
                {
                    throw new InvalidOperationException("NSApplication unavailable");
                }
                MsgSend_AddKvo(_app, Sel("addObserver:forKeyPath:options:context:"),
                    _observer, NsString("effectiveAppearance"), 0, IntPtr.Zero);
                Report();
                return true;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
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
        var appearance = MsgSend(_app, Sel("effectiveAppearance"));
        var name = Utf8(MsgSend(appearance, Sel("name")));
        _onTheme?.Invoke(name is not null && name.Contains("Dark", StringComparison.Ordinal));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ObserveImpl(IntPtr self, IntPtr cmd, IntPtr keyPath, IntPtr obj, IntPtr change, IntPtr context)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            Report();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-theme] callback failed: {ex.Message}");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
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
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    // addObserver:forKeyPath:options:context: - options is NSUInteger
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_AddKvo(IntPtr receiver, IntPtr sel, IntPtr observer, IntPtr keyPath, nuint options, IntPtr context);

    [DllImport(Libobjc, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Libobjc, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);
}

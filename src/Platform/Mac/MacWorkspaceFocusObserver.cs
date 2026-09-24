using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// NSWorkspaceDidActivateApplicationNotification bridged to a C# callback
/// (same dynamic-class + UnmanagedCallersOnly bridge as <see cref="MacStatusBar"/>).
/// Delivery needs the main thread's run loop, which only <see cref="MacAppBootstrap"/> pumps.
/// </summary>
internal static unsafe class MacWorkspaceFocusObserver
{
    /// <summary>Regular is NSApplicationActivationPolicyRegular: a Dock app, as opposed to a UI-element agent (Spotlight, the auth dialog) or a background process.</summary>
    public sealed record ActivatedApp(int Pid, string Name, string? BundlePath, string? ExecutablePath, bool Regular);

    private static Action<ActivatedApp>? _onActivated;
    private static IntPtr _observer;

    public static bool Start(Action<ActivatedApp> onActivated)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }
        if (_observer != IntPtr.Zero)
        {
            _onActivated = onActivated;
            return true;
        }
        try
        {
            _onActivated = onActivated;
            // AppKit is not linked in; NSWorkspace resolves only once the dylib is loaded (MacAppIconExtractor does the same).
            NativeLibrary.Load(Appkit);
            var cls = objc_allocateClassPair(ClassGet("NSObject"), "NexusFocusObserver", IntPtr.Zero);
            if (cls == IntPtr.Zero)
            {
                cls = ClassGet("NexusFocusObserver");
                if (cls == IntPtr.Zero)
                {
                    throw new InvalidOperationException("NexusFocusObserver class allocation failed");
                }
            }
            else
            {
                // - (void)appActivated:(NSNotification *)note -> "v@:@"
                class_addMethod(cls, Sel("appActivated:"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&AppActivatedImpl, "v@:@");
                objc_registerClassPair(cls);
            }

            _observer = MsgSend(MsgSend(cls, Sel("alloc")), Sel("init"));
            var workspace = MsgSend(ClassGet("NSWorkspace"), Sel("sharedWorkspace"));
            var center = MsgSend(workspace, Sel("notificationCenter"));
            if (_observer == IntPtr.Zero || center == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSWorkspace notification center unavailable");
            }
            MsgSend_AddObserver(center, Sel("addObserver:selector:name:object:"),
                _observer, Sel("appActivated:"), NsString("NSWorkspaceDidActivateApplicationNotification"), IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-focus] workspace observer failed: {ex.Message}");
            _observer = IntPtr.Zero;
            return false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void AppActivatedImpl(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        try
        {
            // userInfo[NSWorkspaceApplicationKey] is the NSRunningApplication;
            // messaging nil is a no-op returning nil, so a missing key falls
            // out as pid 0 below.
            var userInfo = MsgSend(notification, Sel("userInfo"));
            var app = MsgSend(userInfo, Sel("objectForKey:"), NsString("NSWorkspaceApplicationKey"));
            if (app == IntPtr.Zero)
            {
                return;
            }
            var pid = MsgSend_Int(app, Sel("processIdentifier"));
            var name = Utf8(MsgSend(app, Sel("localizedName")));
            var bundlePath = Utf8(MsgSend(MsgSend(app, Sel("bundleURL")), Sel("path")));
            var executablePath = Utf8(MsgSend(MsgSend(app, Sel("executableURL")), Sel("path")));
            var policy = MsgSend_Long(app, Sel("activationPolicy"));
            if (pid <= 0 || string.IsNullOrEmpty(name))
            {
                return;
            }
            _onActivated?.Invoke(new ActivatedApp(pid, name, bundlePath, executablePath, policy == 0));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-focus] activation callback failed: {ex.Message}");
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

    // ── objc runtime P/Invoke ────────────────────────────────────────────────

    private const string Libobjc = "/usr/lib/libobjc.dylib";
    private const string Appkit = "/System/Library/Frameworks/AppKit.framework/AppKit";

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

    // processIdentifier - pid_t (int32)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern int MsgSend_Int(IntPtr receiver, IntPtr sel);

    // activationPolicy - NSInteger
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern long MsgSend_Long(IntPtr receiver, IntPtr sel);

    // addObserver:selector:name:object: - (id, SEL, NSString*, id)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_AddObserver(IntPtr receiver, IntPtr sel, IntPtr observer, IntPtr selector, IntPtr name, IntPtr obj);
}

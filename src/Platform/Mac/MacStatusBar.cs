using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// macOS menu bar status item using NSStatusBar via Objective-C runtime P/Invoke.
/// Template image (bolt-on-chip) adapts to dark/light menu bars. Menu items
/// route back to C# static methods via UnmanagedCallersOnly bridge.
///
/// Must be initialized on the main thread. Call Initialize() once at startup.
/// Call SetVisible(bool) to show/hide based on user preference.
/// </summary>
internal static class MacStatusBar
{
    // ── Callbacks ────────────────────────────────────────────────────────────

    private static Action? _onOpenDashboard;
    private static Action? _onOpenSettings;
    private static Action? _onOpenDevices;
    private static Action? _onQuit;
    // Fired when LaunchServices re-activates this already-running .app (the
    // user double-clicked Nexus.app while it was already running). For
    // LSUIElement=true apps, a re-launch never spawns a new process - macOS
    // just delivers a kAEReopenApplication AppleEvent to the existing
    // instance. We forward that to the dashboard-open path so the user
    // sees a window again, mirroring the Windows tray double-click behavior.
    private static Action? _onReopen;

    // ── Objective-C state ────────────────────────────────────────────────────

    private static IntPtr _statusItem; // NSStatusItem (strong ref)
    private static IntPtr _menu;       // NSMenu (strong ref)
    private static IntPtr _targetObj;  // instance of our dynamic NexusStatusTarget class
    private static bool _initialized;
    private static bool? _visible;

    // SEL / Class pointers cached at init
    private static IntPtr _selAlloc;
    private static IntPtr _selInit;
    private static IntPtr _selSharedApplication;
    private static IntPtr _selSystemStatusBar;
    private static IntPtr _selStatusItemWithLength;
    private static IntPtr _selButton;
    private static IntPtr _selSetImage;
    private static IntPtr _selSetTemplate;
    private static IntPtr _selSetMenu;
    private static IntPtr _selAddItem;
    private static IntPtr _selInitWithTitleActionKey;
    private static IntPtr _selSetTarget;
    private static IntPtr _selSeparatorItem;
    private static IntPtr _selRemoveStatusItem;
    private static IntPtr _selInitWithContentsOfFile;
    private static IntPtr _selRelease;

    private static IntPtr _classNSApplication;
    private static IntPtr _classNSStatusBar;
    private static IntPtr _classNSMenu;
    private static IntPtr _classNSMenuItem;
    private static IntPtr _classNSImage;
    private static IntPtr _classNSString;

    // ── Public API ───────────────────────────────────────────────────────────

    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// Initialize the status bar item. Must be called on the main thread.
    /// Pass the absolute path to the template icon PNG.
    /// </summary>
    public static void Initialize(
        string iconPath,
        Action onOpenDashboard,
        Action onOpenSettings,
        Action onOpenDevices,
        Action onQuit,
        Action? onReopen = null)
    {
        if (_initialized)
            return;
        if (!IsSupported)
            return;

        _onOpenDashboard = onOpenDashboard;
        _onOpenSettings = onOpenSettings;
        _onOpenDevices = onOpenDevices;
        _onQuit = onQuit;
        _onReopen = onReopen ?? onOpenDashboard;

        try
        {
            Console.WriteLine($"[mac-status-bar] init iconPath={iconPath} exists={File.Exists(iconPath)}");
            // Load AppKit FIRST so NS* classes are registered in the Objective-C runtime
            if (NSApplicationLoad() == 0)
                throw new InvalidOperationException("NSApplicationLoad() returned NO");

            CacheSelectors();
            EnsureSharedApp();
            RegisterTargetClass();
            // Call finishLaunching BEFORE CreateStatusItem. On macOS 26 Tahoe
            // NSStatusItem creation before the NSApplication has signalled
            // finishLaunching can silently fail to register with the system
            // menu bar service (FrontBoardServices logs show the scene being
            // created, but the Control Center status-extras catalog never
            // picks it up). Calling finishLaunching early fixes it.
            IntPtr nsApp = MsgSend(_classNSApplication, _selSharedApplication);
            IntPtr selFinishLaunching = SelRegister("finishLaunching");
            MsgSend(nsApp, selFinishLaunching);
            Console.WriteLine("[mac-status-bar] finishLaunching called");
            CreateStatusItem(iconPath);
            RegisterReopenHandler();
            _initialized = true;
            Console.WriteLine("[mac-status-bar] init ok");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-status-bar] init failed: {ex.Message}");
        }
    }

    /// <summary>Show or hide the status item. Safe to call from any thread - dispatches to main thread.</summary>
    public static void SetVisible(bool visible)
    {
        if (!_initialized || !IsSupported)
        {
            Console.WriteLine($"[mac-status-bar] SetVisible({visible}) skipped (initialized={_initialized}, supported={IsSupported})");
            return;
        }
        if (_visible.HasValue && _visible.Value == visible)
        {
            Console.WriteLine($"[mac-status-bar] SetVisible({visible}) no-op (already in that state)");
            return;
        }
        _visible = visible;
        Console.WriteLine($"[mac-status-bar] SetVisible({visible}) scheduling on main thread");

        try
        {
            // AppKit calls must happen on the main thread, so we post via
            // performSelectorOnMainThread:withObject:waitUntilDone:NO on our
            // target object. The selector reads the current _visible field.
            IntPtr sel = SelRegister(visible ? "showStatusItem:" : "hideStatusItem:");
            IntPtr selPerform = SelRegister("performSelectorOnMainThread:withObject:waitUntilDone:");
            MsgSend_Perform(_targetObj, selPerform, sel, IntPtr.Zero, false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-status-bar] setVisible failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Block the calling thread running [NSApp run] to pump AppKit events.
    /// MUST be called on the process main thread after Initialize().
    /// Returns when <see cref="StopRunLoop"/> is called.
    /// </summary>
    public static void RunLoop()
    {
        if (!IsSupported || !_initialized)
            return;
        try
        {
            IntPtr nsApp = MsgSend(_classNSApplication, _selSharedApplication);
            IntPtr selRun = SelRegister("run");
            MsgSend(nsApp, selRun);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-status-bar] RunLoop failed: {ex.Message}");
        }
    }

    /// <summary>Stop the run loop. Safe to call from any thread.</summary>
    public static void StopRunLoop()
    {
        if (!IsSupported)
            return;
        try
        {
            // Prefer [NSApp stop:nil] over CFRunLoopStop - it cleanly exits the NSApp event loop.
            IntPtr nsApp = MsgSend(_classNSApplication, _selSharedApplication);
            IntPtr selStop = SelRegister("stop:");
            MsgSend(nsApp, selStop, IntPtr.Zero);
            // Also wake up the run loop so stop: takes effect immediately.
            IntPtr mainLoop = CFRunLoopGetMain();
            if (mainLoop != IntPtr.Zero)
                CFRunLoopWakeUp(mainLoop);
        }
        catch { /* shutting down */ }
    }

    /// <summary>
    /// Wake the NSApp event loop with a no-op application-defined event.
    /// [NSApp stop:] only latches when the loop dequeues an NSEvent, so a
    /// headless agent (no window, no pointer traffic) can sit parked past a
    /// bare stop: indefinitely. Pair with <see cref="StopRunLoop"/> whenever
    /// the stop originates off the main thread; the Quit menu action needs
    /// neither because it already runs inside an event.
    /// </summary>
    public static void PostRunLoopWakeEvent()
    {
        if (!IsSupported)
            return;
        // Callers are threadpool threads with no autorelease pool; the event
        // factory autoreleases, so scope a pool or the object leaks with a
        // runtime complaint.
        IntPtr pool = IntPtr.Zero;
        try
        {
            pool = objc_autoreleasePoolPush();
            const ulong NSEventTypeApplicationDefined = 15;
            IntPtr eventClass = ClassGetPInvoke("NSEvent");
            IntPtr selOther = SelRegister("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:");
            IntPtr evt = MsgSend_OtherEvent(eventClass, selOther,
                NSEventTypeApplicationDefined, default, 0, 0.0, 0, IntPtr.Zero, 0, 0, 0);
            if (evt != IntPtr.Zero)
            {
                IntPtr nsApp = MsgSend(_classNSApplication, _selSharedApplication);
                IntPtr selPost = SelRegister("postEvent:atStart:");
                MsgSend_PtrBool(nsApp, selPost, evt, true);
            }
        }
        catch { /* shutting down */ }
        finally
        {
            if (pool != IntPtr.Zero)
            {
                try { objc_autoreleasePoolPop(pool); } catch { /* shutting down */ }
            }
        }
    }

    // ── Internal: bootstrap ──────────────────────────────────────────────────

    private static void CacheSelectors()
    {
        _selAlloc = SelRegister("alloc");
        _selInit = SelRegister("init");
        _selSharedApplication = SelRegister("sharedApplication");
        _selSystemStatusBar = SelRegister("systemStatusBar");
        _selStatusItemWithLength = SelRegister("statusItemWithLength:");
        _selButton = SelRegister("button");
        _selSetImage = SelRegister("setImage:");
        _selSetTemplate = SelRegister("setTemplate:");
        _selSetMenu = SelRegister("setMenu:");
        _selAddItem = SelRegister("addItem:");
        _selInitWithTitleActionKey = SelRegister("initWithTitle:action:keyEquivalent:");
        _selSetTarget = SelRegister("setTarget:");
        _selSeparatorItem = SelRegister("separatorItem");
        _selRemoveStatusItem = SelRegister("removeStatusItem:");
        _selInitWithContentsOfFile = SelRegister("initWithContentsOfFile:");
        _selRelease = SelRegister("release");

        _classNSApplication = ClassGet("NSApplication");
        _classNSStatusBar = ClassGet("NSStatusBar");
        _classNSMenu = ClassGet("NSMenu");
        _classNSMenuItem = ClassGet("NSMenuItem");
        _classNSImage = ClassGet("NSImage");
        _classNSString = ClassGet("NSString");
    }

    private static void EnsureSharedApp()
    {
        // Ensure sharedApplication is initialized so status bar works.
        IntPtr nsApp = MsgSend(_classNSApplication, _selSharedApplication);
        if (nsApp == IntPtr.Zero)
        {
            throw new InvalidOperationException("[NSApplication sharedApplication] returned nil");
        }

        // Command-line processes without an Info.plist don't get menu bar
        // access by default. Set activation policy to Accessory (menu-bar only,
        // no Dock icon) so [NSStatusBar systemStatusBar] works.
        // NSApplicationActivationPolicyAccessory = 1
        IntPtr selSetActivationPolicy = SelRegister("setActivationPolicy:");
        MsgSendLong_ret_bool(nsApp, selSetActivationPolicy, 1);
    }

    // ── Dynamic Objective-C target class for menu callbacks ──────────────────
    //
    // An NSObject subclass built at runtime as "NexusStatusTarget", one
    // selector per action. Each method is a static C function
    // (UnmanagedCallersOnly) under the "v@:@" encoding, so the class pair must
    // be registered only after every AddMethod below.

    private static unsafe void RegisterTargetClass()
    {
        IntPtr nsObject = ClassGet("NSObject");
        IntPtr targetClass = objc_allocateClassPair(nsObject, "NexusStatusTarget", IntPtr.Zero);
        if (targetClass == IntPtr.Zero)
        {
            // Class might already exist from a previous init attempt; look it up.
            targetClass = ClassGet("NexusStatusTarget");
            if (targetClass == IntPtr.Zero)
                throw new InvalidOperationException("Failed to allocate target class");
        }
        else
        {
            // First-time registration: add methods.
            // Method signature: void (id self, SEL _cmd, id sender) → "v@:@"
            AddMethod(targetClass, "openDashboard:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OpenDashboardImpl, "v@:@");
            AddMethod(targetClass, "openSettings:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OpenSettingsImpl, "v@:@");
            AddMethod(targetClass, "openDevices:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OpenDevicesImpl, "v@:@");
            AddMethod(targetClass, "quitApp:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&QuitImpl, "v@:@");
            AddMethod(targetClass, "showStatusItem:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ShowStatusItemImpl, "v@:@");
            AddMethod(targetClass, "hideStatusItem:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&HideStatusItemImpl, "v@:@");
            // NSAppleEventManager callback signature is
            //   - (void)handleReopen:(NSAppleEventDescriptor *)event withReplyEvent:(NSAppleEventDescriptor *)reply
            // i.e. (id self, SEL _cmd, id event, id reply) -> "v@:@@"
            AddMethod(targetClass, "reopenApp:withReplyEvent:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&ReopenAppImpl, "v@:@@");
            objc_registerClassPair(targetClass);
        }

        _targetObj = MsgSend(MsgSend(targetClass, _selAlloc), _selInit);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OpenDashboardImpl(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        { _onOpenDashboard?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-status-bar] OpenDashboard callback failed: {ex.Message}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OpenSettingsImpl(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        { _onOpenSettings?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-status-bar] OpenSettings callback failed: {ex.Message}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OpenDevicesImpl(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        { _onOpenDevices?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-status-bar] OpenDevices callback failed: {ex.Message}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void QuitImpl(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        { _onQuit?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-status-bar] Quit callback failed: {ex.Message}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ShowStatusItemImpl(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            IntPtr sel = SelRegister("setVisible:");
            MsgSendVoidBool(_statusItem, sel, true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-status-bar] Show callback failed: {ex.Message}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void HideStatusItemImpl(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            IntPtr sel = SelRegister("setVisible:");
            MsgSendVoidBool(_statusItem, sel, false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-status-bar] Hide callback failed: {ex.Message}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ReopenAppImpl(IntPtr self, IntPtr cmd, IntPtr ev, IntPtr reply)
    {
        try
        {
            Console.WriteLine("[mac-status-bar] reopen event received");
            _onReopen?.Invoke();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-status-bar] Reopen callback failed: {ex.Message}"); }
    }

    // ── Create the actual NSStatusItem ───────────────────────────────────────

    private static void CreateStatusItem(string iconPath)
    {
        // [NSStatusBar systemStatusBar]
        IntPtr statusBar = MsgSend(_classNSStatusBar, _selSystemStatusBar);
        if (statusBar == IntPtr.Zero)
            throw new InvalidOperationException("systemStatusBar nil");

        // Fixed 28.0 instead of NSVariableStatusItemLength (-1). Variable length
        // relies on macOS to size the item based on content; if image loading
        // returns an unusable NSImage, the item becomes zero-width and reads
        // as "not showing". 28 guarantees a clickable rectangle at all times.
        _statusItem = MsgSend_double(statusBar, _selStatusItemWithLength, 28.0);
        if (_statusItem == IntPtr.Zero)
            throw new InvalidOperationException("statusItemWithLength: nil");

        // Get the status item's button
        IntPtr btn = MsgSend(_statusItem, _selButton);

        // Load template image: [[NSImage alloc] initWithContentsOfFile:path]
        // Template images on macOS are rendered black (in dark menu bar) or
        // appropriately masked for light menu bars - adapts automatically.
        if (File.Exists(iconPath))
        {
            IntPtr pathNs = NsString(iconPath);
            IntPtr img = MsgSend(MsgSend(_classNSImage, _selAlloc), _selInitWithContentsOfFile, pathNs);
            if (img != IntPtr.Zero)
            {
                MsgSendVoidBool(img, _selSetTemplate, true);
                if (btn != IntPtr.Zero)
                    MsgSend(btn, _selSetImage, img);
            }
        }

        // Build menu
        _menu = MsgSend(MsgSend(_classNSMenu, _selAlloc), _selInit);
        AddMenuItem(_menu, "Open", "openDashboard:");
        AddMenuItem(_menu, "Settings", "openSettings:");
        AddMenuItem(_menu, "Devices", "openDevices:");
        // Separator
        IntPtr sep = MsgSend(_classNSMenuItem, _selSeparatorItem);
        MsgSend(_menu, _selAddItem, sep);
        AddMenuItem(_menu, "Quit Nexus", "quitApp:");

        MsgSend(_statusItem, _selSetMenu, _menu);

        // Explicitly make the status item visible. NSStatusItem is nominally
        // visible by default when created, but on some macOS sessions the
        // button stays hidden until setVisible:YES is called directly. Do it
        // unconditionally here so the icon always shows on first startup; the
        // caller's SetVisible(false) afterwards will hide it if the user has
        // the toggle disabled.
        IntPtr selSetVisible = SelRegister("setVisible:");
        MsgSendVoidBool(_statusItem, selSetVisible, true);
    }

    private static void AddMenuItem(IntPtr menu, string title, string selectorName)
    {
        IntPtr item = MsgSend(_classNSMenuItem, _selAlloc);
        IntPtr titleNs = NsString(title);
        IntPtr emptyKey = NsString("");
        IntPtr sel = SelRegister(selectorName);
        item = MsgSend(item, _selInitWithTitleActionKey, titleNs, sel, emptyKey);
        MsgSend(item, _selSetTarget, _targetObj);
        MsgSend(menu, _selAddItem, item);
    }

    private static IntPtr NsString(string s)
    {
        // [NSString stringWithUTF8String:s]
        IntPtr sel = SelRegister("stringWithUTF8String:");
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s + '\0');
        unsafe
        {
            fixed (byte* p = utf8)
            {
                return MsgSend(_classNSString, sel, (IntPtr)p);
            }
        }
    }

    // ── NSAppleEventManager: re-launched .app reopen handler ────────────────

    private static void RegisterReopenHandler()
    {
        // Equivalent of:
        //   [[NSAppleEventManager sharedAppleEventManager]
        //       setEventHandler:_targetObj
        //           andSelector:@selector(reopenApp:withReplyEvent:)
        //         forEventClass:kCoreEventClass /* 'aevt' */
        //            andEventID:kAEReopenApplication /* 'rapp' */];
        try
        {
            IntPtr aemClass = ClassGet("NSAppleEventManager");
            if (aemClass == IntPtr.Zero)
            {
                Console.Error.WriteLine("[mac-status-bar] NSAppleEventManager class not found");
                return;
            }
            IntPtr selShared = SelRegister("sharedAppleEventManager");
            IntPtr aem = MsgSend(aemClass, selShared);
            if (aem == IntPtr.Zero)
            {
                Console.Error.WriteLine("[mac-status-bar] sharedAppleEventManager nil");
                return;
            }

            IntPtr selSetHandler = SelRegister("setEventHandler:andSelector:forEventClass:andEventID:");
            IntPtr selReopen = SelRegister("reopenApp:withReplyEvent:");
            // FourCharCode constants from <CoreServices/AppleEvents.h>.
            // 'aevt' = kCoreEventClass, 'rapp' = kAEReopenApplication.
            const uint kCoreEventClass = 0x61657674;
            const uint kAEReopenApplication = 0x72617070;
            MsgSend_SetEventHandler(aem, selSetHandler, _targetObj, selReopen, kCoreEventClass, kAEReopenApplication);
            Console.WriteLine("[mac-status-bar] reopen handler registered");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-status-bar] RegisterReopenHandler failed: {ex.Message}");
        }
    }

    // ── Objective-C runtime P/Invoke ─────────────────────────────────────────

    private const string Libobjc = "/usr/lib/libobjc.dylib";
    private const string Appkit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterPInvoke([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr ClassGetPInvoke([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr extraBytes);

    [DllImport(Libobjc, EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Libobjc, EntryPoint = "class_addMethod")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(Appkit, EntryPoint = "NSApplicationLoad")]
    private static extern byte NSApplicationLoad();

    // objc_msgSend variants - signature matters for ARM64/x64 calling conventions.
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_double(IntPtr receiver, IntPtr sel, double arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidBool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendLong_ret_bool(IntPtr receiver, IntPtr sel, long arg1);

    // performSelectorOnMainThread:withObject:waitUntilDone: - signature is (SEL, id, BOOL)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Perform(IntPtr receiver, IntPtr sel, IntPtr selArg, IntPtr withObject, [MarshalAs(UnmanagedType.I1)] bool waitUntilDone);

    // setEventHandler:andSelector:forEventClass:andEventID: - (id, SEL, AEEventClass, AEEventID)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_SetEventHandler(IntPtr receiver, IntPtr sel, IntPtr handler, IntPtr handlerSelector, uint eventClass, uint eventID);

    // NSEvent otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:
    // (NSUInteger, NSPoint, NSUInteger, NSTimeInterval, NSInteger, id, short, NSInteger, NSInteger)
    [StructLayout(LayoutKind.Sequential)]
    private struct NSPointValue { public double X; public double Y; }

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_OtherEvent(IntPtr receiver, IntPtr sel, ulong type, NSPointValue location, ulong modifierFlags, double timestamp, long windowNumber, IntPtr context, short subtype, long data1, long data2);

    // postEvent:atStart: - (id, BOOL)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_PtrBool(IntPtr receiver, IntPtr sel, IntPtr arg1, [MarshalAs(UnmanagedType.I1)] bool arg2);

    [DllImport(Libobjc)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Libobjc)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    // ── CoreFoundation run loop ──────────────────────────────────────────────

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRunLoopGetMain();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(IntPtr runLoop);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopWakeUp(IntPtr runLoop);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IntPtr SelRegister(string name) => SelRegisterPInvoke(name);
    private static IntPtr ClassGet(string name) => ClassGetPInvoke(name);

    private static void AddMethod(IntPtr cls, string selectorName, IntPtr impPtr, string typeEncoding)
    {
        IntPtr sel = SelRegister(selectorName);
        if (!class_addMethod(cls, sel, impPtr, typeEncoding))
        {
            throw new InvalidOperationException($"class_addMethod failed for {selectorName}");
        }
    }
}

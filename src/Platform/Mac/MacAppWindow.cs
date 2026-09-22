using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// NSWindow + WKWebView host - the macOS native shell. WKWebView ships with
/// macOS, so the .app bundle is self-sufficient and never needs Chrome/Edge.
///
/// Uses the standard macOS titled window so the system draws its native
/// title bar with the "Nexus" caption, traffic-light controls
/// (close / minimize / maximize) at the top-left, and the standard drag /
/// resize affordances. WKWebView fills the content view below.
///
/// AppKit calls are dispatched onto the main thread via the same
/// performSelectorOnMainThread bridge MacStatusBar uses. The class registers
/// its own NSObject subclass for menu / window / navigation callbacks.
/// </summary>
internal static class MacAppWindow
{
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private const string DragViewClassName = "NexusTitlebarDragView";

    // Height of the drag strip, in points. Spans the full top bar height (keep
    // in lockstep with --topbar-height in nexus-web's App.module.scss). The
    // strip runs the full window width; its -hitTest: passes clicks through
    // over the button columns (computed in IsTopBarButtonColumn) so the whole
    // bar drags EXCEPT where a control sits - matching the Windows app-region
    // behaviour.
    private const double TitlebarDragStripHeight = 52;

    // Layout constants mirrored from nexus-web's TopBar.module.scss, used by
    // IsTopBarButtonColumn to carve the control columns out of the drag strip.
    // Keep in lockstep with the CSS.
    private const double TopBarSearchPillWidth = 460;   // --search-w
    private const double TopBarLeftPad = 8;             // .topBar padding-left (0.5rem)
    private const double TopBarRightPad = 8;            // .topBar padding-right (0.5rem)
    private const double TopBarIconButton = 32;         // .iconButton width
    private const double TopBarLeftClusterGap = 2.4;    // .leftCluster gap (0.15rem)
    private const double TopBarGearButton = 34;         // .pageSettingsButton width
    private const double TopBarArrowsGroup = 64;        // two history arrows
    private const double TopBarPillGap = 7;             // arrows -> pill gap (0.4rem)
    private const double TopBarRightCluster = 80;       // "..." menu + profile avatar
    private const double TrafficLightInsetMac = 96;     // --mac-traffic-light-inset
    private const double RailWidthMac = 86;             // --rail-width (collapsed sidebar)

    // Downward nudge (points) from the default light position. Kept small so the
    // lights stay within the native title-bar clip region (they can't reach the
    // center of the taller top bar).
    private const double TrafficLightDrop = 8;

    private static IntPtr _window;
    private static IntPtr _webView;
    private static IntPtr _targetObj;
    private static IntPtr _vibrancyView;
    private static IntPtr _vibrancyTint;

    // Behind-window frosted backdrop. Both themes use the subtle under-window
    // material; behindWindow blending means its tint is translucent over a
    // blurred desktop, which alone reads as washed-out grey in dark mode. Dark
    // mode additionally lays a translucent black tint (_vibrancyTint) over the
    // blur so the glass settles to dark grey while keeping the frost; light mode
    // hides that tint.
    private const long VibrancyMaterial = 21;          // underWindowBackground
    private const double VibrancyDarkTintAlpha = 0.50;
    // Default origins of the three traffic-light buttons, captured once so the
    // padding is applied as an absolute offset (never compounding on re-apply).
    private static bool _trafficLightDefaultsCaptured;
    private static readonly double[] _trafficLightBaseOrigin = new double[6];
    private static bool _classRegistered;
    private static string _pendingUrl = "about:blank";
    private static bool _pendingNavigate = true;
    private static readonly object _sync = new();
    // Guards the one-time class registration so concurrent callers wait
    // until _targetObj is fully assigned before any of them dispatches
    // openOrFocus: against it. An Interlocked.CompareExchange race here
    // would let the second caller see "registered" and proceed with
    // _targetObj == IntPtr.Zero.
    private static readonly object _classRegistrationSync = new();

    /// <summary>
    /// Open the dashboard window, or focus the existing one and navigate to
    /// the given URL. Safe to call from any thread - dispatches onto main.
    /// </summary>
    /// <param name="navigateIfOpen">
    /// On an existing window, true reissues loadRequest: and false only brings
    /// it forward. New windows always navigate to the URL regardless of this flag.
    /// </param>
    public static void OpenOrFocus(string url, bool navigateIfOpen = true)
    {
        if (!IsSupported) return;
        if (string.IsNullOrWhiteSpace(url)) return;

        lock (_sync) { _pendingUrl = url; _pendingNavigate = navigateIfOpen; }

        try
        {
            EnsureClassRegistered();

            // Schedule the actual window work on the AppKit main thread.
            IntPtr selPerform = SelRegister("performSelectorOnMainThread:withObject:waitUntilDone:");
            IntPtr selOpenOrFocus = SelRegister("openOrFocus:");
            MsgSend_Perform(_targetObj, selPerform, selOpenOrFocus, IntPtr.Zero, false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-app-window] OpenOrFocus failed: {ex.Message}");
        }
    }

    private static unsafe void EnsureClassRegistered()
    {
        lock (_classRegistrationSync)
        {
            if (_classRegistered) return;

            // Force WebKit framework to load via dlopen so
            // objc_getClass("WKWebView") resolves. WebKit's ObjC classes
            // don't auto-register until the framework is mapped in.
            const int RTLD_NOW = 2;
            var handle = dlopen("/System/Library/Frameworks/WebKit.framework/WebKit", RTLD_NOW);
            if (handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("[mac-app-window] dlopen WebKit failed");
            }

            IntPtr nsObject = ClassGet("NSObject");
            IntPtr targetClass = objc_allocateClassPair(nsObject, "NexusAppWindowTarget", IntPtr.Zero);
            if (targetClass == IntPtr.Zero)
            {
                // A previous registration attempt already created the class.
                targetClass = ClassGet("NexusAppWindowTarget");
                if (targetClass == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to allocate NexusAppWindowTarget");
            }
            else
            {
                AddMethod(targetClass, "openOrFocus:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OpenOrFocusImpl, "v@:@");
                AddMethod(targetClass, "windowWillClose:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&WindowWillCloseImpl, "v@:@");
                AddMethod(targetClass, "windowDidResize:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&WindowDidResizeImpl, "v@:@");
                // WKScriptMessageHandler callback for the page's request-system-accent.
                // Encoding: void, self, _cmd, id userContentController, id WKScriptMessage.
                AddMethod(targetClass, "userContentController:didReceiveScriptMessage:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidReceiveScriptMessageImpl, "v@:@@");
                // OS accent change (distributed notification) → re-push the accent.
                AddMethod(targetClass, "accentChanged:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&AccentChangedImpl, "v@:@");
                AddMethod(targetClass, "pushAccentNow:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&PushAccentNowImpl, "v@:@");
                // WKUIDelegate: target="_blank" / window.open -> open in the default
                // browser. Encoding: id return, self, _cmd, 4 object args.
                AddMethod(targetClass, "webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&CreateWebViewImpl, "@@:@@@@");
                // WKUIDelegate: <input type=file> -> NSOpenPanel. Without this
                // WKWebView silently discards file-chooser activation. Block
                // must be invoked exactly once; omitting the call permanently
                // wedges the input element in WKWebView.
                AddMethod(targetClass, "webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&RunOpenPanelImpl, "v@:@@@@?");
                objc_registerClassPair(targetClass);
            }

            // Transparent NSView subclass for the top drag strip. The window has
            // no title bar, and the full-size WKWebView swallows mouse events in
            // the title-bar region, so the system drag handle is gone. The
            // subclass overrides -mouseDown: to forward the event to the window's
            // native drag loop (see DragViewMouseDownImpl), reinstating window
            // dragging over the custom chrome.
            IntPtr nsView = ClassGet("NSView");
            IntPtr dragClass = objc_allocateClassPair(nsView, DragViewClassName, IntPtr.Zero);
            if (dragClass != IntPtr.Zero)
            {
                AddMethod(dragClass, "mouseDown:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&DragViewMouseDownImpl, "v@:@");
                // -hitTest: returns nil over a control column (click falls through
                // to the WKWebView button) and self elsewhere (drag). Encoding:
                // id return, self, _cmd, CGPoint by value.
                AddMethod(dragClass, "hitTest:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NSPoint, IntPtr>)&DragViewHitTestImpl, "@@:{CGPoint=dd}");
                objc_registerClassPair(dragClass);
            }
            else
            {
                Console.Error.WriteLine($"[mac-app-window] failed to register {DragViewClassName} - window dragging disabled");
            }

            IntPtr selAlloc = SelRegister("alloc");
            IntPtr selInit = SelRegister("init");
            _targetObj = MsgSend(MsgSend(targetClass, selAlloc), selInit);
            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OpenOrFocusImpl(IntPtr self, IntPtr cmd, IntPtr arg)
    {
        try
        {
            string url;
            bool navigate;
            lock (_sync) { url = _pendingUrl; navigate = _pendingNavigate; }

            // A freshly-created window always needs an initial loadRequest:,
            // otherwise the WKWebView shows about:blank. Skip the reload only
            // when the window already existed and the caller asked us to.
            bool freshlyCreated = _window == IntPtr.Zero;
            if (freshlyCreated)
            {
                CreateWindowOnMain();
            }
            if (freshlyCreated || navigate)
            {
                NavigateToOnMain(url);
            }
            BringToFrontOnMain();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-app-window] OpenOrFocusImpl failed: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void WindowWillCloseImpl(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        // User clicked the close button. Drop the window references so the
        // next OpenOrFocus rebuilds a fresh window instead of trying to
        // resurrect a closed one. The service stays running because
        // setReleasedWhenClosed:NO + the menu-bar agent.
        try
        {
            Console.WriteLine("[mac-app-window] window will close");
            _window = IntPtr.Zero;
            _webView = IntPtr.Zero;
            _vibrancyView = IntPtr.Zero;
            _vibrancyTint = IntPtr.Zero;
            // Drop back to Accessory so the Dock icon disappears - we are
            // back to "menu bar agent only" until the user re-opens the
            // dashboard.
            RestoreAccessoryPolicyOnMain();
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void WindowDidResizeImpl(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        // AppKit re-lays-out the traffic lights across some transitions (e.g.
        // exiting full screen); re-apply the padding so it sticks.
        try { ApplyTrafficLightPadding(); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void DidReceiveScriptMessageImpl(IntPtr self, IntPtr cmd, IntPtr userContentController, IntPtr message)
    {
        try
        {
            string? body = NsStringToString(MsgSend(message, SelRegister("body")));
            if (body == "nexus:request-system-accent")
            {
                PushSystemAccent();
            }
            else if (body == "nexus:theme-dark" || body == "nexus:theme-light")
            {
                // Drive the window appearance from the dashboard's resolved theme
                // (not the OS), so the traffic lights + NSVisualEffectView glass
                // follow light/dark with the in-app setting.
                SetWindowAppearance(body == "nexus:theme-dark");
            }
        }
        catch { }
    }

    private static void SetWindowAppearance(bool dark)
    {
        if (_window == IntPtr.Zero) return;
        IntPtr cls = ClassGet("NSAppearance");
        if (cls == IntPtr.Zero) return;
        IntPtr appearance = MsgSend(cls, SelRegister("appearanceNamed:"),
            NsString(dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"));
        if (appearance != IntPtr.Zero)
            MsgSend(_window, SelRegister("setAppearance:"), appearance);

        // Dark mode lays the translucent black tint over the frosted material so
        // the glass reads dark grey rather than washed-out; light mode hides it.
        if (_vibrancyTint != IntPtr.Zero)
            MsgSendVoidBool(_vibrancyTint, SelRegister("setHidden:"), !dark);

        // Forcing the window appearance cascades to the WKWebView, which would
        // pin its prefers-color-scheme to the in-app theme and blind 'system'
        // mode to OS light/dark flips. Re-pin the web view to the live OS
        // appearance so the page's media query keeps tracking the OS.
        SyncWebViewAppearanceToOs();
    }

    // Pin the WKWebView's appearance to the OS (NSApp.effectiveAppearance) so its
    // prefers-color-scheme follows the system, independent of the window's forced
    // chrome appearance. Refreshed whenever the OS appearance changes so 'system'
    // theme mode sees light/dark flips.
    private static void SyncWebViewAppearanceToOs()
    {
        if (_webView == IntPtr.Zero) return;
        IntPtr nsApp = MsgSend(ClassGet("NSApplication"), SelRegister("sharedApplication"));
        if (nsApp == IntPtr.Zero) return;
        IntPtr osAppearance = MsgSend(nsApp, SelRegister("effectiveAppearance"));
        if (osAppearance != IntPtr.Zero)
            MsgSend(_webView, SelRegister("setAppearance:"), osAppearance);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void AccentChangedImpl(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        // The notification fires a beat before controlAccentColor commits the new
        // value, so reading now returns the PREVIOUS accent. Re-read after a short
        // delay (on the main run loop) so the pushed colour is the new one.
        try
        {
            // This observer also fires on OS light/dark flips (it watches
            // AppleInterfaceThemeChangedNotification). Re-pin the web view to the
            // new OS appearance so 'system' theme mode picks up the change - once
            // now and again after the settle delay (effectiveAppearance, like the
            // accent, can read stale on the notification edge).
            SyncWebViewAppearanceToOs();
            MsgSend_PerformAfter(_targetObj,
                SelRegister("performSelector:withObject:afterDelay:"),
                SelRegister("pushAccentNow:"), IntPtr.Zero, 0.3);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PushAccentNowImpl(IntPtr self, IntPtr cmd, IntPtr arg)
    {
        try { SyncWebViewAppearanceToOs(); PushSystemAccent(); } catch { }
    }

    // WKUIDelegate -webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:
    // Fires for target="_blank" links and window.open. WKWebView has no tabs, so
    // returning nil would silently drop the click; instead hand the URL to the
    // default browser (matches the Windows shell's NewWindowRequested handling).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr CreateWebViewImpl(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr configuration, IntPtr navigationAction, IntPtr windowFeatures)
    {
        try
        {
            IntPtr request = navigationAction == IntPtr.Zero ? IntPtr.Zero : MsgSend(navigationAction, SelRegister("request"));
            IntPtr url = request == IntPtr.Zero ? IntPtr.Zero : MsgSend(request, SelRegister("URL"));
            if (url != IntPtr.Zero)
            {
                string? scheme = NsStringToString(MsgSend(url, SelRegister("scheme")))?.ToLowerInvariant();
                if (scheme == "http" || scheme == "https")
                {
                    IntPtr ws = MsgSend(ClassGet("NSWorkspace"), SelRegister("sharedWorkspace"));
                    if (ws != IntPtr.Zero) MsgSend(ws, SelRegister("openURL:"), url);
                }
            }
        }
        catch { }
        return IntPtr.Zero; // nil: do not create an in-app sub-view
    }

    // WKUIDelegate -webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:
    // Presents an NSOpenPanel for <input type=file>. completionHandler must be
    // called exactly once on every code path; WKWebView permanently wedges the
    // input element if the block is never invoked (or invoked twice).
    // arm64 block ABI: invoke fn ptr sits at byte offset 16 (isa 8 + flags 4 +
    // reserved 4); signature is (block, NSArray*|nil) -> void.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void RunOpenPanelImpl(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr openPanelParams, IntPtr frameInfo, IntPtr completionHandler)
    {
        IntPtr panel = IntPtr.Zero;
        try
        {
            panel = MsgSend(ClassGet("NSOpenPanel"), SelRegister("openPanel"));
            if (panel == IntPtr.Zero)
            {
                InvokeCompletionHandler(completionHandler, IntPtr.Zero);
                return;
            }

            MsgSendVoidBool(panel, SelRegister("setCanChooseFiles:"), true);
            MsgSendVoidBool(panel, SelRegister("setCanChooseDirectories:"), false);

            // Mirror the page's [multiple] attribute so the picker matches what
            // the web author requested.
            bool multi = false;
            if (openPanelParams != IntPtr.Zero)
            {
                multi = MsgSend_RetBool(openPanelParams, SelRegister("allowsMultipleSelection"));
            }

            MsgSendVoidBool(panel, SelRegister("setAllowsMultipleSelection:"), multi);

            // NSModalResponseOK == 1. runModal blocks on the main thread until
            // the user picks or cancels; safe here because AppKit calls this
            // delegate method on the main thread.
            long response = (long)MsgSend(panel, SelRegister("runModal"));
            const long NSModalResponseOK = 1;
            IntPtr urls = response == NSModalResponseOK
                ? MsgSend(panel, SelRegister("URLs"))
                : IntPtr.Zero;

            InvokeCompletionHandler(completionHandler, urls);
        }
        catch
        {
            // Always invoke the block, even on an unexpected fault.
            try { InvokeCompletionHandler(completionHandler, IntPtr.Zero); } catch { }
        }
    }

    // Invoke a WKWebView-supplied completion block with an NSArray* (or nil).
    // arm64 Objective-C block layout: isa (8) + flags (4) + reserved (4) +
    // invoke ptr (8) starting at offset 16.
    private static unsafe void InvokeCompletionHandler(IntPtr block, IntPtr nsArray)
    {
        if (block == IntPtr.Zero) return;
        IntPtr invokePtr = *(IntPtr*)(block + 16);
        if (invokePtr == IntPtr.Zero) return;
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)invokePtr;
        fn(block, nsArray);
    }

    // Push the three system traffic-light buttons in from the top-left corner by
    // a fixed pad. Positions are set absolutely from the captured defaults so
    // repeated calls (resize/full-screen) never compound the offset.
    private static void ApplyTrafficLightPadding()
    {
        if (_window == IntPtr.Zero) return;
        IntPtr selButton = SelRegister("standardWindowButton:");
        IntPtr selFrame = SelRegister("frame");
        IntPtr selSetFrame = SelRegister("setFrame:");

        // Read all three button frames up front (and capture defaults once) so we
        // can center the close..zoom group as a unit.
        var frames = new NSRect[3];
        for (long i = 0; i <= 2; i++)
        {
            IntPtr b = MsgSend(_window, selButton, (IntPtr)i);
            if (b == IntPtr.Zero) return;
            frames[i] = MsgSend_GetRect(b, selFrame);
            if (!_trafficLightDefaultsCaptured)
            {
                _trafficLightBaseOrigin[i * 2] = frames[i].x;
                _trafficLightBaseOrigin[i * 2 + 1] = frames[i].y;
            }
        }
        _trafficLightDefaultsCaptured = true;

        // Horizontal: shift the whole group so the close-left..zoom-right span is
        // centered within the collapsed sidebar rail.
        double groupLeft = _trafficLightBaseOrigin[0];
        double groupRight = _trafficLightBaseOrigin[4] + frames[2].width;
        double shiftX = RailWidthMac / 2.0 - (groupLeft + groupRight) / 2.0;

        for (long i = 0; i <= 2; i++)
        {
            IntPtr button = MsgSend(_window, selButton, (IntPtr)i);
            if (button == IntPtr.Zero) continue;
            NSRect f = frames[i];
            f.x = _trafficLightBaseOrigin[i * 2] + shiftX;
            // y grows upward; nudge down a touch from the default. The lights are
            // clipped to the ~28pt native title bar, so they can't reach the
            // center of the taller (52pt) top bar - this is the lowest they sit
            // while staying fully visible.
            f.y = _trafficLightBaseOrigin[i * 2 + 1] - TrafficLightDrop;
            MsgSend_SetRect(button, selSetFrame, f);
        }
    }

    // -mouseDown: for NexusTitlebarDragView: hand the event straight to the
    // window's native drag loop. performWindowDragWithEvent: is the canonical
    // API for a custom drag region - deterministic and independent of focus or
    // isMovableByWindowBackground (unlike mouseDownCanMoveWindow, which also
    // suppresses the mouseDown the view needs to receive).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void DragViewMouseDownImpl(IntPtr self, IntPtr cmd, IntPtr ev)
    {
        try
        {
            IntPtr window = MsgSend(self, SelRegisterPInvoke("window"));
            if (window != IntPtr.Zero)
                MsgSend(window, SelRegisterPInvoke("performWindowDragWithEvent:"), ev);
        }
        catch { }
    }

    // -hitTest: for NexusTitlebarDragView. The strip spans the full bar width;
    // returning nil over a control column lets the click fall through to the
    // sibling WKWebView (so the button works), while returning self everywhere
    // else makes that pixel start a window drag. `point` arrives in the strip's
    // SUPERVIEW (container) coordinates; the strip is at x=0, so point.x is the
    // x within the bar. Traffic-light clicks never reach here - they live in the
    // title-bar layer above the content view.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr DragViewHitTestImpl(IntPtr self, IntPtr cmd, NSPoint point)
    {
        try
        {
            NSRect frame = MsgSend_GetRect(self, SelRegisterPInvoke("frame"));
            bool inside = point.x >= frame.x && point.x <= frame.x + frame.width
                       && point.y >= frame.y && point.y <= frame.y + frame.height;
            if (!inside) return IntPtr.Zero;
            if (IsTopBarButtonColumn(point.x - frame.x, frame.width))
                return IntPtr.Zero; // fall through to the WKWebView button
            return self;            // drag
        }
        // On any fault, fall through (nil) rather than capturing the click for a
        // drag: a non-draggable strip is far less broken than dead top-bar
        // buttons, since hitTest fires on every event in this band.
        catch { return IntPtr.Zero; }
    }

    // True when x (points from the bar's left edge, bar width w) falls in a
    // control column that must stay clickable: the left cluster (collapse
    // toggle + Focus toggle), the history arrows just left of the centered
    // search pill, the page-settings gear just right of it, or the "..." menu
    // + profile cluster (right). Mirrors the layout in TopBar.module.scss; a
    // small margin pads each column so the whole hit-target clears the drag
    // region. Any control added to the web top bar needs its column carved
    // out here or it is click-dead in the mac shell (the strip wins the hit
    // test and starts a window drag). Internal so the geometry is unit-tested
    // without an NSWindow.
    internal static bool IsTopBarButtonColumn(double x, double w)
    {
        const double m = 4; // safety margin around each column
        double center = w / 2.0;

        // Left cluster: bar left pad + macOS traffic-light inset, then the
        // collapse toggle and the Focus toggle (monitoring page) with the
        // cluster gap between. Carved at its two-button width even where only
        // one renders - a sliver of lost drag area, never a dead button.
        double clusterL = TopBarLeftPad + TrafficLightInsetMac;
        double clusterW = TopBarIconButton * 2 + TopBarLeftClusterGap;
        if (x >= clusterL - m && x <= clusterL + clusterW + m) return true;

        // History arrows: TopBarArrowsGroup wide, a gap left of the pill's left edge.
        double pillLeft = center - TopBarSearchPillWidth / 2.0;
        double arrowsR = pillLeft - TopBarPillGap;
        if (x >= arrowsR - TopBarArrowsGroup - m && x <= arrowsR + m) return true;

        // Search pill: centered, and now interactive (a click opens search). It
        // must fall through to the WKWebView rather than start a window drag, so
        // carve its full width out of the strip.
        if (x >= pillLeft - m && x <= pillLeft + TopBarSearchPillWidth + m) return true;

        // Page-settings cluster (.pageSettings): the gear alone, mirror of the
        // history arrows, a gap right of the pill. Only Monitoring registers a
        // settings action, so the cluster is absent on every other page.
        double pillRight = center + TopBarSearchPillWidth / 2.0;
        double gearL = pillRight + TopBarPillGap;
        if (x >= gearL - m && x <= gearL + TopBarGearButton + m) return true;

        // Right cluster: against the right edge.
        if (x >= w - TopBarRightCluster - TopBarRightPad - m && x <= w - TopBarRightPad + m) return true;

        return false;
    }

    private static void CreateWindowOnMain()
    {
        IntPtr classNSWindow = ClassGet("NSWindow");
        IntPtr classWKWebView = ClassGet("WKWebView");
        IntPtr classWKConfig = ClassGet("WKWebViewConfiguration");
        if (classNSWindow == IntPtr.Zero || classWKWebView == IntPtr.Zero || classWKConfig == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[mac-app-window] missing class: NSWindow={classNSWindow:x} WKWebView={classWKWebView:x} WKConfig={classWKConfig:x}");
            return;
        }

        IntPtr selAlloc = SelRegister("alloc");
        IntPtr selInit = SelRegister("init");

        // ── NSWindow ────────────────────────────────────────────────────────
        // Titled window with a full-size content view: the WKWebView fills the
        // whole window (flush to the top edge) and the title bar is made
        // transparent with no caption text below, so the page + sidebar
        // backgrounds paint up behind the system traffic-light controls
        // (close / minimize / zoom) that stay at the top-left. This is the
        // macOS-native equivalent of the Windows shell's borderless top strip:
        // the title bar region remains a system drag handle for free, no custom
        // drag strip required.
        const ulong NSWindowStyleMaskTitled = 1UL << 0;
        const ulong NSWindowStyleMaskClosable = 1UL << 1;
        const ulong NSWindowStyleMaskMiniaturizable = 1UL << 2;
        const ulong NSWindowStyleMaskResizable = 1UL << 3;
        const ulong NSWindowStyleMaskFullSizeContentView = 1UL << 15;
        const ulong styleMask = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
            | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable
            | NSWindowStyleMaskFullSizeContentView;
        const ulong NSBackingStoreBuffered = 2;

        var contentRect = new NSRect { x = 200, y = 200, width = 1280, height = 820 };

        IntPtr win = MsgSend(classNSWindow, selAlloc);
        win = MsgSend_InitWindow(
            win,
            SelRegister("initWithContentRect:styleMask:backing:defer:"),
            contentRect, styleMask, NSBackingStoreBuffered, false);

        MsgSend(win, SelRegister("setTitle:"), NsString("Nexus"));
        // Hide the caption text and let the content show through the title bar
        // so there's no visible bar - only the traffic lights remain. Keep the
        // title set above (used by Mission Control / the Window menu).
        const long NSWindowTitleHidden = 1;
        MsgSendVoidBool(win, SelRegister("setTitlebarAppearsTransparent:"), true);
        MsgSendVoidLong(win, SelRegister("setTitleVisibility:"), NSWindowTitleHidden);
        // Default setReleasedWhenClosed:YES is what we want - when the user
        // closes the window via the red traffic light, AppKit deallocates
        // the NSWindow (and its content view chain, including the
        // WKWebView), freeing the WebContent renderer process. Our
        // windowWillClose: delegate nils _window before that happens so
        // the next OpenOrFocus rebuilds fresh.
        MsgSend(win, SelRegister("setDelegate:"), _targetObj);

        // ── WKWebView ───────────────────────────────────────────────────────
        IntPtr config = MsgSend(MsgSend(classWKConfig, selAlloc), selInit);

        // Tag the page so nexus-web's isMacAppShell() applies the titlebar-inset
        // layout (sidebar brand + top chrome pushed below the traffic lights,
        // backgrounds flush to the top). Mirrors the Windows shell injecting
        // window.nexusShellPlatform = 'windows-app'.
        InjectMacShellMarker(config);

        // Web->host channel (webkit.messageHandlers.nexusHost) for the page's
        // request-system-accent. _targetObj implements
        // userContentController:didReceiveScriptMessage:.
        IntPtr ucc = MsgSend(config, SelRegister("userContentController"));
        if (ucc != IntPtr.Zero)
            MsgSend(ucc, SelRegister("addScriptMessageHandler:name:"), _targetObj, NsString("nexusHost"));

        // Initial frame matches the window content area; autoresizing keeps
        // it filling on resize.
        var webFrame = new NSRect { x = 0, y = 0, width = contentRect.width, height = contentRect.height };
        IntPtr webView = MsgSend(classWKWebView, selAlloc);
        webView = MsgSend_InitWebView(
            webView,
            SelRegister("initWithFrame:configuration:"),
            webFrame, config);

        // NSViewWidthSizable (2) | NSViewHeightSizable (16) = 18
        MsgSendVoidLong(webView, SelRegister("setAutoresizingMask:"), 18);

        // Route target="_blank" / window.open to the default browser (the web
        // view has no tabs/sub-windows). _targetObj implements the WKUIDelegate
        // createWebView callback.
        MsgSend(webView, SelRegister("setUIDelegate:"), _targetObj);

        EnableWebInspector(webView);

        // ── Container + drag strip ──────────────────────────────────────────
        // The window's content view is a plain NSView holding the WKWebView
        // plus a transparent drag strip pinned to the top. The strip restores
        // window dragging that the full-size WKWebView would otherwise swallow.
        // Traffic lights live in the title-bar layer above this view, so they
        // stay clickable over the strip.
        IntPtr classNSView = ClassGet("NSView");
        var containerFrame = new NSRect { x = 0, y = 0, width = contentRect.width, height = contentRect.height };
        IntPtr container = MsgSend(classNSView, selAlloc);
        container = MsgSend_InitFrame(container, SelRegister("initWithFrame:"), containerFrame);

        // Native frosted-glass backdrop: a behind-window NSVisualEffectView shows
        // the real desktop + windows behind, blurred by the window server - zero
        // lag. The window goes non-opaque and the WKWebView transparent so the
        // page's "glass" backdrop reveals it; the flat / gradient modes paint an
        // opaque --backdrop-base over it instead.
        MsgSendVoidBool(win, SelRegister("setOpaque:"), false);
        MsgSend(win, SelRegister("setBackgroundColor:"), MsgSend(ClassGet("NSColor"), SelRegister("clearColor")));
        InstallVibrancyBackdrop(container, containerFrame);
        // WKWebView transparent (KVC drawsBackground is the established knob).
        IntPtr noNumber = MsgSendRetBool(ClassGet("NSNumber"), SelRegister("numberWithBool:"), false);
        MsgSend(webView, SelRegister("setValue:forKey:"), noNumber, NsString("drawsBackground"));

        MsgSend(container, SelRegister("addSubview:"), webView);

        // Drag strip: spans the full window width along the top, full top-bar
        // height. Its -hitTest: passes clicks through over the control columns
        // so the whole bar drags except where a button sits. Non-flipped coords,
        // so y = height - stripHeight. NSViewWidthSizable (2) | NSViewMinYMargin
        // (8) = 10 keeps it full-width and pinned to the top on resize.
        var stripFrame = new NSRect
        {
            x = 0,
            y = contentRect.height - TitlebarDragStripHeight,
            width = contentRect.width,
            height = TitlebarDragStripHeight,
        };
        IntPtr dragView = MsgSend(ClassGet(DragViewClassName), selAlloc);
        dragView = MsgSend_InitFrame(dragView, SelRegister("initWithFrame:"), stripFrame);
        MsgSendVoidLong(dragView, SelRegister("setAutoresizingMask:"), 10);
        // Layer-back the strip so it composites and hit-tests above the
        // layer-backed WKWebView sibling (mixing layer-backed and non-layer-
        // backed siblings otherwise lets the web view win the top pixels).
        MsgSendVoidBool(dragView, SelRegister("setWantsLayer:"), true);
        MsgSend(container, SelRegister("addSubview:"), dragView);

        MsgSend(win, SelRegister("setContentView:"), container);

        // Drop our +1 retains from alloc/init. The container is retained by the
        // window's contentView property; the WKWebView + drag strip are retained
        // by the container's subviews array; the WKWebView holds its
        // configuration. Without these releases the refcount never reaches zero
        // on close, leaking Cocoa objects per open/close cycle (WKWebView's
        // WebContent process is the heavy one, ~150 MB).
        IntPtr selRelease = SelRegister("release");
        MsgSend(webView, selRelease);
        MsgSend(dragView, selRelease);
        MsgSend(container, selRelease);
        MsgSend(config, selRelease);

        // Center on screen.
        MsgSend(win, SelRegister("center"));

        _window = win;
        _webView = webView;

        // Inset the traffic lights now that the window (and its buttons) exist.
        ApplyTrafficLightPadding();

        // Re-read the OS accent the instant the user changes it in System
        // Settings (event-driven distributed notification, no poll).
        RegisterAccentObserver();
    }

    private static void NavigateToOnMain(string url)
    {
        if (_webView == IntPtr.Zero) return;

        IntPtr classNSURL = ClassGet("NSURL");
        IntPtr classNSURLRequest = ClassGet("NSURLRequest");

        IntPtr nsUrlString = NsString(url);
        IntPtr nsUrl = MsgSend(classNSURL, SelRegister("URLWithString:"), nsUrlString);
        if (nsUrl == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[mac-app-window] NSURL URLWithString failed: {url}");
            return;
        }

        IntPtr nsRequest = MsgSend(classNSURLRequest, SelRegister("requestWithURL:"), nsUrl);
        if (nsRequest == IntPtr.Zero) return;

        MsgSend(_webView, SelRegister("loadRequest:"), nsRequest);
    }

    private static bool _mainMenuSet;

    // Build a standard application menu bar (App + Edit) and install it. Console-
    // style apps ship without a MainMenu.nib, so without this the menu bar is
    // empty (no About, and no working Cmd+C/V in WKWebView text fields). The app
    // menu's title is taken from CFBundleName ("Nexus") by AppKit; the item
    // titles we set explicitly. Built once per process.
    private static void SetupMainMenuOnMain()
    {
        if (_mainMenuSet) return;
        try
        {
            IntPtr nsApp = MsgSend(ClassGet("NSApplication"), SelRegister("sharedApplication"));
            if (nsApp == IntPtr.Zero) return;
            IntPtr menuClass = ClassGet("NSMenu");
            IntPtr itemClass = ClassGet("NSMenuItem");
            IntPtr alloc = SelRegister("alloc");
            IntPtr init = SelRegister("init");
            IntPtr addItem = SelRegister("addItem:");
            IntPtr setSubmenu = SelRegister("setSubmenu:");
            IntPtr initItem = SelRegister("initWithTitle:action:keyEquivalent:");
            IntPtr separator = MsgSend(itemClass, SelRegister("separatorItem"));

            IntPtr initMenuTitle = SelRegister("initWithTitle:");

            IntPtr Item(string title, string? sel, string key)
            {
                IntPtr it = MsgSend(itemClass, alloc);
                return MsgSend(it, initItem, NsString(title),
                    sel == null ? IntPtr.Zero : SelRegister(sel), NsString(key));
            }
            // Adds a top-level menu: the bar shows the holder item's title (AppKit
            // overrides the FIRST one with the bundle name "Nexus").
            void Submenu(IntPtr parent, string title, IntPtr[] items)
            {
                IntPtr holder = Item(title, null, "");
                MsgSend(parent, addItem, holder);
                IntPtr sub = MsgSend(MsgSend(menuClass, alloc), initMenuTitle, NsString(title));
                foreach (var it in items) MsgSend(sub, addItem, it);
                MsgSend(holder, setSubmenu, sub);
            }

            IntPtr mainMenu = MsgSend(MsgSend(menuClass, alloc), init);

            // App menu (shown as "Nexus"): About + Quit.
            Submenu(mainMenu, "Nexus", new[]
            {
                Item("About Nexus", "orderFrontStandardAboutPanel:", ""),
                separator,
                Item("Quit Nexus", "terminate:", "q"),
            });

            // Edit menu so standard editing shortcuts work in the web UI.
            Submenu(mainMenu, "Edit", new[]
            {
                Item("Undo", "undo:", "z"),
                Item("Redo", "redo:", "Z"),
                MsgSend(itemClass, SelRegister("separatorItem")),
                Item("Cut", "cut:", "x"),
                Item("Copy", "copy:", "c"),
                Item("Paste", "paste:", "v"),
                Item("Select All", "selectAll:", "a"),
            });

            MsgSend(nsApp, SelRegister("setMainMenu:"), mainMenu);
            _mainMenuSet = true;
        }
        catch { }
    }

    private static void BringToFrontOnMain()
    {
        if (_window == IntPtr.Zero) return;
        SetupMainMenuOnMain();

        IntPtr classNSApp = ClassGet("NSApplication");
        IntPtr nsApp = MsgSend(classNSApp, SelRegister("sharedApplication"));

        // Switch to Regular activation policy so the window can become
        // frontmost. LSUIElement / Accessory mode hides the Dock icon but
        // also prevents the agent from stealing focus, so a freshly-opened
        // NSWindow stays buried behind the user's current app. We bump up
        // to Regular (0) here, activate, then drop back to Accessory (1)
        // when the window closes (windowWillClose:) so the Dock icon
        // disappears again - a Slack/Discord-style "menu bar agent that
        // also has a real window" pattern.
        const long NSApplicationActivationPolicyRegular = 0;
        MsgSendLong_ret_bool(nsApp, SelRegister("setActivationPolicy:"), NSApplicationActivationPolicyRegular);

        MsgSendVoidBool(nsApp, SelRegister("activateIgnoringOtherApps:"), true);
        MsgSend(_window, SelRegister("makeKeyAndOrderFront:"), IntPtr.Zero);
    }

    private static void RestoreAccessoryPolicyOnMain()
    {
        try
        {
            IntPtr classNSApp = ClassGet("NSApplication");
            IntPtr nsApp = MsgSend(classNSApp, SelRegister("sharedApplication"));
            const long NSApplicationActivationPolicyAccessory = 1;
            MsgSendLong_ret_bool(nsApp, SelRegister("setActivationPolicy:"), NSApplicationActivationPolicyAccessory);
        }
        catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Enable Safari Web Inspector on the dashboard web view (right-click ->
    // "Inspect Element"), matching the overlay helper's widget-overlay and
    // panel-kiosk web views. -setInspectable: needs macOS 13.3; below that the
    // knob is the private _developerExtrasEnabled KVC key on the WEB VIEW, not
    // on the configuration - a WKWebViewConfiguration is copied at init, so a
    // post-init write to its preferences is silently dropped. Same receiver and
    // key the Swift helper uses (overlay-helper/main.swift).
    private static void EnableWebInspector(IntPtr webView)
    {
        IntPtr selInspectable = SelRegister("setInspectable:");
        if (MsgSendPtr_RetBool(webView, SelRegister("respondsToSelector:"), selInspectable))
        {
            MsgSendVoidBool(webView, selInspectable, true);
            return;
        }
        // No try/catch: an ObjC exception from setValue:forKey: aborts the
        // process rather than unwinding into managed code, so a catch here
        // would be theatre. respondsToSelector: above is the real guard.
        IntPtr yes = MsgSendRetBool(ClassGet("NSNumber"), SelRegister("numberWithBool:"), true);
        MsgSend(webView, SelRegister("setValue:forKey:"), yes, NsString("_developerExtrasEnabled"));
    }

    // Add a document-start user script that defines window.nexusShellPlatform
    // before any page script runs, so nexus-web can branch its layout the same
    // way it does for the Windows WebView2 shell.
    private static void InjectMacShellMarker(IntPtr config)
    {
        IntPtr classWKUserScript = ClassGet("WKUserScript");
        if (classWKUserScript == IntPtr.Zero) return;
        IntPtr userContentController = MsgSend(config, SelRegister("userContentController"));
        if (userContentController == IntPtr.Zero) return;

        IntPtr source = NsString(
            "Object.defineProperty(window,'nexusShellPlatform',{value:'mac-app',writable:false,configurable:false});");
        const long WKUserScriptInjectionTimeAtDocumentStart = 0;
        IntPtr script = MsgSend(classWKUserScript, SelRegister("alloc"));
        script = MsgSend_InitUserScript(
            script,
            SelRegister("initWithSource:injectionTime:forMainFrameOnly:"),
            source, WKUserScriptInjectionTimeAtDocumentStart, true);
        MsgSend(userContentController, SelRegister("addUserScript:"), script);
        MsgSend(script, SelRegister("release"));
    }

    // Behind-window frosted-glass view filling the window, added behind the
    // (transparent) WKWebView so the real desktop shows through, blurred.
    private static void InstallVibrancyBackdrop(IntPtr container, NSRect frame)
    {
        IntPtr cls = ClassGet("NSVisualEffectView");
        if (cls == IntPtr.Zero) return;
        IntPtr vev = MsgSend(cls, SelRegister("alloc"));
        vev = MsgSend_InitFrame(vev, SelRegister("initWithFrame:"), frame);
        const long BlendingModeBehindWindow = 0;
        // Follow the window's active state: full blur when the window is key,
        // the muted/flat inactive material when it loses focus (native macOS
        // behaviour, handled by the WindowServer at no cost to us).
        const long StateFollowsWindowActiveState = 0;
        const long ViewWidthHeightSizable = 18;
        MsgSendVoidLong(vev, SelRegister("setMaterial:"), VibrancyMaterial);
        MsgSendVoidLong(vev, SelRegister("setBlendingMode:"), BlendingModeBehindWindow);
        MsgSendVoidLong(vev, SelRegister("setState:"), StateFollowsWindowActiveState);
        MsgSendVoidLong(vev, SelRegister("setAutoresizingMask:"), ViewWidthHeightSizable);
        MsgSend(container, SelRegister("addSubview:"), vev);

        // Dark-mode tint: a translucent pure-black NSView layered over the blur so
        // the glass reads dark grey instead of the washed-out grey the bare
        // material gives. Lives as a subview of the effect view (above the blur,
        // below the transparent WKWebView). Hidden by default; SetWindowAppearance
        // unhides it in dark mode. It dims the blur rather than replacing it.
        IntPtr tint = MsgSend(ClassGet("NSView"), SelRegister("alloc"));
        tint = MsgSend_InitFrame(tint, SelRegister("initWithFrame:"), frame);
        MsgSendVoidBool(tint, SelRegister("setWantsLayer:"), true);
        IntPtr tintLayer = MsgSend(tint, SelRegister("layer"));
        IntPtr cg = CGColorCreateGenericGray(0.0, VibrancyDarkTintAlpha);
        if (tintLayer != IntPtr.Zero && cg != IntPtr.Zero)
            MsgSend(tintLayer, SelRegister("setBackgroundColor:"), cg);
        if (cg != IntPtr.Zero) CGColorRelease(cg);
        MsgSendVoidLong(tint, SelRegister("setAutoresizingMask:"), ViewWidthHeightSizable);
        MsgSendVoidBool(tint, SelRegister("setHidden:"), true);
        MsgSend(vev, SelRegister("addSubview:"), tint);

        // Process-lifetime references so SetWindowAppearance can toggle the tint
        // per theme. The view hierarchy retains both (effect view -> container ->
        // window contentView), so the pointers stay valid; drop our +1 allocs.
        _vibrancyView = vev;
        _vibrancyTint = tint;
        MsgSend(tint, SelRegister("release"));
        MsgSend(vev, SelRegister("release"));
    }

    // Dispatch a host->page message event (the page listens via window 'message').
    private static void DispatchHostMessage(string dataObjectLiteral)
    {
        if (_webView == IntPtr.Zero) return;
        string js = "window.dispatchEvent(new MessageEvent('message',{data:" + dataObjectLiteral + "}))";
        MsgSend(_webView, SelRegister("evaluateJavaScript:completionHandler:"), NsString(js), IntPtr.Zero);
    }

    // Read the live OS accent (NSColor.controlAccentColor → sRGB) and push it as
    // #RRGGBB; SystemAccentSync mirrors it into the theme when accentSource='system'.
    private static void PushSystemAccent()
    {
        var hex = ReadAccentHex();
        if (hex != null) DispatchHostMessage("{type:'nexus:system-accent',hex:'" + hex + "'}");
    }

    private static unsafe string? ReadAccentHex()
    {
        try
        {
            IntPtr nsColor = ClassGet("NSColor");
            if (nsColor == IntPtr.Zero) return null;
            IntPtr accent = MsgSend(nsColor, SelRegister("controlAccentColor"));
            if (accent == IntPtr.Zero) return null;
            // Resolve the dynamic colour into sRGB so getRed:green:blue: is valid.
            IntPtr srgb = MsgSend(ClassGet("NSColorSpace"), SelRegister("sRGBColorSpace"));
            IntPtr c = srgb == IntPtr.Zero ? accent : MsgSend(accent, SelRegister("colorUsingColorSpace:"), srgb);
            if (c == IntPtr.Zero) c = accent;
            double r = 0, g = 0, b = 0, a = 0;
            MsgSend4(c, SelRegister("getRed:green:blue:alpha:"), (IntPtr)(&r), (IntPtr)(&g), (IntPtr)(&b), (IntPtr)(&a));
            int ri = Math.Clamp((int)Math.Round(r * 255), 0, 255);
            int gi = Math.Clamp((int)Math.Round(g * 255), 0, 255);
            int bi = Math.Clamp((int)Math.Round(b * 255), 0, 255);
            return "#" + ri.ToString("X2") + gi.ToString("X2") + bi.ToString("X2");
        }
        catch { return null; }
    }

    private static bool _accentObserverRegistered;

    // Observe OS accent/appearance changes (distributed notifications, delivered
    // on the main thread) and re-push the accent. Registered once per process.
    private static void RegisterAccentObserver()
    {
        if (_accentObserverRegistered) return;
        try
        {
            IntPtr dnc = MsgSend(ClassGet("NSDistributedNotificationCenter"), SelRegister("defaultCenter"));
            if (dnc == IntPtr.Zero) return;
            IntPtr sel = SelRegister("accentChanged:");
            IntPtr addObs = SelRegister("addObserver:selector:name:object:");
            foreach (var name in new[]
            {
                "AppleColorPreferencesChangedNotification",
                "AppleAquaColorVariantChanged",
                "AppleInterfaceThemeChangedNotification",
            })
            {
                MsgSend4(dnc, addObs, _targetObj, sel, NsString(name), IntPtr.Zero);
            }
            _accentObserverRegistered = true;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac-app-window] accent observer failed: {ex.Message}"); }
    }

    private static string? NsStringToString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        IntPtr utf8 = MsgSend(nsString, SelRegister("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static IntPtr NsString(string s)
    {
        IntPtr classNSString = ClassGet("NSString");
        IntPtr sel = SelRegister("stringWithUTF8String:");
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s + '\0');
        unsafe
        {
            fixed (byte* p = utf8)
            {
                return MsgSend(classNSString, sel, (IntPtr)p);
            }
        }
    }

    private static void AddMethod(IntPtr cls, string selectorName, IntPtr impPtr, string typeEncoding)
    {
        IntPtr sel = SelRegister(selectorName);
        if (!class_addMethod(cls, sel, impPtr, typeEncoding))
        {
            throw new InvalidOperationException($"class_addMethod failed for {selectorName}");
        }
    }

    private static IntPtr SelRegister(string name) => SelRegisterPInvoke(name);
    private static IntPtr ClassGet(string name) => ClassGetPInvoke(name);

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private const string Libobjc = "/usr/lib/libobjc.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string WebKit = "/System/Library/Frameworks/WebKit.framework/WebKit";

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double x, y, width, height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double x, y; }

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

    [DllImport(AppKit, EntryPoint = "NSApplicationLoad")]
    private static extern byte NSApplicationLoad();

    [DllImport("libSystem.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPStr)] string path, int mode);

    // Translucent grey CGColor for the dark-mode vibrancy tint layer (gray 0 =
    // black). Create-rule: caller owns the +1, released after the layer retains.
    [DllImport(CoreGraphics, EntryPoint = "CGColorCreateGenericGray")]
    private static extern IntPtr CGColorCreateGenericGray(double gray, double alpha);

    [DllImport(CoreGraphics, EntryPoint = "CGColorRelease")]
    private static extern void CGColorRelease(IntPtr color);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    // 4 pointer-sized args: addObserver:selector:name:object: and
    // getRed:green:blue:alpha: (CGFloat* out-params).
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend4(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);

    // +numberWithBool: -> NSNumber (BOOL in w2 on arm64).
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendRetBool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool arg1);

    // No-arg selector that returns BOOL (e.g. -allowsMultipleSelection).
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSend_RetBool(IntPtr receiver, IntPtr sel);

    // performSelector:withObject:afterDelay: -> (SEL, id, NSTimeInterval=double).
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_PerformAfter(IntPtr receiver, IntPtr sel, IntPtr selArg, IntPtr obj, double delay);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidBool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool arg1);

    // respondsToSelector: -> (SEL) returning BOOL.
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendPtr_RetBool(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidLong(IntPtr receiver, IntPtr sel, long arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendLong_ret_bool(IntPtr receiver, IntPtr sel, long arg1);

    // initWithContentRect:styleMask:backing:defer: -> (NSRect, NSUInteger, NSUInteger, BOOL)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_InitWindow(IntPtr receiver, IntPtr sel, NSRect rect, ulong styleMask, ulong backing, [MarshalAs(UnmanagedType.I1)] bool defer);

    // initWithFrame:configuration: -> (NSRect, id)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_InitWebView(IntPtr receiver, IntPtr sel, NSRect frame, IntPtr config);

    // initWithFrame: -> (NSRect)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_InitFrame(IntPtr receiver, IntPtr sel, NSRect frame);

    // -frame -> NSRect (returned in d0-d3 as a 4-double HFA on arm64).
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern NSRect MsgSend_GetRect(IntPtr receiver, IntPtr sel);

    // -setFrame: -> (NSRect)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_SetRect(IntPtr receiver, IntPtr sel, NSRect frame);

    // initWithSource:injectionTime:forMainFrameOnly: -> (id, NSInteger, BOOL)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_InitUserScript(IntPtr receiver, IntPtr sel, IntPtr source, long injectionTime, [MarshalAs(UnmanagedType.I1)] bool forMainFrameOnly);

    // performSelectorOnMainThread:withObject:waitUntilDone:
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Perform(IntPtr receiver, IntPtr sel, IntPtr selArg, IntPtr withObject, [MarshalAs(UnmanagedType.I1)] bool waitUntilDone);
}

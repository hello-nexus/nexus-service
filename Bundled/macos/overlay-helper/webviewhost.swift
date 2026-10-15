// Native host for the panel's Web View widget on a macOS kiosk window.
// Protocol: the panel page posts hello/sync/command to the nexusWebView
// handler; replies go back as 'nexus-webview' CustomEvents.

import AppKit
import WebKit

/// Kiosk window. Key status is granted only while an interactive widget view is shown.
final class KioskPanel: NSPanel {
    var allowKey = false
    var widgetHost: WebViewWidgetHost?
    override var canBecomeKey: Bool { allowKey }
    override var canBecomeMain: Bool { false }

    func takeKey() {
        guard allowKey, !isKeyWindow else { return }
        makeKey()
    }

    func releaseKey() {
        allowKey = false
        if isKeyWindow {
            makeFirstResponder(nil)
            resignKey()
        }
    }
}

/// Flipped so subview frames use the panel page's top-left origin. Only
/// interactive widget views take hits; everything else reaches the panel webview below.
final class WidgetContainerView: NSView {
    override var isFlipped: Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = convert(point, from: superview)
        for sub in subviews.reversed() {
            guard let web = sub as? WidgetWebView, web.interactive, !web.isHidden,
                  web.frame.contains(local)
            else { continue }
            return web.hitTest(local) ?? web
        }
        return nil
    }
}

final class WidgetWebView: WKWebView {
    var interactive = false
    weak var panel: KioskPanel?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        if interactive { panel?.takeKey() }
        super.mouseDown(with: event)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        interactive ? super.menu(for: event) : nil
    }
}

private final class LiveWidgetView {
    let webView: WidgetWebView
    let delegate: WidgetViewDelegate
    var configuredURL: String
    var state = "loading"
    var backObservation: NSKeyValueObservation?

    init(webView: WidgetWebView, delegate: WidgetViewDelegate, configuredURL: String) {
        self.webView = webView
        self.delegate = delegate
        self.configuredURL = configuredURL
    }
}

/// Navigation and UI policy of one widget view. Widget views have no script bridge.
final class WidgetViewDelegate: NSObject, WKNavigationDelegate, WKUIDelegate {
    weak var host: WebViewWidgetHost?
    let id: String

    init(host: WebViewWidgetHost, id: String) {
        self.host = host
        self.id = id
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if navigationAction.shouldPerformDownload {
            decisionHandler(.cancel)
            return
        }
        let scheme = navigationAction.request.url?.scheme?.lowercased() ?? ""
        let topLevel = navigationAction.targetFrame?.isMainFrame ?? true
        let ok = scheme == "http" || scheme == "https" || (!topLevel && scheme == "about")
        decisionHandler(ok ? .allow : .cancel)
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse,
                 decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        let disposition = (navigationResponse.response as? HTTPURLResponse)?
            .value(forHTTPHeaderField: "Content-Disposition")?.lowercased() ?? ""
        if !navigationResponse.canShowMIMEType || disposition.hasPrefix("attachment") {
            decisionHandler(.cancel)
            return
        }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
        host?.viewState(id, "loading", nil)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        host?.viewState(id, "loaded", nil)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        report(error)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        report(error)
    }

    private func report(_ error: Error) {
        let e = error as NSError
        // Cancelled loads and policy-blocked navigations are not page failures.
        if e.domain == NSURLErrorDomain && e.code == NSURLErrorCancelled { return }
        if e.domain == "WebKitErrorDomain" && e.code == 102 { return }
        host?.viewState(id, "error", String(e.code))
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        host?.viewCrashed(id)
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        let scheme = navigationAction.request.url?.scheme?.lowercased() ?? ""
        if scheme == "http" || scheme == "https" { webView.load(navigationAction.request) }
        return nil
    }

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        completionHandler()
    }

    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        completionHandler(false)
    }

    func webView(_ webView: WKWebView, runJavaScriptTextInputPanelWithPrompt prompt: String,
                 defaultText: String?, initiatedByFrame frame: WKFrameInfo,
                 completionHandler: @escaping (String?) -> Void) {
        completionHandler(nil)
    }

    func webView(_ webView: WKWebView, runOpenPanelWith parameters: WKOpenPanelParameters,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping ([URL]?) -> Void) {
        completionHandler(nil)
    }

    @available(macOS 12.0, *)
    func webView(_ webView: WKWebView, requestMediaCapturePermissionFor origin: WKSecurityOrigin,
                 initiatedByFrame frame: WKFrameInfo, type: WKMediaCaptureType,
                 decisionHandler: @escaping (WKPermissionDecision) -> Void) {
        decisionHandler(.deny)
    }
}

/// Main webview delegate of a kiosk window: any main-frame load or content
/// process loss drops every widget view, then defers to the shared recovery.
final class KioskNavigationDelegate: NSObject, WKNavigationDelegate {
    private let inner: OverlayWebViewDelegate
    weak var host: WebViewWidgetHost?

    init(inner: OverlayWebViewDelegate) { self.inner = inner }

    func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
        host?.mainWebViewLost()
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        host?.mainWebViewLost()
        inner.webViewWebContentProcessDidTerminate(webView)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        inner.webView(webView, didFailProvisionalNavigation: navigation, withError: error)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        inner.webView(webView, didFail: navigation, withError: error)
    }
}

final class WebViewWidgetHost: NSObject, WKScriptMessageHandler {
    static let handlerName = "nexusWebView"
    private static let profileId = UUID(uuidString: "6E5B7C1A-3D42-4F0B-9C58-1A7D2E4B8F03")!

    /// macOS 13 has no identified persistent store; its fallback is non-persistent, never the default store.
    private static let dataStore: WKWebsiteDataStore = {
        if #available(macOS 14.0, *) {
            return WKWebsiteDataStore(forIdentifier: profileId)
        }
        return WKWebsiteDataStore.nonPersistent()
    }()

    let container = WidgetContainerView()
    let navigationDelegate: KioskNavigationDelegate
    private weak var mainWebView: WKWebView?
    private weak var panel: KioskPanel?
    private let port: Int
    private var views: [String: LiveWidgetView] = [:]
    private var accepting = false
    private var lastSeq = Int.min
    private var lastSync: Date?
    private var viewport = CGSize.zero
    private var backoff = CrashBackoff()
    private var reported: [String: String] = [:]
    private var watchdog: Timer?
    private var closed = false

    init(port: Int, mainWebView: WKWebView, panel: KioskPanel, navigationDelegate: KioskNavigationDelegate) {
        self.port = port
        self.mainWebView = mainWebView
        self.panel = panel
        self.navigationDelegate = navigationDelegate
        super.init()
        navigationDelegate.host = self
    }

    // MARK: lifecycle

    /// Destroys every widget view and detaches the bridge. Call before the panel webview or window goes away.
    func shutdown() {
        guard !closed else { return }
        closed = true
        destroyAll()
        mainWebView?.configuration.userContentController.removeScriptMessageHandler(forName: Self.handlerName)
        navigationDelegate.host = nil
        container.removeFromSuperview()
    }

    func mainWebViewLost() {
        accepting = false
        destroyAll()
    }

    private func destroyAll() {
        for id in Array(views.keys) { destroyView(id) }
        reported.removeAll()
        updateKeyState()
    }

    private func destroyView(_ id: String) {
        guard let live = views.removeValue(forKey: id) else { return }
        live.backObservation?.invalidate()
        live.webView.stopLoading()
        live.webView.navigationDelegate = nil
        live.webView.uiDelegate = nil
        live.webView.removeFromSuperview()
        if views.isEmpty { stopWatchdog() }
    }

    // MARK: bridge

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard !closed, message.frameInfo.isMainFrame, message.webView === mainWebView,
              isPanelOrigin(message.frameInfo.securityOrigin)
        else { return }
        var parsed = message.body as? [String: Any]
        if parsed == nil, let text = message.body as? String, let data = text.data(using: .utf8) {
            parsed = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        }
        guard let obj = parsed, let type = obj["type"] as? String else { return }
        switch type {
        case "nexus.webview.hello": handleHello(obj)
        case "nexus.webview.sync": handleSync(obj)
        case "nexus.webview.command": handleCommand(obj)
        default: break
        }
    }

    private func isPanelOrigin(_ origin: WKSecurityOrigin) -> Bool {
        origin.protocol == "http" && origin.host == "localhost" && origin.port == port
    }

    private func handleHello(_ obj: [String: Any]) {
        let version = (obj["version"] as? NSNumber)?.intValue
        if version == 1 {
            accepting = true
            lastSeq = Int.min
            send(["type": "nexus.webview.caps", "version": 1, "supported": true,
                  "maxViews": WebViewWidgetLogic.maxViews, "interactive": true])
        } else {
            send(["type": "nexus.webview.caps", "version": 1, "supported": false,
                  "maxViews": 0, "interactive": false])
        }
    }

    private func handleSync(_ obj: [String: Any]) {
        guard accepting, let msg = WebViewWidgetLogic.parseSync(obj), msg.seq >= lastSeq else { return }
        lastSeq = msg.seq
        lastSync = Date()
        viewport = msg.viewport
        apply(msg)
    }

    private func handleCommand(_ obj: [String: Any]) {
        guard accepting, let id = obj["id"] as? String, let live = views[id] else { return }
        switch obj["command"] as? String {
        case "back": if live.webView.canGoBack { live.webView.goBack() }
        case "reload": live.webView.reload()
        default: break
        }
    }

    // MARK: sync

    private func apply(_ msg: WebViewSyncMessage) {
        let now = Date()
        backoff.clearExpired(now: now)
        let blocked = Set(msg.views.map { $0.id }.filter { backoff.isBlocked($0, now: now) })
        let configured = views.mapValues { $0.configuredURL }
        let plan = WebViewWidgetLogic.plan(desired: msg.views, configured: configured, blocked: blocked)

        for id in plan.destroy { destroyView(id) }
        for spec in plan.invalid {
            destroyView(spec.id)
            reportOnce(spec.id, key: "url:" + spec.url, code: "url")
        }
        for id in plan.limited { reportOnce(id, key: "limit", code: "limit") }
        let okIds = Set(plan.create.map { $0.id } + plan.update.map { $0.spec.id })
        for id in reported.keys where okIds.contains(id) { reported.removeValue(forKey: id) }

        for (spec, navigate) in plan.update {
            guard let live = views[spec.id] else { continue }
            if navigate, let url = WebViewWidgetLogic.allowedURL(spec.url) {
                live.configuredURL = spec.url
                live.webView.load(URLRequest(url: url))
            }
            layout(live, spec)
        }
        for spec in plan.create { create(spec) }
        updateKeyState()
        if !views.isEmpty { startWatchdog() }
    }

    private func create(_ spec: WebViewSpec) {
        guard let url = WebViewWidgetLogic.allowedURL(spec.url), !closed else { return }
        let cfg = WKWebViewConfiguration()
        cfg.websiteDataStore = Self.dataStore
        let web = WidgetWebView(frame: .zero, configuration: cfg)
        web.panel = panel
        web.allowsMagnification = false
        web.allowsBackForwardNavigationGestures = false
        let delegate = WidgetViewDelegate(host: self, id: spec.id)
        web.navigationDelegate = delegate
        web.uiDelegate = delegate
        let live = LiveWidgetView(webView: web, delegate: delegate, configuredURL: spec.url)
        live.backObservation = web.observe(\.canGoBack) { [weak self] _, _ in
            guard let self = self, let live = self.views[spec.id] else { return }
            self.sendState(spec.id, live.state, nil)
        }
        views[spec.id] = live
        container.addSubview(web)
        layout(live, spec)
        web.load(URLRequest(url: url))
    }

    private func layout(_ live: LiveWidgetView, _ spec: WebViewSpec) {
        let bounds = mainWebView?.bounds.size ?? .zero
        let frame = WebViewWidgetLogic.mapRect(spec.rect, viewport: viewport, bounds: bounds)
        let web = live.webView
        if let frame = frame, web.frame != frame { web.frame = frame }
        let shown = frame != nil && spec.visible
        if web.isHidden == shown { web.isHidden = !shown }
        if web.interactive != spec.interactive { web.interactive = spec.interactive }
        if web.allowsLinkPreview != spec.interactive { web.allowsLinkPreview = spec.interactive }
    }

    // MARK: keyboard focus

    private func updateKeyState() {
        guard let panel = panel else { return }
        let wantKey = views.values.contains { $0.webView.interactive && !$0.webView.isHidden }
        if wantKey {
            if !panel.allowKey {
                panel.allowKey = true
                panel.makeKey()
                if let web = views.values.first(where: { $0.webView.interactive && !$0.webView.isHidden })?.webView {
                    panel.makeFirstResponder(web)
                }
            }
        } else if panel.allowKey {
            panel.releaseKey()
        }
    }

    // MARK: watchdog

    private func startWatchdog() {
        guard watchdog == nil else { return }
        let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            if WebViewWidgetLogic.watchdogExpired(lastSync: self.lastSync, now: Date()) {
                self.destroyAll()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        watchdog = timer
    }

    private func stopWatchdog() {
        watchdog?.invalidate()
        watchdog = nil
    }

    // MARK: reports to the panel page

    func viewState(_ id: String, _ state: String, _ code: String?) {
        guard let live = views[id] else { return }
        live.state = state
        sendState(id, state, code)
    }

    func viewCrashed(_ id: String) {
        guard views[id] != nil else { return }
        backoff.recordCrash(id, now: Date())
        sendState(id, "crashed", nil)
        destroyView(id)
        updateKeyState()
    }

    private func reportOnce(_ id: String, key: String, code: String) {
        if reported[id] == key { return }
        reported[id] = key
        sendState(id, "error", code)
    }

    private func sendState(_ id: String, _ state: String, _ code: String?) {
        var payload: [String: Any] = [
            "type": "nexus.webview.state", "id": id, "state": state,
            "canGoBack": views[id]?.webView.canGoBack ?? false,
        ]
        if let code = code { payload["code"] = code }
        send(payload)
    }

    private func send(_ payload: [String: Any]) {
        guard let web = mainWebView,
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8)
        else { return }
        web.evaluateJavaScript("window.dispatchEvent(new CustomEvent('nexus-webview',{detail:\(json)}))", completionHandler: nil)
    }
}

// Pure decision logic for the kiosk Web View widget host: message parsing,
// the sync diff, the url-change rule, rect mapping, watchdog and crash
// backoff. Foundation only, so it compiles and runs without AppKit.

import Foundation
import CoreGraphics

struct WebViewSpec: Equatable {
    let id: String
    let url: String
    let rect: CGRect
    let interactive: Bool
    let visible: Bool
}

struct WebViewSyncMessage {
    let seq: Int
    let viewport: CGSize
    let views: [WebViewSpec]
}

struct WebViewPlan {
    /// Specs to create (id not live), in message order.
    var create: [WebViewSpec] = []
    /// Live views to update; navigate is true only when the configured url changed.
    var update: [(spec: WebViewSpec, navigate: Bool)] = []
    var destroy: [String] = []
    /// Ids dropped because more than maxViews valid entries were listed.
    var limited: [String] = []
    /// Entries whose url is not http/https.
    var invalid: [WebViewSpec] = []
}

enum WebViewWidgetLogic {
    static let maxViews = 4
    static let watchdogTimeout: TimeInterval = 3.0
    static let crashBackoff: TimeInterval = 5.0

    static func parseSync(_ obj: [String: Any]) -> WebViewSyncMessage? {
        guard let seq = (obj["seq"] as? NSNumber)?.intValue,
              let vp = obj["viewport"] as? [String: Any],
              let vw = (vp["w"] as? NSNumber)?.doubleValue,
              let vh = (vp["h"] as? NSNumber)?.doubleValue,
              let list = obj["views"] as? [[String: Any]]
        else { return nil }
        var seen = Set<String>()
        var views: [WebViewSpec] = []
        for entry in list {
            guard let id = entry["id"] as? String, !id.isEmpty, seen.insert(id).inserted else { continue }
            let url = (entry["url"] as? String) ?? ""
            let interactive = (entry["interactive"] as? Bool) ?? false
            var visible = (entry["visible"] as? Bool) ?? false
            var rect = CGRect.zero
            if let r = entry["rect"] as? [String: Any],
               let x = (r["x"] as? NSNumber)?.doubleValue,
               let y = (r["y"] as? NSNumber)?.doubleValue,
               let w = (r["w"] as? NSNumber)?.doubleValue,
               let h = (r["h"] as? NSNumber)?.doubleValue,
               x.isFinite, y.isFinite, w.isFinite, h.isFinite, w > 0, h > 0 {
                rect = CGRect(x: x, y: y, width: w, height: h)
            } else {
                visible = false
            }
            views.append(WebViewSpec(id: id, url: url, rect: rect, interactive: interactive, visible: visible))
        }
        return WebViewSyncMessage(seq: seq, viewport: CGSize(width: vw, height: vh), views: views)
    }

    /// http/https with a host, nothing else.
    static func allowedURL(_ s: String) -> URL? {
        guard let u = URL(string: s), let scheme = u.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = u.host, !host.isEmpty
        else { return nil }
        return u
    }

    /// Diff the desired set against the live set. `configured` maps live ids
    /// to the last configured url received for them (never the view's current
    /// location). `blocked` are ids inside a crash backoff.
    static func plan(desired: [WebViewSpec], configured: [String: String], blocked: Set<String>) -> WebViewPlan {
        var plan = WebViewPlan()
        var accepted = Set<String>()
        for spec in desired {
            if allowedURL(spec.url) == nil {
                plan.invalid.append(spec)
                continue
            }
            if accepted.count >= maxViews {
                plan.limited.append(spec.id)
                continue
            }
            if blocked.contains(spec.id) && configured[spec.id] == nil {
                // Counts toward the cap but is not created until the backoff ends.
                accepted.insert(spec.id)
                continue
            }
            accepted.insert(spec.id)
            if let current = configured[spec.id] {
                plan.update.append((spec, current != spec.url))
            } else {
                plan.create.append(spec)
            }
        }
        plan.destroy = configured.keys.filter { !accepted.contains($0) }.sorted()
        return plan
    }

    /// CSS-px rect to a rect in the main webview's top-left coordinates.
    /// Scale is per axis from the real bounds, not from the device pixel ratio.
    static func mapRect(_ rect: CGRect, viewport: CGSize, bounds: CGSize) -> CGRect? {
        guard viewport.width > 0, viewport.height > 0, bounds.width > 0, bounds.height > 0,
              viewport.width.isFinite, viewport.height.isFinite
        else { return nil }
        let sx = bounds.width / viewport.width
        let sy = bounds.height / viewport.height
        return CGRect(x: rect.origin.x * sx, y: rect.origin.y * sy,
                      width: rect.size.width * sx, height: rect.size.height * sy).integral
    }

    static func watchdogExpired(lastSync: Date?, now: Date) -> Bool {
        guard let last = lastSync else { return true }
        return now.timeIntervalSince(last) * 1000 >= watchdogTimeout * 1000
    }
}

struct CrashBackoff {
    private var until: [String: Date] = [:]

    mutating func recordCrash(_ id: String, now: Date) {
        until[id] = now.addingTimeInterval(WebViewWidgetLogic.crashBackoff)
    }

    func isBlocked(_ id: String, now: Date) -> Bool {
        guard let end = until[id] else { return false }
        return now < end
    }

    mutating func clearExpired(now: Date) {
        until = until.filter { now < $0.value }
    }
}

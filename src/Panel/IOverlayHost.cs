namespace Nexus.Service.Panel;

/// <summary>
/// Lifecycle abstraction for the panel kiosk host. Four impls:
/// <c>PanelOverlayHostLauncher</c> on Windows (spawns nexus-overlay.exe
/// out-of-proc; the overlay is a raw Win32 + direct-WebView2 P/Invoke AOT
/// binary, isolated from the service process so a WebView2 crash cannot
/// take the service down), <c>MacOverlayHostLauncher</c> on macOS (spawns
/// a separate AppKit + WKWebView helper since the service is already a
/// .app and the mac publish is non-AOT), <c>LinuxOverlayHost</c> on Linux
/// (hosts only the Y70 kiosk through LinuxPanelKioskHost), and
/// <c>NoopOverlayHost</c> elsewhere. Callers in Program.cs go through this
/// interface so the reconcile loop is platform-agnostic.
/// </summary>
public interface IOverlayHost
{
    bool Start();
    void Stop();
    bool IsRunning { get; }

    /// <summary>
    /// Display→panel assignments changed; the host should re-reconcile its
    /// kiosk windows. No-op on Windows: nexus-overlay already re-polls on
    /// the PrefsChanged push. The macOS helper has no push channel, so the
    /// launcher pokes it over the stdin pipe it already holds open.
    /// </summary>
    void NotifyDisplayAssignmentsChanged();

    /// <summary>
    /// Display identify started or restarted. The payload is one line of
    /// OverlayIdentifyDto JSON. Hosts that read it from /overlay/state ignore this.
    /// </summary>
    void NotifyIdentify(string payloadJson) { }
}

/// <summary>Unsupported-platform fallback. No host ever starts.</summary>
public sealed class NoopOverlayHost : IOverlayHost
{
    public bool IsRunning => false;
    public bool Start() => false;
    public void Stop() { }
    public void NotifyDisplayAssignmentsChanged() { }
}

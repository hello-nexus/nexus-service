namespace Nexus.Service.Panel;

/// <summary>
/// Lifecycle abstraction for the floating desktop widget host. Four impls:
/// <c>PanelOverlayHostLauncher</c> on Windows (spawns nexus-overlay.exe
/// out-of-proc; the overlay is a raw Win32 + direct-WebView2 P/Invoke AOT
/// binary, isolated from the service process so a WebView2 crash cannot
/// take the service down), <c>MacOverlayHostLauncher</c> on macOS (spawns
/// a separate AppKit + WKWebView helper since the service is already a
/// .app and the mac publish is non-AOT), <c>LinuxOverlayHost</c> on Linux
/// (no widgets; hosts only the Y70 kiosk through LinuxPanelKioskHost), and
/// <c>NoopOverlayHost</c> elsewhere. Callers in Program.cs go through this
/// interface so the reconcile loop is platform-agnostic.
/// </summary>
public interface IOverlayHost
{
    bool Start();
    void Stop();
    bool IsRunning { get; }

    /// <summary>
    /// Hook for the service to push the always-on-top toggle to the host.
    /// Currently a no-op on both impls: the SPA's context menu posts the
    /// change via its own webMessage bridge inside each host, so the
    /// service doesn't need to route it. Kept on the interface for future
    /// callers that want to flip the level without an SPA round trip.
    /// </summary>
    void SetAlwaysOnTop(bool value);

    /// <summary>
    /// Display→panel assignments changed; the host should re-reconcile its
    /// kiosk windows. No-op on Windows: nexus-overlay already re-polls on
    /// the PrefsChanged push. The macOS helper has no push channel, so the
    /// launcher pokes it over the stdin pipe it already holds open.
    /// </summary>
    void NotifyDisplayAssignmentsChanged();
}

/// <summary>Unsupported-platform fallback. Desktop widgets never start.</summary>
public sealed class NoopOverlayHost : IOverlayHost
{
    public bool IsRunning => false;
    public bool Start() => false;
    public void Stop() { }
    public void SetAlwaysOnTop(bool value) { }
    public void NotifyDisplayAssignmentsChanged() { }
}

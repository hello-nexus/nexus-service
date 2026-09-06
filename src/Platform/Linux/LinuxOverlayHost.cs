using Nexus.Service.Panel;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Linux <see cref="IOverlayHost"/>: there is no floating-widget host here, so
/// the contract covers only what the Windows overlay's PanelDisplay does for
/// the Y70 - open its kiosk when the panel is attached and Panel.AutoLaunch is
/// on. Y70DisplayHeartbeatWorker and OverlayHostBootstrap already drive that
/// contract on every platform, so they need no Linux branch.
/// </summary>
public sealed class LinuxOverlayHost : IOverlayHost
{
    private readonly LinuxPanelKioskHost _kiosks;

    public LinuxOverlayHost(LinuxPanelKioskHost kiosks)
    {
        _kiosks = kiosks;
    }

    public bool IsRunning => _kiosks.Y70Running;

    /// <summary>Reconcile spawns the Y70 kiosk only while one is attached and
    /// AutoLaunch is on, so a widget-driven Start() with no Y70 is a no-op.</summary>
    public bool Start()
    {
        _kiosks.Reconcile();
        return _kiosks.Y70Running;
    }

    public void Stop() => _kiosks.CloseY70();

    public void SetAlwaysOnTop(bool value) { }

    public void NotifyDisplayAssignmentsChanged() => _kiosks.Reconcile();
}

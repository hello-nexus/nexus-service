namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Rotates the physical Y70 panel display via the Windows display subsystem.
/// On the service side this is fronted by <c>HelperDisplayOrientationProxy</c>,
/// which routes the call through the user-session helper because Session 0
/// cannot see the user's monitors.
/// </summary>
public interface IDisplayOrientationProvider
{
    /// <summary>
    /// Apply the requested orientation to the Y70 display, if connected.
    /// </summary>
    /// <param name="orientation">"Landscape", "Portrait", "LandscapeFlipped", or "PortraitFlipped".</param>
    /// <returns>(ok, error). <c>error</c> is empty on success or when no Y70 is attached.</returns>
    (bool Ok, string Error) SetY70Orientation(string orientation);

    /// <summary>
    /// Apply the requested orientation to the monitor with the given stable
    /// display id (GET /displays id space - promoted-monitor panels). Unlike
    /// the Y70 variant, an absent display IS an error: the caller targeted a
    /// specific monitor.
    /// </summary>
    /// <param name="coverColorHex">
    /// "#rrggbb" solid colour the Windows implementation may show over the
    /// strip of desktop a resolution swap briefly exposes, mid-rotation.
    /// Empty falls back to opaque black rather than skipping the cover; pass
    /// the caller's panel background colour when known (see
    /// <c>PanelDeviceRegistry.ResolveCoverBackgroundHex</c>).
    /// </param>
    (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex);
}

public sealed class NoopDisplayOrientationProvider : IDisplayOrientationProvider
{
    public (bool Ok, string Error) SetY70Orientation(string orientation) => (true, "");
    public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
        => (false, "display rotation is not supported on this platform");
}

using System;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Physical facts for a display model the promoted-monitor grid math knows
/// (<see cref="Dpi"/> is native px/inch, not the Windows scaling DPI).
/// <see cref="Touch"/> asserts the model has an integrated touch digitizer even
/// when the OS pointer-device association misses it (see
/// <see cref="Match"/>), so its promoted panel is interactive.
/// <see cref="DefaultOrientation"/> is the orientation applied once when the
/// model is first auto-promoted from a different one (null = leave as is).
/// </summary>
public sealed record KnownPanelDisplay(string Family, double Dpi, bool Touch, string? DefaultOrientation = null);

/// <summary>
/// Curated displays commonly promoted to Nexus panels, matched by the EDID
/// PnP identity (manufacturer EISA id + hex product code). Windows never
/// surfaces the EDID product-name string on this path - the topology
/// provider composes Name as "{manufacturer} {model}" from the PnP DeviceID
/// (e.g. "CRX ED00") - so a product-name match only fires on macOS/Linux,
/// where the provider reports the EDID name. Supplies the physical density
/// the grid needs (Windows only exposes scaling DPI) and a family id for
/// web-side branding.
/// </summary>
public static class KnownPanelDisplays
{
    public const string XeneonEdgeFamily = "xeneon-edge";

    // Corsair Xeneon Edge: 14.5" 2560x720 (32:9) touch strip.
    // sqrt(2560^2 + 720^2) / 14.5 = 183 px/inch. Integrated touch digitizer,
    // so Touch is asserted even when GetPointerDevices doesn't map it.
    private static readonly KnownPanelDisplay XeneonEdge = new(XeneonEdgeFamily, 183, Touch: true);

    public const string IcueLinkLcd5Family = "icue-link-lcd5";

    // Corsair iCUE LINK 5" LCD Screen Module: DisplayPort glass on the TITAN II
    // pump cap, no touch; dpi is the native diagonal over the 5" panel.
    private static readonly KnownPanelDisplay IcueLinkLcd5 = new(IcueLinkLcd5Family, 294, Touch: false);

    public const string HyteY60Family = "hyte-y60";

    // HYTE Y60 upgrade kit: non-touch bar mounted vertically; dpi is the
    // native diagonal over the panel's physical diagonal.
    private static readonly KnownPanelDisplay HyteY60 = new(
        HyteY60Family, 158, Touch: false, DefaultOrientation: Nexus.Service.Models.Displays.DisplayOrientations.PortraitFlipped);

    private const int Y60NativeWidth = 1920;
    private const int Y60NativeHeight = 515;

    // The kit ships the generic Realtek scaler EDID (manufacturer RTK, product
    // BC32, name "RTK FHD HDR"), which other monitors share, so identity alone
    // is not enough: the native panel size (either way round, since rotation
    // swaps it) must match too. Unknown size never matches.
    private static bool IsY60Resolution(int? width, int? height) =>
        (width == Y60NativeWidth && height == Y60NativeHeight)
        || (width == Y60NativeHeight && height == Y60NativeWidth);

    public static KnownPanelDisplay? Match(string? manufacturer, string? model, string? name, int? width = null, int? height = null)
    {
        if (IsY60Resolution(width, height)
            && ((EqualsIgnoreCase(manufacturer, "RTK") && EqualsIgnoreCase(model, "BC32"))
                || ContainsIgnoreCase(name, "RTK FHD HDR")))
        {
            return HyteY60;
        }
        // Corsair EISA id "CRX", Xeneon Edge product code 0xED00.
        if (EqualsIgnoreCase(manufacturer, "CRX") && EqualsIgnoreCase(model, "ED00"))
            return XeneonEdge;
        if (ContainsIgnoreCase(name, "XENEON EDGE") || ContainsIgnoreCase(model, "XENEON EDGE"))
            return XeneonEdge;
        // EDID: EISA id "XMD", product 0x00EA, name "iCUE LINK 5''".
        if (EqualsIgnoreCase(manufacturer, "XMD") && EqualsIgnoreCase(model, "00EA"))
            return IcueLinkLcd5;
        if (ContainsIgnoreCase(name, "ICUE LINK 5") || ContainsIgnoreCase(model, "ICUE LINK 5"))
            return IcueLinkLcd5;
        return null;
    }

    private static bool EqualsIgnoreCase(string? value, string expected) =>
        !string.IsNullOrEmpty(value) && value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string? value, string fragment) =>
        !string.IsNullOrEmpty(value) && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

#if DEV_TOOLS
namespace Nexus.Service.Platform.Displays;

/// <summary>Dev-tools stand-in for the attached DDC-only Y70 variant, read by <c>displays.panelVariant</c> when no such panel is attached. In-memory: resets with the service.</summary>
public static class DevPanelVariantOverride
{
    private static volatile string _variant = "";

    public static string Variant
    {
        get => _variant;
        set => _variant = value;
    }
}
#endif

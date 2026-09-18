using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sensors;

namespace Nexus.Service.Store;

/// <summary>
/// Apps that belong to one piece of hardware and install themselves once it is
/// present, with no Nexus account required.
/// </summary>
/// <remarks>
/// The pairing is local so a machine with none of this hardware makes no
/// network call. A match waives the account gate, so this table is the whole
/// allowlist and must name only free apps.
/// </remarks>
public sealed class HardwareAppCatalog
{
    /// <summary>Ships with the HYTE Y70TI Ina panel.</summary>
    public const string InaAppId = "com.hellonexus.ina";

    /// <summary>Ships on iBUYPOWER-built PCs.</summary>
    public const string IbuypowerAppId = "com.ibuypower.control";

    /// <summary>SMBIOS system manufacturer names that count as an iBUYPOWER build.</summary>
    private static readonly string[] IbuypowerManufacturers = { "iBUYPOWER" };

    private readonly Func<string> _panelVariant;
    private readonly Func<bool> _isIbuypower;

    public HardwareAppCatalog(OemInfo oem, DisplayTopologyService displays)
        : this(() => ResolvePanelVariant(displays), () => oem.Matches(IbuypowerManufacturers))
    {
    }

    /// <summary>Test seam: supplies the two hardware reads, both of which hit real firmware.</summary>
    internal HardwareAppCatalog(Func<string> panelVariant, Func<bool> isIbuypower)
    {
        _panelVariant = panelVariant;
        _isIbuypower = isIbuypower;
    }

    /// <summary>Every app id in the table, matched or not.</summary>
    public static IReadOnlyList<string> AllAppIds { get; } = new[] { InaAppId, IbuypowerAppId };

    /// <summary>True when the id is one this feature may install without an account.</summary>
    public static bool IsHardwareApp(string appId) =>
        appId is InaAppId or IbuypowerAppId;

    /// <summary>App ids whose hardware is attached right now.</summary>
    public IReadOnlyList<string> MatchedAppIds()
    {
        var matched = new List<string>(AllAppIds.Count);
        foreach (var id in AllAppIds)
        {
            if (IsMatched(id)) matched.Add(id);
        }
        return matched;
    }

    /// <summary>
    /// Whether this app's hardware is attached. An id outside the table is
    /// always false, so a caller can pass any app id to decide whether the
    /// account gate is waived.
    /// </summary>
    public bool IsMatched(string appId) => appId switch
    {
        InaAppId => _panelVariant() == Y70DisplayProtocol.VariantIna,
        IbuypowerAppId => _isIbuypower(),
        _ => false,
    };

    /// <summary>Attached DDC-only Y70 variant; empty until the user-session helper connects, which is why callers re-read rather than caching.</summary>
    private static string ResolvePanelVariant(DisplayTopologyService displays)
    {
        var variant = displays.DdcOnlyY70Variant();
#if DEV_TOOLS
        // Stands in for a panel that is not attached, matching what
        // displays.panelVariant reports to apps.
        if (variant.Length == 0) variant = DevPanelVariantOverride.Variant;
#endif
        return variant;
    }
}

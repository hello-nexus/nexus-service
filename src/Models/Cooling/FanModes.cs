namespace Qos.Service.Models.Cooling;

/// <summary>
/// Single source of truth for the <see cref="FanChannel.Mode"/> wire string.
/// The cooling routes, all fan-control providers, and the panel decode the
/// same set of values — keeping them as named constants here prevents a
/// stray typo (e.g. "manual" vs "Manual") from silently downgrading the UI
/// to BIOS Control.
///
/// Values are case-sensitive and serialised as-is to the panel.
/// </summary>
public static class FanModes
{
    /// <summary>Fan is BIOS-controlled — qos is not driving its PWM.</summary>
    public const string Auto = "Auto";

    /// <summary>User-set fixed duty (no curve attached).</summary>
    public const string Manual = "Manual";

    /// <summary>Driven by an attached curve output.</summary>
    public const string Curve = "Curve";
}

using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>One feature value capability of a HID collection (a HIDP_VALUE_CAPS entry); <see cref="Usage"/>..<see cref="UsageMax"/> is the usage range when <see cref="IsRange"/>.</summary>
public readonly record struct HidValueCap(int ReportId, int UsagePage, int Usage, int LinkUsagePage, int LinkUsage, int BitSize, int ReportCount, int UsageMax = -1, bool IsRange = false, int LinkCollection = 0)
{
    public int LastUsage => IsRange && UsageMax >= Usage ? UsageMax : Usage;
}

/// <summary>Optional capability of an <see cref="IHidDevice"/>: the parsed feature value caps of the opened collection. Only the Windows device implements it.</summary>
public interface IHidFeatureCaps
{
    /// <summary>Null when the caps cannot be read.</summary>
    IReadOnlyList<HidValueCap>? ReadFeatureValueCaps();
}

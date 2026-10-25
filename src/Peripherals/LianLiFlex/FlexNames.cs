using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>Short display names for Flex units: the family, with a number when two units share one.</summary>
public static class FlexNames
{
    public static string Short(FlexFamily family) => family switch
    {
        FlexFamily.SlInfFlex => "SL-INF Flex",
        FlexFamily.P28V2 => "P28 V2",
        _ => "TL Flex",
    };

    /// <summary>TL units before SL-INF units before P28 units, then by the user's sort index and MAC.</summary>
    public static IReadOnlyList<FlexUnit> Ordered(IEnumerable<FlexUnit> units, Func<string, int> sortIndexOf) =>
        units.OrderBy(u => u.Family == FlexFamily.SlInfFlex ? 1 : u.Family == FlexFamily.P28V2 ? 2 : 0)
            .ThenBy(u => sortIndexOf(u.Mac))
            .ThenBy(u => u.Mac, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One rule for every Flex fan name: per family, the paired units (in <see cref="Ordered"/> order) then the
    /// screen-only screens count together; more than one numbers from one, exactly one stays bare.
    /// </summary>
    public static string UnitName(IEnumerable<FlexUnit> units, FlexUnit unit, Func<string, int> sortIndexOf, int screenOnlyOfFamily = 0)
    {
        var sameFamily = Ordered(units.Where(u => u.Family == unit.Family), sortIndexOf);
        var position = sameFamily.ToList().FindIndex(u => string.Equals(u.Mac, unit.Mac, StringComparison.OrdinalIgnoreCase));
        return Numbered(unit.Family, position, sameFamily.Count + screenOnlyOfFamily);
    }

    /// <summary>The family name, numbered from one only when the family has more than one screen or unit.</summary>
    public static string Numbered(FlexFamily family, int position, int count) =>
        count > 1 && position >= 0 ? $"{Short(family)} {position + 1}" : Short(family);
}

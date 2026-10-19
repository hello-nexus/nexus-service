using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>Short display names for Flex units: the family, with a number when two units share one.</summary>
public static class FlexNames
{
    public static string Short(FlexFamily family) => family == FlexFamily.SlInfFlex ? "SL-INF Flex" : "TL Flex";

    /// <summary>TL units before SL-INF units, then by the user's sort index and MAC.</summary>
    public static IReadOnlyList<FlexUnit> Ordered(IEnumerable<FlexUnit> units, Func<string, int> sortIndexOf) =>
        units.OrderBy(u => u.Family == FlexFamily.SlInfFlex ? 1 : 0)
            .ThenBy(u => sortIndexOf(u.Mac))
            .ThenBy(u => u.Mac, StringComparer.Ordinal)
            .ToList();

    /// <summary>"TL Flex" for the only unit of its family, "TL Flex 2" for the second, in <see cref="Ordered"/> order.</summary>
    public static string UnitName(IEnumerable<FlexUnit> units, FlexUnit unit, Func<string, int> sortIndexOf)
    {
        var sameFamily = Ordered(units.Where(u => u.Family == unit.Family), sortIndexOf);
        var position = sameFamily.ToList().FindIndex(u => string.Equals(u.Mac, unit.Mac, StringComparison.OrdinalIgnoreCase));
        return sameFamily.Count > 1 && position > 0 ? $"{Short(unit.Family)} {position + 1}" : Short(unit.Family);
    }

    public static string ProfileName(string profile) => profile switch
    {
        "quiet" => "Quiet",
        "stdsp" => "StdSP",
        "highsp" => "HighSP",
        "fullsp" => "FullSP",
        "fixed" => "Fixed",
        _ when profile.StartsWith("custom", StringComparison.Ordinal) && profile.Length > 6 => $"Custom {profile[6..]}",
        _ => profile,
    };
}

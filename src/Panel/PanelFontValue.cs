namespace Nexus.Service.Panel;

/// <summary>
/// A panel font value is either an embedded font id, stored as given, or
/// <c>system:&lt;family&gt;</c> naming an installed font family.
/// </summary>
public static class PanelFontValue
{
    public const string SystemPrefix = "system:";
    public const int MaxFamilyLength = 128;

    /// <summary>
    /// Normalizes a patched font value. Null means "clear back to the app font"; a rejected
    /// <c>system:</c> value (empty, too long, control characters) returns false so the caller
    /// keeps the stored value.
    /// </summary>
    public static bool TryNormalize(string value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!value.StartsWith(SystemPrefix, StringComparison.Ordinal))
        {
            normalized = value;
            return true;
        }
        var family = value[SystemPrefix.Length..].Trim();
        if (!IsValidFamily(family))
        {
            return false;
        }
        normalized = SystemPrefix + family;
        return true;
    }

    public static bool IsValidFamily(string family)
    {
        if (family.Length == 0 || family.Length > MaxFamilyLength)
        {
            return false;
        }
        foreach (var c in family)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }
        return true;
    }
}

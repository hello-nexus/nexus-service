namespace Nexus.Service.Panel;

/// <summary>A panel skin is a short lowercase slug naming a web-side skin; the service does not know the catalog.</summary>
public static class PanelSkinValue
{
    public const int MaxLength = 32;

    /// <summary>Normalizes a patched skin id. Empty or whitespace clears (null result); a non-slug or over-long value returns false so the caller keeps the stored value.</summary>
    public static bool TryNormalize(string value, out string? normalized)
    {
        normalized = null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }
        if (trimmed.Length > MaxLength)
        {
            return false;
        }
        foreach (var c in trimmed)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
            {
                return false;
            }
        }
        normalized = trimmed;
        return true;
    }
}

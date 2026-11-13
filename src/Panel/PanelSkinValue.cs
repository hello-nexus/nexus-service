namespace Nexus.Service.Panel;

/// <summary>
/// A panel skin or skin variant is an opaque id owned by the web catalog, stored trimmed and
/// as given. Unknown ids are not rejected here.
/// </summary>
public static class PanelSkinValue
{
    public const int MaxLength = 64;

    /// <summary>
    /// Normalizes a patched value. Empty or whitespace means "clear" (null result); an over-long
    /// value or one with control characters returns false so the caller keeps the stored value.
    /// </summary>
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
            if (char.IsControl(c))
            {
                return false;
            }
        }
        normalized = trimmed;
        return true;
    }
}

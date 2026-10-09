using System;

namespace Nexus.Service.Update;

/// <summary>
/// Semver compare for "v{major}.{minor}.{patch}[-prerelease]" version strings.
/// A leading "v"/"V" is stripped. Prerelease precedence follows semver: a build
/// with a "-beta.N" suffix ranks below the same major.minor.patch release.
/// Non-parseable strings are never newer.
/// </summary>
public static class VersionCompare
{
    /// <summary>
    /// Returns true when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="current"/>. Unparseable strings are never newer.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!TryParse(candidate, out var c)) return false;
        if (!TryParse(current, out var cur)) return false;
        return Compare(c, cur) > 0;
    }

    /// <summary>
    /// Returns true when <paramref name="version"/> is a valid semver string
    /// carrying a prerelease suffix (e.g. "v3.1.0-beta.1", "v3.0.0-rc.2").
    /// Unparseable strings return false.
    /// </summary>
    public static bool IsPrerelease(string version)
    {
        if (!TryParse(version, out var v)) return false;
        return v.Pre.Length > 0;
    }

    /// <summary>
    /// Parses the numeric core of "v{major}.{minor}.{patch}" (leading v/V
    /// optional, prerelease suffix accepted but not surfaced here). Returns
    /// false on garbage input. <see cref="IsNewer"/> applies full prerelease
    /// precedence; this overload is for callers that only need the triple.
    /// </summary>
    public static bool TryParseSemver(string tag, out (int Major, int Minor, int Patch) value)
    {
        value = default;
        if (!TryParse(tag, out var v)) return false;
        value = (v.Major, v.Minor, v.Patch);
        return true;
    }

    private static bool TryParse(string tag, out (int Major, int Minor, int Patch, string[] Pre) value)
    {
        value = default;
        if (string.IsNullOrEmpty(tag)) return false;

        var s = tag.AsSpan();
        if (s[0] is 'v' or 'V')
        {
            s = s.Slice(1);
        }

        if (s.IsEmpty) return false;

        // Split the prerelease suffix off at the first '-'.
        var pre = Array.Empty<string>();
        var dash = s.IndexOf('-');
        if (dash == 0) return false;
        ReadOnlySpan<char> core;
        if (dash > 0)
        {
            core = s.Slice(0, dash);
            pre = s.Slice(dash + 1).ToString().Split('.');
            // A dotted prerelease identifier may not be empty ("beta..1").
            foreach (var id in pre)
            {
                if (id.Length == 0) return false;
            }
        }
        else
        {
            core = s;
        }

        // Require at least one dot so bare integers ("63") are not accepted
        // as semver (those were the old monotonic tag format).
        if (core.IndexOf('.') < 0) return false;

        var parts = core.ToString().Split('.');
        if (parts.Length < 1) return false;

        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        var minor = 0;
        var patch = 0;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], out minor) || minor < 0)) return false;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0)) return false;

        value = (major, minor, patch, pre);
        return true;
    }

    private static int Compare(
        (int Major, int Minor, int Patch, string[] Pre) a,
        (int Major, int Minor, int Patch, string[] Pre) b)
    {
        var c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        c = a.Patch.CompareTo(b.Patch);
        if (c != 0) return c;

        // Equal core: a release outranks a prerelease of the same version.
        var aPre = a.Pre.Length > 0;
        var bPre = b.Pre.Length > 0;
        if (!aPre && !bPre) return 0;
        if (!aPre) return 1;
        if (!bPre) return -1;
        return ComparePre(a.Pre, b.Pre);
    }

    private static int ComparePre(string[] a, string[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var aNum = int.TryParse(a[i], out var ai);
            var bNum = int.TryParse(b[i], out var bi);
            int c;
            if (aNum && bNum) c = ai.CompareTo(bi);
            else if (aNum) return -1;   // numeric identifiers rank below alphanumeric
            else if (bNum) return 1;
            else c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        // All shared identifiers equal: more identifiers = higher precedence.
        return a.Length.CompareTo(b.Length);
    }
}

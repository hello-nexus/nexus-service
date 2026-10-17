using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Nexus.Service.Platform.Displays;

/// <summary>Sysfs reads for input nodes; no dependencies beyond System.IO.</summary>
internal static class LinuxSysfsInput
{
    /// <summary>
    /// USB hub siblings (VID_xxxx&amp;PID_xxxx) of an input node's USB device: resolve
    /// the class link (relative on real systems), walk up to the directory with
    /// idVendor, then list the other USB devices under the same hub. Empty when the
    /// node has no USB ancestor (virtual or I2C devices).
    /// </summary>
    internal static List<string> HubSiblings(string sysName, string sysRoot = "/sys")
    {
        var result = new List<string>();
        try
        {
            var link = new DirectoryInfo(Path.Combine(sysRoot, "class", "input", sysName));
            var dir = Resolve(link);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "idVendor")))
            {
                dir = dir.Parent;
            }
            if (dir?.Parent is null)
            {
                return result;
            }
            foreach (var sibling in dir.Parent.EnumerateDirectories())
            {
                if (sibling.FullName == dir.FullName)
                {
                    continue;
                }
                var vid = ReadText(Path.Combine(sibling.FullName, "idVendor"));
                var pid = ReadText(Path.Combine(sibling.FullName, "idProduct"));
                if (vid is not null && pid is not null)
                {
                    result.Add($"VID_{vid.ToUpperInvariant()}&PID_{pid.ToUpperInvariant()}");
                }
            }
        }
        catch (Exception)
        {
            // A failed walk yields no companions, the same fail-open the Windows snapshot has for the Y70 entry.
        }
        return result;
    }

    // readlink -f semantics: a relative target is relative to the link's own directory.
    private static DirectoryInfo Resolve(DirectoryInfo link)
    {
        if (link.LinkTarget is not { } target)
        {
            return link;
        }
        var full = Path.GetFullPath(target, link.Parent!.FullName);
        var resolved = new DirectoryInfo(full).ResolveLinkTarget(true) as DirectoryInfo;
        return resolved ?? new DirectoryInfo(full);
    }

    internal static int? ReadHex(string path)
        => ReadText(path) is { } t && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}

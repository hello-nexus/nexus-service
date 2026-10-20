using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace Nexus.Service.Activity;

/// <summary>Pure manifest and file-name logic for picking a packaged app's tile logo.</summary>
internal static partial class UwpLogoResolver
{
    /// <summary>
    /// The relative logo path of the manifest's first Application:
    /// Square44x44Logo, else Square150x150Logo. Null when neither is declared.
    /// </summary>
    public static string? ManifestLogo(string manifestXml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(manifestXml), settings);
        var inApplication = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (!inApplication)
            {
                inApplication = reader.LocalName == "Application";
                continue;
            }
            if (reader.LocalName != "VisualElements") continue;
            var small = reader.GetAttribute("Square44x44Logo");
            if (!string.IsNullOrEmpty(small)) return small;
            var medium = reader.GetAttribute("Square150x150Logo");
            return string.IsNullOrEmpty(medium) ? null : medium;
        }
        return null;
    }

    /// <summary>
    /// The plain <c>base.scale-N.ext</c> file with the highest N, which is the image the
    /// shell draws in the normal theme; else the highest-scale <c>base*ext</c> file, the
    /// first listed among equals. Null when none match, so the caller falls back to the
    /// declared logo path.
    /// </summary>
    public static string? BestScaledFile(string logoFileName, IEnumerable<string> fileNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(logoFileName);
        var ext = Path.GetExtension(logoFileName);
        var candidates = fileNames
            .Where(n => n.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)
                && n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                && n.Length >= baseName.Length + ext.Length)
            .ToList();
        var plainPrefix = baseName + ".scale-";
        var plain = candidates
            .Where(n => n.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(n.AsSpan(plainPrefix.Length, n.Length - plainPrefix.Length - ext.Length), out _))
            .OrderByDescending(Scale)
            .FirstOrDefault();
        return plain ?? candidates.OrderByDescending(Scale).FirstOrDefault();
    }

    private static int Scale(string name)
    {
        var m = ScaleRegex().Match(name);
        return m.Success && int.TryParse(m.Groups[1].Value, out var s) ? s : 0;
    }

    [GeneratedRegex(@"scale-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScaleRegex();
}

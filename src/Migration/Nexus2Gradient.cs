using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Nexus.Service.Migration;

/// <summary>One colour stop; At is 0-1 along the gradient line.</summary>
internal readonly record struct Nexus2GradientStop(byte R, byte G, byte B, double At);

/// <summary>A Nexus 2 Y70 gradient background (a CSS linear-gradient string), drawn the way a
/// browser draws it so it can be imported as a still image.</summary>
internal sealed record Nexus2Gradient(double AngleDeg, IReadOnlyList<Nexus2GradientStop> Stops)
{
    /// <summary>Parses "linear-gradient(&lt;deg&gt;?, &lt;color&gt; &lt;pct&gt;?, ...)" with hex, rgb() or rgba()
    /// colours (alpha ignored: Nexus 2's presets are opaque). Null for anything else.</summary>
    public static Nexus2Gradient? Parse(string css)
    {
        const string prefix = "linear-gradient(";
        var s = css.Trim();
        if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !s.EndsWith(')'))
        {
            return null;
        }
        var args = SplitTopLevel(s[prefix.Length..^1]);
        if (args.Count == 0)
        {
            return null;
        }

        // CSS default direction is "to bottom".
        double angle = 180;
        var first = args[0].Trim();
        if (first.EndsWith("deg", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(first[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
        {
            angle = deg;
            args.RemoveAt(0);
        }

        var colors = new List<(byte R, byte G, byte B, double? At)>();
        foreach (var arg in args)
        {
            var part = arg.Trim();
            double? at = null;
            var space = part.LastIndexOf(' ');
            if (space > 0 && part.EndsWith('%')
                && double.TryParse(part[(space + 1)..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                at = Math.Clamp(pct / 100, 0, 1);
                part = part[..space].Trim();
            }
            if (ParseColor(part) is not { } c)
            {
                return null;
            }
            colors.Add((c.R, c.G, c.B, at));
        }
        if (colors.Count < 2)
        {
            return null;
        }

        // A stop without a position spreads evenly by index; a position before an earlier stop's is
        // raised to it, as CSS does, so the line never runs backwards.
        var stops = new List<Nexus2GradientStop>(colors.Count);
        var floor = 0.0;
        for (var i = 0; i < colors.Count; i++)
        {
            var at = Math.Max(colors[i].At ?? (double)i / (colors.Count - 1), floor);
            floor = at;
            stops.Add(new Nexus2GradientStop(colors[i].R, colors[i].G, colors[i].B, at));
        }
        return new Nexus2Gradient(angle, stops);
    }

    /// <summary>The colour at a pixel centre of a w x h box, per the CSS gradient line: through the
    /// centre at AngleDeg (0 = up, clockwise), long enough that the corners land on 0 and 1.</summary>
    public (byte R, byte G, byte B) ColorAt(double x, double y, int w, int h)
    {
        var a = AngleDeg * Math.PI / 180;
        var sin = Math.Sin(a);
        var cos = Math.Cos(a);
        var length = Math.Abs(w * sin) + Math.Abs(h * cos);
        var t = length <= 0 ? 0 : ((x - w / 2.0) * sin - (y - h / 2.0) * cos) / length + 0.5;
        t = Math.Clamp(t, 0, 1);

        if (t <= Stops[0].At)
        {
            return (Stops[0].R, Stops[0].G, Stops[0].B);
        }
        for (var i = 1; i < Stops.Count; i++)
        {
            var prev = Stops[i - 1];
            var next = Stops[i];
            if (t <= next.At)
            {
                var span = next.At - prev.At;
                var f = span <= 0 ? 1 : (t - prev.At) / span;
                return (Lerp(prev.R, next.R, f), Lerp(prev.G, next.G, f), Lerp(prev.B, next.B, f));
            }
        }
        var last = Stops[^1];
        return (last.R, last.G, last.B);
    }

    /// <summary>Writes the gradient as a 24-bit bottom-up BMP, the simplest format the bundled ffmpeg decodes.</summary>
    public void WriteBmp(string path, int w, int h)
    {
        var rowBytes = (w * 3 + 3) & ~3;
        var imageBytes = rowBytes * h;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + imageBytes);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(w);
        writer.Write(h);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(imageBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        var row = new byte[rowBytes];
        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = ColorAt(x + 0.5, y + 0.5, w, h);
                row[x * 3] = b;
                row[x * 3 + 1] = g;
                row[x * 3 + 2] = r;
            }
            writer.Write(row);
        }
    }

    private static byte Lerp(byte a, byte b, double f) => (byte)Math.Round(a + (b - a) * f);

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(s[start..i]);
                    start = i + 1;
                    break;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    private static (byte R, byte G, byte B)? ParseColor(string s)
    {
        if (s.StartsWith('#'))
        {
            var hex = s[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            }
            return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? ((byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : null;
        }
        var open = s.IndexOf('(');
        if (open <= 0 || !s.EndsWith(')'))
        {
            return null;
        }
        var fn = s[..open].Trim().ToLowerInvariant();
        if (fn is not ("rgb" or "rgba"))
        {
            return null;
        }
        var channels = s[(open + 1)..^1].Split(',');
        if (channels.Length < 3)
        {
            return null;
        }
        var rgb = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(channels[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
            {
                return null;
            }
            rgb[i] = (byte)Math.Clamp(Math.Round(c), 0, 255);
        }
        return (rgb[0], rgb[1], rgb[2]);
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Calendar.Ics;

internal sealed class IcsLine
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public List<KeyValuePair<string, string>> Params { get; init; } = new();

    public string? Param(string name)
    {
        foreach (var p in Params)
        {
            if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }
        return null;
    }
}

internal static class IcsLexer
{
    /// <summary>Splits on line breaks and joins folded continuation lines (a leading space or tab).</summary>
    public static List<string> Unfold(string text)
    {
        var result = new List<string>();
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text.Substring(1);
        }
        StringBuilder? current = null;
        var start = 0;
        while (start <= text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl;
            var len = end - start;
            if (len > 0 && text[start + len - 1] == '\r')
            {
                len--;
            }
            var segment = text.AsSpan(start, len);
            if (segment.Length > 0 && (segment[0] == ' ' || segment[0] == '\t'))
            {
                current?.Append(segment.Slice(1));
            }
            else
            {
                if (current is not null && current.Length > 0)
                {
                    result.Add(current.ToString());
                }
                current = new StringBuilder(segment.ToString());
            }
            if (nl < 0)
            {
                break;
            }
            start = nl + 1;
        }
        if (current is not null && current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }

    /// <summary>Parses <c>NAME;PARAM=value;PARAM="quoted":value</c>; null when there is no colon.</summary>
    public static IcsLine? ParseLine(string line)
    {
        var inQuote = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ':' && !inQuote)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0)
        {
            return null;
        }
        var head = line.Substring(0, colon);
        var value = line.Substring(colon + 1);
        var parts = SplitOutsideQuotes(head, ';');
        var name = parts[0].Trim().ToUpperInvariant();
        if (name.Length == 0)
        {
            return null;
        }
        var ps = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < parts.Count; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = parts[i].Substring(0, eq).Trim().ToUpperInvariant();
            var val = parts[i].Substring(eq + 1).Trim();
            if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
            {
                val = val.Substring(1, val.Length - 2);
            }
            ps.Add(new KeyValuePair<string, string>(key, val));
        }
        return new IcsLine { Name = name, Value = value, Params = ps };
    }

    private static List<string> SplitOutsideQuotes(string s, char sep)
    {
        var parts = new List<string>();
        var inQuote = false;
        var from = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                inQuote = !inQuote;
            }
            else if (s[i] == sep && !inQuote)
            {
                parts.Add(s.Substring(from, i - from));
                from = i + 1;
            }
        }
        parts.Add(s.Substring(from));
        return parts;
    }

    /// <summary>Reverses TEXT escaping: <c>\n \N \, \; \\</c>.</summary>
    public static string UnescapeText(string s)
    {
        if (s.IndexOf('\\') < 0)
        {
            return s;
        }
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n == 'n' || n == 'N' ? '\n' : n);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

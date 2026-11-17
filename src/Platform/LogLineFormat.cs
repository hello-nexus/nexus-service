using System.Globalization;
using System.Text;

namespace Nexus.Service.Platform;

/// <summary>
/// The one line format every Nexus log file uses: <c>&lt;UTC ISO-8601 with ms&gt;Z LVL [tag] message</c>,
/// with a three-char level (INF, WRN, ERR) and key=value bodies. Shared so the user-session
/// writers (helper, tray) and the per-feature logs (gpu, volume, pawnio) cannot drift from
/// nexus-service.log, which already writes this shape.
/// </summary>
internal static class LogLineFormat
{
    public const string Info = "INF";
    public const string Warn = "WRN";
    public const string Error = "ERR";

    private const int MaxTagLength = 40;

    public static string Timestamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds one log line without the trailing newline. A message that already opens with a
    /// <c>[tag]</c> keeps it; otherwise <paramref name="defaultTag"/> is prefixed. Line breaks are
    /// folded so one entry stays one line. <paramref name="pid"/> follows the tag as <c>pid=N</c>
    /// for files several processes append to. <paramref name="newline"/> appends the terminator for append-only writers.
    /// </summary>
    public static string Line(DateTime utc, string level, string defaultTag, string message, int? pid = null, bool newline = false)
    {
        message = Fold(message);
        string tag;
        string body;
        if (TryTakeTag(message, out var found, out var rest))
        {
            tag = found;
            body = rest;
        }
        else
        {
            tag = defaultTag;
            body = message;
        }

        var sb = new StringBuilder(48 + body.Length);
        sb.Append(Timestamp(utc)).Append(' ').Append(level).Append(" [").Append(tag).Append(']');
        if (pid is { } p)
            sb.Append(" pid=").Append(p.ToString(CultureInfo.InvariantCulture));
        if (body.Length > 0)
            sb.Append(' ').Append(body);
        if (newline)
            sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Folds CR/LF runs to " | " and trims, so a multi-line message cannot produce untimestamped continuation lines.</summary>
    public static string Fold(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;
        if (message.AsSpan().IndexOfAny('\r', '\n') < 0)
            return message.Trim();
        var parts = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", parts);
    }

    /// <summary>Last namespace segment of a logger category, generic arguments dropped, in kebab case ("Microsoft.Hosting.Lifetime" gives "lifetime", "MdnsAdvertiser" gives "mdns-advertiser").</summary>
    public static string Kebab(string category)
    {
        if (string.IsNullOrEmpty(category))
            return "log";
        // Generic categories read "Ns.Outer.Gen<Ns.Arg>" or "Ns.Gen`1": name the generic type, not its argument.
        var cut = category.IndexOfAny(new[] { '<', '`' });
        var name = cut > 0 ? category[..cut] : category;
        var start = name.LastIndexOf('.') + 1;
        var segment = name.AsSpan(start);
        if (segment.IsEmpty)
            return "log";
        var sb = new StringBuilder(segment.Length + 4);
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && !char.IsUpper(segment[i - 1]) && segment[i - 1] != '-';
                var acronymEnd = i > 0 && char.IsUpper(segment[i - 1]) && i + 1 < segment.Length && char.IsLower(segment[i + 1]);
                if (prevLower || acronymEnd)
                    sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    internal static bool TryTakeTag(string message, out string tag, out string rest)
    {
        tag = string.Empty;
        rest = message;
        if (message.Length < 3 || message[0] != '[')
            return false;
        var close = message.IndexOf(']');
        if (close < 2 || close > MaxTagLength)
            return false;
        var inner = message.AsSpan(1, close - 1);
        foreach (var c in inner)
        {
            if (c == ' ' || c == '[')
                return false;
        }
        tag = inner.ToString();
        rest = message[(close + 1)..].TrimStart();
        return true;
    }
}

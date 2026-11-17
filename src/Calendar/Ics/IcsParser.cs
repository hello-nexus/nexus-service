using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Calendar.Ics;

/// <summary>
/// Hand-written RFC 5545 reader for the subset calendar subscriptions use.
/// Lenient: a malformed property is skipped, a malformed event is dropped, and
/// nothing thrown carries input text (exceptions reach logs and error reports).
/// </summary>
public static class IcsParser
{
    public const int MaxNotesChars = 4096;
    public const int MaxEvents = 50_000;
    private const int MaxTitleChars = 500;

    public static IcsFeed Parse(string text)
    {
        var lines = IcsLexer.Unfold(text);
        var feed = new IcsFeed();
        var sawCalendar = false;
        IcsEvent? ev = null;
        var startSeen = false;
        IcsZoneDef? zone = null;
        IcsObservance? obs = null;
        var skipDepth = 0;

        foreach (var raw in lines)
        {
            var line = IcsLexer.ParseLine(raw);
            if (line is null)
            {
                continue;
            }
            if (line.Name == "BEGIN")
            {
                var kind = line.Value.ToUpperInvariant();
                if (kind == "VCALENDAR")
                {
                    sawCalendar = true;
                }
                else if (skipDepth > 0)
                {
                    skipDepth++;
                }
                else if (kind == "VEVENT" && ev is null && zone is null)
                {
                    ev = new IcsEvent();
                    startSeen = false;
                }
                else if (kind == "VTIMEZONE" && ev is null && zone is null)
                {
                    zone = new IcsZoneDef();
                }
                else if ((kind == "STANDARD" || kind == "DAYLIGHT") && zone is not null && obs is null)
                {
                    obs = new IcsObservance();
                }
                else
                {
                    skipDepth = 1;
                }
                continue;
            }
            if (line.Name == "END")
            {
                var kind = line.Value.ToUpperInvariant();
                if (skipDepth > 0)
                {
                    skipDepth--;
                }
                else if (kind == "VEVENT" && ev is not null)
                {
                    if (startSeen && Finish(ev, feed) && feed.Events.Count < MaxEvents)
                    {
                        feed.Events.Add(ev);
                    }
                    ev = null;
                }
                else if ((kind == "STANDARD" || kind == "DAYLIGHT") && zone is not null && obs is not null)
                {
                    zone.Observances.Add(obs);
                    obs = null;
                }
                else if (kind == "VTIMEZONE" && zone is not null)
                {
                    if (zone.Tzid.Length > 0 && zone.Observances.Count > 0)
                    {
                        feed.Zones.Add(zone);
                    }
                    zone = null;
                }
                continue;
            }
            if (skipDepth > 0)
            {
                continue;
            }
            if (obs is not null)
            {
                ReadObservanceProperty(obs, line);
            }
            else if (zone is not null)
            {
                if (line.Name == "TZID")
                {
                    zone.Tzid = line.Value.Trim();
                }
            }
            else if (ev is not null)
            {
                startSeen |= ReadEventProperty(ev, line, feed);
            }
            else if (line.Name == "X-WR-TIMEZONE")
            {
                feed.DefaultTz = line.Value.Trim();
            }
        }

        if (!sawCalendar)
        {
            throw new IcsFormatException();
        }
        return feed;
    }

    private static void ReadObservanceProperty(IcsObservance obs, IcsLine line)
    {
        switch (line.Name)
        {
            case "DTSTART":
                if (TryStamp(line, out var st, out _))
                {
                    obs.StartTicks = st.Ticks;
                }
                break;
            case "TZOFFSETFROM":
                if (TryOffset(line.Value, out var from))
                {
                    obs.OffsetFromSeconds = from;
                }
                break;
            case "TZOFFSETTO":
                if (TryOffset(line.Value, out var to))
                {
                    obs.OffsetToSeconds = to;
                }
                break;
            case "RRULE":
                obs.Rrule = line.Value;
                break;
            case "RDATE":
                foreach (var part in line.Value.Split(','))
                {
                    if (TryDateTime(part.Trim(), out var ticks, out _, out _))
                    {
                        obs.RdateTicks.Add(ticks);
                    }
                }
                break;
        }
    }

    /// <returns>True when the line was a readable DTSTART.</returns>
    private static bool ReadEventProperty(IcsEvent ev, IcsLine line, IcsFeed feed)
    {
        switch (line.Name)
        {
            case "UID":
                ev.Uid = line.Value.Trim();
                break;
            case "SUMMARY":
                ev.Summary = Cap(IcsLexer.UnescapeText(line.Value), MaxTitleChars);
                break;
            case "LOCATION":
                ev.Location = NullIfEmpty(Cap(IcsLexer.UnescapeText(line.Value), MaxTitleChars));
                break;
            case "DESCRIPTION":
                ev.Description = NullIfEmpty(Cap(IcsLexer.UnescapeText(line.Value), MaxNotesChars));
                break;
            case "STATUS":
                var s = line.Value.Trim().ToUpperInvariant();
                ev.Cancelled = s == "CANCELLED";
                ev.Tentative = s == "TENTATIVE";
                break;
            case "DTSTART":
                if (TryStamp(line, out var start, out var allDay))
                {
                    ev.Start = start;
                    ev.AllDay = allDay;
                    return true;
                }
                break;
            case "DTEND":
                if (TryStamp(line, out var end, out _))
                {
                    ev.End = end;
                }
                break;
            case "DURATION":
                if (TryDuration(line.Value, out var dur))
                {
                    ev.DurationSeconds = dur;
                }
                break;
            case "RRULE":
                ev.Rrule = line.Value;
                break;
            case "EXDATE":
                foreach (var part in line.Value.Split(','))
                {
                    if (TryStampValue(part.Trim(), line, out var ex))
                    {
                        ev.Exdates.Add(ex);
                    }
                }
                break;
            case "RDATE":
                foreach (var part in line.Value.Split(','))
                {
                    // PERIOD values (start/end) keep their start and use the master duration.
                    var slash = part.IndexOf('/');
                    var piece = (slash >= 0 ? part.Substring(0, slash) : part).Trim();
                    if (TryStampValue(piece, line, out var rd))
                    {
                        ev.Rdates.Add(rd);
                    }
                }
                break;
            case "RECURRENCE-ID":
                if (TryStamp(line, out var rid, out _))
                {
                    ev.RecurrenceId = rid;
                }
                break;
            case "X-WR-TIMEZONE":
                feed.DefaultTz ??= line.Value.Trim();
                break;
        }
        return false;
    }

    private static bool Finish(IcsEvent ev, IcsFeed feed)
    {
        if (ev.Uid.Length == 0)
        {
            ev.Uid = "nouid:" + ev.Summary + "|" + ev.Start.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        if (ev.Rrule is not null && RecurrenceRule.Parse(ev.Rrule) is { Supported: false })
        {
            ev.Rrule = null;
            feed.UnsupportedRules++;
        }
        return true;
    }

    private static string Cap(string s, int max) => s.Length > max ? s.Substring(0, max) : s;
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static bool TryStamp(IcsLine line, out IcsStamp stamp, out bool allDay) =>
        TryStampValue(line.Value.Trim(), line, out stamp, out allDay);

    private static bool TryStampValue(string value, IcsLine line, out IcsStamp stamp) =>
        TryStampValue(value, line, out stamp, out _);

    private static bool TryStampValue(string value, IcsLine line, out IcsStamp stamp, out bool allDay)
    {
        stamp = new IcsStamp();
        allDay = false;
        var isDateParam = string.Equals(line.Param("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase);
        if (!TryDateTime(value, out var ticks, out var utc, out var dateOnly))
        {
            return false;
        }
        allDay = dateOnly || isDateParam;
        stamp.Ticks = ticks;
        stamp.Tz = utc ? "UTC" : allDay ? null : NullIfEmpty(line.Param("TZID")?.Trim() ?? "");
        return true;
    }

    internal static bool TryDateTime(string v, out long ticks, out bool utc, out bool dateOnly)
    {
        ticks = 0;
        utc = false;
        dateOnly = false;
        if (v.Length < 8)
        {
            return false;
        }
        if (!Digits(v, 0, 4, out var y) || !Digits(v, 4, 2, out var mo) || !Digits(v, 6, 2, out var d))
        {
            return false;
        }
        int h = 0, mi = 0, s = 0;
        if (v.Length == 8)
        {
            dateOnly = true;
        }
        else
        {
            if (v[8] != 'T' && v[8] != 't')
            {
                return false;
            }
            if (v.Length < 15 || !Digits(v, 9, 2, out h) || !Digits(v, 11, 2, out mi) || !Digits(v, 13, 2, out s))
            {
                return false;
            }
            utc = v.Length > 15 && (v[15] == 'Z' || v[15] == 'z');
        }
        if (y < 1 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 60)
        {
            return false;
        }
        ticks = new DateTime(y, mo, d, h, mi, Math.Min(s, 59), DateTimeKind.Unspecified).Ticks;
        return true;
    }

    private static bool Digits(string s, int at, int n, out int value)
    {
        value = 0;
        if (at + n > s.Length)
        {
            return false;
        }
        for (var i = 0; i < n; i++)
        {
            var c = s[at + i];
            if (c < '0' || c > '9')
            {
                return false;
            }
            value = value * 10 + (c - '0');
        }
        return true;
    }

    internal static bool TryOffset(string v, out int seconds)
    {
        seconds = 0;
        v = v.Trim();
        if (v.Length < 5 || (v[0] != '+' && v[0] != '-'))
        {
            return false;
        }
        if (!Digits(v, 1, 2, out var h) || !Digits(v, 3, 2, out var m))
        {
            return false;
        }
        var s = 0;
        if (v.Length >= 7 && !Digits(v, 5, 2, out s))
        {
            return false;
        }
        seconds = (h * 3600 + m * 60 + s) * (v[0] == '-' ? -1 : 1);
        return true;
    }

    internal static bool TryDuration(string v, out long seconds)
    {
        seconds = 0;
        v = v.Trim();
        var sign = 1;
        var i = 0;
        if (i < v.Length && (v[i] == '+' || v[i] == '-'))
        {
            sign = v[i] == '-' ? -1 : 1;
            i++;
        }
        if (i >= v.Length || (v[i] != 'P' && v[i] != 'p'))
        {
            return false;
        }
        i++;
        long total = 0;
        var inTime = false;
        long num = 0;
        var haveNum = false;
        for (; i < v.Length; i++)
        {
            var c = char.ToUpperInvariant(v[i]);
            if (c >= '0' && c <= '9')
            {
                num = num * 10 + (c - '0');
                haveNum = true;
                continue;
            }
            if (c == 'T')
            {
                inTime = true;
                continue;
            }
            if (!haveNum)
            {
                return false;
            }
            switch (c)
            {
                case 'W': total += num * 7 * 86400; break;
                case 'D': total += num * 86400; break;
                case 'H' when inTime: total += num * 3600; break;
                case 'M' when inTime: total += num * 60; break;
                case 'S' when inTime: total += num; break;
                default: return false;
            }
            num = 0;
            haveNum = false;
        }
        seconds = total * sign;
        return true;
    }
}

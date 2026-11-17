using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Calendar.Ics;

internal enum RuleFreq { Daily, Weekly, Monthly, Yearly }

/// <summary>An RRULE value. <see cref="Supported"/> is false for parts this expander does not implement.</summary>
internal sealed class RecurrenceRule
{
    public RuleFreq Freq { get; private set; }
    public int Interval { get; private set; } = 1;
    public int? Count { get; private set; }
    public string? Until { get; private set; }
    public List<(int Ordinal, DayOfWeek Day)> ByDay { get; } = new();
    public List<int> ByMonthDay { get; } = new();
    public List<int> ByMonth { get; } = new();
    public List<int> BySetPos { get; } = new();
    public DayOfWeek Wkst { get; private set; } = DayOfWeek.Monday;
    public bool Supported { get; private set; } = true;

    public static RecurrenceRule Parse(string value)
    {
        var rule = new RecurrenceRule();
        var haveFreq = false;
        foreach (var part in value.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = part.Substring(0, eq).Trim().ToUpperInvariant();
            var val = part.Substring(eq + 1).Trim();
            switch (key)
            {
                case "FREQ":
                    switch (val.ToUpperInvariant())
                    {
                        case "DAILY": rule.Freq = RuleFreq.Daily; haveFreq = true; break;
                        case "WEEKLY": rule.Freq = RuleFreq.Weekly; haveFreq = true; break;
                        case "MONTHLY": rule.Freq = RuleFreq.Monthly; haveFreq = true; break;
                        case "YEARLY": rule.Freq = RuleFreq.Yearly; haveFreq = true; break;
                        default: rule.Supported = false; break;
                    }
                    break;
                case "INTERVAL":
                    if (int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var iv) && iv > 0)
                    {
                        rule.Interval = iv;
                    }
                    else
                    {
                        rule.Supported = false;
                    }
                    break;
                case "COUNT":
                    if (int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c > 0)
                    {
                        rule.Count = c;
                    }
                    else
                    {
                        rule.Supported = false;
                    }
                    break;
                case "UNTIL":
                    rule.Until = val;
                    break;
                case "BYDAY":
                    foreach (var d in val.Split(','))
                    {
                        if (TryByDay(d.Trim(), out var entry))
                        {
                            rule.ByDay.Add(entry);
                        }
                        else
                        {
                            rule.Supported = false;
                        }
                    }
                    break;
                case "BYMONTHDAY":
                    AddInts(rule, val, rule.ByMonthDay, -31, 31);
                    break;
                case "BYMONTH":
                    AddInts(rule, val, rule.ByMonth, 1, 12);
                    break;
                case "BYSETPOS":
                    AddInts(rule, val, rule.BySetPos, -366, 366);
                    break;
                case "WKST":
                    if (TryDay(val, out var w))
                    {
                        rule.Wkst = w;
                    }
                    break;
                default:
                    // BYWEEKNO, BYYEARDAY, BYHOUR, BYMINUTE, BYSECOND and anything unknown.
                    rule.Supported = false;
                    break;
            }
        }
        if (!haveFreq)
        {
            rule.Supported = false;
        }
        return rule;
    }

    private static void AddInts(RecurrenceRule rule, string val, List<int> into, int min, int max)
    {
        foreach (var p in val.Split(','))
        {
            if (int.TryParse(p.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n != 0 && n >= min && n <= max)
            {
                into.Add(n);
            }
            else
            {
                rule.Supported = false;
            }
        }
    }

    private static bool TryByDay(string s, out (int, DayOfWeek) entry)
    {
        entry = default;
        if (s.Length < 2)
        {
            return false;
        }
        var ord = 0;
        var dayText = s.Substring(s.Length - 2);
        var prefix = s.Substring(0, s.Length - 2);
        if (prefix.Length > 0 && !int.TryParse(prefix, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ord))
        {
            return false;
        }
        if (!TryDay(dayText, out var d))
        {
            return false;
        }
        entry = (ord, d);
        return true;
    }

    private static bool TryDay(string s, out DayOfWeek d)
    {
        switch (s.ToUpperInvariant())
        {
            case "SU": d = DayOfWeek.Sunday; return true;
            case "MO": d = DayOfWeek.Monday; return true;
            case "TU": d = DayOfWeek.Tuesday; return true;
            case "WE": d = DayOfWeek.Wednesday; return true;
            case "TH": d = DayOfWeek.Thursday; return true;
            case "FR": d = DayOfWeek.Friday; return true;
            case "SA": d = DayOfWeek.Saturday; return true;
            default: d = default; return false;
        }
    }
}

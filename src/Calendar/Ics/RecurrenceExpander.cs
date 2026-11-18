using System;
using System.Collections.Generic;

namespace Nexus.Service.Calendar.Ics;

/// <summary>
/// Expands an RRULE in wall-clock time (no zone), so DST never shifts an
/// instance. The caller converts each local start to an instant.
/// </summary>
internal static class RecurrenceExpander
{
    /// <summary>Upper bound on periods walked for one rule, a guard against rules that never match.</summary>
    private const int MaxPeriods = 60_000;

    /// <param name="untilLocal">Inclusive upper bound in wall-clock time, or null.</param>
    /// <param name="rangeFrom">Local starts before this are counted but not yielded; also the fast-forward target when there is no COUNT.</param>
    /// <param name="rangeTo">Local starts after this end the expansion.</param>
    public static IEnumerable<DateTime> Expand(DateTime dtstart, RecurrenceRule rule, DateTime? untilLocal, DateTime rangeFrom, DateTime rangeTo)
    {
        var time = dtstart.TimeOfDay;
        long emitted = 0;
        var i = rule.Count is null ? FirstPeriod(dtstart, rule, rangeFrom) : 0;
        for (var guard = 0; guard < MaxPeriods; guard++, i++)
        {
            var periodStart = PeriodStart(dtstart, rule, i);
            if (periodStart.Date > rangeTo.Date)
            {
                yield break;
            }
            var days = Candidates(dtstart, rule, periodStart);
            if (rule.BySetPos.Count > 0)
            {
                days = ApplySetPos(days, rule.BySetPos);
            }
            foreach (var day in days)
            {
                var start = day.Date + time;
                if (start < dtstart)
                {
                    continue;
                }
                if (untilLocal is { } until && start > until)
                {
                    yield break;
                }
                emitted++;
                if (start >= rangeFrom && start <= rangeTo)
                {
                    yield return start;
                }
                if (rule.Count is { } count && emitted >= count)
                {
                    yield break;
                }
                if (start > rangeTo)
                {
                    yield break;
                }
            }
        }
    }

    private static int FirstPeriod(DateTime dtstart, RecurrenceRule rule, DateTime rangeFrom)
    {
        if (rangeFrom <= dtstart)
        {
            return 0;
        }
        long n;
        switch (rule.Freq)
        {
            case RuleFreq.Daily:
                n = (long)(rangeFrom.Date - dtstart.Date).TotalDays / rule.Interval;
                break;
            case RuleFreq.Weekly:
                n = (long)(WeekStart(rangeFrom.Date, rule.Wkst) - WeekStart(dtstart.Date, rule.Wkst)).TotalDays / (7L * rule.Interval);
                break;
            case RuleFreq.Monthly:
                n = ((rangeFrom.Year - dtstart.Year) * 12L + rangeFrom.Month - dtstart.Month) / rule.Interval;
                break;
            default:
                n = (rangeFrom.Year - dtstart.Year) / (long)rule.Interval;
                break;
        }
        return (int)Math.Max(0, Math.Min(n - 1, int.MaxValue - MaxPeriods));
    }

    private static DateTime PeriodStart(DateTime dtstart, RecurrenceRule rule, long i)
    {
        try
        {
            switch (rule.Freq)
            {
                case RuleFreq.Daily:
                    return dtstart.Date.AddDays(i * rule.Interval);
                case RuleFreq.Weekly:
                    return WeekStart(dtstart.Date, rule.Wkst).AddDays(i * rule.Interval * 7);
                case RuleFreq.Monthly:
                    return new DateTime(dtstart.Year, dtstart.Month, 1).AddMonths((int)(i * rule.Interval));
                default:
                    return new DateTime(dtstart.Year, 1, 1).AddYears((int)(i * rule.Interval));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MaxValue.Date;
        }
    }

    internal static DateTime WeekStart(DateTime date, DayOfWeek wkst)
    {
        var diff = ((int)date.DayOfWeek - (int)wkst + 7) % 7;
        return date.AddDays(-diff);
    }

    private static List<DateTime> Candidates(DateTime dtstart, RecurrenceRule rule, DateTime periodStart)
    {
        var result = new List<DateTime>();
        if (periodStart.Year >= 9999)
        {
            return result;
        }
        DateTime first;
        int span;
        switch (rule.Freq)
        {
            case RuleFreq.Daily:
                first = periodStart;
                span = 1;
                break;
            case RuleFreq.Weekly:
                first = periodStart;
                span = 7;
                break;
            case RuleFreq.Monthly:
                first = periodStart;
                span = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);
                break;
            default:
                first = periodStart;
                span = DateTime.IsLeapYear(periodStart.Year) ? 366 : 365;
                break;
        }
        for (var k = 0; k < span; k++)
        {
            var d = first.AddDays(k);
            if (d.Year > 9998)
            {
                break;
            }
            if (Matches(dtstart, rule, d))
            {
                result.Add(d);
            }
        }
        return result;
    }

    private static bool Matches(DateTime dtstart, RecurrenceRule rule, DateTime d)
    {
        var hasMonthDay = rule.ByMonthDay.Count > 0;
        var hasDay = rule.ByDay.Count > 0;
        if (rule.ByMonth.Count > 0)
        {
            if (!rule.ByMonth.Contains(d.Month))
            {
                return false;
            }
        }
        else if (rule.Freq == RuleFreq.Yearly && !hasMonthDay && !hasDay && d.Month != dtstart.Month)
        {
            return false;
        }

        if (hasMonthDay)
        {
            var dim = DateTime.DaysInMonth(d.Year, d.Month);
            var ok = false;
            foreach (var n in rule.ByMonthDay)
            {
                if ((n > 0 && d.Day == n) || (n < 0 && d.Day == dim + n + 1))
                {
                    ok = true;
                    break;
                }
            }
            if (!ok)
            {
                return false;
            }
        }
        if (hasDay)
        {
            var ok = false;
            foreach (var (ordinal, day) in rule.ByDay)
            {
                if (d.DayOfWeek != day)
                {
                    continue;
                }
                if (ordinal == 0 || rule.Freq is RuleFreq.Daily or RuleFreq.Weekly || OrdinalMatches(d, ordinal, rule))
                {
                    ok = true;
                    break;
                }
            }
            if (!ok)
            {
                return false;
            }
        }
        if (!hasMonthDay && !hasDay)
        {
            switch (rule.Freq)
            {
                case RuleFreq.Weekly:
                    return d.DayOfWeek == dtstart.DayOfWeek;
                case RuleFreq.Monthly:
                case RuleFreq.Yearly:
                    return d.Day == dtstart.Day;
            }
        }
        return true;
    }

    private static bool OrdinalMatches(DateTime d, int ordinal, RecurrenceRule rule)
    {
        // Within the month for MONTHLY and for YEARLY with BYMONTH, within the year otherwise.
        var inMonth = rule.Freq == RuleFreq.Monthly || rule.ByMonth.Count > 0;
        int nth, fromEnd;
        if (inMonth)
        {
            nth = (d.Day - 1) / 7 + 1;
            fromEnd = (DateTime.DaysInMonth(d.Year, d.Month) - d.Day) / 7 + 1;
        }
        else
        {
            nth = (d.DayOfYear - 1) / 7 + 1;
            fromEnd = ((DateTime.IsLeapYear(d.Year) ? 366 : 365) - d.DayOfYear) / 7 + 1;
        }
        return ordinal > 0 ? nth == ordinal : fromEnd == -ordinal;
    }

    private static List<DateTime> ApplySetPos(List<DateTime> days, List<int> positions)
    {
        var picked = new List<DateTime>();
        foreach (var p in positions)
        {
            var idx = p > 0 ? p - 1 : days.Count + p;
            if (idx >= 0 && idx < days.Count && !picked.Contains(days[idx]))
            {
                picked.Add(days[idx]);
            }
        }
        picked.Sort();
        return picked;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Models.Calendar;

namespace Nexus.Service.Calendar.Ics;

/// <summary>
/// Turns cached masters and overrides into the concrete events inside a range.
/// Recurrence is expanded here, at query time, so any range works without
/// re-syncing.
/// </summary>
public static class IcsEventExpander
{
    private const long DayMs = 86_400_000L;
    /// <summary>All-day dates carry no zone; they are matched against the range with this slack so a viewer in any zone still gets them.</summary>
    private const long AllDaySlackMs = 14L * 3_600_000L;
    private const int MaxInstancesPerEvent = 5_000;

    public static void Expand(IcsFeed feed, string calendarId, long fromMs, long toMs, List<CalendarEventDto> into)
    {
        var zones = new IcsZoneResolver(feed);

        var overrides = new Dictionary<string, List<IcsEvent>>(StringComparer.Ordinal);
        foreach (var e in feed.Events)
        {
            if (e.RecurrenceId is not null)
            {
                if (!overrides.TryGetValue(e.Uid, out var list))
                {
                    overrides[e.Uid] = list = new List<IcsEvent>();
                }
                list.Add(e);
            }
        }

        foreach (var e in feed.Events)
        {
            if (e.RecurrenceId is not null)
            {
                if (!e.Cancelled)
                {
                    AddIfOverlapping(e, calendarId, KeyOf(e, e.RecurrenceId, zones), SingleOccurrence(e, zones), fromMs, toMs, into);
                }
                continue;
            }
            if (e.Cancelled)
            {
                continue;
            }
            overrides.TryGetValue(e.Uid, out var mine);
            ExpandMaster(e, mine, calendarId, zones, fromMs, toMs, into);
        }
    }

    private readonly record struct Occurrence(long StartMs, long EndMs, string? StartDate, string? EndDate);

    private static void ExpandMaster(IcsEvent e, List<IcsEvent>? overrides, string calendarId, IcsZoneResolver zones, long fromMs, long toMs, List<CalendarEventDto> into)
    {
        var master = SingleOccurrence(e, zones);
        if (e.Rrule is null && e.Rdates.Count == 0)
        {
            AddIfOverlapping(e, calendarId, KeyOf(e, null, zones), master, fromMs, toMs, into);
            return;
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in e.Exdates)
        {
            excluded.Add(KeyOfStamp(e, x, zones));
        }
        if (overrides is not null)
        {
            foreach (var o in overrides)
            {
                excluded.Add(KeyOfStamp(e, o.RecurrenceId!, zones));
            }
        }

        var zone = e.AllDay ? FixedUtcZone.Instance : zones.Resolve(e.Start.Tz);
        var startLocal = new DateTime(e.Start.Ticks, DateTimeKind.Unspecified);
        var count = 0;

        void Emit(DateTime local)
        {
            if (count >= MaxInstancesPerEvent)
            {
                return;
            }
            var occ = e.AllDay
                ? AllDayOccurrence(local, master)
                : new Occurrence(zone.LocalToUtcMs(local), zone.LocalToUtcMs(local) + (master.EndMs - master.StartMs), null, null);
            var key = e.AllDay ? DateKey(local) : occ.StartMs.ToString(CultureInfo.InvariantCulture);
            if (excluded.Contains(key))
            {
                return;
            }
            if (AddIfOverlapping(e, calendarId, key, occ, fromMs, toMs, into))
            {
                count++;
            }
        }

        // Local-time padding covers zone offsets and the event's own length at the range edges.
        var spanMs = Math.Max(0, master.EndMs - master.StartMs);
        var rangeFromLocal = LocalBound(zone, fromMs - spanMs, -1, e.AllDay);
        var rangeToLocal = LocalBound(zone, toMs, 1, e.AllDay);

        if (e.Rrule is not null)
        {
            var rule = RecurrenceRule.Parse(e.Rrule);
            if (rule.Supported)
            {
                DateTime? until = UntilLocal(rule.Until, zone, e.AllDay);
                foreach (var local in RecurrenceExpander.Expand(startLocal, rule, until, rangeFromLocal, rangeToLocal))
                {
                    Emit(local);
                }
            }
        }
        else
        {
            // RDATE-only: the master instance itself is not repeated by the rule, so it is emitted here.
            Emit(startLocal);
        }
        foreach (var rd in e.Rdates)
        {
            var local = new DateTime(rd.Ticks, DateTimeKind.Unspecified);
            if (rd.Tz == "UTC" && !e.AllDay)
            {
                local = zone.UtcMsToLocal(FixedUtcZone.Instance.LocalToUtcMs(local));
            }
            Emit(local);
        }
    }

    private static DateTime LocalBound(IIcsZone zone, long utcMs, int padDays, bool allDay)
    {
        var local = allDay
            ? FixedUtcZone.Instance.UtcMsToLocal(utcMs)
            : zone.UtcMsToLocal(Math.Clamp(utcMs, -62135596800000L, 253402300799000L));
        try
        {
            return local.AddDays(padDays);
        }
        catch (ArgumentOutOfRangeException)
        {
            return padDays < 0 ? DateTime.MinValue : DateTime.MaxValue;
        }
    }

    private static DateTime? UntilLocal(string? until, IIcsZone zone, bool allDay)
    {
        if (until is null || !IcsParser.TryDateTime(until, out var ticks, out var utc, out var dateOnly))
        {
            return null;
        }
        var t = new DateTime(ticks, DateTimeKind.Unspecified);
        if (dateOnly)
        {
            return t.AddDays(1).AddTicks(-1);
        }
        if (utc && !allDay)
        {
            return zone.UtcMsToLocal(FixedUtcZone.Instance.LocalToUtcMs(t));
        }
        return t;
    }

    private static Occurrence SingleOccurrence(IcsEvent e, IcsZoneResolver zones)
    {
        if (e.AllDay)
        {
            var startDate = new DateTime(e.Start.Ticks, DateTimeKind.Unspecified).Date;
            DateTime endDate;
            if (e.End is not null)
            {
                endDate = new DateTime(e.End.Ticks, DateTimeKind.Unspecified).Date;
            }
            else if (e.DurationSeconds is { } d)
            {
                endDate = startDate.AddDays(Math.Max(1, (int)Math.Ceiling(d / 86400.0)));
            }
            else
            {
                endDate = startDate.AddDays(1);
            }
            if (endDate <= startDate)
            {
                endDate = startDate.AddDays(1);
            }
            return new Occurrence(DateMs(startDate), DateMs(endDate), DateKey(startDate), DateKey(endDate));
        }

        var zone = zones.Resolve(e.Start.Tz);
        var start = zone.LocalToUtcMs(new DateTime(e.Start.Ticks, DateTimeKind.Unspecified));
        long end;
        if (e.End is not null)
        {
            end = zones.Resolve(e.End.Tz ?? e.Start.Tz).LocalToUtcMs(new DateTime(e.End.Ticks, DateTimeKind.Unspecified));
        }
        else if (e.DurationSeconds is { } d)
        {
            end = start + d * 1000;
        }
        else
        {
            end = start;
        }
        return new Occurrence(start, Math.Max(start, end), null, null);
    }

    private static Occurrence AllDayOccurrence(DateTime day, Occurrence master)
    {
        var spanDays = (int)Math.Max(1, Math.Round((master.EndMs - master.StartMs) / (double)DayMs));
        var start = day.Date;
        var end = start.AddDays(spanDays);
        return new Occurrence(DateMs(start), DateMs(end), DateKey(start), DateKey(end));
    }

    private static long DateMs(DateTime date) =>
        TimeZoneInfoMidnight(date);

    /// <summary>Epoch ms of local midnight on the PC, so all-day events sort sensibly; clients use the date strings.</summary>
    private static long TimeZoneInfoMidnight(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    private static string DateKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string KeyOf(IcsEvent e, IcsStamp? recurrenceId, IcsZoneResolver zones) =>
        recurrenceId is null ? "" : KeyOfStamp(e, recurrenceId, zones);

    private static string KeyOfStamp(IcsEvent e, IcsStamp stamp, IcsZoneResolver zones)
    {
        var local = new DateTime(stamp.Ticks, DateTimeKind.Unspecified);
        if (e.AllDay)
        {
            return DateKey(local);
        }
        var ms = zones.Resolve(stamp.Tz ?? e.Start.Tz).LocalToUtcMs(local);
        return ms.ToString(CultureInfo.InvariantCulture);
    }

    private static bool AddIfOverlapping(IcsEvent e, string calendarId, string recurrenceKey, Occurrence occ, long fromMs, long toMs, List<CalendarEventDto> into)
    {
        long lo = occ.StartMs, hi = occ.EndMs;
        if (e.AllDay)
        {
            lo -= AllDaySlackMs;
            hi += AllDaySlackMs;
        }
        var overlaps = lo < toMs && (hi > fromMs || (hi == lo && lo >= fromMs));
        if (!overlaps)
        {
            return false;
        }
        into.Add(new CalendarEventDto
        {
            Id = EventId(calendarId, e.Uid, recurrenceKey),
            CalendarId = calendarId,
            Title = e.Summary,
            Location = e.Location,
            Notes = e.Description,
            AllDay = e.AllDay,
            Start = occ.StartMs,
            End = occ.EndMs,
            StartDate = occ.StartDate,
            EndDate = occ.EndDate,
            Status = e.Tentative ? "tentative" : "confirmed",
        });
        return true;
    }

    /// <summary>Stable id: a hash of the source, the UID and the instance key, so a refresh never renames an event.</summary>
    internal static string EventId(string calendarId, string uid, string recurrenceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(calendarId + "\n" + uid + "\n" + recurrenceKey));
        return "ev_" + Convert.ToHexString(bytes, 0, 10).ToLowerInvariant();
    }
}

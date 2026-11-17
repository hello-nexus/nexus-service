using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Calendar.Ics;

/// <summary>Converts wall-clock times in one zone to and from instants.</summary>
internal interface IIcsZone
{
    /// <summary>A wall time inside a spring-forward gap resolves as if the clock had already moved.</summary>
    long LocalToUtcMs(DateTime local);

    DateTime UtcMsToLocal(long utcMs);
}

internal sealed class TimeZoneInfoZone : IIcsZone
{
    private readonly TimeZoneInfo _tz;

    public TimeZoneInfoZone(TimeZoneInfo tz) => _tz = tz;

    public long LocalToUtcMs(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, _tz);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    public DateTime UtcMsToLocal(long utcMs) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime, _tz);
}

/// <summary>A zone built from the feed's own VTIMEZONE observances.</summary>
internal sealed class VTimeZoneZone : IIcsZone
{
    private readonly IcsZoneDef _def;
    private readonly Dictionary<int, List<(DateTime OnsetLocal, int OffsetTo, int OffsetFrom)>> _byYear = new();

    public VTimeZoneZone(IcsZoneDef def) => _def = def;

    public long LocalToUtcMs(DateTime local)
    {
        var offset = OffsetForLocal(local);
        var utcTicks = local.Ticks - offset * TimeSpan.TicksPerSecond;
        return new DateTimeOffset(new DateTime(Math.Clamp(utcTicks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    public DateTime UtcMsToLocal(long utcMs)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime;
        // The offset in effect for the instant: try the offsets this zone uses and keep the one that round-trips.
        foreach (var offset in CandidateOffsets(utc.Year))
        {
            var local = utc.AddSeconds(offset);
            if (OffsetForLocal(local) == offset)
            {
                return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            }
        }
        return DateTime.SpecifyKind(utc.AddSeconds(OffsetForLocal(utc)), DateTimeKind.Unspecified);
    }

    private IEnumerable<int> CandidateOffsets(int year)
    {
        var seen = new HashSet<int>();
        foreach (var t in Transitions(year))
        {
            if (seen.Add(t.OffsetTo))
            {
                yield return t.OffsetTo;
            }
        }
        foreach (var o in _def.Observances)
        {
            if (seen.Add(o.OffsetToSeconds))
            {
                yield return o.OffsetToSeconds;
            }
        }
    }

    private int OffsetForLocal(DateTime local)
    {
        // Transitions are in the wall time of the offset they leave, so the latest one at or before
        // the wall time decides the offset. Search the neighbouring years for a Dec/Jan boundary.
        var best = default((DateTime OnsetLocal, int OffsetTo, int OffsetFrom));
        var found = false;
        for (var y = local.Year - 1; y <= local.Year; y++)
        {
            foreach (var t in Transitions(y))
            {
                if (t.OnsetLocal <= local && (!found || t.OnsetLocal > best.OnsetLocal))
                {
                    best = t;
                    found = true;
                }
            }
        }
        if (found)
        {
            return best.OffsetTo;
        }
        var first = _def.Observances.OrderBy(o => o.StartTicks).First();
        return first.OffsetFromSeconds;
    }

    private List<(DateTime OnsetLocal, int OffsetTo, int OffsetFrom)> Transitions(int year)
    {
        lock (_byYear)
        {
            if (_byYear.TryGetValue(year, out var cached))
            {
                return cached;
            }
        }
        var list = new List<(DateTime, int, int)>();
        var from = new DateTime(Math.Max(1, year), 1, 1);
        var to = new DateTime(Math.Min(9998, year), 12, 31, 23, 59, 59);
        foreach (var o in _def.Observances)
        {
            var start = new DateTime(Math.Clamp(o.StartTicks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Unspecified);
            if (start >= from && start <= to)
            {
                list.Add((start, o.OffsetToSeconds, o.OffsetFromSeconds));
            }
            foreach (var r in o.RdateTicks)
            {
                var rd = new DateTime(Math.Clamp(r, 0, DateTime.MaxValue.Ticks), DateTimeKind.Unspecified);
                if (rd >= from && rd <= to)
                {
                    list.Add((rd, o.OffsetToSeconds, o.OffsetFromSeconds));
                }
            }
            if (o.Rrule is not null && start <= to)
            {
                var rule = RecurrenceRule.Parse(o.Rrule);
                if (rule.Supported)
                {
                    DateTime? until = TryUntil(rule.Until);
                    foreach (var onset in RecurrenceExpander.Expand(start, rule, until, from, to))
                    {
                        list.Add((onset, o.OffsetToSeconds, o.OffsetFromSeconds));
                    }
                }
            }
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        lock (_byYear)
        {
            _byYear[year] = list;
        }
        return list;
    }

    private static DateTime? TryUntil(string? until)
    {
        if (until is not null && IcsParser.TryDateTime(until, out var ticks, out _, out _))
        {
            return new DateTime(ticks, DateTimeKind.Unspecified);
        }
        return null;
    }
}

internal sealed class FixedUtcZone : IIcsZone
{
    public static readonly FixedUtcZone Instance = new();

    public long LocalToUtcMs(DateTime local) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public DateTime UtcMsToLocal(long utcMs) =>
        DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime, DateTimeKind.Unspecified);
}

/// <summary>
/// Looks a TZID up in this order: the host's own database, the Windows/IANA name
/// map, the feed's VTIMEZONE, then the PC's local zone. The map step exists
/// because invariant globalization drops the ICU id conversion on both sides.
/// </summary>
internal sealed class IcsZoneResolver
{
    private readonly IReadOnlyList<IcsZoneDef> _defs;
    private readonly string? _defaultTz;
    private readonly Dictionary<string, IIcsZone> _cache = new(StringComparer.Ordinal);

    public IcsZoneResolver(IcsFeed feed)
    {
        _defs = feed.Zones;
        _defaultTz = feed.DefaultTz;
    }

    /// <summary>Distinct TZIDs that fell through to the local zone.</summary>
    public int Fallbacks { get; private set; }

    public IIcsZone Resolve(string? tz)
    {
        if (tz == "UTC")
        {
            return FixedUtcZone.Instance;
        }
        var key = string.IsNullOrWhiteSpace(tz) ? (_defaultTz ?? "") : tz;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var zone = key.Length == 0 ? new TimeZoneInfoZone(TimeZoneInfo.Local) : Lookup(key);
        _cache[key] = zone;
        return zone;
    }

    private IIcsZone Lookup(string tzid)
    {
        if (TryNative(tzid) is { } direct)
        {
            return new TimeZoneInfoZone(direct);
        }
        var mapped = WindowsZoneMap.ToIana(tzid) is { } iana && TryNative(iana) is { } viaIana
            ? viaIana
            : WindowsZoneMap.ToWindows(tzid) is { } win ? TryNative(win) : null;
        if (mapped is not null)
        {
            return new TimeZoneInfoZone(mapped);
        }
        var def = _defs.FirstOrDefault(d => string.Equals(d.Tzid, tzid, StringComparison.OrdinalIgnoreCase));
        if (def is not null)
        {
            return new VTimeZoneZone(def);
        }
        Fallbacks++;
        return new TimeZoneInfoZone(TimeZoneInfo.Local);
    }

    private static TimeZoneInfo? TryNative(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }
}

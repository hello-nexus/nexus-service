using System.Collections.Generic;

namespace Nexus.Service.Calendar.Ics;

/// <summary>
/// A parsed feed: VEVENT masters and overrides kept as authored (never expanded),
/// plus the feed's own VTIMEZONE definitions. This is what the on-disk cache stores.
/// </summary>
public sealed class IcsFeed
{
    public List<IcsEvent> Events { get; set; } = new();
    public List<IcsZoneDef> Zones { get; set; } = new();
    /// <summary>X-WR-TIMEZONE, the zone floating times in the feed are meant in.</summary>
    public string? DefaultTz { get; set; }
    /// <summary>Events whose recurrence uses parts this parser does not expand; they show as a single instance.</summary>
    public int UnsupportedRules { get; set; }
}

/// <summary>A wall-clock time (or a date, at midnight) with the zone it is expressed in.</summary>
public sealed class IcsStamp
{
    public long Ticks { get; set; }
    /// <summary>Null = floating, "UTC" = trailing Z, otherwise a TZID.</summary>
    public string? Tz { get; set; }
}

public sealed class IcsEvent
{
    public string Uid { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Location { get; set; }
    public string? Description { get; set; }
    public bool AllDay { get; set; }
    public IcsStamp Start { get; set; } = new();
    public IcsStamp? End { get; set; }
    public long? DurationSeconds { get; set; }
    public string? Rrule { get; set; }
    public List<IcsStamp> Exdates { get; set; } = new();
    public List<IcsStamp> Rdates { get; set; } = new();
    public IcsStamp? RecurrenceId { get; set; }
    public bool Cancelled { get; set; }
    public bool Tentative { get; set; }
}

public sealed class IcsZoneDef
{
    public string Tzid { get; set; } = "";
    public List<IcsObservance> Observances { get; set; } = new();
}

public sealed class IcsObservance
{
    public long StartTicks { get; set; }
    public int OffsetFromSeconds { get; set; }
    public int OffsetToSeconds { get; set; }
    public string? Rrule { get; set; }
    public List<long> RdateTicks { get; set; } = new();
}

/// <summary>Raised for a body that is not an iCalendar stream. The message never carries input text.</summary>
public sealed class IcsFormatException : System.Exception
{
    public IcsFormatException() : base("Not an iCalendar document.") { }
}

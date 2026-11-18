using System.Collections.Generic;
using Nexus.Service.Calendar.Ics;

namespace Nexus.Service.Calendar;

/// <summary>
/// <c>&lt;NexusRoot&gt;/calendar/sources.json</c>. Deliberately not part of
/// NexusSettings: settings reach support bundles, exports and cloud backup,
/// and this file holds feed URLs.
/// </summary>
public sealed class CalendarSourcesFile
{
    public int RefreshMinutes { get; set; } = 15;
    public List<CalendarSourceRecord> Sources { get; set; } = new();
}

public sealed class CalendarSourceRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "ics";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4f8cff";
    /// <summary>The feed URL after SecretProtector.Protect.</summary>
    public string UrlProtected { get; set; } = "";
    public string? Host { get; set; }
    /// <summary>True once the name is final: user-chosen, or already taken from the feed. Auto-naming runs only while false.</summary>
    public bool NameFixed { get; set; }
    public long? LastSyncedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary><c>&lt;NexusRoot&gt;/calendar/cache/&lt;sourceId&gt;.json</c>: parsed masters, so panels show events offline and right after a restart.</summary>
public sealed class CalendarFeedCache
{
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public IcsFeed Feed { get; set; } = new();
}

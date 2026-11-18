using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Calendar;

/// <summary>Wire shape of one subscribed calendar. Never carries the feed URL, only its host.</summary>
public sealed class CalendarSourceDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = CalendarKinds.Ics;
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4f8cff";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Host { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LastSyncedAt { get; set; }
    public string Status { get; set; } = CalendarStatuses.Ok;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Error { get; set; }
}

public sealed class CalendarEventDto
{
    public string Id { get; set; } = "";
    public string CalendarId { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Location { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Notes { get; set; }
    public bool AllDay { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? StartDate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EndDate { get; set; }
    public string Status { get; set; } = "confirmed";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ProviderId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Etag { get; set; }
}

public sealed class CalendarListResponse
{
    public List<CalendarSourceDto> Calendars { get; set; } = new();
}

public sealed class CalendarEventsResponse
{
    public List<CalendarEventDto> Events { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? SyncedAt { get; set; }
    /// <summary>True when the range held more events than one response carries; the rest were dropped.</summary>
    public bool Truncated { get; set; }
}

public sealed class CalendarCreateRequest
{
    public string? Url { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
}

public sealed class CalendarPatchRequest
{
    public string? Name { get; set; }
    public string? Color { get; set; }
}

public sealed class CalendarSettingsDto
{
    public int RefreshMinutes { get; set; } = 15;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LastSyncedAt { get; set; }
    public bool Syncing { get; set; }
}

public sealed class CalendarSettingsRequest
{
    public int RefreshMinutes { get; set; }
}

public sealed class CalendarProbeRequest
{
    public string? Url { get; set; }
}

/// <summary>What an address would be named if added: the feed's own calendar name (or null) and its host.</summary>
public sealed class CalendarProbeResponse
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Name { get; set; }
    public string Host { get; set; } = "";
}

public sealed class CalendarRefreshResponse
{
    public bool Started { get; set; }
}

/// <summary>
/// 400 body. <c>error</c> is the machine code (also copied to <c>msg</c> so a
/// client reading the shared ApiResponse envelope still gets the code).
/// </summary>
public sealed class CalendarErrorResponse
{
    public string Error { get; set; } = "";
    public string Msg { get; set; } = "";
}

public sealed class CalendarChangedFrame
{
    public long Revision { get; set; }
}

public static class CalendarKinds
{
    public const string Ics = "ics";
}

public static class CalendarStatuses
{
    public const string Ok = "ok";
    public const string Syncing = "syncing";
    public const string Error = "error";
}

public static class CalendarErrors
{
    public const string Unreachable = "unreachable";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "notFound";
    public const string NotIcs = "notIcs";
    public const string TooLarge = "tooLarge";
    public const string InvalidUrl = "invalidUrl";
    public const string InvalidRefresh = "invalidRefresh";
    public const string InvalidRange = "invalidRange";
    public const string LimitReached = "limitReached";
}

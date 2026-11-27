using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Reminders;

public sealed class ReminderListDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#0A84FF";
    public string Icon { get; set; } = "list";
    public int Order { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class ReminderDto
{
    public string Id { get; set; } = "";
    public string ListId { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Notes { get; set; }
    /// <summary>Local date, <c>yyyy-MM-dd</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DueDate { get; set; }
    /// <summary>Local time, <c>HH:mm</c>; only with a due date.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DueTime { get; set; }
    public int Priority { get; set; }
    public bool Flagged { get; set; }
    public bool Completed { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? CompletedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Url { get; set; }
    public int Order { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ReminderStoreResponse
{
    public List<ReminderListDto> Lists { get; set; } = new();
    public List<ReminderDto> Items { get; set; } = new();
}

public sealed class ReminderClearResponse
{
    public int Removed { get; set; }
}

/// <summary>Error body. <c>code</c> is the machine code; <c>error</c> and <c>msg</c> repeat it, mirroring the calendar error body.</summary>
public sealed class ReminderErrorResponse
{
    public string Code { get; set; } = "";
    public string Error { get; set; } = "";
    public string Msg { get; set; } = "";
}

public sealed class ReminderChangedFrame
{
    public long Revision { get; set; }
}

public static class ReminderErrors
{
    public const string Invalid = "invalid";
    public const string NotFound = "notFound";
    public const string LimitReached = "limitReached";
    public const string LastList = "lastList";
    /// <summary>The write did not reach disk; nothing changed.</summary>
    /// <summary>The store file exists but cannot be loaded right now; nothing is served or written until it can.</summary>
    public const string Unavailable = "unavailable";
    public const string SaveFailed = "saveFailed";
    /// <summary>A reorder did not list exactly the list's current items; refetch and retry.</summary>
    public const string Stale = "stale";
}

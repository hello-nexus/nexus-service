using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Sentry;

/// <summary>GET /sentry and the body every Sentry mutation returns.</summary>
public sealed class SentryStatusResponse
{
    public bool Supported { get; set; }
    public bool Armed { get; set; }
    public bool Locked { get; set; }
    public int AlertPhones { get; set; }

    /// <summary>Epoch ms of the last alert, or an explicit null before the first one: the panel page tests for null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LastAlertAt { get; set; }

    public int CooldownSeconds { get; set; }
}

/// <summary>POST /sentry/arm. <see cref="Lock"/> true locks the PC first (Settings); false arms only while already locked (phone).</summary>
public sealed class SentryArmBody
{
    public bool Lock { get; set; }
}

/// <summary>Error body for the Sentry routes: <c>{ "error": "not_locked" }</c>.</summary>
public sealed class SentryErrorResponse
{
    public string Error { get; set; } = "";
}

/// <summary>PUT /panel/phone/push. <c>{pc}</c> in Title/Body is replaced with the machine name at send time.</summary>
public sealed class PanelPhonePushBody
{
    public string Platform { get; set; } = "";
    public string Token { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>One target of POST /push/sentry on nexus-api.</summary>
public sealed class SentryPushTarget
{
    public string Platform { get; set; } = "";
    public string Token { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

public sealed class SentryPushRequest
{
    public List<SentryPushTarget> Targets { get; set; } = new();
}

public sealed class SentryPushResult
{
    public string Token { get; set; } = "";

    /// <summary>sent | rate_limited | invalid | unavailable | error.</summary>
    public string Status { get; set; } = "";
}

public sealed class SentryPushResponse
{
    public List<SentryPushResult> Results { get; set; } = new();
}

using System.Collections.Generic;

namespace Nexus.Service.Models.Conflicts;

/// <summary>
/// Single match returned by <see cref="Nexus.Service.Conflicts.ConflictWatcher"/>.
/// Pid is the lowest matched pid for the process name (the SPA only ever
/// asks to terminate by Id; the pid is informational).
/// </summary>
public sealed class DetectedConflict
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int Pid { get; set; }
}

/// <summary>One enabled autostart entry of a conflicting app, as the UI names it back on disable.</summary>
public sealed class ConflictAutostartEntry
{
    /// <summary>"runKeyMachine", "runKeyUser", "service" or "scheduledTask".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Run value name, or service name.</summary>
    public string EntryName { get; set; } = "";
}

/// <summary>
/// Autostart state of one detected conflict. Only apps carrying a verified
/// recipe appear at all; an empty <see cref="Entries"/> for a listed app means
/// nothing is currently starting it at boot.
/// </summary>
public sealed class ConflictAutostartStatus
{
    public string Id { get; set; } = "";
    public List<ConflictAutostartEntry> Entries { get; set; } = new();
}

public sealed class GetConflictAutostartResponse
{
    public List<ConflictAutostartStatus> Apps { get; set; } = new();
}

/// <summary>Body for POST /conflicts/autostart/disable.</summary>
public sealed class DisableConflictAutostartBody
{
    public string Id { get; set; } = "";
}

public sealed class DisableConflictAutostartResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
    /// <summary>How many entries were disabled and verified disabled by a re-read.</summary>
    public int Disabled { get; set; }
}

/// <summary>Available is false when the console user's Lighting key could not be read, in which case every other field is meaningless.</summary>
public sealed class WindowsDynamicLightingState
{
    public bool Available { get; set; }

    /// <summary>Settings > Personalization > Dynamic Lighting, "Use Dynamic Lighting on my devices".</summary>
    public bool Enabled { get; set; }

    /// <summary>Dynamic Lighting devices connected right now; with none there is nothing to contend for.</summary>
    public int DeviceCount { get; set; }
}

/// <summary>Body for POST /conflicts/dynamic-lighting. A null leaves the setting untouched.</summary>
public sealed class SetWindowsDynamicLightingBody
{
    public bool? Enabled { get; set; }
}

public sealed class GetConflictsResponse
{
    public List<DetectedConflict> Conflicts { get; set; } = new();
}

/// <summary>
/// Body for POST /conflicts/kill. Caller passes the catalog Id of the
/// conflict to terminate; the service resolves it back to the registered
/// process names and kills every matching pid.
/// </summary>
public sealed class KillConflictBody
{
    public string Id { get; set; } = "";
}

public sealed class KillConflictResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
    /// <summary>True when the app was running before this call and is not running after it.</summary>
    public bool Killed { get; set; }
}

/// <summary>
/// WebSocket push frame on topic "conflicts". Sent each time the detected
/// set changes - an app appearing, leaving, or restarting under a new pid.
/// The list is the full current
/// snapshot - clients overwrite rather than diff.
/// </summary>
public sealed class ConflictsFrame
{
    public List<DetectedConflict> Conflicts { get; set; } = new();
}

/// <summary>
/// One <see cref="Nexus.Service.Conflicts.ConflictAppDefinition"/> as the
/// settings UI sees it. Process and service names stay server-side: the SPA
/// only ever addresses an app by Id.
/// </summary>
public sealed class ConflictCatalogApp
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
}

/// <summary>Response for GET /conflicts/catalog - the full set of apps the startup shutdown can act on.</summary>
public sealed class GetConflictCatalogResponse
{
    public List<ConflictCatalogApp> Apps { get; set; } = new();
}

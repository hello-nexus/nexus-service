using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Service.Integrations.HomeAssistant;

public sealed class HaEntityDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Domain prefix of entity_id; always one of the supported domains.</summary>
    public string Domain { get; set; } = "";
    public string State { get; set; } = "";
    public bool On { get; set; }
    public bool Reachable { get; set; }
    public int BrightnessPct { get; set; }
    public bool SupportsBrightness { get; set; }
    public bool SupportsColor { get; set; }
    public bool SupportsColorTemp { get; set; }
    public int[]? Rgb { get; set; }
    public int ColorTempK { get; set; }
    public string Area { get; set; } = "";
    /// <summary>attributes.unit_of_measurement, empty when absent.</summary>
    public string Unit { get; set; } = "";
    /// <summary>attributes.device_class, empty when absent.</summary>
    public string DeviceClass { get; set; } = "";
    /// <summary>Cover current_position, negative when absent or not a cover.</summary>
    public int PositionPct { get; set; } = -1;
    /// <summary>Lock only: the lock demands a code, which Nexus cannot supply.</summary>
    public bool CodeRequired { get; set; }
    /// <summary>Entity registry hides it (hidden_by or entity_category set).</summary>
    public bool Hidden { get; set; }
}

public sealed class HaConfigResponse
{
    public string Url { get; set; } = "";
    /// <summary>True when a token is stored (token is never returned).</summary>
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
}

public sealed class HaConfigBody
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
}

public sealed class HaConfigSetResponse
{
    public bool Ok { get; set; }
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
}

public sealed class HaEntitiesResponse
{
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
    public List<HaEntityDto> Entities { get; set; } = new();
}

public sealed class HaSetEntityBody
{
    public string EntityId { get; set; } = "";
    public bool? On { get; set; }
    public int? BrightnessPct { get; set; }
    public int[]? Rgb { get; set; }
    public int? ColorTempK { get; set; }
    /// <summary>Non-toggle commands: run, open, close, stop, lock, unlock.</summary>
    public string? Action { get; set; }
}

public sealed class HomeAssistantChangedFrame
{
    public long Revision { get; set; }
    /// <summary>Bumped on every lovelace_updated event; dashboards refetch config when it changes.</summary>
    public long DashboardsRevision { get; set; }
    /// <summary>
    /// True when the room-view entities (light/switch) or registry data changed; false when only
    /// watched dashboard entities or a dashboard config changed.
    /// </summary>
    public bool RoomsChanged { get; set; }
}

public sealed class HaDashboardDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class HaDashboardsResponse
{
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
    public List<HaDashboardDto> Dashboards { get; set; } = new();
}

public sealed class HaDashboardResponse
{
    public string Id { get; set; } = "";
    /// <summary>Empty on success, else "not_connected", "not_found" or "failed".</summary>
    public string Error { get; set; } = "";
    /// <summary>HA's raw Lovelace config, passed through untouched.</summary>
    public JsonElement? Config { get; set; }
    /// <summary>DashboardsRevision at the start of the request, to compare against later frames.</summary>
    public long Revision { get; set; }
}

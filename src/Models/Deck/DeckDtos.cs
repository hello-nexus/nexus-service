using System.Collections.Generic;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Deck;

/// <summary>One host-wide deck preset's identity for GET /deck/presets; never carries the deck config tree (see <see cref="DeckPresetFull"/>).</summary>
public class DeckPresetSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Cols { get; set; }
    public int Rows { get; set; }
    public List<PresetAppBinding>? Apps { get; set; }
    public string? TemplateId { get; set; }
    public int PageCount { get; set; }
}

/// <summary>GET /deck/presets/{id} - a summary plus the full config tree.</summary>
public sealed class DeckPresetFull : DeckPresetSummary
{
    public DeckConfig Deck { get; set; } = new();
}

public sealed class DeckPresetsListResponse
{
    public List<DeckPresetSummary> Presets { get; set; } = new();
}

public sealed class DeckPresetResponse
{
    public DeckPresetFull? Preset { get; set; }
}

/// <summary>PUT /deck/presets/{id}/apps response - a summary only, unlike the full-config PUT /deck/presets/{id}.</summary>
public sealed class DeckPresetAppsResponse
{
    public DeckPresetSummary? Preset { get; set; }
}

/// <summary>POST /deck/presets body: at most one of Deck / TemplateId / CopyOfPresetId; none of the three creates an empty preset.</summary>
public sealed class CreateDeckPresetRequest
{
    public string Name { get; set; } = "";
    public int Cols { get; set; }
    public int Rows { get; set; }
    public DeckConfig? Deck { get; set; }
    public string? TemplateId { get; set; }
    public string? CopyOfPresetId { get; set; }
}

/// <summary>One .nexus-deck template before per-request installed-app resolution (see <see cref="DeckPresetCatalog"/>).</summary>
public class DeckTemplateSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public DeckPackageMatch? Match { get; set; }
    public int PageCount { get; set; }
}

/// <summary>GET /deck/templates response item: a summary plus the installed-app resolution against IShortcutsProvider.GetAll().</summary>
public sealed class DeckTemplateDto : DeckTemplateSummary
{
    public string? InstalledAppId { get; set; }
    public string? InstalledAppName { get; set; }
    public string? ProcessName { get; set; }
}

public sealed class DeckTemplatesListResponse
{
    public List<DeckTemplateDto> Templates { get; set; } = new();
}

/// <summary>PUT /deck/presets/{id} body - the editor's auto-save; every field is optional.</summary>
public sealed class UpdateDeckPresetRequest
{
    public string? Name { get; set; }
    public DeckConfig? Deck { get; set; }
    public int? Cols { get; set; }
    public int? Rows { get; set; }
}

public sealed class DeckInstancesResponse
{
    public Dictionary<string, DeckInstance> Instances { get; set; } = new();
}

public sealed class DeckInstanceResponse
{
    public DeckInstance Instance { get; set; } = new();
}

/// <summary>PUT /deck/instances/{id} body; every field is optional.</summary>
public sealed class UpdateDeckInstanceRequest
{
    public string? Mode { get; set; }
    public string? ActivePresetId { get; set; }
}

/// <summary>
/// Multiplex frame for the "deck" topic (AllowPanel). A single discriminated
/// shape, matching StreamDeckChangedFrame's convention: fields outside a
/// frame's own Kind stay null and are omitted on the wire.
/// </summary>
public sealed class DeckChangedFrame
{
    public long Revision { get; set; }
    /// <summary>"preset" | "presets" | "active" | "recents".</summary>
    public string Kind { get; set; } = "";
    /// <summary>preset.</summary>
    public string? PresetId { get; set; }
    /// <summary>preset.</summary>
    public DeckPresetSummary? Summary { get; set; }
    /// <summary>preset.</summary>
    public DeckConfig? Deck { get; set; }
    /// <summary>presets.</summary>
    public List<DeckPresetSummary>? Presets { get; set; }
    /// <summary>active.</summary>
    public string? InstanceId { get; set; }
    /// <summary>active.</summary>
    public DeckInstance? Instance { get; set; }
    /// <summary>recents. Wire name "apps", matching RecentAppsResponse and the CONTRACT ADDENDUM's recents shape.</summary>
    public List<Nexus.Service.Persistence.RecentApp>? Apps { get; set; }
    /// <summary>recents.</summary>
    public string? FocusedProcessKey { get; set; }
}

/// <summary>GET /deck/recent-apps.</summary>
public sealed class RecentAppsResponse
{
    public List<Nexus.Service.Persistence.RecentApp> Apps { get; set; } = new();
    public List<string> Excluded { get; set; } = new();
    public string? FocusedProcessKey { get; set; }
}

/// <summary>PUT /deck/recent-apps/excluded body.</summary>
public sealed class SetRecentAppsExcludedRequest
{
    public List<string> ProcessKeys { get; set; } = new();
}

/// <summary>POST /deck/recent-apps/activate body.</summary>
public sealed class ActivateRecentAppRequest
{
    public string ProcessKey { get; set; } = "";
}

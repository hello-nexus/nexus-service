using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>A named snapshot of the desktop dashboard layout (profile-scoped).</summary>
public sealed class DashboardPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Null = the install-default desktop layout, as with a null live layout.</summary>
    public PanelLayoutDto? Layout { get; set; }
}

public sealed class DashboardPresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class DashboardPresetsResponse
{
    public List<DashboardPresetDto> Presets { get; set; } = new();
    // Always serialize; null = no preset loaded. WhenWritingNull would omit it.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? ActiveId { get; set; }
    /// <summary>False until the dashboard sends its starter set; the client seeds it once, in the UI language.</summary>
    public bool Seeded { get; set; }
}

public sealed class DashboardPresetSeedBody
{
    /// <summary>The first entry becomes the active preset.</summary>
    public List<DashboardPreset> Presets { get; set; } = new();
}

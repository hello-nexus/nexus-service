using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// One imported panel-background asset. Stored under
/// panel-backgrounds/&lt;deviceId&gt;/&lt;id&gt;/ as media.mp4 or media.jpg
/// (already cropped and scaled to the device's native resolution), plus
/// thumb.jpg and meta.json. Source file is not retained.
/// An asset that kept its transparency uses media.png / media.gif and thumb.png
/// instead; <see cref="Alpha"/> selects between the two sets.
/// </summary>
public sealed class PanelBgItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // "static" | "animated"
    public int Width { get; set; }
    public int Height { get; set; }
    public long ImportedAtUnixMs { get; set; }
    public double DurationSec { get; set; }

    /// <summary>Media is png (static) or gif (animated) and the thumbnail png. False for assets imported before transparency support.</summary>
    public bool Alpha { get; set; }
}

public sealed class PanelBgListResponse
{
    public List<PanelBgItem> Items { get; set; } = new();
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class PanelBgImportResponse
{
    public PanelBgItem? Item { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class PanelBgResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class PanelBgStageResponse
{
    public string? StageId { get; set; }
    /// <summary>video | gif | image: which element the cropper renders the source with.</summary>
    public string? MediaKind { get; set; }

    /// <summary>The staged source carries real transparency, so the cropper has a "keep transparency" choice to offer.</summary>
    public bool Alpha { get; set; }

    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

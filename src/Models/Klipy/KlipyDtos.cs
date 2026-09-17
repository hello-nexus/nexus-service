using System.Collections.Generic;

namespace Nexus.Service.Models.Klipy;

/// <summary>One GIF in the picker grid. The thumbnail is fetched from
/// /api/klipy/thumb/{slug}; Width/Height describe the file an import would
/// bake, so the client can compute the centre crop without a second round trip.</summary>
public sealed class KlipyGifDto
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Inline data: URI placeholder Klipy ships with every item.</summary>
    public string? BlurPreview { get; set; }
}

public sealed class KlipySearchResponse
{
    public List<KlipyGifDto> Items { get; set; } = new();
    public bool HasNext { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

/// <summary>A Klipy pick to stage for the lighting cropper; /media/commit finishes it.</summary>
public sealed class KlipyImportRequest
{
    public string Slug { get; set; } = "";
}

/// <summary>A Klipy pick to stage for one device's cropper; /background-media/commit finishes it.</summary>
public sealed class KlipyPanelBgStageRequest
{
    public string Slug { get; set; } = "";
}

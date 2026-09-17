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

public sealed class KlipyImportRequest
{
    public string Slug { get; set; } = "";
    /// <summary>Normalized "x,y,w,h" against the source, as /media/commit takes.</summary>
    public string Crop { get; set; } = "";
}

/// <summary>A Klipy pick imported as one device's panel background. w/h are the
/// device's panel size, as /background-media/commit takes them.</summary>
public sealed class KlipyPanelBgImportRequest
{
    public string Slug { get; set; } = "";
    public string Crop { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    public bool KeepTransparency { get; set; } = true;
    /// <summary>Letterbox the whole frame instead of filling the panel.</summary>
    public bool Fit { get; set; }
}

using System.Collections.Generic;

namespace Nexus.Service.Models.Media;

public sealed class MediaItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // "static" | "animated"
    public int Frames { get; set; }
    public int Fps { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long ImportedAtUnixMs { get; set; }
}

public sealed class MediaLibraryResponse
{
    public List<MediaItem> Items { get; set; } = new();
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class MediaImportResponse
{
    public MediaItem? Item { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class MediaPlayResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class MediaCurrentResponse
{
    public string? MediaId { get; set; }
    public MediaItem? Item { get; set; }
}

public sealed class MediaStageResponse
{
    public string? StageId { get; set; }
    /// <summary>video | gif | image: which element the cropper renders the source with.</summary>
    public string? MediaKind { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

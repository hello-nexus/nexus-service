using System.Collections.Generic;

namespace Nexus.Service.Models.Gallery;

/// <summary>Values for <see cref="GallerySource.Kind"/>.</summary>
public static class GallerySourceKinds
{
    public const string File = "file";
    public const string Folder = "folder";
    /// <summary>Add-time only: the service stats the path to pick file/folder.</summary>
    public const string Auto = "auto";
    /// <summary>Legacy stored-copy kind; migrated to <see cref="File"/> on load.</summary>
    public const string Upload = "upload";
}

public sealed class GallerySource
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long AddedAtUnixMs { get; set; }
    /// <summary>
    /// Item ids of a folder source the user removed from the gallery. The
    /// files stay on disk untouched; restoring clears this list.
    /// </summary>
    public List<string> Excluded { get; set; } = new();
}

/// <summary>Persistence shape of gallery/sources.json.</summary>
public sealed class GallerySourcesFile
{
    public List<GallerySource> Sources { get; set; } = new();
}

public sealed class GallerySourcesResponse
{
    public List<GallerySource> Sources { get; set; } = new();
}

public sealed class AddGallerySourceBody
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
}

/// <summary>Machine-readable codes for <see cref="GallerySourceMutationResponse.Code"/>.</summary>
public static class GalleryErrorCodes
{
    /// <summary>The path is already registered as a source of the same kind.</summary>
    public const string Duplicate = "duplicate";
}

public sealed class GallerySourceMutationResponse
{
    public GallerySource? Source { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
    /// <summary>Stable error code the UI can branch on; empty when n/a.</summary>
    public string Code { get; set; } = "";
}

public sealed class GalleryExcludeBody
{
    public string ItemId { get; set; } = "";
}

/// <summary>Values for <see cref="GalleryItem.Kind"/>.</summary>
public static class GalleryItemKinds
{
    public const string Image = "image";
    public const string Video = "video";
}

public sealed class GalleryItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceId { get; set; } = "";
    /// <summary>
    /// <see cref="GalleryItemKinds"/>. Decided by extension: a video plays in
    /// the panel's own &lt;video&gt; from the untouched file, so only
    /// browser-native containers are ever enumerated.
    /// </summary>
    public string Kind { get; set; } = GalleryItemKinds.Image;
}

public sealed class GalleryItemsResponse
{
    public List<GalleryItem> Items { get; set; } = new();
}

public sealed class GalleryPickBody
{
    public bool Folder { get; set; }
}

public sealed class GalleryPickResponse
{
    public List<string> Paths { get; set; } = new();
    public bool Cancelled { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
}

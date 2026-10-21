using System.Collections.Generic;
using System.Text.Json.Serialization;

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

/// <summary>
/// A named subset of the library a gallery widget instance can play instead
/// of everything. Membership is resolved against the live item list: every
/// item of a listed source (so a folder's new files join on their own) minus
/// <see cref="ExcludedIds"/>, plus each item picked one by one.
/// </summary>
public sealed class GalleryPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long CreatedAtUnixMs { get; set; }
    /// <summary>Sources included whole.</summary>
    public List<string> SourceIds { get; set; } = new();
    /// <summary>Items picked individually, from sources not included whole.</summary>
    public List<string> ItemIds { get; set; } = new();
    /// <summary>Items of a whole-included source left out of this playlist.</summary>
    public List<string> ExcludedIds { get; set; } = new();
}

/// <summary>Persistence shape of gallery/sources.json.</summary>
public sealed class GallerySourcesFile
{
    public List<GallerySource> Sources { get; set; } = new();
    public List<GalleryPlaylist> Playlists { get; set; } = new();
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
    /// <summary>The path is already registered as a source of the same kind,
    /// or a playlist already carries that name.</summary>
    public const string Duplicate = "duplicate";
    /// <summary>The playlist cap is reached.</summary>
    public const string Limit = "limit";
    /// <summary>No playlist has the requested id.</summary>
    public const string NotFound = "not_found";
    /// <summary>A playlist name is empty or too long.</summary>
    public const string InvalidName = "invalid_name";
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
    /// <summary>True when the global item cap dropped media; omitted otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }
    /// <summary>Media files found across all sources (a lower bound when a scan hit its ceiling); omitted when not truncated.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Total { get; set; }
    /// <summary>
    /// Carried with the items (not on a route of its own) so every panel
    /// surface, including a relayed phone, resolves a widget's playlist from
    /// the one read it already makes.
    /// </summary>
    public List<GalleryPlaylist> Playlists { get; set; } = new();
}

/// <summary>Create (name only) or update body. A null field is left as is.</summary>
public sealed class GalleryPlaylistBody
{
    public string? Name { get; set; }
    public List<string>? SourceIds { get; set; }
    public List<string>? ItemIds { get; set; }
    public List<string>? ExcludedIds { get; set; }
}

/// <summary>Values for <see cref="GalleryPlaylistUse.Surface"/>.</summary>
public static class GalleryPlaylistUseSurfaces
{
    public const string Dashboard = "dashboard";
    public const string Panel = "panel";
    public const string Desktop = "desktop";
}

/// <summary>Gallery widgets on one surface that play a playlist.</summary>
public sealed class GalleryPlaylistUse
{
    public string Surface { get; set; } = "";
    /// <summary>The panel device's name; empty for the dashboard and desktop widgets.</summary>
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class GalleryPlaylistUsageResponse
{
    public List<GalleryPlaylistUse> Uses { get; set; } = new();
}

public sealed class GalleryPlaylistMutationResponse
{
    public GalleryPlaylist? Playlist { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
    public string Code { get; set; } = "";
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

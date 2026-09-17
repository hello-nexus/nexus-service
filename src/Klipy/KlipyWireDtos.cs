using System.Text.Json.Serialization;

namespace Nexus.Service.Klipy;

// api.klipy.com wire shapes. Only the fields the picker needs are mapped; the
// payload carries several more per item (id, tags, jpg/webm variants).
// Snake_case names are explicit because AppJsonContext's policy is camelCase.

public sealed class KlipyEnvelope
{
    [JsonPropertyName("result")] public bool? Result { get; set; }
    [JsonPropertyName("data")] public KlipyPage? Data { get; set; }
}

public sealed class KlipyPage
{
    [JsonPropertyName("data")] public KlipyWireItem[]? Items { get; set; }
    [JsonPropertyName("has_next")] public bool? HasNext { get; set; }
}

public sealed class KlipyWireItem
{
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    /// <summary>"gif" for content; "ad" marks a sponsored slot, which this
    /// build never requests and drops defensively.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("blur_preview")] public string? BlurPreview { get; set; }
    [JsonPropertyName("file")] public KlipyFileSizes? File { get; set; }
}

public sealed class KlipyFileSizes
{
    [JsonPropertyName("hd")] public KlipyFileTypes? Hd { get; set; }
    [JsonPropertyName("md")] public KlipyFileTypes? Md { get; set; }
    [JsonPropertyName("sm")] public KlipyFileTypes? Sm { get; set; }
    [JsonPropertyName("xs")] public KlipyFileTypes? Xs { get; set; }
}

public sealed class KlipyFileTypes
{
    [JsonPropertyName("gif")] public KlipyFileMeta? Gif { get; set; }
    [JsonPropertyName("webp")] public KlipyFileMeta? Webp { get; set; }
    [JsonPropertyName("mp4")] public KlipyFileMeta? Mp4 { get; set; }
}

public sealed class KlipyFileMeta
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}

/// <summary>Body of the share/view triggers: Klipy's per-user analytics key.</summary>
public sealed class KlipyCustomerBody
{
    [JsonPropertyName("customer_id")] public string CustomerId { get; set; } = "";
}

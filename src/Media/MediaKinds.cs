using System;
using System.IO;

namespace Nexus.Service.Media;

/// <summary>How the cropper should show a staged source: a <c>&lt;video&gt;</c>, a
/// <c>&lt;img&gt;</c> that animates on its own, or the still preview. Only
/// containers a browser plays count as video; the rest get the still, and a
/// codec the browser lacks inside a playable container is the client's fallback.</summary>
public static class MediaKinds
{
    public const string Video = "video";
    public const string Gif = "gif";
    public const string Image = "image";

    public static string FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => Gif,
        ".mp4" or ".webm" or ".mov" or ".m4v" => Video,
        _ => Image,
    };

    /// <summary>Content type for serving a staged source as-is.</summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => "image/gif",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };
}

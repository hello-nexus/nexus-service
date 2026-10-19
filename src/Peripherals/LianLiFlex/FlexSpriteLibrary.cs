using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Media;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>A stored sprite: a transparent image the 2D animation moves around.</summary>
public sealed record FlexSprite(string Id, string Name, int Width, int Height, bool Builtin = false);

/// <summary>
/// Sprites keep their alpha channel, which the screen media library's JPEG frames cannot, so they
/// live in their own store as PNG files. A GIF imports as its first frame.
/// </summary>
public sealed class FlexSpriteLibrary
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxEdge = 400;
    private const string SpriteFile = "sprite.png";
    private const string NameFile = "name.txt";

    private readonly string _rootDir;

    public FlexSpriteLibrary()
        : this(MediaLibrary.DeviceStoreDir("lianli-flex-sprites"))
    {
    }

    internal FlexSpriteLibrary(string rootDir)
    {
        _rootDir = rootDir;
        Directory.CreateDirectory(rootDir);
    }

    public const string BuiltinPrefix = "builtin-";
    private static readonly string[] BuiltinKeys = ["dot", "heart", "star", "ring"];
    private const int BuiltinEdge = 96;

    public static bool IsBuiltin(string id) => id.StartsWith(BuiltinPrefix, StringComparison.Ordinal) && Array.IndexOf(BuiltinKeys, id[BuiltinPrefix.Length..]) >= 0;

    private static SKBitmap RenderBuiltin(string id)
    {
        var bmp = RenderKit.NewImage(BuiltinEdge, BuiltinEdge);
        using var canvas = new SKCanvas(bmp);
        var c = BuiltinEdge / 2f;
        var white = new SKColor(255, 255, 255);
        switch (id[BuiltinPrefix.Length..])
        {
            case "dot":
                RenderKit.FillCircle(canvas, white, new SKPoint(c, c), 40);
                break;
            case "ring":
                using (var paint = RenderKit.Stroke(white, 12))
                {
                    canvas.DrawCircle(c, c, 36, paint);
                }
                break;
            case "star":
            {
                var points = new SKPoint[10];
                for (var i = 0; i < 10; i++)
                {
                    var radius = i % 2 == 0 ? 44f : 18f;
                    var angle = (-90 + (i * 36)) * Math.PI / 180;
                    points[i] = new SKPoint(c + (float)(radius * Math.Cos(angle)), c + (float)(radius * Math.Sin(angle)));
                }
                using var path = RenderKit.Polygon(points, true);
                RenderKit.FillPath(canvas, white, path);
                break;
            }
            default:
            {
                var outline = new SKPoint[48];
                for (var i = 0; i < outline.Length; i++)
                {
                    var t = i * 2 * Math.PI / outline.Length;
                    var hx = 16 * Math.Pow(Math.Sin(t), 3);
                    var hy = (13 * Math.Cos(t)) - (5 * Math.Cos(2 * t)) - (2 * Math.Cos(3 * t)) - Math.Cos(4 * t);
                    outline[i] = new SKPoint(c + (float)(hx * 2.6), c - (float)(hy * 2.6) + 4);
                }
                using var path = RenderKit.Polygon(outline, true);
                RenderKit.FillPath(canvas, new SKColor(255, 64, 96), path);
                break;
            }
        }
        return bmp;
    }

    public IReadOnlyList<FlexSprite> List()
    {
        var items = new List<(FlexSprite Sprite, DateTime Written)>();
        foreach (var dir in Directory.GetDirectories(_rootDir))
        {
            var id = Path.GetFileName(dir);
            if (!MediaLibrary.IsValidId(id) || !File.Exists(Path.Combine(dir, SpriteFile)))
            {
                continue;
            }
            using var bitmap = Load(id);
            if (bitmap is null)
            {
                continue;
            }
            var name = File.Exists(Path.Combine(dir, NameFile)) ? File.ReadAllText(Path.Combine(dir, NameFile)) : id;
            items.Add((new FlexSprite(id, name, bitmap.Width, bitmap.Height), Directory.GetCreationTimeUtc(dir)));
        }
        var all = BuiltinKeys.Select(k => new FlexSprite(BuiltinPrefix + k, k, BuiltinEdge, BuiltinEdge, true)).ToList();
        all.AddRange(items.OrderByDescending(i => i.Written).Select(i => i.Sprite));
        return all;
    }

    /// <summary>Stores the image as a sprite, scaled down to fit the display when larger; an error message on a bad file.</summary>
    public (FlexSprite? Sprite, string? Error) Import(byte[] bytes, string originalName)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            return (null, $"sprite must be 1 byte to {MaxBytes / (1024 * 1024)} MB");
        }
        using var decoded = RenderKit.Decode(bytes);
        if (decoded is null)
        {
            return (null, "not a supported image");
        }
        SKBitmap sprite = decoded;
        SKBitmap? scaled = null;
        if (decoded.Width > MaxEdge || decoded.Height > MaxEdge)
        {
            var factor = (double)MaxEdge / Math.Max(decoded.Width, decoded.Height);
            scaled = RenderKit.Resize(decoded, Math.Max(1, (int)(decoded.Width * factor)), Math.Max(1, (int)(decoded.Height * factor)));
            sprite = scaled;
        }
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(originalName);
            var id = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
            if (!MediaLibrary.IsValidId(id))
            {
                id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}";
            }
            var dir = Path.Combine(_rootDir, id);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, SpriteFile), RenderKit.EncodePng(sprite));
            var name = string.IsNullOrWhiteSpace(baseName) ? id : baseName.Trim();
            File.WriteAllText(Path.Combine(dir, NameFile), name.Length > 80 ? name[..80] : name);
            return (new FlexSprite(id, name, sprite.Width, sprite.Height), null);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>The sprite's pixels; the caller disposes. Null for an unknown id.</summary>
    public SKBitmap? Load(string id)
    {
        if (IsBuiltin(id))
        {
            return RenderBuiltin(id);
        }
        if (!MediaLibrary.IsValidId(id))
        {
            return null;
        }
        var path = Path.Combine(_rootDir, id, SpriteFile);
        return File.Exists(path) ? RenderKit.Decode(File.ReadAllBytes(path)) : null;
    }

    /// <summary>True for a built-in or an imported sprite.</summary>
    public bool Exists(string id) => IsBuiltin(id) || PngPath(id) is not null;

    /// <summary>PNG bytes of any sprite, built-in or imported; null for an unknown id.</summary>
    public byte[]? PngBytes(string id)
    {
        using var bitmap = Load(id);
        return bitmap is null ? null : RenderKit.EncodePng(bitmap);
    }

    public string? PngPath(string id)
    {
        if (!MediaLibrary.IsValidId(id))
        {
            return null;
        }
        var path = Path.Combine(_rootDir, id, SpriteFile);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string id)
    {
        if (IsBuiltin(id))
        {
            return false;
        }
        if (!MediaLibrary.IsValidId(id))
        {
            return false;
        }
        var dir = Path.Combine(_rootDir, id);
        if (!Directory.Exists(dir))
        {
            return false;
        }
        Directory.Delete(dir, recursive: true);
        return true;
    }
}

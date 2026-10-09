using System.IO;
using SkiaSharp;

namespace Nexus.Service.Tests;

/// <summary>Decode helpers for asserting on rendered device images.</summary>
internal static class TestImages
{
    /// <summary>The encoded image's size, or null when the bytes do not decode.</summary>
    public static ImageSize? Identify(byte[] bytes)
    {
        var info = SKBitmap.DecodeBounds(bytes);
        return info.Width > 0 && info.Height > 0 ? new ImageSize(info.Width, info.Height) : null;
    }

    /// <summary>Decodes to an RGBA bitmap; throws on bytes that are no image, so a test fails at the decode.</summary>
    public static SKBitmap Decode(byte[] bytes) =>
        SKBitmap.Decode(bytes, SKBitmap.DecodeBounds(bytes).WithColorType(SKColorType.Rgba8888))
        ?? throw new InvalidDataException("bytes do not decode as an image");

    /// <summary>A bitmap filled with one color.</summary>
    public static SKBitmap Solid(int width, int height, SKColor color)
    {
        var image = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        image.Erase(color);
        return image;
    }
}

internal sealed record ImageSize(int Width, int Height);

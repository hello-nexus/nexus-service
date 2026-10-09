using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Orients (user rotation), applies a model's fixed wire transform, and
/// encodes a square rendered key image to that model's wire bytes. Shared by
/// StreamDeckConnectionWorker's monitoring/weather tiles and
/// Rendering.DeckKeyRenderer, so every deck key goes through one encode path.
/// </summary>
public static class DeckWireImageEncoder
{
    /// <summary>Null when the encode fails or the resulting length does not match the model's wire format.</summary>
    public static byte[]? Encode(SKBitmap rendered, StreamDeckModel model, int orientation)
    {
        var raw = new DeckRawImage(rendered.Width, rendered.Height, RenderKit.ToRgba32Bytes(rendered));
        var oriented = DeckKeyTransformer.ApplyOrientation(raw, orientation);
        var transformed = DeckKeyTransformer.ApplyKeyTransform(oriented, DeckKeyTransformer.ParseTransform(model.Transform));

        byte[] wireBytes;
        using (var transformedImage = RenderKit.FromRgba32Bytes(transformed.Data, transformed.Width, transformed.Height))
        {
            wireBytes = model.ImageFormat switch
            {
                StreamDeckImageFormat.Bmp => BmpEncoder.Encode(RenderKit.ToRgb24(transformedImage), transformed.Width, transformed.Height),
                StreamDeckImageFormat.Jpeg => EncodeKeyJpeg(transformedImage, model),
                _ => System.Array.Empty<byte>(),
            };
        }

        return wireBytes.Length == 0 || !model.IsValidWireImageLength(wireBytes.Length) ? null : wireBytes;
    }

    // Studio keys are wider than tall: the square render is centered on black.
    private static byte[] EncodeKeyJpeg(SKBitmap square, StreamDeckModel model)
    {
        if (model.KeyWidth == square.Width && model.KeyHeight == square.Height)
        {
            return RenderKit.EncodeJpeg(square);
        }
        using var key = RenderKit.NewImage(model.KeyWidth, model.KeyHeight, SKColors.Black);
        using (var canvas = new SKCanvas(key))
        {
            RenderKit.DrawImage(canvas, square, (model.KeyWidth - square.Width) / 2, (model.KeyHeight - square.Height) / 2);
        }
        return RenderKit.EncodeJpeg(key);
    }

    /// <summary>
    /// Applies the model's screen transform to an upright strip, segment or
    /// info-screen image and JPEG-encodes it (no user orientation: the
    /// screens are fixed to the device). Region coordinates stay logical.
    /// </summary>
    public static byte[] EncodeScreen(SKBitmap upright, StreamDeckModel model)
    {
        var raw = new DeckRawImage(upright.Width, upright.Height, RenderKit.ToRgba32Bytes(upright));
        var transformed = DeckKeyTransformer.ApplyKeyTransform(raw, DeckKeyTransformer.ParseTransform(model.ScreenTransform));
        using var image = RenderKit.FromRgba32Bytes(transformed.Data, transformed.Width, transformed.Height);
        return RenderKit.EncodeJpeg(image);
    }
}

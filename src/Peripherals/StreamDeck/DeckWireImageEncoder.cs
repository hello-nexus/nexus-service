using Nexus.Service.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
    public static byte[]? Encode(Image<Rgba32> rendered, StreamDeckModel model, int orientation)
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
                StreamDeckImageFormat.Jpeg => RenderKit.EncodeJpeg(transformedImage),
                _ => System.Array.Empty<byte>(),
            };
        }

        return wireBytes.Length == 0 || !model.IsValidWireImageLength(wireBytes.Length) ? null : wireBytes;
    }
}

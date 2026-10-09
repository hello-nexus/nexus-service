namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Per-model pixel transform a rendered key bitmap needs before it matches
/// what the physical hardware expects on the wire (StreamDeckModel.Transform).
/// </summary>
public enum DeckKeyTransform { None, FlipBoth, MirrorXRot90, Rot90Ccw }

/// <summary>RGBA, row-major, top-down pixel buffer. Mirrors nexus-web's deckKeyTransform.ts RawImage.</summary>
public readonly struct DeckRawImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public DeckRawImage(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Data = data;
    }
}

/// <summary>
/// C# port of nexus-web's deckKeyTransform.ts rotate/mirror math, byte-for-
/// byte identical (see DeckKeyTransformTests, whose vectors are copied from
/// deckKeyTransform.test.ts) so a physical key bitmap the service renders
/// matches what the web preview would produce for the same model/orientation.
/// </summary>
public static class DeckKeyTransformer
{
    public static DeckKeyTransform ParseTransform(string? transform) => transform switch
    {
        "flipBoth" => DeckKeyTransform.FlipBoth,
        "mirrorXRot90" => DeckKeyTransform.MirrorXRot90,
        "rot90Ccw" => DeckKeyTransform.Rot90Ccw,
        _ => DeckKeyTransform.None,
    };

    /// <summary>Applies a model's fixed wire transform to a freshly-painted (unrotated) key bitmap.</summary>
    public static DeckRawImage ApplyKeyTransform(DeckRawImage img, DeckKeyTransform transform) => transform switch
    {
        DeckKeyTransform.None => img,
        DeckKeyTransform.FlipBoth => Rotate180(img),
        DeckKeyTransform.MirrorXRot90 => Rotate90Ccw(MirrorX(img)),
        DeckKeyTransform.Rot90Ccw => Rotate90Ccw(img),
        _ => img,
    };

    /// <summary>
    /// Counter-rotates a freshly-painted key bitmap to compensate for the
    /// deck's physical mounting rotation (PhysicalDeckSettings.Orientation),
    /// so content still reads upright to the viewer. Runs before
    /// ApplyKeyTransform, a separate, fixed per-model hardware wiring quirk
    /// independent of how the user has physically mounted the deck. Any value
    /// other than 0/90/180/270 is a pass-through.
    /// </summary>
    public static DeckRawImage ApplyOrientation(DeckRawImage img, int orientationDegrees) => orientationDegrees switch
    {
        90 => Rotate90Ccw(img),
        180 => Rotate180(img),
        270 => Rotate90Cw(img),
        _ => img,
    };

    private static DeckRawImage Rotate180(DeckRawImage img)
    {
        var data = new byte[img.Data.Length];
        var pixels = img.Width * img.Height;
        for (var i = 0; i < pixels; i++)
        {
            var srcOffset = i * 4;
            var dstOffset = (pixels - 1 - i) * 4;
            CopyPixel(img.Data, srcOffset, data, dstOffset);
        }
        return new DeckRawImage(img.Width, img.Height, data);
    }

    private static DeckRawImage MirrorX(DeckRawImage img)
    {
        var data = new byte[img.Data.Length];
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var srcOffset = (y * img.Width + x) * 4;
                var dstOffset = (y * img.Width + (img.Width - 1 - x)) * 4;
                CopyPixel(img.Data, srcOffset, data, dstOffset);
            }
        }
        return new DeckRawImage(img.Width, img.Height, data);
    }

    /// <summary>(x, y) in the source lands at (y, width-1-x) in the destination, so the output is height x width.</summary>
    private static DeckRawImage Rotate90Ccw(DeckRawImage img)
    {
        var outWidth = img.Height;
        var data = new byte[img.Data.Length];
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var srcOffset = (y * img.Width + x) * 4;
                var dstX = y;
                var dstY = img.Width - 1 - x;
                var dstOffset = (dstY * outWidth + dstX) * 4;
                CopyPixel(img.Data, srcOffset, data, dstOffset);
            }
        }
        return new DeckRawImage(outWidth, img.Width, data);
    }

    /// <summary>(x, y) in the source lands at (height-1-y, x) in the destination, so the output is height x width.</summary>
    private static DeckRawImage Rotate90Cw(DeckRawImage img)
    {
        var outWidth = img.Height;
        var data = new byte[img.Data.Length];
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var srcOffset = (y * img.Width + x) * 4;
                var dstX = img.Height - 1 - y;
                var dstY = x;
                var dstOffset = (dstY * outWidth + dstX) * 4;
                CopyPixel(img.Data, srcOffset, data, dstOffset);
            }
        }
        return new DeckRawImage(outWidth, img.Width, data);
    }

    private static void CopyPixel(byte[] src, int srcOffset, byte[] dst, int dstOffset)
    {
        dst[dstOffset] = src[srcOffset];
        dst[dstOffset + 1] = src[srcOffset + 1];
        dst[dstOffset + 2] = src[srcOffset + 2];
        dst[dstOffset + 3] = src[srcOffset + 3];
    }
}

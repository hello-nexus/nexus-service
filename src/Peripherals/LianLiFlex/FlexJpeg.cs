using Nexus.Service.Rendering;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// What the Flex screens take: baseline 4:2:0 JPEGs (anything else is ignored and the screen keeps its
/// old picture) that carry a JFIF header (one without it is not turned by the rotation setting).
/// </summary>
public static class FlexJpeg
{
    private static readonly byte[] Jfif =
    {
        0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
    };

    // Stills and animation frames are loaded once and pushed over and over; the converted copy is kept as
    // long as the loaded bytes are, so a clip is converted on its first pass only.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]> Converted = new();

    /// <summary>The picture as the screens take it: re-encoded when it is not baseline 4:2:0, then given a JFIF header.</summary>
    public static byte[] Prepare(byte[] jpeg)
    {
        if (Converted.TryGetValue(jpeg, out var cached))
        {
            return cached;
        }
        var prepared = WithJfif(To420(jpeg));
        if (!ReferenceEquals(prepared, jpeg))
        {
            Converted.AddOrUpdate(jpeg, prepared);
        }
        return prepared;
    }

    /// <summary>The input when it is baseline 4:2:0 or not a readable JPEG; otherwise a 4:2:0 re-encode of the same picture.</summary>
    public static byte[] To420(byte[] jpeg)
    {
        if (IsBaseline420OrUnknown(jpeg))
        {
            return jpeg;
        }
        using var bitmap = RenderKit.Decode(jpeg);
        return bitmap is null ? jpeg : RenderKit.EncodeJpeg(bitmap);
    }

    /// <summary>True for an 8-bit baseline JPEG with luma 2x2 and both chroma components 1x1, and for bytes that are not a JPEG.</summary>
    internal static bool IsBaseline420OrUnknown(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return true;
        }
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker == 0xC0)
            {
                return i + 4 + 6 + 9 <= jpeg.Length
                    && jpeg[i + 4] == 8
                    && jpeg[i + 9] == 3
                    && jpeg[i + 11] == 0x22
                    && jpeg[i + 14] == 0x11
                    && jpeg[i + 17] == 0x11;
            }
            if (marker is 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                return false;
            }
            if (marker == 0xDA)
            {
                return true;
            }
            i += 2 + length;
        }
        return true;
    }

    /// <summary>The same picture with a JFIF header; the input itself when it has one or is not a JPEG.</summary>
    public static byte[] WithJfif(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || (jpeg[2] == 0xFF && jpeg[3] == 0xE0))
        {
            return jpeg;
        }
        var result = new byte[jpeg.Length + Jfif.Length];
        result[0] = jpeg[0];
        result[1] = jpeg[1];
        Jfif.CopyTo(result, 2);
        System.Array.Copy(jpeg, 2, result, 2 + Jfif.Length, jpeg.Length - 2);
        return result;
    }
}

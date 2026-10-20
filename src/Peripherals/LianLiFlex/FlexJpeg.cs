namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// The Flex screens turn only JPEGs that carry a JFIF header: one without it (an ffmpeg-encoded still)
/// stays at the orientation it was drawn in, whatever the rotation setting says.
/// </summary>
public static class FlexJpeg
{
    private static readonly byte[] Jfif =
    {
        0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
    };

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

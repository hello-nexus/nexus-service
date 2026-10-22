namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// DXGI Desktop Duplication hands back the surface in the output's native
/// (unrotated) orientation, while DXGI_OUTDUPL_DESC.Rotation says how the
/// desktop is turned on screen. Values follow DXGI_MODE_ROTATION and mean the
/// surface must be rotated clockwise by that angle to match the desktop.
/// </summary>
public static class DesktopRotation
{
    public const int Identity = 1;
    public const int Rotate90 = 2;
    public const int Rotate180 = 3;
    public const int Rotate270 = 4;

    public static bool SwapsAxes(int rotation) => rotation is Rotate90 or Rotate270;

    /// <summary>
    /// Maps pixel (x, y) of a desktop-oriented destination of size
    /// dstW x dstH to the pixel of a native-orientation surface of size
    /// surfW x surfH that supplies it.
    /// </summary>
    public static (int X, int Y) MapToSurface(int x, int y, int dstW, int dstH, int surfW, int surfH, int rotation)
    {
        switch (rotation)
        {
            case Rotate90:
                return (y * surfW / dstH, (dstW - 1 - x) * surfH / dstW);
            case Rotate180:
                return ((dstW - 1 - x) * surfW / dstW, (dstH - 1 - y) * surfH / dstH);
            case Rotate270:
                return ((dstH - 1 - y) * surfW / dstH, x * surfH / dstW);
            default:
                return (x * surfW / dstW, y * surfH / dstH);
        }
    }
}

using System;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The ASUS Ryujin III's 320x240 LCD. Frames are raw BGR888 on the bulk pipe in 4 KB
/// chunks, then a commit over HID - the panel holds the pixels until it is told to show
/// them. Live mode has to be switched on first, or the panel keeps playing its stored
/// animation. Reconstructed from third-party documentation of the Ryujin II, which shares
/// the controller; no unit has been run against it.
///
/// Only product id 0x1AA2 carries the screen; the other documented Ryujin ids are the same
/// controller without one.
/// </summary>
public static class RyujinProtocol
{
    public const int ProductIdWithLcd = 0x1AA2;
    public const int Width = 320;
    public const int Height = 240;

    /// <summary>320 x 240 x 3 bytes of BGR888.</summary>
    public const int FrameBytes = Width * Height * 3;

    /// <summary>Documented chunk size; the panel is not known to accept larger.</summary>
    public const int ChunkBytes = 4096;

    /// <summary>Gap the documented live-mode start leaves after each of its commands.</summary>
    public const int StartCommandGapMs = 100;

    /// <summary>Resets the panel, selects the live source, then announces the first frame.</summary>
    public static byte[][] EncodeLiveStart()
    {
        var reset = new byte[65];
        reset[0] = 0xEC;
        reset[1] = 0xD0;
        var live = new byte[65];
        live[0] = 0xEC;
        live[1] = 0x51;
        live[2] = 0x20;
        return new[] { reset, live, EncodeCommit() };
    }

    /// <summary>
    /// Tells the panel to display what was just uploaded and announces the next frame of
    /// <see cref="FrameBytes"/>. Rides the HID control channel, not the bulk pipe.
    /// </summary>
    public static byte[] EncodeCommit()
    {
        var report = new byte[65];
        report[0] = 0xEC;
        report[1] = 0x7F;
        report[2] = 0x03;
        report[4] = 0x84;
        report[5] = 0x03;
        return report;
    }

    /// <summary>
    /// Converts a captured BGRA frame into the packed BGR888 the panel takes, dropping
    /// alpha. The capture is already blue-first, so the channels pass through in order -
    /// this only removes the fourth byte.
    /// </summary>
    public static int EncodeFrame(ReadOnlySpan<byte> bgra, Span<byte> destination)
    {
        if (bgra.Length < Width * Height * 4)
        {
            throw new ArgumentException($"frame must be at least {Width * Height * 4} bytes", nameof(bgra));
        }
        if (destination.Length < FrameBytes)
        {
            throw new ArgumentException($"destination must be at least {FrameBytes} bytes", nameof(destination));
        }
        int w = 0;
        for (int i = 0; i + 3 < Width * Height * 4; i += 4)
        {
            destination[w++] = bgra[i];
            destination[w++] = bgra[i + 1];
            destination[w++] = bgra[i + 2];
        }
        return w;
    }
}

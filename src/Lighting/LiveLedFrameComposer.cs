using System;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Lighting;

/// <summary>
/// Builds the frame the dashboard receives from the engine frame: the same v3
/// layout, with each device's LED section replaced by what the writers actually
/// drive (before the channel swap). A device no writer is driving gets a
/// zero-length section so the preview shows it grey.
/// </summary>
public static class LiveLedFrameComposer
{
    /// <summary>
    /// Writes the composed frame into <paramref name="dst"/> (resized as needed) and returns its length,
    /// or 0 when <paramref name="frame"/> is not a v3 frame, in which case the caller sends it unchanged.
    /// </summary>
    public static int Compose(ReadOnlySpan<byte> frame, DeviceFrame[] devices, LiveLedSink sink, ref byte[] dst)
    {
        if (frame.Length < 6 || frame[0] != 0x03)
        {
            return 0;
        }
        var canvasW = frame[1] | (frame[2] << 8);
        var canvasH = frame[3] | (frame[4] << 8);
        var pos = 5 + canvasW * canvasH * 3;
        if (pos >= frame.Length)
        {
            return 0;
        }
        if (dst.Length < frame.Length)
        {
            dst = new byte[frame.Length + 256];
        }
        frame[..pos].CopyTo(dst);
        var count = frame[pos];
        dst[pos] = count;
        var read = pos + 1;
        var write = pos + 1;

        for (var d = 0; d < count && read + 3 <= frame.Length; d++)
        {
            var index = frame[read];
            var ledCount = frame[read + 1] | (frame[read + 2] << 8);
            var sectionBytes = ledCount * 3;
            if (read + 3 + sectionBytes > frame.Length)
            {
                break;
            }
            read += 3 + sectionBytes;

            var dev = Find(devices, index);
            var written = 0;
            dst[write] = index;
            var body = write + 3;
            if (dev is not null)
            {
                var fresh = sink.TryGetFresh(dev.Id);
                if (fresh is not null)
                {
                    written = Math.Min(fresh.Length, sectionBytes);
                    fresh.AsSpan(0, written).CopyTo(dst.AsSpan(body));
                }
            }
            written -= written % 3;
            dst[write + 1] = (byte)((written / 3) & 0xFF);
            dst[write + 2] = (byte)((written / 3) >> 8);
            write = body + written;
        }
        return write;
    }

    private static DeviceFrame? Find(DeviceFrame[] devices, byte index)
    {
        for (var i = 0; i < devices.Length; i++)
        {
            if (DeviceFrameIndex.WireByte(devices[i]) == index)
            {
                return devices[i];
            }
        }
        return null;
    }
}

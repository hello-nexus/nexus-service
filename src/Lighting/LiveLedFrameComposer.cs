using System;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Lighting;

/// <summary>Builds the dashboard frame from an engine frame: same v3 layout, each device's LED section replaced by what its writer drives, zero-length when nothing is.</summary>
public static class LiveLedFrameComposer
{
    /// <summary>Composes into <paramref name="scratch"/> and returns an exact-size copy the caller may hold across an async send, or null when the frame is not v3.</summary>
    public static byte[]? ComposeCopy(ReadOnlySpan<byte> frame, DeviceFrame[] devices, LiveLedSink sink, ref byte[] scratch)
    {
        var length = Compose(frame, devices, sink, ref scratch);
        return length > 0 ? scratch.AsSpan(0, length).ToArray() : null;
    }

    /// <summary>Writes the composed frame into <paramref name="dst"/> (grown as needed) and returns its length, or 0 when <paramref name="frame"/> is not v3 and should go out unchanged. Sections follow <paramref name="devices"/>, the snapshot the frame was serialized from.</summary>
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

        for (var d = 0; d < count && d < devices.Length && read + 3 <= frame.Length; d++)
        {
            var ledCount = frame[read + 1] | (frame[read + 2] << 8);
            var sectionBytes = ledCount * 3;
            if (read + 3 + sectionBytes > frame.Length)
            {
                break;
            }
            dst[write] = frame[read];
            var body = write + 3;
            var written = Math.Max(0, sink.CopyFresh(devices[d].Id, dst.AsSpan(body, sectionBytes)));
            written -= written % 3;
            dst[write + 1] = (byte)((written / 3) & 0xFF);
            dst[write + 2] = (byte)((written / 3) >> 8);
            write = body + written;
            read += 3 + sectionBytes;
        }
        return write;
    }
}

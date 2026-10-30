using System;
using System.Threading;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Shared-memory frame handoff between the overlay (writer) and the service (reader) for raw
/// BGRA stream sessions on Windows. The layout is a 64 byte header followed by one frame, guarded
/// by a seqlock: the writer makes the sequence odd, copies, then makes it even; the reader copies
/// and keeps the frame only when the sequence was even and unchanged. nexus-overlay carries the
/// mirrored writer (src/Media/SharedFrameWriter.cs); the two must stay layout-compatible.
/// </summary>
public static unsafe class StreamFrameSection
{
    public const int HeaderBytes = 64;
    public const int Magic = 0x4653584E;

    private const int SequenceOffset = 8;
    private const int WidthOffset = 16;
    private const int HeightOffset = 20;
    private const int BytesOffset = 28;

    public static long SectionBytes(int frameBytes) => HeaderBytes + (long)frameBytes;

    /// <summary>Header initialised by the creator so a reader never sees a half-written frame before the first write.</summary>
    public static void Initialize(byte* view)
    {
        *(int*)view = Magic;
    }

    /// <summary>
    /// Copies the newest frame whose sequence differs from <paramref name="lastSequence"/> into
    /// <paramref name="destination"/>. False when nothing new, the frame is torn, or it does not fit.
    /// </summary>
    public static bool TryRead(byte* view, long capacity, ref long lastSequence, Span<byte> destination, out int length)
    {
        length = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var sequence = Volatile.Read(ref *(long*)(view + SequenceOffset));
            if ((sequence & 1) != 0)
            {
                Thread.SpinWait(100);
                continue;
            }
            if (sequence == lastSequence || sequence == 0 || *(int*)view != Magic)
            {
                return false;
            }
            var bytes = *(int*)(view + BytesOffset);
            if (bytes <= 0 || bytes > destination.Length || HeaderBytes + (long)bytes > capacity)
            {
                return false;
            }
            new ReadOnlySpan<byte>(view + HeaderBytes, bytes).CopyTo(destination);
            Thread.MemoryBarrier();
            if (Volatile.Read(ref *(long*)(view + SequenceOffset)) == sequence)
            {
                lastSequence = sequence;
                length = bytes;
                return true;
            }
        }
        return false;
    }
}

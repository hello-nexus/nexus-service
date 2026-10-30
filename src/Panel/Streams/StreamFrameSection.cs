using System;
using System.Threading;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Shared-memory frame handoff between the overlay (writer) and the service (reader) for raw
/// BGRA stream sessions on Windows. The layout is <see cref="HeaderBytes"/> of header followed by
/// one frame, guarded by a seqlock: the writer makes the sequence odd, copies, then makes it
/// even; the reader copies and keeps the frame only when the sequence was even and unchanged.
/// The reader publishes the last sequence it consumed so the writer can detect a stalled reader.
/// nexus-overlay carries the mirrored writer (src/Media/SharedFrameWriter.cs); the two must stay
/// layout-compatible. One section serves one writer: each ingest bind creates a fresh section
/// named after the writer's nonce, so a superseded writer is left on a section nobody reads.
/// </summary>
public static unsafe class StreamFrameSection
{
    public const int HeaderBytes = 64;
    public const int Magic = 0x4653584E;

    private const int SequenceOffset = 8;
    private const int WidthOffset = 16;
    private const int HeightOffset = 20;
    private const int BytesOffset = 28;
    private const int ConsumedOffset = 32;

    public const string NonceHeader = "X-Nexus-Frame-Writer";
    private const int NonceLength = 16;

    public static long SectionBytes(int frameBytes) => HeaderBytes + (long)frameBytes;

    /// <summary>A writer nonce is 16 lowercase hex digits; anything else would let a caller steer the object name.</summary>
    public static bool IsValidNonce(string? nonce)
    {
        if (nonce is null || nonce.Length != NonceLength)
        {
            return false;
        }
        foreach (var c in nonce)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    public static string SectionName(string baseName, string nonce) => baseName + "-" + nonce;

    public static string ReadyName(string sectionName) => sectionName + "-Ready";

    /// <summary>Owner-only DACL: SYSTEM plus the one user the overlay runs as, read/write on the section and event.</summary>
    public static string? Sddl(string? userSid)
    {
        if (!IsSid(userSid))
        {
            return null;
        }
        return $"D:(A;;GA;;;SY)(A;;GRGW;;;{userSid})";
    }

    private static bool IsSid(string? sid)
    {
        if (sid is null || !sid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }
        var parts = sid.Substring(4).Split('-');
        if (parts.Length < 2)
        {
            return false;
        }
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 10)
            {
                return false;
            }
            foreach (var c in part)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>True when the writer has published a frame the reader has not consumed; cheap enough to check before renting a buffer.</summary>
    public static bool HasNew(byte* view, long lastSequence)
    {
        var sequence = Volatile.Read(ref *(long*)(view + SequenceOffset));
        return (sequence & 1) == 0 && sequence != 0 && sequence != lastSequence;
    }

    public static void PublishConsumed(byte* view, long sequence)
    {
        Volatile.Write(ref *(long*)(view + ConsumedOffset), sequence);
    }

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

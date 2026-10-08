namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Wire framing for the overlay -> service ingest stream. One frame is
/// <c>u32_le payloadLength | u8 flags | payload</c>; the payload is exactly
/// one H.264 Annex-B access unit. nexus-overlay carries a mirrored writer
/// (src/Media/FrameFraming.cs); the two must stay byte-compatible.
/// </summary>
public static class StreamFraming
{
    public const int HeaderSize = 5;

    /// <summary>Payload is an IDR access unit (safe resync point).</summary>
    public const byte FlagIdr = 0x01;

    /// <summary>Zero-length keepalive; proves ingest liveness, never enqueued.</summary>
    public const byte FlagControl = 0x02;

    public const byte KnownFlags = FlagIdr | FlagControl;

    /// <summary>A payload above this is framing corruption: the largest real one is a raw BGRA 2288x1080 frame (9.9 MB).</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
}

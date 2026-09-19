using System;
using System.Buffers;
using System.Threading;

namespace Nexus.Service.Panel.Streams;

/// <summary>One decoded ingest frame: flags plus the raw payload bytes.</summary>
public sealed class StreamFrame
{
    /// <summary>
    /// Its own pool rather than <see cref="ArrayPool{T}.Shared"/>: Shared keeps a per-core
    /// cache, so multi-megabyte raw frames end up retained as one buffer per core that ever
    /// handled one. A handful covers the queue depth plus the frame in flight.
    /// </summary>
    public static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(StreamFraming.MaxPayloadBytes, 6);

    private int _released;

    public required byte Flags { get; init; }

    /// <summary>Backing array; a pooled one is usually longer than <see cref="Length"/>.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Payload bytes actually in use; -1 means the whole array.</summary>
    public int Length { get; init; } = -1;

    /// <summary>Rented from <see cref="Pool"/>, so <see cref="Release"/> must run once.</summary>
    public bool Pooled { get; init; }

    public ReadOnlySpan<byte> Bytes => Payload.AsSpan(0, Length < 0 ? Payload.Length : Length);

    public bool IsIdr => (Flags & StreamFraming.FlagIdr) != 0;
    public bool IsControl => (Flags & StreamFraming.FlagControl) != 0;

    /// <summary>
    /// Hands a pooled payload back once the frame is written or dropped. Idempotent, because
    /// the drop paths and the send path both own frames and must not double-return.
    /// </summary>
    public void Release()
    {
        if (Pooled && Interlocked.Exchange(ref _released, 1) == 0)
        {
            Pool.Return(Payload);
        }
    }
}

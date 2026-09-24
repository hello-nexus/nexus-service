using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.AspNetCore.Connections;

namespace Nexus.Service.Platform;

/// <summary>
/// Kestrel memory pool with larger pinned blocks. A socket receive fills at most one block,
/// so the default 4 KB blocks cost one receive call per 4 KB; a streamed LCD panel pushes
/// megabytes a second through one loopback request.
/// </summary>
public sealed class LargeBlockMemoryPoolFactory : IMemoryPoolFactory<byte>
{
    public MemoryPool<byte> Create(MemoryPoolOptions? options = null) => new LargeBlockMemoryPool();
}

public sealed class LargeBlockMemoryPool : MemoryPool<byte>
{
    public const int BlockSize = 64 * 1024;
    // Idle blocks kept for reuse, the rest left to the GC. Covers a raw frame buffered in the
    // request pipe plus the connection's read-ahead, so a steady stream allocates nothing.
    public const int MaxRetainedBlocks = 128;
    // Blocks that stayed free for a whole interval are released, so a burst does not pin memory for good.
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<byte[]> _free = new();
    private readonly Timer _trimTimer;
    private int _freeCount;
    // Fewest free blocks seen since the last trim: that many sat unused the whole interval.
    private int _lowWater;
    private bool _disposed;

    public LargeBlockMemoryPool()
    {
        _trimTimer = new Timer(_ => Trim(), null, TrimInterval, TrimInterval);
    }

    public override int MaxBufferSize => BlockSize;

    internal int RetainedCount => Volatile.Read(ref _freeCount);

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minBufferSize, BlockSize);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_free.TryDequeue(out var array))
        {
            LowerLowWater(Interlocked.Decrement(ref _freeCount));
        }
        else
        {
            array = GC.AllocateUninitializedArray<byte>(BlockSize, pinned: true);
        }
        return new Block(this, array);
    }

    private void Return(byte[] array)
    {
        if (_disposed || Interlocked.Increment(ref _freeCount) > MaxRetainedBlocks)
        {
            Interlocked.Decrement(ref _freeCount);
            return;
        }
        _free.Enqueue(array);
    }

    /// <summary>Releases the blocks that went unused for a whole interval.</summary>
    internal void Trim()
    {
        var idle = Interlocked.Exchange(ref _lowWater, Volatile.Read(ref _freeCount));
        for (var i = 0; i < idle && _free.TryDequeue(out _); i++)
        {
            LowerLowWater(Interlocked.Decrement(ref _freeCount));
        }
    }

    private void LowerLowWater(int count)
    {
        var seen = Volatile.Read(ref _lowWater);
        while (count < seen)
        {
            var prior = Interlocked.CompareExchange(ref _lowWater, count, seen);
            if (prior == seen) return;
            seen = prior;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _trimTimer.Dispose();
        _free.Clear();
    }

    private sealed class Block : IMemoryOwner<byte>
    {
        private LargeBlockMemoryPool? _pool;
        private readonly byte[] _array;

        public Block(LargeBlockMemoryPool pool, byte[] array)
        {
            _pool = pool;
            _array = array;
            // Pinned-array memory makes Pin() free for the socket layer.
            Memory = MemoryMarshal.CreateFromPinnedArray(array, 0, array.Length);
        }

        public Memory<byte> Memory { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _pool, null)?.Return(_array);
        }
    }
}

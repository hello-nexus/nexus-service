using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Pixel-pack buffers for queued readbacks, one per slot. GL thread only.</summary>
internal sealed class PackPool
{
    private readonly List<uint> _buffers = new();
    private int _generation = -1;
    private int _bytes;

    /// <summary>Buffer for <paramref name="slot"/>, sized for <paramref name="bytes"/>; created on first use. A new context generation forgets the old handles, which died with their context.</summary>
    public unsafe uint Get(GL gl, int generation, int slot, int bytes)
    {
        if (generation != _generation || bytes != _bytes)
        {
            if (generation == _generation)
            {
                foreach (var b in _buffers)
                {
                    gl.DeleteBuffer(b);
                }
            }
            _buffers.Clear();
            _generation = generation;
            _bytes = bytes;
        }
        while (_buffers.Count <= slot)
        {
            var id = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, id);
            gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)bytes, null, BufferUsageARB.StreamRead);
            _buffers.Add(id);
        }
        return _buffers[slot];
    }
}

/// <summary>
/// Collects the glReadPixels of several renders so the GPU wait is paid once, not per frame. While a batch is
/// open on the calling thread, a <see cref="ShaderEffect"/> draws and queues its read into a pixel-pack buffer
/// instead of blocking on it. <see cref="Resolve"/> maps every queued buffer in one GL job, and
/// <see cref="Apply"/> converts a group's pixels into the canvases the renders targeted. Disposing resolves and
/// applies whatever is left, so a canvas is complete once the batch closes. One batch per thread; a nested
/// <see cref="TryBegin"/> returns null.
/// </summary>
internal sealed class ReadbackBatch : IDisposable
{
    private struct Entry
    {
        public CanvasBuffer Canvas;
        public int Group;
        public bool Ok;
        public int Generation;
        public bool Applied;
    }

    [ThreadStatic]
    private static ReadbackBatch? _cache;

    [ThreadStatic]
    private static ReadbackBatch? _current;

    private readonly List<Entry> _entries = new();
    private byte[] _resolved = Array.Empty<byte>();
    private GpuContext? _ctx;
    private int _bytes;
    private bool _es;
    private int _w;
    private int _h;
    private bool _resolvedOnce;

    public static ReadbackBatch? Current => _current;

    /// <summary>True when a queued read did not land in its canvas since this batch opened.</summary>
    public bool Lost { get; private set; }

    /// <summary>Group the next queued renders belong to; <see cref="Apply"/> converts one group at a time.</summary>
    public int Group { get; set; }

    /// <summary>Opens a batch on this thread, or null when batching is off for the context or one is already open.</summary>
    public static ReadbackBatch? TryBegin(GpuContext ctx)
    {
        if (_current is not null || !ctx.BatchReadback)
        {
            return null;
        }
        var b = _cache ??= new ReadbackBatch();
        b.Reset(ctx);
        _current = b;
        return b;
    }

    /// <summary>True when <paramref name="fx"/> would queue its readback into the open batch, or no batch is open to care.</summary>
    internal static bool CanDefer(IEffect fx) =>
        fx is ShaderEffect { TestRender: null } shader && (_current is null || _current.Matches(shader.Context));

    internal bool Matches(GpuContext ctx) => ReferenceEquals(_ctx, ctx);

    private void Reset(GpuContext ctx)
    {
        _ctx = ctx;
        _entries.Clear();
        _resolvedOnce = false;
        Lost = false;
        Group = 0;
    }

    /// <summary>GL thread: queue the read of the bound framebuffer. Returns false when nothing could be queued.</summary>
    internal unsafe bool Enqueue(GL gl, GpuContext ctx, int w, int h, bool es, CanvasBuffer canvas)
    {
        if (_resolvedOnce)
        {
            return false;
        }
        _w = w;
        _h = h;
        _es = es;
        _bytes = w * h * (es ? 4 : 3);
        var slot = _entries.Count;
        var pbo = ctx.Pack.Get(gl, ctx.Generation, slot, _bytes);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbo);
        gl.ReadPixels(0, 0, (uint)w, (uint)h, es ? PixelFormat.Rgba : PixelFormat.Rgb, PixelType.UnsignedByte, (void*)0);
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        _entries.Add(new Entry { Canvas = canvas, Group = Group, Ok = false, Generation = ctx.Generation });
        return true;
    }

    /// <summary>Maps every queued buffer in one GL job and copies the pixels out. The first map waits for the GPU; the rest find it done.</summary>
    public void Resolve()
    {
        if (_resolvedOnce)
        {
            return;
        }
        // Closes the render phase even when nothing was queued, so a later render cannot slip into this batch.
        _resolvedOnce = true;
        if (_entries.Count == 0 || _ctx is null)
        {
            return;
        }
        var ctx = _ctx;
        ctx.Invoke(ResolveOnGl);
    }

    private unsafe void ResolveOnGl()
    {
        var ctx = _ctx!;
        var gl = ctx.Gl;
        var total = _bytes * _entries.Count;
        if (_resolved.Length < total)
        {
            _resolved = new byte[total];
        }
        try
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var pbo = ctx.Pack.Get(gl, ctx.Generation, i, _bytes);
                gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbo);
                var p = gl.MapBufferRange(BufferTargetARB.PixelPackBuffer, 0, (nuint)_bytes, MapBufferAccessMask.ReadBit);
                if (p is null)
                {
                    continue;
                }
                new ReadOnlySpan<byte>(p, _bytes).CopyTo(_resolved.AsSpan(i * _bytes, _bytes));
                gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
                var e = _entries[i];
                e.Ok = e.Generation == ctx.Generation;
                _entries[i] = e;
            }
        }
        finally
        {
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        }
        if (_es && ctx.CheckContextLost())
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                e.Ok = false;
                _entries[i] = e;
            }
        }
    }

    /// <summary>Converts the pixels of one group into their canvases. Resolves first when that has not happened.</summary>
    public void Apply(int group)
    {
        Resolve();
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Group != group || e.Applied)
            {
                continue;
            }
            e.Applied = true;
            _entries[i] = e;
            Lost |= !e.Ok;
            // Canvas resized outside what was rendered: drop the frame, as the synchronous path does.
            if (e.Ok && e.Canvas.Width == _w && e.Canvas.Height == _h)
            {
                e.Canvas.WriteFromGl(_resolved.AsSpan(i * _bytes, _bytes), _es);
            }
        }
    }

    /// <summary>Drops everything queued without touching a canvas.</summary>
    public void Cancel()
    {
        _resolvedOnce = true;
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            e.Applied = true;
            _entries[i] = e;
        }
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_current, this))
        {
            return;
        }
        _current = null;
        try
        {
            Resolve();
            for (var i = 0; i < _entries.Count; i++)
            {
                if (!_entries[i].Applied)
                {
                    Apply(_entries[i].Group);
                }
            }
        }
        finally
        {
            _entries.Clear();
        }
    }
}

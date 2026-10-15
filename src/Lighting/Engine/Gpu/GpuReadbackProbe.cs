using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Non-baseline readback variants for the diagnostic sequence. Runs on the GL
/// thread only. Any GL error throws, and the caller falls back to baseline.
/// </summary>
internal sealed class GpuReadbackProbe
{
    private const int RingSize = 3;
    private const double SleepWaitCapMs = 25;

    private readonly uint[] _pbo = new uint[RingSize];
    private readonly nint[] _fence = new nint[RingSize];
    private readonly long[] _frameOf = new long[RingSize];
    private long _frame;
    private bool _ringReady;
    private int _ringBytes;
    private byte[] _rgb = Array.Empty<byte>();
    private byte[] _bgra = Array.Empty<byte>();
    private byte[] _raw = Array.Empty<byte>();

    /// <summary>True when the canvas was written this frame; false keeps the previous colors.</summary>
    public bool Read(GL gl, int w, int h, GpuDiagMode mode, CanvasBuffer canvas)
    {
        DrainErrors(gl);
        var wrote = mode switch
        {
            GpuDiagMode.AsyncReadback => ReadAsync(gl, w, h, canvas),
            GpuDiagMode.SleepWait => ReadAfterSleepWait(gl, w, h, canvas),
            GpuDiagMode.BgraReadback => ReadBgra(gl, w, h, canvas),
            _ => throw new InvalidOperationException("not a probe mode"),
        };
        CheckError(gl, GpuDiag.ToName(mode));
        return wrote;
    }

    /// <summary>Releases outstanding fences when the sequence leaves the async variant.</summary>
    public void ResetAsync(GL gl)
    {
        for (var i = 0; i < RingSize; i++)
        {
            if (_fence[i] != 0)
            {
                gl.DeleteSync(_fence[i]);
                _fence[i] = 0;
            }
        }
    }

    private static void DrainErrors(GL gl)
    {
        for (var i = 0; i < 8 && gl.GetError() != GLEnum.NoError; i++)
        {
        }
    }

    private static void CheckError(GL gl, string what)
    {
        var err = gl.GetError();
        if (err != GLEnum.NoError)
        {
            throw new InvalidOperationException($"GL error {err} in {what}");
        }
    }

    private unsafe bool ReadAsync(GL gl, int w, int h, CanvasBuffer canvas)
    {
        var size = w * h * 3;
        var sync = GpuDiag.Begin(GpuDiag.Phase.GlSync);
        if (!_ringReady || _ringBytes != size)
        {
            for (var i = 0; i < RingSize; i++)
            {
                _pbo[i] = gl.GenBuffer();
                gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pbo[i]);
                gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)size, null, BufferUsageARB.StreamRead);
            }
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
            _ringReady = true;
            _ringBytes = size;
        }
        sync.End();

        var read = GpuDiag.Begin(GpuDiag.Phase.GlRead);
        var slot = (int)(_frame % RingSize);
        if (_fence[slot] != 0)
        {
            gl.DeleteSync(_fence[slot]);
            _fence[slot] = 0;
        }
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pbo[slot]);
        gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgb, PixelType.UnsignedByte, (void*)0);
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        read.End();

        sync = GpuDiag.Begin(GpuDiag.Phase.GlSync);
        _fence[slot] = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);
        _frameOf[slot] = _frame;
        gl.Flush();
        var current = _frame++;
        var ready = -1;
        for (var k = current - 1; k >= 0 && k > current - RingSize; k--)
        {
            var s = (int)(k % RingSize);
            if (_fence[s] == 0 || _frameOf[s] != k)
            {
                continue;
            }
            var st = gl.ClientWaitSync(_fence[s], 0u, 0UL);
            if (st == GLEnum.WaitFailed)
            {
                throw new InvalidOperationException("glClientWaitSync WAIT_FAILED");
            }
            if (st is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
            {
                ready = s;
                break;
            }
        }
        if (ready >= 0)
        {
            var readyFrame = _frameOf[ready];
            for (var i = 0; i < RingSize; i++)
            {
                if (_fence[i] != 0 && _frameOf[i] <= readyFrame)
                {
                    gl.DeleteSync(_fence[i]);
                    _fence[i] = 0;
                }
            }
        }
        sync.End();
        if (ready < 0)
        {
            return false;
        }

        read = GpuDiag.Begin(GpuDiag.Phase.GlRead);
        if (_rgb.Length < size)
        {
            _rgb = new byte[size];
        }
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pbo[ready]);
        var p = gl.MapBufferRange(BufferTargetARB.PixelPackBuffer, 0, (nuint)size, (uint)MapBufferAccessMask.ReadBit);
        if (p == null)
        {
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
            throw new InvalidOperationException("glMapBufferRange returned null");
        }
        var rowBytes = w * 3;
        var src = new ReadOnlySpan<byte>(p, size);
        for (var y = 0; y < h; y++)
        {
            src.Slice((h - 1 - y) * rowBytes, rowBytes).CopyTo(_rgb.AsSpan(y * rowBytes, rowBytes));
        }
        gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        read.End();

        var post = GpuDiag.Begin(GpuDiag.Phase.GlPost);
        if (canvas.Width == w && canvas.Height == h)
        {
            canvas.WriteFromRgb(_rgb.AsSpan(0, size));
        }
        post.End();
        return true;
    }

    // Replaces the driver's CPU wait inside glReadPixels with a sleep poll on a fence.
    private unsafe bool ReadAfterSleepWait(GL gl, int w, int h, CanvasBuffer canvas)
    {
        var sync = GpuDiag.Begin(GpuDiag.Phase.GlSync);
        var fence = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);
        gl.Flush();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var st = gl.ClientWaitSync(fence, 0u, 0UL);
            if (st is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
            {
                break;
            }
            if (st == GLEnum.WaitFailed)
            {
                gl.DeleteSync(fence);
                throw new InvalidOperationException("glClientWaitSync WAIT_FAILED");
            }
            if (sw.Elapsed.TotalMilliseconds > SleepWaitCapMs)
            {
                break;
            }
            Thread.Sleep(1);
        }
        gl.DeleteSync(fence);
        sync.End();

        var size = w * h * 3;
        var read = GpuDiag.Begin(GpuDiag.Phase.GlRead);
        if (_rgb.Length < size)
        {
            _rgb = new byte[size];
        }
        if (_raw.Length < size)
        {
            _raw = new byte[size];
        }
        fixed (byte* p = _raw)
        {
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgb, PixelType.UnsignedByte, p);
        }
        read.End();

        var post = GpuDiag.Begin(GpuDiag.Phase.GlPost);
        var rowBytes = w * 3;
        for (var y = 0; y < h; y++)
        {
            System.Buffer.BlockCopy(_raw, (h - 1 - y) * rowBytes, _rgb, y * rowBytes, rowBytes);
        }
        if (canvas.Width == w && canvas.Height == h)
        {
            canvas.WriteFromRgb(_rgb.AsSpan(0, size));
        }
        post.End();
        return true;
    }

    private unsafe bool ReadBgra(GL gl, int w, int h, CanvasBuffer canvas)
    {
        var size = w * h * 3;
        var px4 = w * h * 4;
        var read = GpuDiag.Begin(GpuDiag.Phase.GlRead);
        if (_bgra.Length < px4)
        {
            _bgra = new byte[px4];
        }
        if (_rgb.Length < size)
        {
            _rgb = new byte[size];
        }
        fixed (byte* p = _bgra)
        {
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        }
        read.End();

        var post = GpuDiag.Begin(GpuDiag.Phase.GlPost);
        for (var y = 0; y < h; y++)
        {
            var src = (h - 1 - y) * w * 4;
            var dst = y * w * 3;
            for (var x = 0; x < w; x++)
            {
                _rgb[dst] = _bgra[src + 2];
                _rgb[dst + 1] = _bgra[src + 1];
                _rgb[dst + 2] = _bgra[src];
                src += 4;
                dst += 3;
            }
        }
        if (canvas.Width == w && canvas.Height == h)
        {
            canvas.WriteFromRgb(_rgb.AsSpan(0, size));
        }
        post.End();
        return true;
    }
}

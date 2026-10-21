#if WINDOWS
using System;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Nexus.Service.Lighting.Capture;

public sealed class DxgiScreenCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _screenW, _screenH;
    private int _surfW, _surfH, _rotation = DesktopRotation.Identity;
    private bool _frameAcquired, _disposed;

    public bool IsInitialized => _duplication is not null;
    /// <summary>A non-timeout AcquireNextFrame failure (ACCESS_LOST); the instance never recovers and must be re-created.</summary>
    public bool IsLost { get; private set; }
    /// <summary>Desktop-oriented size (what the user sees), not the native surface size.</summary>
    public int ScreenWidth => _screenW;
    public int ScreenHeight => _screenH;

    public bool Initialize(uint outputIndex = 0)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1(0u, out var adapter);
            // null feature-levels = let D3D pick its default set; the binding's param isn't nullable-annotated.
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null!, out _device, out _, out _context);
            if (_device is null || _context is null) { adapter.Dispose(); return false; }
            adapter.EnumOutputs(outputIndex, out var output);
            adapter.Dispose();
            var desc = output.Description;
            _screenW = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            _screenH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            output.Dispose();
            _duplication = output1.DuplicateOutput(_device);
            _rotation = (int)_duplication.Description.Rotation;
            if (_rotation < DesktopRotation.Identity || _rotation > DesktopRotation.Rotate270) _rotation = DesktopRotation.Identity;
            // A rotated output duplicates in native orientation: the surface has the axes swapped.
            if (DesktopRotation.SwapsAxes(_rotation)) { _surfW = _screenH; _surfH = _screenW; }
            else { _surfW = _screenW; _surfH = _screenH; }
            CreateStaging();
            return true;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[dxgi-capture] init failed: {ex.Message}"); Dispose(); return false; }
    }

    private void CreateStaging()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_surfW, Height = (uint)_surfH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    public bool AcquireFrame(uint timeoutMs = 0)
    {
        if (_duplication is null || _context is null || _stagingTexture is null) return false;
        if (_frameAcquired) ReleaseFrame();
        try
        {
            var result = _duplication.AcquireNextFrame(timeoutMs, out _, out var resource);
            if (result.Failure)
            {
                if (result.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code) IsLost = true;
                return false;
            }
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            // CopyResource silently does nothing on a size mismatch, which leaves the staging texture black.
            var td = tex.Description;
            if ((int)td.Width != _surfW || (int)td.Height != _surfH)
            {
                _surfW = (int)td.Width; _surfH = (int)td.Height;
                CreateStaging();
            }
            _context.CopyResource(_stagingTexture!, tex);
            _context.Flush();
            _duplication.ReleaseFrame();
            resource.Dispose();
            _frameAcquired = true;
            return true;
        }
        catch { IsLost = true; return false; }
    }

    public byte[]? ReadRegion(int x, int y, int w, int h)
    {
        if (!_frameAcquired || _context is null || _stagingTexture is null) return null;
        x = Math.Clamp(x, 0, _screenW - 1); y = Math.Clamp(y, 0, _screenH - 1);
        w = Math.Clamp(w, 1, _screenW - x); h = Math.Clamp(h, 1, _screenH - y);
        try
        {
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                var rgb = new byte[w * h * 3];
                var srcPtr = mapped.DataPointer; var rowPitch = (int)mapped.RowPitch;
                for (int row = 0; row < h; row++)
                {
                    for (int col = 0; col < w; col++)
                    {
                        var (sx, sy) = DesktopRotation.MapToSurface(x + col, y + row, _screenW, _screenH, _surfW, _surfH, _rotation);
                        var pixel = srcPtr + sy * rowPitch + sx * 4; var dstOff = (row * w + col) * 3;
                        rgb[dstOff] = Marshal.ReadByte(pixel + 2);
                        rgb[dstOff + 1] = Marshal.ReadByte(pixel + 1);
                        rgb[dstOff + 2] = Marshal.ReadByte(pixel);
                    }
                }
                return rgb;
            }
            finally { _context.Unmap(_stagingTexture, 0); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Downscale the currently-acquired frame into a flat RGB24 byte
    /// buffer (size <paramref name="dstW"/>*<paramref name="dstH"/>*3).
    /// Used by the user-session helper to ship canvas-resolution frames
    /// to the service over the named pipe without allocating a full
    /// CanvasBuffer.
    /// </summary>
    public unsafe bool BlitToBuffer(byte[] dst, int dstW, int dstH)
    {
        if (!_frameAcquired || _context is null || _stagingTexture is null) return false;
        if (dst.Length < dstW * dstH * 3) return false;
        try
        {
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                var rowPitch = (int)mapped.RowPitch;
                var src = (byte*)mapped.DataPointer;
                fixed (byte* dstPtr = dst)
                {
                    for (int y = 0; y < dstH; y++)
                    {
                        var dstRow = dstPtr + y * dstW * 3;
                        for (int x = 0; x < dstW; x++)
                        {
                            var (sx, sy) = DesktopRotation.MapToSurface(x, y, dstW, dstH, _surfW, _surfH, _rotation);
                            var p = src + sy * rowPitch + sx * 4;
                            var d = dstRow + x * 3;
                            d[0] = p[2];
                            d[1] = p[1];
                            d[2] = p[0];
                        }
                    }
                }
                return true;
            }
            finally { _context.Unmap(_stagingTexture, 0); }
        }
        catch { return false; }
    }

    public unsafe bool BlitToCanvas(Engine.CanvasBuffer canvas)
    {
        if (!_frameAcquired || _context is null || _stagingTexture is null) return false;
        try
        {
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                var cw = canvas.Width;
                var ch = canvas.Height;
                var rowPitch = (int)mapped.RowPitch;
                var src = (byte*)mapped.DataPointer;
                for (int y = 0; y < ch; y++)
                {
                    for (int x = 0; x < cw; x++)
                    {
                        var (sx, sy) = DesktopRotation.MapToSurface(x, y, cw, ch, _surfW, _surfH, _rotation);
                        var p = src + sy * rowPitch + sx * 4;
                        canvas.SetPixel(x, y, p[2], p[1], p[0]);
                    }
                }
                return true;
            }
            finally { _context.Unmap(_stagingTexture, 0); }
        }
        catch { return false; }
    }

    public void ReleaseFrame()
    {
        _frameAcquired = false;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        ReleaseFrame(); _duplication?.Dispose(); _stagingTexture?.Dispose(); _context?.Dispose(); _device?.Dispose();
    }
}
#endif

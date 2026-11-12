#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Nexus.Service.Lighting.Capture;

public enum AcquireOutcome
{
    None,
    Timeout,
    Lost,
    /// <summary>The desktop image changed.</summary>
    Frame,
    /// <summary>Only the pointer shape or position changed.</summary>
    PointerOnly,
}

public sealed class DxgiScreenCapture : IDisposable
{
    private const string Tag = "[mirror-diag]";
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutput1? _output1;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingW, _stagingH;
    private int _screenW, _screenH;
    private int _surfW, _surfH, _rotation = DesktopRotation.Identity;
    private bool _frameAcquired, _disposed, _hasImage;
    private MirrorGpuDownsampler? _gpu;
    private int _downW, _downH;
    private bool _gpuFailed, _frameOnGpu;
    private byte[]? _gpuRgb;

    public bool UsingGpu => _frameOnGpu;
    public bool IsInitialized => _duplication is not null;
    /// <summary>A non-timeout AcquireNextFrame failure (ACCESS_LOST and friends); recover with <see cref="TryRecreateDuplication"/> or a new instance.</summary>
    public bool IsLost { get; private set; }
    /// <summary>The loss needs a new D3D device, not just a new duplication.</summary>
    public bool NeedsFullRebuild { get; private set; }
    /// <summary>Receives [mirror-diag] lines; the helper points it at its log.</summary>
    public Action<string>? Diag { get; set; }
    /// <summary>Log an init failure line; the helper turns it off for repeat retries.</summary>
    public bool LogInitFailure { get; set; } = true;
    /// <summary>When set, a frame with no desktop image change skips the copy and AcquireFrame returns false.</summary>
    public bool SkipPointerOnlyFrames { get; set; }
    public AcquireOutcome LastOutcome { get; private set; }
    public uint LastAccumulatedFrames { get; private set; }
    public double LastAcquireMs { get; private set; }
    public double LastCopyMs { get; private set; }
    public double LastMapMs { get; private set; }
    public double LastSampleMs { get; private set; }
    /// <summary>Desktop-oriented size (what the user sees), not the native surface size.</summary>
    public int ScreenWidth => _screenW;
    public int ScreenHeight => _screenH;

    private static double ElapsedMs(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

    private static int HrOf(Exception ex) => ex is SharpGen.Runtime.SharpGenException sg ? sg.ResultCode.Code : ex.HResult;

    private static string Hr(int hr) => DxgiErrorClassifier.Describe(hr);

    public bool Initialize(uint outputIndex = 0)
    {
        var step = "CreateDXGIFactory1";
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            step = "EnumAdapters1";
            var r = factory.EnumAdapters1(0u, out var adapter);
            if (r.Failure) return InitFail(step, r.Code, null);
            IDXGIOutput? output = null;
            string adapterName;
            var outputCount = 1;
            try
            {
                step = "D3D11CreateDevice";
                // null feature-levels = let D3D pick its default set; the binding's param isn't nullable-annotated.
                r = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null!, out _device, out _, out _context);
                if (r.Failure || _device is null || _context is null) return InitFail(step, r.Failure ? r.Code : DxgiErrorClassifier.Fail, null);
                adapterName = adapter.Description1.Description;
                step = "EnumOutputs";
                r = adapter.EnumOutputs(outputIndex, out output);
                if (r.Failure) return InitFail(step, r.Code, null);
                while (adapter.EnumOutputs((uint)outputCount, out var extra).Success) { extra.Dispose(); outputCount++; }
            }
            finally { adapter.Dispose(); }
            var desc = output!.Description;
            _screenW = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            _screenH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            step = "QueryInterface IDXGIOutput1";
            try { _output1 = output.QueryInterface<IDXGIOutput1>(); }
            finally { output.Dispose(); }
            step = "DuplicateOutput";
            _duplication = _output1.DuplicateOutput(_device);
            ApplyDuplicationGeometry();
            step = "CreateTexture2D staging";
            EnsureStaging();
            var dm = _duplication.Description.ModeDescription;
            var c = desc.DesktopCoordinates;
            Diag?.Invoke($"{Tag} init output={outputIndex}/{outputCount} device={desc.DeviceName} coords={c.Left},{c.Top},{c.Right},{c.Bottom} rotation={desc.Rotation} attached={(bool)desc.AttachedToDesktop} adapter=\"{adapterName}\" dup={dm.Width}x{dm.Height} format={dm.Format} refresh={dm.RefreshRate.Numerator}/{dm.RefreshRate.Denominator} staging={_stagingW}x{_stagingH}");
            return true;
        }
        catch (Exception ex) { return InitFail(step, HrOf(ex), ex); }
    }

    /// <summary>Sets the grid the consumer reads; each frame is then shrunk on the GPU so only that many pixels are mapped.</summary>
    public void ConfigureDownsample(int width, int height)
    {
        _downW = width;
        _downH = height;
    }

    private bool InitFail(string step, int hr, Exception? ex)
    {
        var detail = ex is null ? "" : $" {ex.GetType().Name}: {ex.Message}";
        if (LogInitFailure) Diag?.Invoke($"{Tag} init failed step={step} hr={Hr(hr)}{detail}");
        Console.Error.WriteLine($"[dxgi-capture] init failed at {step}: {Hr(hr)}{detail}");
        Dispose();
        return false;
    }

    /// <summary>Reads rotation and surface orientation from the current duplication and output.</summary>
    private void ApplyDuplicationGeometry()
    {
        _rotation = (int)_duplication!.Description.Rotation;
        if (_rotation < DesktopRotation.Identity || _rotation > DesktopRotation.Rotate270) _rotation = DesktopRotation.Identity;
        // A rotated output duplicates in native orientation: the surface has the axes swapped.
        if (DesktopRotation.SwapsAxes(_rotation)) { _surfW = _screenH; _surfH = _screenW; }
        else { _surfW = _screenW; _surfH = _screenH; }
    }

    private void EnsureStaging()
    {
        if (_stagingTexture is not null && _stagingW == _surfW && _stagingH == _surfH) return;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _stagingTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_surfW, Height = (uint)_surfH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _stagingW = _surfW; _stagingH = _surfH;
        _hasImage = false;
    }

    private void MarkLost(string step, int hr, Exception? ex)
    {
        IsLost = true;
        _frameAcquired = false;
        var removed = 0;
        try { removed = _device?.DeviceRemovedReason.Code ?? 0; } catch { }
        NeedsFullRebuild = removed != 0 || DxgiErrorClassifier.Classify(hr) == DxgiFailureAction.RebuildDevice;
        var detail = ex is null ? "" : $" {ex.GetType().Name}: {ex.Message}";
        Diag?.Invoke($"{Tag} lost at {step} hr={Hr(hr)} deviceRemovedReason={Hr(removed)} action={(NeedsFullRebuild ? "rebuild-device" : "recreate-duplication")}{detail}");
    }

    /// <summary>
    /// Replaces only the IDXGIOutputDuplication on the existing device and output,
    /// reusing the staging texture when the size is unchanged. On failure the
    /// instance stays lost and <see cref="NeedsFullRebuild"/> is set.
    /// </summary>
    public bool TryRecreateDuplication()
    {
        if (_disposed || _device is null || _output1 is null) return false;
        var t0 = Stopwatch.GetTimestamp();
        var step = "DuplicateOutput";
        try
        {
            _frameAcquired = false;
            _duplication?.Dispose();
            _duplication = null;
            _duplication = _output1.DuplicateOutput(_device);
            step = "Output description";
            var c = _output1.Description.DesktopCoordinates;
            _screenW = c.Right - c.Left;
            _screenH = c.Bottom - c.Top;
            ApplyDuplicationGeometry();
            step = "CreateTexture2D staging";
            EnsureStaging();
            // The first frame of a new duplication must always be copied.
            _hasImage = false;
            IsLost = false;
            NeedsFullRebuild = false;
            var dm = _duplication.Description.ModeDescription;
            Diag?.Invoke($"{Tag} duplication recreated in {ElapsedMs(t0):F1}ms dup={dm.Width}x{dm.Height} rotation={_rotation} staging={_stagingW}x{_stagingH}");
            return true;
        }
        catch (Exception ex)
        {
            _duplication?.Dispose();
            _duplication = null;
            IsLost = true;
            NeedsFullRebuild = true;
            Diag?.Invoke($"{Tag} recreate failed at {step} hr={Hr(HrOf(ex))} {ex.GetType().Name}: {ex.Message}; falling back to full rebuild");
            return false;
        }
    }

    public bool AcquireFrame(uint timeoutMs = 0)
    {
        LastOutcome = AcquireOutcome.None;
        LastAccumulatedFrames = 0;
        LastAcquireMs = 0;
        LastCopyMs = 0;
        if (_duplication is null || _context is null || _stagingTexture is null) return false;
        if (_frameAcquired) ReleaseFrame();
        var duplicationFrameHeld = false;
        var step = "AcquireNextFrame";
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            var result = _duplication.AcquireNextFrame(timeoutMs, out var info, out var resource);
            LastAcquireMs = ElapsedMs(t0);
            if (result.Failure)
            {
                if (result.Code == DxgiErrorClassifier.WaitTimeout) { LastOutcome = AcquireOutcome.Timeout; return false; }
                LastOutcome = AcquireOutcome.Lost;
                MarkLost(step, result.Code, null);
                return false;
            }
            duplicationFrameHeld = true;
            LastAccumulatedFrames = info.AccumulatedFrames;
            LastOutcome = info.AccumulatedFrames == 0 ? AcquireOutcome.PointerOnly : AcquireOutcome.Frame;
            if (SkipPointerOnlyFrames && info.AccumulatedFrames == 0 && _hasImage)
            {
                step = "ReleaseFrame";
                _duplication.ReleaseFrame();
                duplicationFrameHeld = false;
                resource?.Dispose();
                return false;
            }
            step = "QueryInterface ID3D11Texture2D";
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            var td = tex.Description;
            if ((int)td.Width != _surfW || (int)td.Height != _surfH)
            {
                _surfW = (int)td.Width; _surfH = (int)td.Height;
                step = "CreateTexture2D staging";
                EnsureStaging();
            }
            _frameOnGpu = false;
            var t1 = Stopwatch.GetTimestamp();
            if (_downW > 0 && _downH > 0 && !_gpuFailed)
            {
                step = "GpuDownsample";
                try
                {
                    _gpu ??= new MirrorGpuDownsampler(_device!, _context);
                    _gpu.Downsample(tex, _downW, _downH, _surfW, _surfH, _rotation);
                    _frameOnGpu = true;
                }
                catch (Exception ex)
                {
                    _gpuFailed = true;
                    Diag?.Invoke($"{Tag} gpu downsample unavailable, using cpu path {ex.GetType().Name}: {ex.Message}");
                    _gpu?.Dispose();
                    _gpu = null;
                }
            }
            if (!_frameOnGpu)
            {
                step = "CopyResource";
                _context.CopyResource(_stagingTexture!, tex);
            }
            _context.Flush();
            LastCopyMs = ElapsedMs(t1);
            step = "ReleaseFrame";
            _duplication.ReleaseFrame();
            duplicationFrameHeld = false;
            resource.Dispose();
            _frameAcquired = true;
            _hasImage = true;
            return true;
        }
        catch (Exception ex)
        {
            LastOutcome = AcquireOutcome.Lost;
            if (duplicationFrameHeld) { try { _duplication.ReleaseFrame(); } catch { } }
            MarkLost(step, HrOf(ex), ex);
            return false;
        }
    }

    public byte[]? ReadRegion(int x, int y, int w, int h)
    {
        if (!_frameAcquired || _frameOnGpu || _context is null || _stagingTexture is null) return null;
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
        if (!_frameAcquired || _context is null) return false;
        if (dst.Length < dstW * dstH * 3) return false;
        if (_frameOnGpu) return TryReadGpu(dst, dstW, dstH);
        if (_stagingTexture is null) return false;
        try
        {
            var tMap = Stopwatch.GetTimestamp();
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            LastMapMs = ElapsedMs(tMap);
            var tSample = Stopwatch.GetTimestamp();
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
                LastSampleMs = ElapsedMs(tSample);
                return true;
            }
            finally { _context.Unmap(_stagingTexture, 0); }
        }
        catch { return false; }
    }

    private bool TryReadGpu(byte[] dst, int w, int h)
    {
        try
        {
            var t = Stopwatch.GetTimestamp();
            var ok = _gpu is not null && _gpu.ReadRgb(dst, w, h);
            LastMapMs = ElapsedMs(t);
            return ok;
        }
        catch { return false; }
    }

    public unsafe bool BlitToCanvas(Engine.CanvasBuffer canvas)
    {
        if (!_frameAcquired || _context is null) return false;
        if (_frameOnGpu)
        {
            var rgb = _gpuRgb is { } r && r.Length == canvas.Width * canvas.Height * 3 ? r : (_gpuRgb = new byte[canvas.Width * canvas.Height * 3]);
            if (!TryReadGpu(rgb, canvas.Width, canvas.Height)) return false;
            for (int y = 0, i = 0; y < canvas.Height; y++)
            {
                for (int x = 0; x < canvas.Width; x++, i += 3) canvas.SetPixel(x, y, rgb[i], rgb[i + 1], rgb[i + 2]);
            }
            return true;
        }
        if (_stagingTexture is null) return false;
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
        ReleaseFrame(); _duplication?.Dispose(); _output1?.Dispose(); _gpu?.Dispose(); _stagingTexture?.Dispose(); _context?.Dispose(); _device?.Dispose();
    }
}
#endif

using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine.Effects;

namespace Nexus.Service.Lighting.Engine;

public sealed class LightingEngine : IDisposable
{
    private readonly CanvasBuffer _canvas;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile IEffect? _currentEffect;
    private volatile DeviceFrame[] _devices = Array.Empty<DeviceFrame>();
    private byte[] _frameBuffer = Array.Empty<byte>();

    public LightingEngine() { _canvas = new CanvasBuffer(160, 90); }

    public int FrameIntervalMs { get; set; } = 33;

    /// <summary>
    /// Selects the canvas-to-LED sampling technique. True = LED FOOTPRINT
    /// SAMPLING: each LED integrates its cell of the device frame (see
    /// <see cref="SampleLedFootprint"/>), so sparse content - music-reactive
    /// bars, isolated lit pixels - registers anywhere inside the frame.
    /// False = POINT SAMPLING: each LED reads the single canvas pixel under
    /// its mapped point; content that misses those exact pixels leaves the
    /// device dark. Flip to false to revert to the pre-footprint behaviour.
    /// </summary>
    public bool FootprintSamplingEnabled { get; set; } = true;
    public event Action<ReadOnlyMemory<byte>>? OnFrame;
    public event Action? OnEffectChanged;
    public string CurrentEffectName => _currentEffect?.Name ?? "none";
    public IEffect? CurrentEffect => _currentEffect;
    public DeviceFrame[] Devices => _devices;
    public void UpdateDevices(DeviceFrame[] devices) { _devices = devices; }

    public void SetEffect(IEffect effect)
    {
        lock (_lock)
        {
            var old = _currentEffect;
            _currentEffect = effect;
            try
            { old?.Dispose(); }
            catch { }
            if (_loopTask is null || _loopTask.IsCompleted)
            { _cts = new CancellationTokenSource(); _loopTask = Task.Run(() => RunLoopAsync(_cts.Token)); }
        }
        // Handler may start/stop an audio-capture subprocess; must not run under the engine lock.
        OnEffectChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_lock)
        {
            var old = _currentEffect;
            _currentEffect = null;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try
            { old?.Dispose(); }
            catch { }
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }

            try
            { SerializeAndBroadcast(); }
            catch { }
        }
        // Handler may start/stop an audio-capture subprocess; must not run under the engine lock.
        OnEffectChanged?.Invoke();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // PeriodicTimer allocates once per loop vs Task.Delay allocating a
        // fresh Task every frame. Period is reloaded each tick so live
        // FrameIntervalMs changes propagate without restarting the loop.
        var periodMs = Math.Max(1, FrameIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var effect = _currentEffect;
                if (effect is null)
                {
                    break;
                }

                try
                {
                    effect.RenderFrame(_canvas, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    SampleDevicesFromCanvas();
                    if (effect is GameSyncEffect gs)
                    {
                        gs.WriteToDevices(_devices);
                    }
                    SerializeAndBroadcast();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[lighting-engine] {effect.Name} threw: {ex.Message}"); }

                var nextPeriodMs = Math.Max(1, FrameIntervalMs);
                if (nextPeriodMs != periodMs)
                {
                    periodMs = nextPeriodMs;
                    timer.Period = TimeSpan.FromMilliseconds(periodMs);
                }
                try
                {
                    if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }
        }
    }

    private void SampleDevicesFromCanvas()
    {
        var devices = _devices;
        var cw = _canvas.Width;
        var ch = _canvas.Height;
        const float CW = 1000f, CH = 600f;
        foreach (var dev in devices)
        {
            // Preview LED count from the editor's unsaved draft; clamped to the
            // physical buffer size because SetLed bounds-guards on LedCount.
            var ledCount = dev.PreviewLedCount is { } pc
                ? Math.Min(pc, dev.LedCount)
                : dev.LedCount;
            if (ledCount <= 0)
            {
                continue;
            }

            // LEDs above the preview count are not part of the draft; zero them
            // so they don't show stale colour from a prior render.
            for (int z = ledCount; z < dev.LedCount; z++)
            {
                dev.SetLed(z, 0, 0, 0);
            }

            var rectX = dev.X / CW * cw;
            var rectY = dev.Y / CH * ch;
            var rectW = dev.W / CW * cw;
            var rectH = dev.H / CH * ch;
            var rot = ((dev.Rotation % 360) + 360) % 360;

            // Preview layout: editor draft positions keyed by LED index.
            // When present, build parallel U/V/Disabled arrays in index order
            // so the same UV sampling path below works without branching.
            var previewLayout = dev.PreviewLayout;
            float[]? devLedU;
            float[]? devLedV;
            bool[]? devLedDisabled;
            if (previewLayout is not null && previewLayout.Length > 0)
            {
                var pu = new float[ledCount];
                var pv = new float[ledCount];
                bool[]? pd = null;
                foreach (var pos in previewLayout)
                {
                    var idx = pos.Index;
                    if (idx < 0 || idx >= ledCount)
                    {
                        continue;
                    }
                    pu[idx] = pos.U;
                    pv[idx] = pos.V;
                    if (pos.Disabled)
                    {
                        pd ??= new bool[ledCount];
                        pd[idx] = true;
                    }
                }
                devLedU = pu;
                devLedV = pv;
                devLedDisabled = pd;
            }
            else
            {
                // Keyboards and other matrix devices provide per-LED UVs so each key
                // samples from its real 2D position inside the rectangle instead of
                // being stretched along a single axis. Rotation is applied to the UV
                // coordinates around the rectangle centre so reorienting the board
                // keeps the mapping right.
                devLedU = dev.LedU;
                devLedV = dev.LedV;
                devLedDisabled = dev.LedDisabled;
            }

            if (devLedU is not null && devLedV is not null
                && devLedU.Length == ledCount && devLedV.Length == ledCount)
            {
                // Cell dims from a uniform cols x rows density estimate matched
                // to the rect aspect. The true UV layout may be non-uniform, so
                // neighbouring cells can overlap or leave small gaps; keyboards
                // land at ~key-sized cells and keep per-key sharpness.
                var aspect = rectH > 0.001f ? rectW / rectH : 1f;
                var cols = Math.Max(1, (int)MathF.Round(MathF.Sqrt(ledCount * aspect)));
                var rows = Math.Max(1, (ledCount + cols - 1) / cols);
                var uvHalfW = Math.Max(1f, rectW / cols) * 0.5f;
                var uvHalfH = Math.Max(1f, rectH / rows) * 0.5f;
                for (int i = 0; i < ledCount; i++)
                {
                    if (devLedDisabled is not null && i < devLedDisabled.Length && devLedDisabled[i])
                    {
                        dev.SetLed(i, 0, 0, 0);
                        continue;
                    }
                    var u = devLedU[i];
                    var v = devLedV[i];
                    float ur, vr;
                    switch (rot)
                    {
                        case 90:
                            ur = 1f - v;
                            vr = u;
                            break;
                        case 180:
                            ur = 1f - u;
                            vr = 1f - v;
                            break;
                        case 270:
                            ur = v;
                            vr = 1f - u;
                            break;
                        default:
                            ur = u;
                            vr = v;
                            break;
                    }
                    var sx = rectX + ur * rectW;
                    var sy = rectY + vr * rectH;
                    var (r, g, b) = FootprintSamplingEnabled
                        ? SampleLedFootprint(sx - uvHalfW, sy - uvHalfH, sx + uvHalfW, sy + uvHalfH)
                        : _canvas.GetPixel((int)sx, (int)sy);
                    dev.SetLed(i, r, g, b);
                }
                continue;
            }

            // Linear strip fallback: walk the LEDs along the rotation axis.
            var cx = rectX + rectW * 0.5f;
            var cy = rectY + rectH * 0.5f;
            float dx, dy;
            switch (rot)
            {
                case 90:
                    dx = 0f;
                    dy = rectH;
                    break;
                case 180:
                    dx = -rectW;
                    dy = 0f;
                    break;
                case 270:
                    dx = 0f;
                    dy = -rectH;
                    break;
                default:
                    dx = rectW;
                    dy = 0f;
                    break;
            }
            // Each LED's cell spans the full frame breadth across the strip axis
            // and one LED pitch along it, so content anywhere inside the frame
            // reaches the LED at that position instead of only the centerline.
            float linHalfW, linHalfH;
            if (rot is 90 or 270)
            {
                linHalfW = Math.Max(1f, rectW) * 0.5f;
                linHalfH = Math.Max(1f, rectH / ledCount) * 0.5f;
            }
            else
            {
                linHalfW = Math.Max(1f, rectW / ledCount) * 0.5f;
                linHalfH = Math.Max(1f, rectH) * 0.5f;
            }
            var denom = ledCount > 1 ? 1f / (ledCount - 1) : 0f;
            for (int i = 0; i < ledCount; i++)
            {
                if (devLedDisabled is not null && i < devLedDisabled.Length && devLedDisabled[i])
                {
                    dev.SetLed(i, 0, 0, 0);
                    continue;
                }
                var t = ledCount > 1 ? i * denom - 0.5f : 0f;
                var sx = cx + t * dx;
                var sy = cy + t * dy;
                var (r, g, b) = FootprintSamplingEnabled
                    ? SampleLedFootprint(sx - linHalfW, sy - linHalfH, sx + linHalfW, sy + linHalfH)
                    : _canvas.GetPixel((int)sx, (int)sy);
                dev.SetLed(i, r, g, b);
            }
        }

        ApplyTestOverlays(devices);
    }

    /// <summary>
    /// LED FOOTPRINT SAMPLING (gated by <see cref="FootprintSamplingEnabled"/>):
    /// samples the LED's cell of the canvas instead of a single point, so sparse
    /// content (music-reactive bars, isolated lit pixels) registers wherever it
    /// lands inside the frame. Samples are weighted by max-channel luminance
    /// squared: lit pixels dominate dark filler, an all-dark cell stays black,
    /// and hue is preserved (a per-channel max would mix channels from different
    /// pixels). Reads every canvas pixel in the cell; cells tile the frame
    /// (linear) or partition it by LED density (UV), so per-device cost is
    /// bounded by the frame's canvas-pixel area per rendered frame.
    /// </summary>
    private (byte r, byte g, byte b) SampleLedFootprint(float x0, float y0, float x1, float y1)
    {
        var ix0 = Math.Clamp((int)MathF.Floor(x0), 0, _canvas.Width - 1);
        var iy0 = Math.Clamp((int)MathF.Floor(y0), 0, _canvas.Height - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(x1) - 1, ix0, _canvas.Width - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(y1) - 1, iy0, _canvas.Height - 1);
        long wSum = 0, rSum = 0, gSum = 0, bSum = 0;
        for (int py = iy0; py <= iy1; py++)
        {
            for (int px = ix0; px <= ix1; px++)
            {
                var (r, g, b) = _canvas.GetPixel(px, py);
                int m = Math.Max(r, Math.Max(g, b));
                if (m == 0)
                {
                    continue;
                }
                long w = m * m;
                wSum += w;
                rSum += w * r;
                gSum += w * g;
                bSum += w * b;
            }
        }
        if (wSum == 0)
        {
            return (0, 0, 0);
        }
        return ((byte)(rSum / wSum), (byte)(gSum / wSum), (byte)(bSum / wSum));
    }

    private static void ApplyTestOverlays(DeviceFrame[] devices)
    {
        foreach (var dev in devices)
        {
            var previewCount = dev.PreviewLedCount is { } pc ? Math.Min(pc, dev.LedCount) : dev.LedCount;

            var highlights = dev.HighlightLeds;
            if (highlights is not null && highlights.Count > 0)
            {
                for (int i = 0; i < previewCount; i++)
                {
                    if (highlights.Contains(i))
                    {
                        dev.SetLed(i, 255, 255, 255);
                    }
                    else
                    {
                        dev.SetLed(i, 0, 0, 0);
                    }
                }
                continue;
            }

            var pattern = dev.TestPattern;
            if (pattern is not null)
            {
                if (pattern == "none")
                {
                    dev.Fill(0, 0, 0);
                }
                else
                {
                    // Prefer preview layout UVs when present; fall back to saved LedU/LedV.
                    float[]? ledU = null;
                    float[]? ledV = null;
                    var previewLayout = dev.PreviewLayout;
                    if (previewLayout is not null && previewLayout.Length > 0)
                    {
                        var pu = new float[previewCount];
                        var pv = new float[previewCount];
                        foreach (var pos in previewLayout)
                        {
                            if (pos.Index >= 0 && pos.Index < previewCount)
                            {
                                pu[pos.Index] = pos.U;
                                pv[pos.Index] = pos.V;
                            }
                        }
                        ledU = pu;
                        ledV = pv;
                    }
                    else
                    {
                        ledU = dev.LedU;
                        ledV = dev.LedV;
                    }

                    if (ledU is not null && ledV is not null)
                    {
                        var elapsed = dev.TestPatternStartMs > 0
                            ? Environment.TickCount64 - dev.TestPatternStartMs
                            : 0L;
                        var phase = (float)(elapsed % 2000 / 2000.0);
                        for (int i = 0; i < previewCount && i < ledU.Length && i < ledV.Length; i++)
                        {
                            var u = ledU[i];
                            var v = ledV[i];
                            float t = pattern == "horizontal" ? u : v;
                            var band = 1f - Math.Min(1f, Math.Abs(t - phase) * 5f);
                            dev.SetLed(i,
                                (byte)(band * 255), (byte)(band * 255), (byte)(band * 255));
                        }
                    }
                }
            }
        }
    }

    private void SerializeAndBroadcast()
    {
        var devices = _devices;
        var canvasBytes = _canvas.ByteCount;
        var totalSize = 1 + 4 + canvasBytes + 1;
        foreach (var dev in devices)
        {
            totalSize += 1 + 2 + dev.LedCount * 3;
        }

        if (_frameBuffer.Length < totalSize)
        {
            _frameBuffer = new byte[totalSize + 256];
        }

        var buf = _frameBuffer;
        buf[0] = 0x03;
        var pos = 1;
        var cw = (ushort)_canvas.Width;
        var ch = (ushort)_canvas.Height;
        buf[pos++] = (byte)(cw & 0xFF);
        buf[pos++] = (byte)(cw >> 8);
        buf[pos++] = (byte)(ch & 0xFF);
        buf[pos++] = (byte)(ch >> 8);
        _canvas.Pixels.CopyTo(buf.AsSpan(pos));
        pos += canvasBytes;
        buf[pos++] = (byte)Math.Min(devices.Length, 255);
        foreach (var dev in devices)
        {
            buf[pos++] = (byte)dev.Index;
            var lc = (ushort)dev.LedCount;
            buf[pos++] = (byte)(lc & 0xFF);
            buf[pos++] = (byte)(lc >> 8);
            dev.LedBytes.CopyTo(buf.AsSpan(pos));
            pos += dev.LedCount * 3;
        }
        OnFrame?.Invoke(new ReadOnlyMemory<byte>(_frameBuffer, 0, pos));
    }

    public void Dispose() { Stop(); try { _cts?.Dispose(); } catch { } _cts = null; }
}

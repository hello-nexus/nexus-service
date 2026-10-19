using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Capture;
using Nexus.Service.Persistence;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// Live screen content for a Flex screen: the shared screen capture, cut to a square and scaled to the
/// display. The capture is held through a read-only observer, so it never retargets what the lighting
/// effects capture; when one of them holds it at another size, that size is what arrives.
/// </summary>
public sealed class FlexScreenCapture : IDisposable
{
    public const int CaptureWidth = 640;
    public const int CaptureHeight = 360;
    private const int PanelEdge = FlexOverlayRenderer.FrameSize;
    public const int FrameIntervalMs = 66;

    private readonly Func<IScreenFrameSource?> _open;
    private readonly Func<string> _defaultMonitor;
    private readonly Func<string, (int Width, int Height)?> _nativeSize;
    private readonly object _gate = new();
    private readonly Dictionary<string, IScreenFrameSource> _handles = new(StringComparer.OrdinalIgnoreCase);

    public FlexScreenCapture(Func<IScreenFrameSource?> openObserver, Func<string> defaultMonitor, Func<string, (int Width, int Height)?>? nativeSize = null)
    {
        _open = openObserver;
        _defaultMonitor = defaultMonitor;
        _nativeSize = nativeSize ?? (_ => null);
    }

    /// <summary>
    /// The capture size that leaves the chosen square at least the panel's edge in pixels, capped at the
    /// display's own size, so a small crop is cut from real pixels instead of being stretched. When a
    /// lighting effect already holds the capture, its size is what arrives and the crop scales that.
    /// </summary>
    internal static (int Width, int Height) CaptureSizeFor(LianLiFlexScreenSource? source, int nativeWidth, int nativeHeight)
    {
        var crop = source is { CropSize: > 0 and <= 1 } ? source.CropSize : 1.0;
        var scale = Math.Min(1.0, PanelEdge / (crop * Math.Min(nativeWidth, nativeHeight)));
        return (Math.Max(2, (int)Math.Round(nativeWidth * scale)), Math.Max(2, (int)Math.Round(nativeHeight * scale)));
    }

    /// <summary>A 400x400 JPEG of the selected square, or null while no frame has arrived.</summary>
    public byte[]? RenderJpeg(string serial, LianLiFlexScreenSource? source)
    {
        IScreenFrameSource? handle;
        lock (_gate)
        {
            if (!_handles.TryGetValue(serial, out handle))
            {
                handle = _open();
                if (handle is null)
                {
                    return null;
                }
                _handles[serial] = handle;
            }
        }
        var monitor = string.IsNullOrEmpty(source?.DisplayId) ? _defaultMonitor() : source!.DisplayId;
        var (captureW, captureH) = _nativeSize(monitor) is { } native
            ? CaptureSizeFor(source, native.Width, native.Height)
            : (CaptureWidth, CaptureHeight);
        handle.Start(monitor, captureW, captureH);
        var rgb = handle.TryAcquireFrame(out var width, out var height);
        if (rgb is null || width <= 0 || height <= 0 || rgb.Length < width * height * 3)
        {
            return null;
        }
        var (x, y, edge) = SquareOf(source, width, height);
        using var frame = new SKBitmap(RenderKit.Info(edge, edge));
        var pixels = frame.GetPixelSpan();
        for (var row = 0; row < edge; row++)
        {
            for (var col = 0; col < edge; col++)
            {
                var s = (((y + row) * width) + x + col) * 3;
                var d = ((row * edge) + col) * 4;
                pixels[d] = rgb[s];
                pixels[d + 1] = rgb[s + 1];
                pixels[d + 2] = rgb[s + 2];
                pixels[d + 3] = 255;
            }
        }
        using var scaled = edge == FlexOverlayRenderer.FrameSize ? null : RenderKit.Resize(frame, FlexOverlayRenderer.FrameSize, FlexOverlayRenderer.FrameSize);
        return RenderKit.EncodeJpeg(scaled ?? frame);
    }

    /// <summary>The square to cut: the normalized crop when it fits, else the largest centered one.</summary>
    internal static (int X, int Y, int Edge) SquareOf(LianLiFlexScreenSource? source, int width, int height)
    {
        var edge = Math.Min(width, height);
        if (source is null || source.CropSize <= 0 || source.CropSize > 1)
        {
            return ((width - edge) / 2, (height - edge) / 2, edge);
        }
        edge = Math.Max(1, (int)Math.Round(source.CropSize * Math.Min(width, height)));
        var x = Math.Clamp((int)Math.Round(source.CropX * width), 0, width - edge);
        var y = Math.Clamp((int)Math.Round(source.CropY * height), 0, height - edge);
        return (x, y, edge);
    }

    /// <summary>The display's pixel size from its listed name, e.g. "Display 1 (1920x1200)"; an empty id is the selected (else first) display.</summary>
    internal static (int Width, int Height)? NativeSizeOf(Nexus.Service.Models.Lighting.ScreenSyncOptions options, string monitorId)
    {
        var id = string.IsNullOrEmpty(monitorId) ? options.SelectedMonitor : monitorId;
        var monitor = options.Monitors.Find(m => m.Id == id) ?? (options.Monitors.Count > 0 ? options.Monitors[0] : null);
        if (monitor is null)
        {
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(monitor.Name, @"\((\d+)x(\d+)\)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var w) && int.TryParse(match.Groups[2].Value, out var h) && w > 0 && h > 0
            ? (w, h)
            : null;
    }

    public void Release(string serial)
    {
        IScreenFrameSource? handle;
        lock (_gate)
        {
            _handles.Remove(serial, out handle);
        }
        handle?.Stop();
    }

    public void Dispose()
    {
        List<IScreenFrameSource> all;
        lock (_gate)
        {
            all = new List<IScreenFrameSource>(_handles.Values);
            _handles.Clear();
        }
        all.ForEach(h => h.Stop());
    }
}

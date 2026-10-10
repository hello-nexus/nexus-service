using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// macOS monitor topology via CoreGraphics. Identity (stable id, name,
/// manufacturer) comes from <see cref="MacDisplayBrightnessProvider"/>'s
/// display-handle enumeration so the id space matches GET /displays.
/// Bounds are global display-space points; pixel size comes from the
/// current CGDisplayMode, scale = pixels / points, physical DPI from
/// CGDisplayScreenSize (mm). AOT-safe: blittable P/Invoke only.
/// </summary>
public sealed class MacDisplayTopologyProvider : IDisplayTopologyProvider
{
    public bool PositionsAvailable => true;

    public IReadOnlyList<RawDisplayInfo>? Enumerate()
    {
        var results = new List<RawDisplayInfo>();
        try
        {
            var handles = MacDisplayBrightnessProvider.EnumerateDisplayHandles();
            for (var index = 0; index < handles.Count; index++)
            {
                var handle = handles[index];
                var bounds = CGDisplayBounds(handle.DisplayId);

                var pixelWidth = 0;
                var pixelHeight = 0;
                var mode = CGDisplayCopyDisplayMode(handle.DisplayId);
                if (mode != IntPtr.Zero)
                {
                    try
                    {
                        pixelWidth = (int)CGDisplayModeGetPixelWidth(mode);
                        pixelHeight = (int)CGDisplayModeGetPixelHeight(mode);
                    }
                    finally { CGDisplayModeRelease(mode); }
                }
                if (pixelWidth <= 0 || pixelHeight <= 0)
                {
                    pixelWidth = (int)Math.Round(bounds.Width);
                    pixelHeight = (int)Math.Round(bounds.Height);
                }

                double? scale = bounds.Width > 0
                    ? Math.Round(pixelWidth / bounds.Width, 2)
                    : null;

                var sizeMm = CGDisplayScreenSize(handle.DisplayId);
                var dpi = DisplayDensity.FromPhysicalMm(sizeMm.Width, sizeMm.Height, pixelWidth, pixelHeight);

                results.Add(new RawDisplayInfo
                {
                    Id = handle.Id,
                    Number = index + 1,
                    Name = handle.Name,
                    Manufacturer = handle.Manufacturer,
                    Model = handle.Model,
                    X = (int)Math.Round(bounds.X),
                    Y = (int)Math.Round(bounds.Y),
                    Width = (int)Math.Round(bounds.Width),
                    Height = (int)Math.Round(bounds.Height),
                    ResolutionWidth = pixelWidth,
                    ResolutionHeight = pixelHeight,
                    Scale = scale,
                    Dpi = dpi,
                    IsPrimary = CGDisplayIsMain(handle.DisplayId) != 0,
                    IsInternal = handle.IsInternal,
                    Orientation = DisplayOrientations.FromMacDegrees(CGDisplayRotation(handle.DisplayId)),
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-mac] topology enumerate failed: {ex.Message}");
        }
        return results;
    }

    // -- P/Invoke -----------------------------------------------------------

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    [DllImport(CoreGraphics)]
    private static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    private static extern CGSize CGDisplayScreenSize(uint display);

    [DllImport(CoreGraphics)]
    private static extern int CGDisplayIsMain(uint display);

    [DllImport(CoreGraphics)]
    private static extern double CGDisplayRotation(uint display);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern nint CGDisplayModeGetPixelHeight(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern void CGDisplayModeRelease(IntPtr mode);
}

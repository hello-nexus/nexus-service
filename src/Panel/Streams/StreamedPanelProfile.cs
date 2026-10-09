using System;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel.Streams;

/// <summary>Wire format the overlay produces for a streamed panel.</summary>
public enum StreamCodec
{
    /// <summary>H.264 Annex-B access units, for transports fronting a video decoder.</summary>
    H264 = 0,

    /// <summary>
    /// Whole uncompressed BGRA frames, for glass that takes a framebuffer directly.
    /// Costs orders of magnitude more bytes, so it only suits a small, slow panel.
    /// </summary>
    RawBgra = 1,
}

/// <summary>
/// Render + encode parameters for one streamed panel device kind. The
/// profile is the headless-config source of truth: it stamps the panel
/// record's capabilities and sizes the overlay's off-screen render host.
/// Per-serial overrides (fps/bitrate/profile kind) live in
/// <see cref="StreamedPanelStore"/>.
/// </summary>
public sealed record StreamedPanelProfile
{
    /// <summary>Stable id persisted as a per-serial override key.</summary>
    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>PanelSurfaces value stamped on the record; picks the default layout.</summary>
    public required string Surface { get; init; }

    /// <summary>
    /// Optional branding key stamped on the record's capabilities. Surfaces shared by more
    /// than one model (the cooler-LCD surfaces) need something finer than the surface name
    /// for the sidebar icon and label; promoted displays already use the same field.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>The panel takes a backlight command, so its record carries a brightness.</summary>
    public bool SupportsBrightness { get; init; }

    /// <summary>The panel's transport can hand the glass to Windows as a secondary monitor.</summary>
    public bool SupportsSecondaryMonitor { get; init; }

    /// <summary>The panel renders tall when its record's Portrait is on.</summary>
    public bool SupportsPortrait { get; init; }

    /// <summary>Physical px/inch, which sizes the widget grid; null leaves the surface's estimate.</summary>
    public double? Dpi { get; init; }

    public required int CssWidth { get; init; }
    public required int CssHeight { get; init; }
    public double Dpr { get; init; } = 1.0;
    public int Fps { get; init; } = 60;

    /// <summary>Panels longer than this on either side render at a lower resolution unless their record asks for high resolution.</summary>
    public const int RenderScaleMinSide = 1000;

    public const int PerformanceMinShortSide = 320;

    // Small whole-number ratios, so the nearest-neighbour upscale repeats pixels evenly.
    private static readonly double[] PerformanceScales = { 1.0 / 2, 2.0 / 3, 3.0 / 4 };

    /// <summary>The lowest scale that keeps <see cref="PerformanceMinShortSide"/> px on the short side.</summary>
    public double PerformanceRenderScale
    {
        get
        {
            foreach (var scale in PerformanceScales)
            {
                if (FramePixels(Math.Min(CssWidth, CssHeight), Dpr * scale) >= PerformanceMinShortSide)
                {
                    return scale;
                }
            }
            return 1.0;
        }
    }

    /// <summary>
    /// Fraction of the native resolution the overlay renders at; the ingest scales each frame
    /// back up to native. Below 1 it trades sharpness for render, capture and copy cost.
    /// </summary>
    public double RenderScale { get; init; } = 1.0;

    /// <summary>Only raw frames can be scaled back up before the device sees them.</summary>
    public bool SupportsRenderScale => Codec == StreamCodec.RawBgra && Math.Max(CssWidth, CssHeight) > RenderScaleMinSide;

    /// <summary>The overlay's frame length for a CSS length at a pixel ratio: its own even(round(css * dpr)).</summary>
    public static int FramePixels(int cssLength, double dpr) => Math.Max(2, (int)Math.Round(cssLength * dpr) & ~1);
    public int BitrateKbps { get; init; } = 8000;

    /// <summary>BitrateKbps is ignored when this is not <see cref="StreamCodec.H264"/>.</summary>
    public StreamCodec Codec { get; init; } = StreamCodec.H264;

    public const int MaxWriteBatchFrames = 8;

    /// <summary>
    /// Frames concatenated into one transport write. The paced writer ticks
    /// at Fps/batch and sends the batch as a single write, so a transport
    /// whose throughput is bounded per round trip (one ack per write, as on
    /// the D213's adb chain) carries batch-times more frames per second. A
    /// display-rate-bound device gains nothing: excess frames congest the
    /// chain and the queue trims. Above 1 the device shows frames in bursts
    /// of this size, so keep it at 1 unless the transport is the ceiling.
    /// Consumers read <see cref="EffectiveWriteBatchFrames"/>: the writer's
    /// tick interval and the pacing policy's send size must agree on one
    /// clamped value.
    /// </summary>
    public int WriteBatchFrames { get; init; } = 1;

    public int EffectiveWriteBatchFrames => Math.Clamp(WriteBatchFrames, 1, MaxWriteBatchFrames);

    public PanelDeviceCapabilities BuildCapabilities() => new()
    {
        Surface = Surface,
        Family = Family,
        SupportsBrightness = SupportsBrightness ? true : null,
        SupportsSecondaryMonitor = SupportsSecondaryMonitor ? true : null,
        SupportsPortrait = SupportsPortrait ? true : null,
        SupportsRenderScale = SupportsRenderScale ? true : null,
        Dpi = Dpi,
        Touch = false,
        CssWidth = CssWidth,
        CssHeight = CssHeight,
        Dpr = Dpr,
    };
}

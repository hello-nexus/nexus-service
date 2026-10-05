using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Migration;

/// <summary>Translated Y70 layout plus a report of what carried over.</summary>
internal sealed class Nexus2Y70LayoutResult
{
    public PanelLayoutDto? Layout { get; set; }
    public int Pages { get; set; }
    public int Widgets { get; set; }
    public int MappedWidgets { get; set; }
    public List<string> DroppedTypes { get; } = new();
}

/// <summary>Where the Y70 background Nexus 2 is showing comes from.</summary>
internal enum Nexus2Y70BackgroundSource
{
    /// <summary>A CSS background other than a linear gradient; nothing to bring over.</summary>
    None,
    /// <summary>A CSS linear gradient (the gradient presets), drawn into a still image.</summary>
    Gradient,
    /// <summary>An image or video file on disk: a custom upload or a downloaded preset.</summary>
    File,
    /// <summary>The particles video packed in Nexus 2's app.asar, its default and its fallback for a missing file.</summary>
    BundledParticles,
}

/// <summary>Translated Y70 theme -> panel appearance (accent + background).</summary>
internal sealed class Nexus2AppearanceResult
{
    public bool Available { get; set; }
    public string? AccentHex { get; set; }
    public Nexus2Y70BackgroundSource Background { get; set; }
    public string? BackgroundPath { get; set; }
    public Nexus2Gradient? BackgroundGradient { get; set; }
    /// <summary>Nexus 2 shows a video's first frame instead of playing it.</summary>
    public bool BackgroundStill { get; set; }
    /// <summary>Nexus 2's "Transparent Background": the Y70 window shows the desktop through it.</summary>
    public bool Transparent { get; set; }
    /// <summary>Widget tile opacity, 0-1 on both sides.</summary>
    public double? WidgetOpacity { get; set; }
}

/// <summary>Translated Q60 face: the active page's widget plus every other
/// page's translated config stashed by widget type (first-seen wins).</summary>
internal sealed class Nexus2Q60FaceResult
{
    public bool Available { get; set; }
    public string? ActiveWidgetType { get; set; }
    public Dictionary<string, JsonElement>? ActiveConfig { get; set; }
    public string? AccentHex { get; set; }
    public Dictionary<string, Dictionary<string, JsonElement>> StashedConfigs { get; } = new();
}

/// <summary>Q60 wallpaper resolved against q60\web\user-media, or a stock preset in q60\web\bgs.</summary>
internal sealed class Nexus2WallpaperResult
{
    public bool Available { get; set; }
    public string? AbsolutePath { get; set; }
    public string? FileName { get; set; }
    /// <summary>Nexus 2 cycles every q60\web\user-media file instead of showing the wallpaper.</summary>
    public bool Playlist { get; set; }
    public int? PlaylistIntervalSec { get; set; }
}

/// <summary>Y70 gallery widgets' referenced files, deduplicated by path.</summary>
internal sealed class Nexus2GallerySourcesResult
{
    public bool Available { get; set; }
    public List<string> ExistingPaths { get; } = new();
    public int Missing { get; set; }
}

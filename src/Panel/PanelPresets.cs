using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Panel;

/// <summary>
/// Capture and apply for <see cref="PanelPreset"/>. The live record is the
/// active preset's working copy: nothing writes into a preset while it is
/// loaded, and switching away captures the live fields back into it. That
/// covers every writer of the record (the device page, the panel itself,
/// widget settings) without hooking each one.
/// </summary>
public static class PanelPresets
{
    public const int Cap = 10;

    /// <summary>Saves the live personalization into the loaded preset, if any.</summary>
    public static void CaptureActive(PanelDeviceRecord record)
    {
        var active = record.Presets?.Find(p => p.Id == record.ActivePresetId);
        if (active is not null)
            Copy(record, active);
    }

    /// <summary>Deep-copies every personalization field, nulls included, so a
    /// field left at its default in the source is reset in the target.</summary>
    public static void Copy(IPanelPersonalization from, IPanelPersonalization to)
    {
        to.Layout = CloneLayout(from.Layout);
        to.ThemeMode = from.ThemeMode;
        to.AccentColor = from.AccentColor;
        to.BackgroundColor = from.BackgroundColor;
        to.BackgroundColorLight = from.BackgroundColorLight;
        to.BackgroundMode = from.BackgroundMode;
        to.BackgroundEffect = from.BackgroundEffect;
        to.BackgroundTemplate = from.BackgroundTemplate;
        to.BackgroundTemplates = from.BackgroundTemplates is null ? null : new Dictionary<string, int>(from.BackgroundTemplates);
        to.BackgroundOpacity = from.BackgroundOpacity;
        to.Backdrop = from.Backdrop;
        to.BackgroundMediaId = from.BackgroundMediaId;
        to.BackgroundMediaType = from.BackgroundMediaType;
        to.BackgroundMediaAlpha = from.BackgroundMediaAlpha;
        to.BackgroundMediaSlideshow = from.BackgroundMediaSlideshow;
        to.BackgroundMediaInterval = from.BackgroundMediaInterval;
        to.BackgroundMediaShuffle = from.BackgroundMediaShuffle;
        to.BackgroundMediaFinishVideos = from.BackgroundMediaFinishVideos;
        to.BackgroundMediaOrder = from.BackgroundMediaOrder is null ? null : new List<string>(from.BackgroundMediaOrder);
        to.BackgroundFrostLevel = from.BackgroundFrostLevel;
        to.GaugeGradient = from.GaugeGradient?.Select(s => new PanelGaugeGradientStop { At = s.At, Color = s.Color }).ToList();
        to.WidgetOpacity = from.WidgetOpacity;
        to.WidgetLabels = from.WidgetLabels;
        to.WidgetPadding = from.WidgetPadding;
        to.ThemeSyncWithDesktop = from.ThemeSyncWithDesktop;
        to.AccentSyncWithDesktop = from.AccentSyncWithDesktop;
    }

    public static PanelPresetsResponse ToResponse(PanelDeviceRecord record) => new()
    {
        Presets = record.Presets?.ConvertAll(p => new PanelPresetDto
        {
            Id = p.Id,
            Name = p.Name,
            Apps = p.Apps?.ConvertAll(a => new Nexus.Service.Models.Devices.PresetAppDto
            {
                Id = a.Id,
                Name = a.Name,
                ProcessName = a.ProcessName,
            }) ?? new(),
        }) ?? new(),
        ActiveId = record.ActivePresetId,
    };

    // Widget settings edit the layout in place, so a shared reference would
    // leak the live edit into the stored preset.
    private static PanelLayoutDto? CloneLayout(PanelLayoutDto? layout) => layout is null
        ? null
        : JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(layout, PersistenceJsonContext.Default.PanelLayoutDto),
            PersistenceJsonContext.Default.PanelLayoutDto);
}

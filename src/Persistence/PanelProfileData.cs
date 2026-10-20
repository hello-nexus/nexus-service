using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Persistence;

/// <summary>
/// Everything the Panels profile category carries, in one place. A profile
/// file holds panel records stripped to personalization plus the match keys
/// (id, surface, display fingerprint), and the cooler display content blocks.
/// Identity, display binding, capabilities, timestamps, enabled state and
/// hardware or mount settings stay with the live records and never travel.
/// A new personalization field joins through <see cref="IPanelPersonalization"/>
/// and <see cref="PanelPresets.Copy"/>; a new cooler content field is one line
/// in <see cref="CopyCoolerContent"/> (or <see cref="CopyScreenContent"/>).
/// </summary>
public static class PanelProfileData
{
    /// <summary>Writes a profile-file or export copy of <paramref name="source"/>'s panel data onto <paramref name="file"/>, replacing what it held.</summary>
    public static void WriteTo(NexusSettings file, NexusSettings source)
    {
        var records = new Dictionary<string, PanelDeviceRecord>(StringComparer.Ordinal);
        foreach (var live in source.PanelDevices.Values)
        {
            if (IsPhone(live))
            {
                continue;
            }
            records[live.Id] = Strip(live);
        }
        file.PanelDevices = records;

        file.Tryx = new TryxSettings();
        file.Devices = new DevicesSettings();
        CopyCoolerContent(source, file, creating: true);
    }

    /// <summary>
    /// Copies panel data from a profile file onto the matching live records. Never creates or removes a record
    /// or screen, and leaves unmatched panels as they are. A file with no panel records (written before the
    /// category existed) leaves the cooler content alone too.
    /// </summary>
    public static void ApplyToLive(NexusSettings live, NexusSettings file)
    {
        if (file.PanelDevices.Count == 0)
        {
            return;
        }
        foreach (var saved in file.PanelDevices.Values)
        {
            var target = Match(live, saved);
            if (target is null)
            {
                continue;
            }
            CopyLook(saved, target);
        }
        CopyCoolerContent(file, live, creating: false);
    }

    /// <summary>Personalization reset for every live record plus default cooler content; the profile-wide counterpart of <see cref="PanelDeviceRegistry.ResetToDefaults"/>.</summary>
    public static void Reset(NexusSettings target)
    {
        var blank = new PanelPreset();
        foreach (var record in target.PanelDevices.Values.Where(r => !IsPhone(r)))
        {
            PanelPresets.Copy(blank, record);
            record.Layout = PanelLayoutDefaults.StripSeedFor(record);
            record.LightingSelection = null;
            record.CoolingSelection = null;
            record.Presets = null;
            record.ActivePresetId = null;
        }

        var fresh = new NexusSettings();
        CopyCoolerContent(fresh, target, creating: false);
        foreach (var screen in AllScreens(target))
        {
            CopyScreenContent(new LianLiWirelessScreenSettings(), screen);
        }
    }

    /// <summary>True when the record's look equals a never-customized one (no personalization, no presets).</summary>
    public static bool IsDefaultLook(PanelDeviceRecord record)
    {
        if (record.Presets is { Count: > 0 } || record.ActivePresetId is not null)
        {
            return false;
        }
        var blank = new PanelPreset();
        var copy = new PanelPreset();
        PanelPresets.Copy(record, copy);
        return JsonSerializer.Serialize(copy, Serialization.PersistenceJsonContext.Default.PanelPreset)
            == JsonSerializer.Serialize(blank, Serialization.PersistenceJsonContext.Default.PanelPreset);
    }

    private static bool IsPhone(PanelDeviceRecord r) =>
        string.Equals(r.Capabilities?.Surface, PanelSurfaces.Phone, StringComparison.Ordinal);

    private static PanelDeviceRecord Strip(PanelDeviceRecord live)
    {
        var stripped = new PanelDeviceRecord
        {
            Id = live.Id,
            Capabilities = new PanelDeviceCapabilities { Surface = live.Capabilities?.Surface },
            DisplayFingerprint = live.DisplayFingerprint,
        };
        CopyLook(live, stripped);
        return stripped;
    }

    private static void CopyLook(PanelDeviceRecord from, PanelDeviceRecord to)
    {
        PanelPresets.Copy(from, to);
        to.Presets = from.Presets?.Select(ClonePreset).ToList();
        to.ActivePresetId = from.ActivePresetId;
    }

    private static PanelPreset ClonePreset(PanelPreset p)
    {
        var clone = new PanelPreset
        {
            Id = p.Id,
            Name = p.Name,
            Apps = p.Apps?.Select(a => new PresetAppBinding { Id = a.Id, Name = a.Name, ProcessName = a.ProcessName }).ToList(),
        };
        PanelPresets.Copy(p, clone);
        return clone;
    }

    /// <summary>Same id, else the one live record of the same single-instance surface, else a monitor with the same fingerprint.</summary>
    private static PanelDeviceRecord? Match(NexusSettings live, PanelDeviceRecord saved)
    {
        if (live.PanelDevices.TryGetValue(saved.Id, out var byId) && !IsPhone(byId))
        {
            return byId;
        }

        var surface = saved.Capabilities?.Surface;
        if (surface is not null && PanelSurfaces.IsSingleInstance(surface))
        {
            return live.PanelDevices.Values
                .Where(r => string.Equals(r.Capabilities?.Surface, surface, StringComparison.Ordinal))
                .OrderByDescending(r => r.LastSeenAt)
                .FirstOrDefault();
        }

        if (saved.DisplayFingerprint is { } fp)
        {
            return live.PanelDevices.Values.FirstOrDefault(r =>
                r.DisplayFingerprint is { } rf
                && rf.Manufacturer == fp.Manufacturer
                && rf.Product == fp.Product
                && rf.Serial == fp.Serial
                && rf.SerialText == fp.SerialText);
        }
        return null;
    }

    /// <summary>
    /// Cooler display content: Corsair LCD media, the Lian Li fan screens (wireless and Uni Fan TL) and the
    /// Tryx overlay and media. Brightness, rotation, lock sleep, order and device-held media stay hardware-side.
    /// With <paramref name="creating"/> false only screens the destination already has are touched.
    /// </summary>
    private static void CopyCoolerContent(NexusSettings from, NexusSettings to, bool creating)
    {
        to.Devices.Corsair.LcdSelectedMediaId = from.Devices.Corsair.LcdSelectedMediaId;

        CopyScreens(from.Devices.TlLcdScreens, to.Devices.TlLcdScreens, creating);
        CopyScreens(from.Devices.LianLiWireless.Screens, to.Devices.LianLiWireless.Screens, creating);

        var f = from.Tryx;
        var t = to.Tryx;
        t.OverlayItems = f.OverlayItems.Select(i => new TryxOverlaySensorItemSettings
        {
            SensorId = i.SensorId, Device = i.Device, Label = i.Label, X = i.X, Y = i.Y,
        }).ToList();
        t.OverlayColor = f.OverlayColor;
        t.OverlayAlign = f.OverlayAlign;
        t.OverlayFilter = f.OverlayFilter;
        t.OverlayOpacity = f.OverlayOpacity;
        t.OverlayFont = f.OverlayFont;
        t.OverlaySize = f.OverlaySize;
        t.CurrentMedia = f.CurrentMedia;
        t.CurrentMediaIsCustom = f.CurrentMediaIsCustom;
        t.SlideshowEnabled = f.SlideshowEnabled;
        t.SlideshowIntervalSec = f.SlideshowIntervalSec;
        t.SlideshowShuffle = f.SlideshowShuffle;
        t.SlideshowFinishVideos = f.SlideshowFinishVideos;
    }

    private static void CopyScreens(
        Dictionary<string, LianLiWirelessScreenSettings> from,
        Dictionary<string, LianLiWirelessScreenSettings> to,
        bool creating)
    {
        foreach (var (key, screen) in from)
        {
            if (!to.TryGetValue(key, out var dest))
            {
                if (!creating)
                {
                    continue;
                }
                dest = new LianLiWirelessScreenSettings();
                to[key] = dest;
            }
            CopyScreenContent(screen, dest);
        }
    }

    private static void CopyScreenContent(LianLiWirelessScreenSettings from, LianLiWirelessScreenSettings to)
    {
        to.NexusPanel = from.NexusPanel;
        to.ContentType = from.ContentType;
        to.MediaId = from.MediaId;
        to.SensorSource = from.SensorSource;
        to.SensorStyle = from.SensorStyle;
        to.ClockFace = from.ClockFace;
        to.AnimationId = from.AnimationId;
        to.ColorA = from.ColorA;
        to.ColorB = from.ColorB;
        to.TempUnit = from.TempUnit;
    }

    private static IEnumerable<LianLiWirelessScreenSettings> AllScreens(NexusSettings s) =>
        s.Devices.TlLcdScreens.Values.Concat(s.Devices.LianLiWireless.Screens.Values);
}

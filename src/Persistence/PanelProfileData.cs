using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Persistence;

/// <summary>
/// Everything the Panels profile category carries, in one place. A profile
/// file holds panel records stripped to personalization plus the match keys
/// (id, surface, hashed display fingerprint), and the cooler display content
/// blocks, under the <see cref="NexusSettings.HasPanelsData"/> marker.
/// Identity, display binding, capabilities, timestamps, enabled state and
/// hardware or mount settings stay with the live records and never travel.
/// A new personalization field joins through <see cref="IPanelPersonalization"/>
/// and <see cref="PanelPresets.Copy"/>; a new cooler content field is one line
/// in <see cref="CopyCoolerContent"/> or a screen copier. A layout migration
/// that walks <c>NexusSettings.PanelDevices</c> must run on a profile file
/// before <see cref="ApplyToLive"/> copies from it (see ProfileManager.LoadProfileIntoSettings).
/// </summary>
public static class PanelProfileData
{
    /// <summary>Saved records for panels absent from this PC are dropped after this long unseen.</summary>
    internal static readonly TimeSpan KeepAbsentFor = TimeSpan.FromDays(90);
    /// <summary>At most this many such records are kept, newest first.</summary>
    internal const int MaxAbsentRecords = 8;
    /// <summary>Serialized size the kept absent records may total; the api caps a profile at 512 KB.</summary>
    internal const int MaxAbsentBytes = 128 * 1024;

    private const long DayMs = 86_400_000;

    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Writes a profile-file or export copy of <paramref name="source"/>'s panel data onto <paramref name="file"/>.
    /// Records the file already holds that match no source record are kept, so looks for panels that
    /// exist only on another PC survive a save here.
    /// </summary>
    public static void WriteTo(NexusSettings file, NexusSettings source)
    {
        var live = source.PanelDevices.Values.Where(r => !IsPhone(r)).ToList();
        var matched = Pair(file.PanelDevices.Values.ToList(), live);
        var records = new Dictionary<string, PanelDeviceRecord>(StringComparer.Ordinal);
        var now = Clock();
        var oldest = now - KeepAbsentFor;
        var bytes = 0;
        var absent = file.PanelDevices.Values
            .Where(saved => !matched.ContainsKey(saved) && DateTimeOffset.FromUnixTimeMilliseconds(saved.LastSeenAt) >= oldest)
            .OrderByDescending(saved => saved.LastSeenAt);
        foreach (var saved in absent)
        {
            bytes += JsonSerializer.SerializeToUtf8Bytes(saved, PersistenceJsonContext.Default.PanelDeviceRecord).Length;
            if (records.Count >= MaxAbsentRecords || bytes > MaxAbsentBytes)
            {
                break;
            }
            records[saved.Id] = saved;
        }
        foreach (var record in live)
        {
            records[record.Id] = Strip(record);
        }
        file.PanelDevices = records;
        file.HasPanelsData = true;

        file.Tryx = new TryxSettings();
        file.Devices = new DevicesSettings();
        CopyCoolerContent(source, file, creating: true);
    }

    /// <summary>
    /// Copies panel data from a profile file onto the matching live records. Never creates or removes a record
    /// or screen, and leaves unmatched panels as they are. A file without the marker (written before the
    /// category existed) changes nothing.
    /// </summary>
    public static void ApplyToLive(NexusSettings live, NexusSettings file)
    {
        if (!file.HasPanelsData)
        {
            return;
        }
        var saved = file.PanelDevices.Values.ToList();
        var targets = live.PanelDevices.Values.Where(r => !IsPhone(r)).ToList();
        foreach (var (src, dst) in Pair(saved, targets))
        {
            CopyLook(src, dst);
            // A layout left unset on a streamed strip reseeds the way a personalization reset does.
            dst.Layout ??= PanelLayoutDefaults.StripSeedFor(dst);
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
            record.Presets = null;
            record.ActivePresetId = null;
        }

        CopyCoolerContent(new NexusSettings(), target, creating: false);
        foreach (var screen in target.Devices.TlLcdScreens.Values.Concat(target.Devices.LianLiWireless.Screens.Values))
        {
            CopyScreenContent(new LianLiWirelessScreenSettings(), screen);
        }
        foreach (var aio in target.Devices.LianLiWireless.AioScreens.Values)
        {
            CopyAioContent(new LianLiAioScreenSettings(), aio);
        }
    }

    /// <summary>True when a live record's look equals a never-customized one: no presets, no personalization, and a layout that is unset, the strip seed, or the surface's install default.</summary>
    public static bool IsDefaultLook(PanelDeviceRecord record)
    {
        if (record.Presets is { Count: > 0 } || record.ActivePresetId is not null)
        {
            return false;
        }
        var copy = new PanelPreset();
        PanelPresets.Copy(record, copy);
        var layout = copy.Layout;
        copy.Layout = null;
        if (Json(copy) != Json(new PanelPreset()))
        {
            return false;
        }
        if (layout is null)
        {
            return true;
        }
        var shape = Shape(layout);
        var seed = PanelLayoutDefaults.StripSeedFor(record);
        return (seed is not null && shape == Shape(seed))
            || shape == Shape(PanelLayoutDefaults.ForSurface(record.Capabilities?.Surface ?? PanelSurfaces.Y70));
    }

    /// <summary>The match key a profile file stores in place of the raw monitor fingerprint, which names the EDID serial.</summary>
    public static string? FingerprintKey(DisplayFingerprint? fp)
    {
        if (fp is null)
        {
            return null;
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{fp.Manufacturer}|{fp.Product}|{fp.Serial}|{fp.SerialText}"));
        return Convert.ToHexString(bytes);
    }

    private static string Json(PanelPreset p) => JsonSerializer.Serialize(p, PersistenceJsonContext.Default.PanelPreset);

    // Ids are regenerated per build, so shapes compare with ids normalized.
    private static string Shape(PanelLayoutDto layout)
    {
        var clone = JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(layout, PersistenceJsonContext.Default.PanelLayoutDto),
            PersistenceJsonContext.Default.PanelLayoutDto)!;
        return JsonSerializer.Serialize(PanelLayoutDefaults.WithStableIds(clone, ""), PersistenceJsonContext.Default.PanelLayoutDto);
    }

    private static bool IsPhone(PanelDeviceRecord r) =>
        string.Equals(r.Capabilities?.Surface, PanelSurfaces.Phone, StringComparison.Ordinal);

    /// <summary>One-time fix for presets saved before WidgetSize joined them: they take their record's size.</summary>
    public static void BackfillPresetWidgetSize(NexusSettings settings)
    {
        foreach (var record in settings.PanelDevices.Values)
        {
            foreach (var preset in record.Presets ?? [])
            {
                preset.WidgetSize ??= record.WidgetSize;
            }
        }
    }

    // LastSeenAt on a stripped record is the live one cut to the UTC day, so it ages out records for panels absent from this PC yet stays
    // stable across sync ticks (the payload hash must not move when nothing changed).
    private static PanelDeviceRecord Strip(PanelDeviceRecord live)
    {
        var stripped = new PanelDeviceRecord
        {
            Id = live.Id,
            LastSeenAt = live.LastSeenAt - live.LastSeenAt % DayMs,
            Capabilities = new PanelDeviceCapabilities { Surface = live.Capabilities?.Surface },
            FingerprintKey = live.FingerprintKey ?? FingerprintKey(live.DisplayFingerprint),
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

    /// <summary>
    /// Pairs saved records with live ones, each side used once. Same id first. Then a single-instance
    /// surface, only when exactly one saved and one live record are left on it (a multi-screen cooler
    /// registers several records on one surface, and those pair by id alone). Then a monitor fingerprint
    /// key, only when it is unique on both sides, so identical monitors never swap looks.
    /// </summary>
    private static Dictionary<PanelDeviceRecord, PanelDeviceRecord> Pair(List<PanelDeviceRecord> saved, List<PanelDeviceRecord> live)
    {
        var pairs = new Dictionary<PanelDeviceRecord, PanelDeviceRecord>();
        var claimed = new HashSet<PanelDeviceRecord>();
        foreach (var s in saved)
        {
            var hit = live.FirstOrDefault(l => string.Equals(l.Id, s.Id, StringComparison.Ordinal));
            if (hit is not null && claimed.Add(hit))
            {
                pairs[s] = hit;
            }
        }

        PairUnique(saved, live, pairs, claimed, r =>
            r.Capabilities?.Surface is { } surface && PanelSurfaces.IsSingleInstance(surface) ? surface : null);
        PairUnique(saved, live, pairs, claimed, r => r.FingerprintKey ?? FingerprintKey(r.DisplayFingerprint));
        return pairs;
    }

    private static void PairUnique(
        List<PanelDeviceRecord> saved, List<PanelDeviceRecord> live,
        Dictionary<PanelDeviceRecord, PanelDeviceRecord> pairs, HashSet<PanelDeviceRecord> claimed,
        Func<PanelDeviceRecord, string?> key)
    {
        var open = saved.Where(s => !pairs.ContainsKey(s) && key(s) is not null).ToList();
        foreach (var group in open.GroupBy(s => key(s)!))
        {
            var left = live.Where(l => !claimed.Contains(l) && key(l) == group.Key).ToList();
            if (group.Count() == 1 && left.Count == 1)
            {
                pairs[group.First()] = left[0];
                claimed.Add(left[0]);
            }
        }
    }

    /// <summary>
    /// Cooler display content: Corsair LCD media, the Lian Li fan and AIO screens and the Tryx overlay and
    /// media. Brightness, rotation, lock sleep, order and device-held media stay hardware-side. With
    /// <paramref name="creating"/> false only screens the destination already has are touched.
    /// </summary>
    private static void CopyCoolerContent(NexusSettings from, NexusSettings to, bool creating)
    {
        to.Devices.Corsair.LcdSelectedMediaId = from.Devices.Corsair.LcdSelectedMediaId;

        CopyEntries(from.Devices.TlLcdScreens, to.Devices.TlLcdScreens, creating, CopyScreenContent);
        CopyEntries(from.Devices.LianLiWireless.Screens, to.Devices.LianLiWireless.Screens, creating, CopyScreenContent);
        CopyEntries(from.Devices.LianLiWireless.AioScreens, to.Devices.LianLiWireless.AioScreens, creating, CopyAioContent);

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

    private static void CopyEntries<T>(Dictionary<string, T> from, Dictionary<string, T> to, bool creating, Action<T, T> copy)
        where T : new()
    {
        foreach (var (key, entry) in from)
        {
            if (!to.TryGetValue(key, out var dest))
            {
                if (!creating)
                {
                    continue;
                }
                dest = new T();
                to[key] = dest;
            }
            copy(entry, dest);
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

    private static void CopyAioContent(LianLiAioScreenSettings from, LianLiAioScreenSettings to)
    {
        to.Theme = from.Theme;
        to.LabelColor = from.LabelColor;
        to.ValueColor = from.ValueColor;
        to.UnitColor = from.UnitColor;
        to.ShowCpuTemp = from.ShowCpuTemp;
        to.ShowCpuLoad = from.ShowCpuLoad;
        to.ShowGpuTemp = from.ShowGpuTemp;
        to.ShowGpuLoad = from.ShowGpuLoad;
        to.ShowFanSpeed = from.ShowFanSpeed;
        to.LoopInterval = from.LoopInterval;
        to.NexusWidgets = from.NexusWidgets;
    }
}

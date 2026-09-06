using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Generic per-segment composition for first-party frame writers: each tick,
/// every zone frame of a device copies its slices into per-segment color
/// buffers (applying the zone card's power / brightness / identify state),
/// and the writer pushes the segment buffers to the hardware. With the
/// default partition this reproduces the legacy one-frame-per-segment writer
/// byte for byte; with custom partitions it composes spanning and partial
/// zones into the same hardware buffers.
/// </summary>
public static class SegmentFrameComposer
{
    private const int IdentifyFlashHalfPeriodMs = 250;

    /// <summary>
    /// Compose all zone frames of <paramref name="structure"/> into
    /// <paramref name="segmentBuffers"/> (one buffer per segment, sized via
    /// <see cref="EnsureBuffers"/>). Returns per-segment flags telling the
    /// writer which hardware surfaces actually had a live zone frame.
    /// </summary>
    public static bool[] Compose(
        DeviceStructure structure,
        IReadOnlyList<ResolvedZone> zones,
        IReadOnlyList<DeviceFrame> frames,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness,
        double masterMul,
        long nowTicks,
        Np50IdentifyTracker? identify,
        RgbColor[][] segmentBuffers)
    {
        var touched = new bool[structure.Segments.Count];
        for (var s = 0; s < segmentBuffers.Length; s++)
        {
            Array.Clear(segmentBuffers[s], 0, segmentBuffers[s].Length);
        }

        foreach (var zone in zones)
        {
            DeviceFrame? frame = null;
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames[i].Id == zone.Id)
                { frame = frames[i]; break; }
            }
            if (frame is null)
            {
                continue;
            }

            // The keeb firmware-brightness level (masterMul, set by the knob and
            // the Settings slider) multiplies the per-zone software level, which the
            // global master brightness caps: effective = min(global, zone) * masterMul.
            var mul = ComputeBrightnessMul(zone.Id, disabled, uncontrolled, prefs, globalBrightness, out var adjust) * masterMul;
            var identifying = false;
            var identifyOn = false;
            if (identify is not null && identify.TryGetActive(zone.Id, nowTicks, out var startTicks))
            {
                identifying = true;
                var elapsedMs = (nowTicks - startTicks) / TimeSpan.TicksPerMillisecond;
                identifyOn = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            }

            var src = frame.LedBytes;
            var zoneLocal = 0;
            foreach (var slice in zone.Slices)
            {
                if (slice.Segment < 0 || slice.Segment >= segmentBuffers.Length)
                {
                    zoneLocal += slice.Count;
                    continue;
                }
                var buf = segmentBuffers[slice.Segment];
                var count = Math.Min(slice.Count, Math.Max(0, buf.Length - slice.Start));
                if (identifying)
                {
                    var c = identifyOn ? new RgbColor(255, 255, 255) : default;
                    for (var i = 0; i < count; i++)
                    {
                        buf[slice.Start + i] = c;
                    }
                }
                else if (!adjust.IsIdentity)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var off = (zoneLocal + i) * 3;
                        if (off + 2 >= src.Length || mul <= 0.0)
                        {
                            buf[slice.Start + i] = default;
                            continue;
                        }
                        adjust.Apply(src[off], src[off + 1], src[off + 2], mul, out var ar, out var ag, out var ab);
                        buf[slice.Start + i] = new RgbColor(ar, ag, ab);
                    }
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        var off = (zoneLocal + i) * 3;
                        if (off + 2 >= src.Length || mul <= 0.0)
                        {
                            buf[slice.Start + i] = default;
                        }
                        else if (mul >= 0.999)
                        {
                            buf[slice.Start + i] = new RgbColor(src[off], src[off + 1], src[off + 2]);
                        }
                        else
                        {
                            buf[slice.Start + i] = new RgbColor(
                                (byte)(src[off] * mul), (byte)(src[off + 1] * mul), (byte)(src[off + 2] * mul));
                        }
                    }
                }
                touched[slice.Segment] = true;
                zoneLocal += slice.Count;
            }
        }
        return touched;
    }

    /// <summary>Allocate / resize one color buffer per segment, sized to the hardware-reported counts.</summary>
    public static void EnsureBuffers(DeviceStructure structure, ref RgbColor[][] buffers)
    {
        if (buffers.Length != structure.Segments.Count)
        {
            buffers = new RgbColor[structure.Segments.Count][];
        }
        for (var s = 0; s < buffers.Length; s++)
        {
            var size = Math.Max(0, structure.Segments[s].FrameLedCount);
            if (buffers[s] is null || buffers[s].Length != size)
            {
                buffers[s] = new RgbColor[size];
            }
        }
    }

    /// <summary>Combined off-switch + per-card brightness capped by the master
    /// level: a card never renders brighter than master (min(device/100, global)).
    /// An uncontrolled zone renders black too - the shared hardware transport means
    /// only the whole physical device can be handed back to firmware, so a
    /// zone-level uncontrolled flag on a device with controlled siblings just blacks it.</summary>
    public static double ComputeBrightnessMul(string id,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness,
        out DeviceColorAdjust adjust)
    {
        adjust = DeviceColorAdjust.Identity;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == id)
            {
                return 0.0;
            }
        }
        for (var i = 0; i < uncontrolled.Count; i++)
        {
            if (uncontrolled[i] == id)
            {
                return 0.0;
            }
        }
        int devBrightness;
        // One lookup feeds both the brightness and the colour trim.
        try
        {
            if (prefs.TryGetValue(id, out var pref) && pref is not null)
            {
                devBrightness = pref.Brightness;
                adjust = DeviceColorAdjust.For(pref);
            }
            else
            {
                devBrightness = 100;
            }
        }
        catch (InvalidOperationException) { devBrightness = 100; }
        return Math.Min(Math.Clamp(devBrightness, 0, 100) / 100.0, globalBrightness);
    }
}

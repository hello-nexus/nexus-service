using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>Stable ids for OpenRGB devices that report neither serial nor location.</summary>
public static class OpenRgbPinnedIds
{
    /// <summary>
    /// Sets <see cref="RgbDevice.PinnedId"/> on every serial- and location-less
    /// device: its pinned id when it has one, otherwise the "openrgb-N" its
    /// position gives it now (the id its settings were saved under), moved past
    /// any number another pin holds. Returns the pins to persist.
    /// </summary>
    public static List<KeyValuePair<string, string>> Assign(IReadOnlyList<RgbDevice> devices, IReadOnlyDictionary<string, string> pins)
    {
        var added = new List<KeyValuePair<string, string>>();
        Dictionary<string, int>? ordinals = null;
        HashSet<string>? used = null;
        foreach (var d in devices)
        {
            if (d.HasStableHardwareId)
            {
                continue;
            }
            ordinals ??= new Dictionary<string, int>(StringComparer.Ordinal);
            ordinals.TryGetValue(d.Name, out var ordinal);
            ordinals[d.Name] = ordinal + 1;
            var key = $"{d.Name}#{ordinal}";
            if (pins.TryGetValue(key, out var pinned))
            {
                d.PinnedId = pinned;
                continue;
            }
            if (used is null)
            {
                used = new HashSet<string>(pins.Values, StringComparer.Ordinal);
            }
            var n = d.Index;
            var id = $"openrgb-{n}";
            while (used.Contains(id))
            {
                id = $"openrgb-{++n}";
            }
            used.Add(id);
            d.PinnedId = id;
            added.Add(new KeyValuePair<string, string>(key, id));
        }
        return added;
    }
}

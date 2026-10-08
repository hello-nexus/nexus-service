using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Game controllers whose OpenRGB detectors stay disabled until the user turns
/// the controller's card on: the Sony detectors open the gamepad's own HID
/// interface and write its output report, which fights games and Steam Input
/// for the controller. A present controller shows as a detector-excluded card
/// keyed by <see cref="KeyOf"/>; turning that card on records the detector in
/// <see cref="DevicesSettings.OpenRgbGamepadDetectorsAllowed"/>.
/// </summary>
public static class OpenRgbGamepadDefaults
{
    /// <param name="Detector">REGISTER_*_DETECTOR name in nexus-rgb/openrgb-headless, verbatim.</param>
    public sealed record Entry(string Detector, string Vendor, int VendorId, int ProductId);

    /// <summary>OpenRGB DEVICE_TYPE_GAMEPAD.</summary>
    private const uint GamepadType = 10;

    private const string KeyPrefix = "openrgb-gamepad-";

    public static readonly IReadOnlyList<Entry> Entries = new Entry[]
    {
        new("Sony DualShock 4", "Sony", 0x054C, 0x05C4),
        new("Sony DualShock 4", "Sony", 0x054C, 0x09CC),
        new("Sony DualShock 4", "Sony", 0x054C, 0x0BA0),
        new("Sony DualSense", "Sony", 0x054C, 0x0CE6),
        new("Sony DualSense Edge", "Sony", 0x054C, 0x0DF2),
        new("GameSir Nova 2 Lite", "GameSir", 0x3537, 0x100F),
    };

    /// <summary>Exclusion key of a detector's synthesized card.</summary>
    public static string KeyOf(string detector) =>
        KeyPrefix + detector.ToLowerInvariant().Replace(' ', '-');

    public static bool IsAllowed(NexusSettings s, string detector) =>
        s.Devices.OpenRgbGamepadDetectorsAllowed.Contains(detector);

    /// <summary>Detectors to keep disabled whether or not the controller is plugged in.</summary>
    public static SortedSet<string> HeldOffDetectors(NexusSettings s)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in Entries)
        {
            if (!IsAllowed(s, e.Detector))
                names.Add(e.Detector);
        }
        return names;
    }

    /// <summary>True for the synthesized card key of a detector still held off.</summary>
    public static bool IsHeldOffKey(NexusSettings s, string key)
    {
        foreach (var e in Entries)
        {
            if (KeyOf(e.Detector) == key)
                return !IsAllowed(s, e.Detector);
        }
        return false;
    }

    /// <summary>True when (vid, pid) belongs to a held-off controller, which no re-detect can find.</summary>
    public static bool IsHeldOff(NexusSettings s, int vendorId, int productId)
    {
        foreach (var e in Entries)
        {
            if (e.VendorId == vendorId && e.ProductId == productId && !IsAllowed(s, e.Detector))
                return true;
        }
        return false;
    }

    /// <summary>True when <see cref="ApplyPresence"/> would change anything.</summary>
    public static bool PresenceChanges(NexusSettings s, IReadOnlySet<string> presentDetectors)
    {
        foreach (var detector in HeldOffDetectors(s))
        {
            var key = KeyOf(detector);
            var wants = WantsCard(s, detector, presentDetectors);
            if (wants != s.Devices.OpenRgbDetectorExclusions.ContainsKey(key)
                || (wants && !s.Devices.UncontrolledLightingDevices.Contains(key)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A controller the user excluded before it was held off already has its own off card.</summary>
    private static bool WantsCard(NexusSettings s, string detector, IReadOnlySet<string> presentDetectors)
    {
        if (!presentDetectors.Contains(detector))
            return false;
        var key = KeyOf(detector);
        foreach (var kv in s.Devices.OpenRgbDetectorExclusions)
        {
            if (kv.Key != key && kv.Value.DetectorName == detector)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Adds a card for each held-off controller in <paramref name="presentDetectors"/>
    /// and drops the card of one no longer present. Returns whether anything changed.
    /// Runs inside IConfigStore.Update; collections are replaced, never mutated.
    /// </summary>
    public static bool ApplyPresence(NexusSettings s, IReadOnlySet<string> presentDetectors)
    {
        var exclusions = s.Devices.OpenRgbDetectorExclusions;
        var uncontrolled = s.Devices.UncontrolledLightingDevices;
        Dictionary<string, OpenRgbDetectorExclusion>? nextExclusions = null;
        List<string>? nextUncontrolled = null;
        foreach (var detector in HeldOffDetectors(s))
        {
            var key = KeyOf(detector);
            var present = WantsCard(s, detector, presentDetectors);
            // A layout preset swap can drop the key from the uncontrolled list while the card stays.
            if (present == exclusions.ContainsKey(key) && (!present || uncontrolled.Contains(key)))
                continue;
            nextExclusions ??= new Dictionary<string, OpenRgbDetectorExclusion>(exclusions);
            nextUncontrolled ??= new List<string>(uncontrolled);
            if (present)
            {
                nextExclusions[key] = new OpenRgbDetectorExclusion
                {
                    DetectorName = detector,
                    DeviceName = detector,
                    Vendor = VendorOf(detector),
                    LedCount = 1,
                    Type = GamepadType,
                };
                if (!nextUncontrolled.Contains(key))
                    nextUncontrolled.Add(key);
            }
            else
            {
                nextExclusions.Remove(key);
                nextUncontrolled.Remove(key);
            }
        }
        if (nextExclusions is null || nextUncontrolled is null)
            return false;
        s.Devices.OpenRgbDetectorExclusions = nextExclusions;
        s.Devices.UncontrolledLightingDevices = nextUncontrolled;
        return true;
    }

    /// <summary>Allows the held-off detector behind an excluded card the user turned on; a no-op for any other device.</summary>
    public static void AllowFor(NexusSettings s, string exclusionKey)
    {
        if (!s.Devices.OpenRgbDetectorExclusions.TryGetValue(exclusionKey, out var snap) || IsAllowed(s, snap.DetectorName))
            return;
        foreach (var e in Entries)
        {
            if (e.Detector != snap.DetectorName)
                continue;
            s.Devices.OpenRgbGamepadDetectorsAllowed = new List<string>(s.Devices.OpenRgbGamepadDetectorsAllowed) { e.Detector };
            return;
        }
    }

    private static string VendorOf(string detector)
    {
        foreach (var e in Entries)
        {
            if (e.Detector == detector)
                return e.Vendor;
        }
        return "";
    }
}

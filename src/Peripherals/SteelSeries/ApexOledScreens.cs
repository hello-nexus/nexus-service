using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.SteelSeries;

/// <summary>One attached keyboard screen: its model, the HID collection that takes frames, and the key that tells two keyboards apart.</summary>
public sealed record ApexOledScreen(ApexOledModel Model, HidDeviceInfo Info, string Key);

/// <summary>
/// Finds attached Apex screens. Discovery and the transport both go through here, so they always
/// pick the same HID collection of the same keyboard.
/// </summary>
public static class ApexOledScreens
{
    /// <summary>Report id byte plus the frame report body: the smallest feature report length that can carry a frame.</summary>
    public const int MinFeatureLength = ApexOledProtocol.FeatureReportLength - 1;

    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    /// <summary>Key of a keyboard whose HID interfaces carry no serial: stable across replug, unlike a device path. Such keyboards are told apart by model only, so two serial-less units of one model count as one.</summary>
    private const string UnitKey = "unit0";

    /// <summary>
    /// <paramref name="enforceLength"/> is Windows only, where a feature report longer than the
    /// collection's is refused: collections known to be too short are dropped and one that reports a
    /// fitting length is preferred. Elsewhere lengths are ignored, because they come from the first
    /// top-level collection of the descriptor and are not reliable for this one. Either way the pick
    /// among equals is the lowest path, so it never depends on enumeration order.
    /// </summary>
    public static List<ApexOledScreen> Find(IHidEnumerator hid, bool? enforceLength = null)
    {
        var enforce = enforceLength ?? OperatingSystem.IsWindows();
        var screens = new List<ApexOledScreen>();
        foreach (var model in ApexOledProtocol.Models)
        {
            var candidates = new List<HidDeviceInfo>();
            foreach (var info in hid.Find(ApexOledProtocol.VendorId, model.ProductId))
            {
                if (info.UsagePage == ApexOledProtocol.ControlUsagePage)
                {
                    candidates.Add(info);
                }
            }
            candidates.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
            var chosen = new Dictionary<string, HidDeviceInfo>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var info in candidates)
            {
                // A length of zero means the platform could not size the collection; only a known short one is ruled out.
                if (enforce && info.FeatureReportByteLength is > 0 and < MinFeatureLength)
                {
                    continue;
                }
                var key = string.IsNullOrEmpty(info.Serial) ? UnitKey : info.Serial;
                if (!chosen.TryGetValue(key, out var held))
                {
                    order.Add(key);
                    chosen[key] = info;
                }
                else if (enforce && held.FeatureReportByteLength == 0 && info.FeatureReportByteLength >= MinFeatureLength)
                {
                    chosen[key] = info;
                }
            }
            foreach (var key in order)
            {
                screens.Add(new ApexOledScreen(model, chosen[key], key));
            }
            if (candidates.Count > 0 && order.Count == 0)
            {
                LogOnce($"short:{model.ProductId}",
                    $"{model.Name}: no screen collection takes a {ApexOledProtocol.FeatureReportLength} byte feature report; the screen cannot be driven");
            }
        }
        return screens;
    }

    private static void LogOnce(string key, string message)
    {
        lock (Logged)
        {
            if (!Logged.Add(key))
            {
                return;
            }
        }
        ServiceLog.Warn($"[steelseries-apex-oled] {message}");
    }
}

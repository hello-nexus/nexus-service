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

    public static List<ApexOledScreen> Find(IHidEnumerator hid)
    {
        var screens = new List<ApexOledScreen>();
        foreach (var model in ApexOledProtocol.Models)
        {
            var best = new Dictionary<string, ApexOledScreen>(StringComparer.Ordinal);
            var order = new List<string>();
            var onPage = 0;
            foreach (var info in hid.Find(ApexOledProtocol.VendorId, model.ProductId))
            {
                if (info.UsagePage != ApexOledProtocol.ControlUsagePage)
                {
                    continue;
                }
                onPage++;
                // A length of zero means the platform could not size the collection; only a known
                // short one is ruled out (Windows refuses a feature report longer than the collection's).
                if (info.FeatureReportByteLength is > 0 and < MinFeatureLength)
                {
                    continue;
                }
                var key = string.IsNullOrEmpty(info.Serial) ? info.Path : info.Serial;
                if (!best.TryGetValue(key, out var held))
                {
                    order.Add(key);
                    best[key] = new ApexOledScreen(model, info, key);
                }
                else if (held.Info.FeatureReportByteLength == 0 && info.FeatureReportByteLength >= MinFeatureLength)
                {
                    best[key] = new ApexOledScreen(model, info, key);
                }
            }
            foreach (var key in order)
            {
                screens.Add(best[key]);
            }
            if (onPage > 0 && order.Count == 0)
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

using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Monitors that exist only because a Nexus device integration created or owns
/// them: the virtual display a streamed panel's secondary monitor mode brings
/// up, and a Lian Li LCD running in its USB display mode. The device's own entry
/// represents them, so they never become a generic display panel record and
/// never appear as standalone displays. Matched on the EDID manufacturer and
/// product code, never on resolution. The Lian Li identity is shared with
/// unrelated USB displays, so it counts only together with adapter evidence
/// naming the Lian Li device.
/// </summary>
public static class DeviceOwnedDisplays
{
    // Always device-owned on EDID identity alone.
    private static readonly (string Manufacturer, string Product)[] Owned =
    {
        ("NXS", "0001"),
    };

    // Shared EDID identity that is owned only with adapter evidence.
    private const string SharedUsbManufacturer = "TUR";
    private const string SharedUsbProduct = "0000";

    // Spacing in the vendor name varies between drivers, so it is ignored.
    private static bool HasLianLiEvidence(string? evidence)
    {
        if (string.IsNullOrEmpty(evidence)) return false;
        var compact = evidence.Replace(" ", "", StringComparison.Ordinal);
        return compact.Contains("LIANLI", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("VID_1A86&PID_AD20", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the identity is owned only together with adapter evidence, so reading that evidence is worthwhile.</summary>
    public static bool NeedsAdapterEvidence(string? manufacturer, string? product)
        => string.Equals(manufacturer, SharedUsbManufacturer, StringComparison.OrdinalIgnoreCase)
           && string.Equals(product, SharedUsbProduct, StringComparison.OrdinalIgnoreCase);

    /// <summary>An owned or conditionally owned identity, worth one diagnostic line.</summary>
    public static bool IsCandidate(RawDisplayInfo info)
        => IsOwned(info)
           || NeedsAdapterEvidence(info.Manufacturer, info.Model)
           || NeedsAdapterEvidence(info.Fingerprint?.Manufacturer, info.Fingerprint?.Product);

    public static bool IsOwned(string? manufacturer, string? product, string? adapterEvidence = null)
    {
        if (string.IsNullOrEmpty(manufacturer) || string.IsNullOrEmpty(product)) return false;
        foreach (var (m, p) in Owned)
        {
            if (string.Equals(manufacturer, m, StringComparison.OrdinalIgnoreCase)
                && string.Equals(product, p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return string.Equals(manufacturer, SharedUsbManufacturer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(product, SharedUsbProduct, StringComparison.OrdinalIgnoreCase)
            && HasLianLiEvidence(adapterEvidence);
    }

    /// <summary>EDID identity only: a stored record carries no adapter evidence, so only always-owned identities match.</summary>
    public static bool IsOwned(DisplayFingerprint? fingerprint)
        => fingerprint is not null && IsOwned(fingerprint.Manufacturer, fingerprint.Product);

    public static bool IsOwned(RawDisplayInfo info)
    {
        var evidence = info.AdapterEvidence;
        return IsOwned(info.Fingerprint?.Manufacturer, info.Fingerprint?.Product, evidence)
            || IsOwned(info.Manufacturer, info.Model, evidence)
            || IsOwnedHardwareId(info.RawHardwareId);
    }

    /// <summary>The Windows PnP interface path (\\?\DISPLAY#TUR0000#...) or a display id names the owned EDID identity.</summary>
    public static bool IsOwnedHardwareId(string? hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return false;
        foreach (var (m, p) in Owned)
        {
            var identity = m + p;
            if (hardwareId.Contains("DISPLAY#" + identity + "#", StringComparison.OrdinalIgnoreCase)
                || hardwareId.StartsWith(identity + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes panel records that earlier builds allocated for an always-owned
    /// display and returns their ids. Matches the stored fingerprint, falling
    /// back to the display id, which starts with the EDID identity. Tolerates
    /// missing collections and null entries.
    /// </summary>
    public static IReadOnlyList<string> PruneRecords(NexusSettings settings)
    {
        var records = settings?.PanelDevices;
        var stale = new List<string>();
        if (records is null || records.Count == 0) return stale;
        foreach (var kv in records)
        {
            var record = kv.Value;
            if (record is null) continue;
            if (IsOwned(record.DisplayFingerprint) || IsOwnedHardwareId(record.DisplayId)) stale.Add(kv.Key);
        }
        foreach (var id in stale) records.Remove(id);
        return stale;
    }

    /// <summary>Deletes a removed panel record's uploaded background media under <paramref name="mediaRoot"/>; best-effort.</summary>
    public static void DeleteRecordMedia(string mediaRoot, string recordId)
    {
        try
        {
            if (!Nexus.Service.Panel.PanelBgLibrary.IsValidId(recordId)) return;
            var dir = Path.Combine(mediaRoot, recordId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays] media cleanup for removed record failed: {ex.Message}");
        }
    }
}

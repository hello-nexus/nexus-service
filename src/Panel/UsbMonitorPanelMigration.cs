using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>A USB device in secondary-monitor mode and the separate panel record that sat on its virtual display.</summary>
public sealed class UsbMonitorPanelPair
{
    public string UsbId { get; set; } = "";
    public string VirtualId { get; set; } = "";
}

/// <summary>
/// v29: a USB device's secondary monitor now shows the device's own panel, so the panel a user
/// built on the virtual display (a separate record) becomes the device's look. Pairs are found
/// once in the live settings (only the live record carries the monitor flag) and kept, so each
/// profile file is migrated when it is first read at a lower version. A profile is stamped
/// afterwards and never copied again, so later edits are not overwritten. Widget positions are
/// copied as stored; the web repaginates every layout against the real grid when it renders.
/// </summary>
public static class UsbMonitorPanelMigration
{
    public const int SchemaVersion = 29;

    private const string VirtualManufacturer = "NXS";
    private const string VirtualHardwareId = "NXS0001";

    internal static bool IsVirtualMonitorRecord(PanelDeviceRecord r) =>
        string.Equals(r.DisplayFingerprint?.Manufacturer, VirtualManufacturer, StringComparison.OrdinalIgnoreCase)
        || (r.DisplayId?.Contains(VirtualHardwareId, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Live settings below v29: records the pairs and copies the looks.</summary>
    public static bool Apply(NexusSettings doc)
    {
        var usb = doc.PanelDevices.Values
            .Where(r => r.SecondaryMonitor == true && r.Capabilities?.SupportsSecondaryMonitor == true)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        // One virtual monitor exists system-wide; the record to carry over is the most recently used one with a layout.
        var virtualRecord = doc.PanelDevices.Values
            .Where(r => IsVirtualMonitorRecord(r) && r.Layout is not null)
            .OrderByDescending(r => r.LastSeenAt)
            .FirstOrDefault();
        if (usb.Count == 0 || virtualRecord is null)
        {
            return false;
        }
        doc.UsbMonitorPanelPairs = usb.Select(u => new UsbMonitorPanelPair { UsbId = u.Id, VirtualId = virtualRecord.Id }).ToList();
        foreach (var record in usb)
        {
            CopyLook(virtualRecord, record);
        }
        return true;
    }

    /// <summary>A profile file or payload below v29: copies the looks for the pairs found in the live settings, then stamps it.</summary>
    public static bool ApplyProfile(NexusSettings? data, IReadOnlyList<UsbMonitorPanelPair>? pairs)
    {
        if (data is null || data.SchemaVersion >= SchemaVersion)
        {
            return false;
        }
        if (pairs is not null)
        {
            foreach (var pair in pairs)
            {
                if (data.PanelDevices.TryGetValue(pair.UsbId, out var to)
                    && data.PanelDevices.TryGetValue(pair.VirtualId, out var from)
                    && from.Layout is not null)
                {
                    CopyLook(from, to);
                }
            }
        }
        data.SchemaVersion = SchemaVersion;
        return true;
    }

    private static void CopyLook(PanelDeviceRecord from, PanelDeviceRecord to)
    {
        PanelProfileData.CopyLook(from, to);
        // A see-through backdrop means nothing on a USB display.
        to.Backdrop = null;
        if (to.Layout is not null && to.Capabilities?.Surface is { Length: > 0 } surface)
        {
            to.Layout.Surface = surface;
        }
    }
}

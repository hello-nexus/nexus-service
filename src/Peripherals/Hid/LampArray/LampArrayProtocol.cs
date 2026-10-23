using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hid.LampArray;

/// <summary>Report IDs of one LampArray collection, resolved from the device.</summary>
public readonly record struct LampArrayReportIds(byte Attributes, byte Request, byte Response, byte MultiUpdate, byte RangeUpdate, byte Control);

public readonly record struct LampArrayAttributes(int LampCount, uint WidthUm, uint HeightUm, uint DepthUm, uint Kind, uint MinUpdateIntervalUs)
{
    public static LampArrayAttributes Empty => default;
}

public readonly record struct LampAttributes(int LampId, int XUm, int YUm, int ZUm, uint Purposes, uint LatencyUs, byte RedLevels, byte GreenLevels, byte BlueLevels, byte IntensityLevels, bool Programmable, byte InputBinding);

/// <summary>
/// HID Lighting and Illumination page (0x59) LampArray reports. Field layouts follow the
/// HID Usage Tables definition and the OpenRGB HID LampArray driver; report IDs are device
/// defined and resolved from the collection's value caps, never assumed.
/// </summary>
public static class LampArrayProtocol
{
    public const int UsagePage = 0x59;
    public const int LampArrayUsage = 0x01;

    public const int UsageAttributesReport = 0x02;
    public const int UsageLampCount = 0x03;
    public const int UsageBoundingBoxWidth = 0x04;
    public const int UsageRequestReport = 0x20;
    public const int UsageLampId = 0x21;
    public const int UsageResponseReport = 0x22;
    public const int UsagePositionX = 0x23;
    public const int UsageMultiUpdateReport = 0x50;
    public const int UsageRedChannel = 0x51;
    public const int UsageUpdateFlags = 0x55;
    public const int UsageRangeUpdateReport = 0x60;
    public const int UsageLampIdStart = 0x61;
    public const int UsageControlReport = 0x70;
    public const int UsageAutonomousMode = 0x71;

    public const int MultiUpdateLamps = 8;
    public const byte UpdateComplete = 0x01;
    public const int MaxLamps = 256;

    public const int AttributesBytes = 1 + 2 + 4 * 5;
    public const int ResponseBytes = 1 + 2 + 4 * 5 + 6;
    public const int MultiUpdateBytes = 1 + 1 + 1 + 2 * MultiUpdateLamps + 4 * MultiUpdateLamps;
    public const int RangeUpdateBytes = 1 + 1 + 2 + 2 + 4;

    /// <summary>
    /// Resolves the six report IDs from the value caps: the link collection usage names the report
    /// (the same mapping the OpenRGB driver reads from the descriptor), with the report's own field
    /// usages as a fallback. Null with <paramref name="reason"/> when a report is missing, and when
    /// the multi-update fields are not the 8 x 16-bit ids plus 8 x 8-bit channels this encoder writes.
    /// </summary>
    public static LampArrayReportIds? ResolveReportIds(IReadOnlyList<HidValueCap> caps, out string reason)
    {
        var ids = new LampArrayReportIds(
            Find(caps, UsageAttributesReport, UsageBoundingBoxWidth),
            Find(caps, UsageRequestReport, -1),
            Find(caps, UsageResponseReport, UsagePositionX),
            Find(caps, UsageMultiUpdateReport, UsageRedChannel),
            Find(caps, UsageRangeUpdateReport, UsageLampIdStart),
            Find(caps, UsageControlReport, UsageAutonomousMode));
        if (ids.Attributes == 0 || ids.Request == 0 || ids.Response == 0 || ids.MultiUpdate == 0 || ids.RangeUpdate == 0 || ids.Control == 0)
        {
            reason = $"missing report ids {Describe(ids)}";
            return null;
        }
        if (!Has(caps, ids.MultiUpdate, UsageLampId, 16, MultiUpdateLamps)
            || !Has(caps, ids.MultiUpdate, UsageRedChannel, 8, MultiUpdateLamps)
            || !Has(caps, ids.MultiUpdate, UsageLampCount, 8, 1)
            || !Has(caps, ids.Attributes, UsageLampCount, 16, 1)
            || !Has(caps, ids.Control, UsageAutonomousMode, 8, 1))
        {
            reason = $"unexpected field layout {Describe(ids)}";
            return null;
        }
        reason = "";
        return ids;
    }

    public static string Describe(LampArrayReportIds ids) =>
        $"attr={ids.Attributes} req={ids.Request} resp={ids.Response} multi={ids.MultiUpdate} range={ids.RangeUpdate} ctrl={ids.Control}";

    private static byte Find(IReadOnlyList<HidValueCap> caps, int linkUsage, int fieldUsage)
    {
        foreach (var c in caps)
        {
            if (c.LinkUsagePage == UsagePage && c.LinkUsage == linkUsage && c.ReportId != 0) return (byte)c.ReportId;
        }
        if (fieldUsage < 0) return 0;
        foreach (var c in caps)
        {
            if (c.UsagePage == UsagePage && c.Usage == fieldUsage && c.ReportId != 0) return (byte)c.ReportId;
        }
        return 0;
    }

    private static bool Has(IReadOnlyList<HidValueCap> caps, int reportId, int usage, int bits, int count)
    {
        foreach (var c in caps)
        {
            if (c.ReportId == reportId && c.UsagePage == UsagePage && c.Usage == usage) return c.BitSize == bits && c.ReportCount == count;
        }
        return false;
    }

    public static byte[] BuildControl(LampArrayReportIds ids, bool autonomous) => new byte[] { ids.Control, autonomous ? (byte)1 : (byte)0 };

    public static byte[] BuildLampRequest(LampArrayReportIds ids, int lampId) => new byte[] { ids.Request, (byte)lampId, (byte)(lampId >> 8) };

    /// <summary>Up to <see cref="MultiUpdateLamps"/> lamps starting at <paramref name="first"/>; the final chunk of a frame carries UpdateComplete.</summary>
    public static byte[] BuildMultiUpdate(LampArrayReportIds ids, ReadOnlySpan<int> lampIds, ReadOnlySpan<RgbColor> colors, ReadOnlySpan<byte> intensity, bool complete)
    {
        var n = Math.Min(MultiUpdateLamps, lampIds.Length);
        var buf = new byte[MultiUpdateBytes];
        buf[0] = ids.MultiUpdate;
        buf[1] = (byte)n;
        buf[2] = complete ? UpdateComplete : (byte)0;
        for (var i = 0; i < n; i++)
        {
            buf[3 + i * 2] = (byte)lampIds[i];
            buf[4 + i * 2] = (byte)(lampIds[i] >> 8);
            var o = 3 + 2 * MultiUpdateLamps + i * 4;
            buf[o] = colors[i].R;
            buf[o + 1] = colors[i].G;
            buf[o + 2] = colors[i].B;
            buf[o + 3] = intensity[i];
        }
        return buf;
    }

    public static byte[] BuildRangeUpdate(LampArrayReportIds ids, int firstLamp, int lastLamp, RgbColor color, byte intensity)
    {
        var buf = new byte[RangeUpdateBytes];
        buf[0] = ids.RangeUpdate;
        buf[1] = UpdateComplete;
        buf[2] = (byte)firstLamp;
        buf[3] = (byte)(firstLamp >> 8);
        buf[4] = (byte)lastLamp;
        buf[5] = (byte)(lastLamp >> 8);
        buf[6] = color.R;
        buf[7] = color.G;
        buf[8] = color.B;
        buf[9] = intensity;
        return buf;
    }

    /// <summary>Parses a LampArrayAttributesReport (byte 0 is the report id). False when too short.</summary>
    public static bool TryParseAttributes(ReadOnlySpan<byte> report, out LampArrayAttributes attrs)
    {
        attrs = default;
        if (report.Length < AttributesBytes) return false;
        attrs = new LampArrayAttributes(U16(report, 1), U32(report, 3), U32(report, 7), U32(report, 11), U32(report, 15), U32(report, 19));
        return true;
    }

    /// <summary>Parses a LampAttributesResponseReport (byte 0 is the report id). False when too short.</summary>
    public static bool TryParseLampAttributes(ReadOnlySpan<byte> report, out LampAttributes lamp)
    {
        lamp = default;
        if (report.Length < ResponseBytes) return false;
        lamp = new LampAttributes(U16(report, 1), (int)U32(report, 3), (int)U32(report, 7), (int)U32(report, 11), U32(report, 15), U32(report, 19),
            report[23], report[24], report[25], report[26], report[27] != 0, report[28]);
        return true;
    }

    /// <summary>Per-lamp intensity channel: full for RGB lamps (and ignored by them when absent); the brightest channel for an intensity-only lamp.</summary>
    public static byte IntensityFor(LampAttributes lamp, RgbColor color) =>
        lamp.RedLevels == 0 && lamp.GreenLevels == 0 && lamp.BlueLevels == 0 && lamp.IntensityLevels > 0
            ? Math.Max(color.R, Math.Max(color.G, color.B))
            : (byte)255;

    /// <summary>Stock layout from lamp positions normalized to the lamps' own bounding box, or null (linear default) when the positions carry no spread.</summary>
    public static (float[] U, float[] V)? ComputeUv(IReadOnlyList<LampAttributes> lamps)
    {
        if (lamps.Count < 2) return null;
        long minX = long.MaxValue, maxX = long.MinValue, minY = long.MaxValue, maxY = long.MinValue;
        foreach (var l in lamps)
        {
            minX = Math.Min(minX, l.XUm);
            maxX = Math.Max(maxX, l.XUm);
            minY = Math.Min(minY, l.YUm);
            maxY = Math.Max(maxY, l.YUm);
        }
        if (maxX == minX && maxY == minY) return null;
        var u = new float[lamps.Count];
        var v = new float[lamps.Count];
        for (var i = 0; i < lamps.Count; i++)
        {
            u[i] = maxX == minX ? 0.5f : (float)(lamps[i].XUm - minX) / (maxX - minX);
            v[i] = maxY == minY ? 0.5f : (float)(lamps[i].YUm - minY) / (maxY - minY);
        }
        return (u, v);
    }

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static int U16(ReadOnlySpan<byte> b, int o) => b[o] | (b[o + 1] << 8);

    private static uint U32(ReadOnlySpan<byte> b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}

using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>Host readings the fan's built-in themes draw; units follow L-Connect's wireless sensor block.</summary>
public readonly record struct Slv3FlexLcdSensors(
    double CpuTemp, double CpuUsage, double GpuTemp, double GpuUsage,
    double CpuClockMhz, double GpuClockMhz, double DiskTemp, double DiskUsage,
    double MemoryTotalMb, double MemoryUsedMb, double CpuPower, double GpuPower,
    double CpuFanRpm, double GpuFanRpm, double PumpRpm, double CpuVoltage, double GpuVoltage,
    double UploadKbps, double DownloadKbps);

/// <summary>One fan's built-in theme choice.</summary>
public sealed record Slv3FlexLcdFan(int Theme, int Source, uint[]? Colors);

/// <summary>One TL / SL-INF LCD Flex chain's wireless-theme settings, as the hub drives them.</summary>
public sealed record Slv3FlexLcdChain(string Mac, bool Enabled, int Brightness, int Rotation, IReadOnlyList<Slv3FlexLcdFan> Fans);

/// <summary>Everything the hub needs for one drive tick.</summary>
public sealed record Slv3FlexLcdFrame(IReadOnlyDictionary<string, Slv3FlexLcdChain> Chains, Slv3FlexLcdSensors Sensors);

/// <summary>
/// The wireless-theme LCD of TL / SL-INF LCD Flex fans (L-Connect RFController *ByWiredLess): the
/// ClockSync body carries the host readings and a per-slot table of themes, and a sequenced
/// SendColors frame carries each fan's colors. Every layout here is a USBPcap of L-Connect 3.
/// </summary>
public static class Slv3FlexLcd
{
    public const int ThemeCount = 29;
    public const int DataSourceCount = 25;
    public const int FixedBlockLength = 50;
    public const int TableLength = 168;
    public const int LcdBlockLength = FixedBlockLength + TableLength;
    public const int SlotStride = 12;
    public const int MaxFans = 4;
    public const int ColorBytesPerFan = 19;
    public const int ColorBufferLength = MaxFans * ColorBytesPerFan;
    public const int ColorMarkerOffset = 18;

    /// <summary>fans_type values of the fans that carry a screen: SL-INF Flex 43/44/47/48, TL Flex 51/52/55/56.</summary>
    public static bool IsLcdFanType(int fansType) => fansType is 43 or 44 or 47 or 48 or 51 or 52 or 55 or 56;

    /// <summary>User-editable colors of a theme: 5 = two graph colors and three text colors, 4 = one graph color, 3 = text only, 0 = none.</summary>
    public static int ColorCount(int theme) => theme switch
    {
        0 or 1 or 2 or 4 or 5 or 7 or 11 or 12 => 5,
        3 or 6 or 8 or 9 or 10 => 4,
        >= 13 and <= 18 => 0,
        >= 19 and <= 28 => 3,
        _ => 5,
    };

    // L-Connect GetWiredlessThemeDefColors: 24-bit RGB, in the order of the array's length (4: graph, title, data, unit; 5: graph1, graph2, title, data, unit; 6: title, data, unit, then their shadows).
    private static readonly uint[][] Defaults =
    {
        new uint[] { 11088639, 16777215, 16777215, 16777215, 16777215 },
        new uint[] { 1260991, 6922239, 16777215, 16777215, 16777215 },
        new uint[] { 16744703, 11698431, 5905693, 5905693, 5905693 },
        new uint[] { 64507, 16777215, 16777215, 16777215 },
        new uint[] { 10092543, 16770093, 16777215, 16777215, 16777215 },
        new uint[] { 10299332, 3405311, 16766718, 14089727, 16766718 },
        new uint[] { 10038883, 16777215, 16777215, 16777215 },
        new uint[] { 65445, 35071, 16777215, 35071, 16777215 },
        new uint[] { 16711680, 16777215, 16777215, 16777215 },
        new uint[] { 255, 16777215, 16777215, 16777215 },
        new uint[] { 255, 16777215, 16777215, 16777215 },
        new uint[] { 16711680, 65280, 16777215, 16777215, 16777215 },
        new uint[] { 16711830, 16711680, 0, 0, 0 },
        new uint[1], new uint[1], new uint[1], new uint[1], new uint[1], new uint[1],
        new uint[] { 16777215, 16777215, 16777215, 0, 0, 0 },
        new uint[] { 3355443, 3355443, 3355443, 16777215, 16777215, 16777215 },
        new uint[] { 16777215, 16777215, 16777215, 2495249, 2495249, 2495249 },
        new uint[] { 16777215, 16777215, 16777215, 16777215, 16777215, 16777215 },
        new uint[] { 3158064, 3158064, 3158064, 16512752, 16512752, 16512752 },
        new uint[] { 3158064, 3158064, 3158064, 16644338, 16644338, 16644338 },
        new uint[] { 16777215, 16777215, 16777215, 16777215, 16777215, 16777215 },
        new uint[] { 16777215, 16777215, 16777215, 16777215, 16777215, 16777215 },
        new uint[] { 16777215, 16777215, 16777215, 0, 0, 0 },
        new uint[] { 16777215, 16777215, 16777215, 0, 0, 0 },
    };

    /// <summary>The theme's default user colors, <see cref="ColorCount"/> long.</summary>
    public static uint[] DefaultColors(int theme)
    {
        var count = ColorCount(theme);
        var result = new uint[count];
        if (theme < 0 || theme >= Defaults.Length || count == 0)
        {
            return result;
        }
        var d = Defaults[theme];
        switch (count)
        {
            case 5 when d.Length == 5:
                Array.Copy(d, result, 5);
                break;
            case 4 when d.Length == 4:
                Array.Copy(d, result, 4);
                break;
            case 3 when d.Length == 6:
                Array.Copy(d, result, 3);
                break;
        }
        return result;
    }

    private static uint ShadowDefault(int theme, int index) =>
        theme >= 0 && theme < Defaults.Length && Defaults[theme].Length == 6 ? Defaults[theme][3 + index] : 0;

    /// <summary>
    /// A fan's 19-byte color slot at its cache index inside the 76-byte SendColors buffer: six RGB triples in
    /// L-Connect's order, then the change marker at byte 18.
    /// </summary>
    public static byte[] BuildColorBuffer(int cacheIndex, int theme, uint[]? user, byte marker)
    {
        var buffer = new byte[ColorBufferLength];
        if (cacheIndex < 0 || cacheIndex >= MaxFans)
        {
            return buffer;
        }
        var count = ColorCount(theme);
        var colors = user is { } u && u.Length == count ? u : DefaultColors(theme);
        var list = new List<uint>(6);
        switch (count)
        {
            case 5:
                list.AddRange(new[] { colors[0], colors[1], colors[2], colors[3], colors[4], colors[4] });
                break;
            case 4:
                list.AddRange(new[] { colors[0], colors[1], colors[2], colors[3], 0u, 0u });
                break;
            case 3:
                list.AddRange(new[] { colors[0], colors[1], colors[2], ShadowDefault(theme, 0), ShadowDefault(theme, 1), ShadowDefault(theme, 2) });
                break;
        }
        var at = cacheIndex * ColorBytesPerFan;
        foreach (var c in list)
        {
            buffer[at++] = (byte)(c >> 16);
            buffer[at++] = (byte)(c >> 8);
            buffer[at++] = (byte)c;
        }
        buffer[(cacheIndex * ColorBytesPerFan) + ColorMarkerOffset] = marker;
        return buffer;
    }

    /// <summary>L-Connect toWirelessFanDirection: the stored rotation 0..3 as the byte the table carries.</summary>
    public static byte Direction(int rotation) => (byte)(((4 - Math.Clamp(rotation, 0, 3)) % 4) + 1);

    /// <summary>
    /// A fan's index inside the chain's table: SL-INF Flex counts from the left unless right-attached, every
    /// other LCD family counts from the right.
    /// </summary>
    public static int CacheIndex(bool isSlInfFlex, bool rightAttach, int fanCount, int serviceIndex)
    {
        var count = Math.Min(MaxFans, Math.Max(0, fanCount));
        if (serviceIndex < 0 || serviceIndex >= count)
        {
            return -1;
        }
        var reverse = rightAttach || !isSlInfFlex;
        return reverse ? count - serviceIndex - 1 : serviceIndex;
    }

    /// <summary>One slot of the table: themes and sources by cache index, the direction byte and the brightness.</summary>
    public readonly record struct Row(int RxType, byte[] Themes, byte[] Sources, byte Direction, byte Brightness, int FanCount = MaxFans);

    /// <summary>L-Connect GetFixedData: the 50-byte host reading block with the wall clock at [32..38].</summary>
    public static byte[] BuildFixedBlock(Slv3FlexLcdSensors s, DateTime now)
    {
        var a = new byte[FixedBlockLength];
        static byte B(double v) => (byte)Math.Max(0, Math.Min(255, (int)v));
        a[0] = B(s.CpuTemp);
        a[1] = B(s.CpuUsage);
        a[2] = B(s.GpuTemp);
        a[3] = B(s.GpuUsage);
        Put16(a, 4, (int)s.CpuClockMhz);
        Put16(a, 6, (int)s.GpuClockMhz);
        a[8] = B(s.DiskTemp);
        a[9] = B(s.DiskUsage);
        Put16(a, 10, (int)s.MemoryTotalMb);
        Put16(a, 12, (int)s.MemoryUsedMb);
        Put16(a, 14, (int)s.CpuPower * 10);
        Put16(a, 16, (int)s.GpuPower * 10);
        Put16(a, 18, (int)s.CpuFanRpm);
        Put16(a, 20, (int)s.GpuFanRpm);
        Put16(a, 22, (int)s.PumpRpm);
        a[24] = B(s.CpuVoltage * 100.0);
        a[25] = B(s.GpuVoltage * 100.0);
        Put24(a, 26, (int)s.UploadKbps);
        Put24(a, 29, (int)s.DownloadKbps);
        Put16(a, 32, now.Year);
        a[34] = (byte)now.Month;
        a[35] = (byte)now.Day;
        a[36] = (byte)now.Hour;
        a[37] = (byte)now.Minute;
        a[38] = (byte)now.Second;
        return a;
    }

    /// <summary>The 218 bytes the ClockSync body carries after the header: the fixed block, then the 14-slot table.</summary>
    public static byte[] BuildLcdBlock(IReadOnlyList<Row> rows, Slv3FlexLcdSensors sensors, DateTime now)
    {
        var block = new byte[LcdBlockLength];
        BuildFixedBlock(sensors, now).CopyTo(block, 0);
        foreach (var row in rows)
        {
            if (row.RxType < Slv3Protocol.MinSlot || row.RxType > 14)
            {
                continue;
            }
            var at = FixedBlockLength + ((row.RxType - 1) * SlotStride);
            for (var i = 0; i < Math.Min(MaxFans, row.FanCount); i++)
            {
                block[at + i] = row.Themes[i];
                block[at + 4 + i] = (byte)((row.Direction << 5) | (row.Sources[i] & 0x1F));
            }
            block[at + 8] = row.Brightness;
        }
        return block;
    }

    private static void Put16(byte[] a, int at, int v)
    {
        a[at] = (byte)(v >> 8);
        a[at + 1] = (byte)v;
    }

    private static void Put24(byte[] a, int at, int v)
    {
        a[at] = (byte)(v >> 16);
        a[at + 1] = (byte)(v >> 8);
        a[at + 2] = (byte)v;
    }
}

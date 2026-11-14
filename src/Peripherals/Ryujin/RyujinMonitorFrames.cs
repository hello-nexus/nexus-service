using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>The latest readings a screen slot can show; null means not available.</summary>
internal readonly record struct RyujinReadings(float? CpuC, float? GpuC, float? LiquidC, int? PumpRpm, int? FanRpm);

/// <summary>The sensors a screen slot can show, each with the name and unit glyph Armoury Crate sent for it.</summary>
internal static class RyujinMonitorSensors
{
    public const string CpuTemp = "cpu-temp";
    public const string GpuTemp = "gpu-temp";
    public const string LiquidTemp = "liquid-temp";
    public const string PumpRpm = "pump-rpm";
    public const string FanRpm = "fan-rpm";

    public const int MinCount = 2;
    public const int MaxCount = 3;

    public static readonly string[] Available = { CpuTemp, GpuTemp, LiquidTemp, PumpRpm, FanRpm };
    public static readonly string[] DefaultSlots = { CpuTemp, GpuTemp, PumpRpm };

    public static bool IsValid(string? id) => id is not null && Array.IndexOf(Available, id) >= 0;

    public static string Name(string id) => id switch
    {
        CpuTemp => "CPU",
        GpuTemp => "GPU",
        LiquidTemp => "Liquid",
        PumpRpm => "AIO Pump",
        FanRpm => "AIO Fan",
        _ => id,
    };

    /// <summary>Always three valid ids: a stored slot that is missing or unknown falls back to its default.</summary>
    public static string[] NormalizeSlots(IReadOnlyList<string>? stored)
    {
        var slots = new string[MaxCount];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = stored is not null && i < stored.Count && IsValid(stored[i]) ? stored[i] : DefaultSlots[i];
        }
        return slots;
    }

    public static int NormalizeCount(int stored) => stored is MinCount or MaxCount ? stored : MaxCount;
}

internal static class RyujinMonitorFrames
{
    /// <summary>Slots 0 to sensorCount - 1, each from its chosen source. A slot whose reading is missing is skipped, so the screen keeps what it last showed rather than a made-up value.</summary>
    public static IReadOnlyList<byte[]> Build(int sensorCount, IReadOnlyList<string> slots, RyujinReadings readings)
    {
        int count = RyujinMonitorSensors.NormalizeCount(sensorCount);
        var chosen = RyujinMonitorSensors.NormalizeSlots(slots);
        var frames = new List<byte[]>(count);
        for (int slot = 0; slot < count; slot++)
        {
            string id = chosen[slot];
            string? value = Format(id, readings);
            if (value is not null)
            {
                frames.Add(RyujinMonitorProtocol.EncodeSensorText(slot, RyujinMonitorSensors.Name(id), value));
            }
        }
        return frames;
    }

    private static string? Format(string id, RyujinReadings r) => id switch
    {
        RyujinMonitorSensors.CpuTemp => Temp(r.CpuC),
        RyujinMonitorSensors.GpuTemp => Temp(r.GpuC),
        RyujinMonitorSensors.LiquidTemp => Temp(r.LiquidC),
        RyujinMonitorSensors.PumpRpm => Rpm(r.PumpRpm),
        RyujinMonitorSensors.FanRpm => Rpm(r.FanRpm),
        _ => null,
    };

    private static string? Temp(float? c) => c is { } v && float.IsFinite(v) ? RyujinMonitorProtocol.FormatTemperature(v) : null;

    private static string? Rpm(int? rpm) => rpm is { } v && v >= 0 ? RyujinMonitorProtocol.FormatRpm(v) : null;
}

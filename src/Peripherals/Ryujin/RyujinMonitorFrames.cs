using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>The three sensor slots Armoury Crate fills by default: CPU and GPU temperature, and the pump speed.</summary>
internal static class RyujinMonitorFrames
{
    /// <summary>A slot whose reading is missing is skipped, so the screen keeps what it last showed rather than a made-up value.</summary>
    public static IReadOnlyList<byte[]> Build(float? cpuC, float? gpuC, int? pumpRpm)
    {
        var frames = new List<byte[]>(3);
        if (cpuC is { } cpu && float.IsFinite(cpu))
        {
            frames.Add(RyujinMonitorProtocol.EncodeSensorText(
                RyujinMonitorProtocol.CpuSlot, RyujinMonitorProtocol.CpuName, RyujinMonitorProtocol.FormatTemperature(cpu)));
        }
        if (gpuC is { } gpu && float.IsFinite(gpu))
        {
            frames.Add(RyujinMonitorProtocol.EncodeSensorText(
                RyujinMonitorProtocol.GpuSlot, RyujinMonitorProtocol.GpuName, RyujinMonitorProtocol.FormatTemperature(gpu)));
        }
        if (pumpRpm is { } rpm && rpm >= 0)
        {
            frames.Add(RyujinMonitorProtocol.EncodeSensorText(
                RyujinMonitorProtocol.PumpSlot, RyujinMonitorProtocol.PumpName, RyujinMonitorProtocol.FormatRpm(rpm)));
        }
        return frames;
    }
}

using System;

namespace Nexus.Service.Panel.Streams;

/// <summary>How a device record's frame-rate cap limits a model rate.</summary>
public static class StreamFpsCap
{
    /// <summary>Null cap leaves the model rate alone; a cap above it never raises it.</summary>
    public static int Effective(int modelFps, int? cap) => cap is { } c ? Math.Min(c, modelFps) : modelFps;
}

using System;

namespace Nexus.Service.Panel.Streams;

/// <summary>The global streamed-panel frame-rate cap: the allowed values and how one caps a model rate.</summary>
public static class StreamFpsCap
{
    public static readonly int[] Allowed = { 30, 20, 15, 10, 5, 1 };

    public static bool IsValid(int? cap) => cap is null || Array.IndexOf(Allowed, cap.Value) >= 0;

    /// <summary>Null cap leaves the model rate alone; a cap above it never raises it.</summary>
    public static int Effective(int modelFps, int? cap) => cap is { } c ? Math.Min(c, modelFps) : modelFps;
}

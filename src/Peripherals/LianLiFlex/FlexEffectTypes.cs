using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiFlex;

public enum FlexFamily
{
    TlFlex,
    SlInfFlex,
}

/// <summary>Which LEDs of a fan an effect fills. Whole fan, or one zone of it.</summary>
public enum FlexScope
{
    All,
    /// <summary>TL first half (LED 0..12 of each 26) and SL-INF inner ring.</summary>
    Up,
    /// <summary>TL second half (LED 13..25 of each 26).</summary>
    Down,
    Inner,
    Center,
    Outer,
}

/// <summary>Inputs of one effect render. Brightness is the 0..255 level applied inside the generators that bake it.</summary>
public sealed record FlexEffectRequest(
    FlexScope Scope, int Fans, int Direction, int BrightnessLevel, IReadOnlyList<RgbColor> Colors);

/// <summary>
/// One rendered animation: <see cref="Frames"/> holds FrameCount * Leds * 3 bytes, frame-major,
/// R,G,B per LED. <see cref="IntervalBaseMs"/> is the per-frame base interval that the speed
/// multiplier scales.
/// </summary>
public sealed class FlexRawAnimation
{
    public required byte[] Frames { get; init; }
    public required int FrameCount { get; init; }
    public required int Leds { get; init; }
    public required double IntervalBaseMs { get; init; }
}

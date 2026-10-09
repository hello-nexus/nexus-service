using System;

namespace Nexus.Service.Peripherals.StreamDeck;

public enum StreamDeckInputKind { Keys, DialPress, DialRotate, Touch }

public enum StreamDeckTouchKind { Tap, Long, Flick }

/// <summary>
/// One decoded input report. Keys carries full key-state snapshots (plus the
/// Neo touch-key snapshot); dial presses carry every dial's down state; dial
/// rotations carry signed ticks per dial (positive is clockwise); touches
/// carry strip coordinates, with a flick's end point in X2/Y2.
/// </summary>
public sealed class StreamDeckInput
{
    public StreamDeckInputKind Kind { get; private init; }
    public bool[] Keys { get; private init; } = Array.Empty<bool>();
    public bool[] TouchKeys { get; private init; } = Array.Empty<bool>();
    public bool[] DialDown { get; private init; } = Array.Empty<bool>();
    public int[] DialTicks { get; private init; } = Array.Empty<int>();
    public StreamDeckTouchKind TouchKind { get; private init; }
    public int X { get; private init; }
    public int Y { get; private init; }
    public int X2 { get; private init; }
    public int Y2 { get; private init; }

    public static StreamDeckInput ForKeys(bool[] keys, bool[] touchKeys) =>
        new() { Kind = StreamDeckInputKind.Keys, Keys = keys, TouchKeys = touchKeys };

    public static StreamDeckInput ForDialPress(bool[] down) =>
        new() { Kind = StreamDeckInputKind.DialPress, DialDown = down };

    public static StreamDeckInput ForDialRotate(int[] ticks) =>
        new() { Kind = StreamDeckInputKind.DialRotate, DialTicks = ticks };

    public static StreamDeckInput ForTouch(StreamDeckTouchKind kind, int x, int y, int x2, int y2) =>
        new() { Kind = StreamDeckInputKind.Touch, TouchKind = kind, X = x, Y = y, X2 = x2, Y2 = y2 };
}

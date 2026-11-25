using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// When each frame of a GIF or video clip goes to a fan screen. A frame is due at the sum of the
/// delays before it, counted from the clip's start, so a slow push does not stretch the clip.
/// After a push the next frame is the one the clip is showing at the earliest moment the screen
/// may take another, so a screen slower than the clip skips frames instead of slowing it down.
/// </summary>
internal static class FanLcdClipTiming
{
    /// <summary>The frame-rate cap of a screen the user has not set one for.</summary>
    public const int DefaultFps = 30;

    /// <summary>A frame with no usable delay shows for at least this long.</summary>
    public const int MinDelayMs = 10;

    /// <summary>
    /// The cap a screen's stored value means, in fps: unset is <see cref="DefaultFps"/>, and 0 is
    /// Off, no cap, so the clip plays at its own rate.
    /// </summary>
    public static int CapOf(int? stored) => stored ?? DefaultFps;

    /// <summary>The value to store for a requested cap: 0 stays Off, anything else is clamped like a panel's cap.</summary>
    public static int ToStored(int requested) => Math.Min(requested, Nexus.Service.Panel.PanelDeviceRegistry.MaxFpsCap);

    /// <summary>The least ms between two pushes under a cap; none when the cap is Off.</summary>
    public static double MinGapMs(int capFps) => capFps <= 0 ? 0 : 1000.0 / capFps;

    /// <summary>Each frame's start in ms from the clip's start, then the clip's length as the last entry.</summary>
    public static double[] Starts(IReadOnlyList<int> delaysMs)
    {
        var starts = new double[delaysMs.Count + 1];
        for (var i = 0; i < delaysMs.Count; i++)
        {
            starts[i + 1] = starts[i] + Math.Max(MinDelayMs, delaysMs[i]);
        }
        return starts;
    }

    /// <summary>
    /// The frame to push after <paramref name="current"/> and when (ms from the clip's start), no
    /// sooner than <paramref name="earliestMs"/>. A next frame equal to the frame count means the
    /// clip ends at the returned time.
    /// </summary>
    public static (int Next, double DueMs) Next(double[] starts, int current, double earliestMs)
    {
        var frames = starts.Length - 1;
        var next = current + 1;
        if (next >= frames)
        {
            return (frames, Math.Max(starts[frames], earliestMs));
        }
        var due = Math.Max(starts[next], earliestMs);
        if (starts[frames] <= due)
        {
            return (frames, due);
        }
        while (next + 1 < frames && starts[next + 1] <= due)
        {
            next++;
        }
        return (next, due);
    }
}

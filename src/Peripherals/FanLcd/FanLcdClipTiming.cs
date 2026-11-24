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
    /// <summary>The TL-LCD Wireless shows 60 fps.</summary>
    public const int TlWirelessMaxFps = 60;

    /// <summary>Every other fan screen.</summary>
    public const int DefaultMaxFps = 30;

    /// <summary>A frame with no usable delay shows for at least this long.</summary>
    public const int MinDelayMs = 10;

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

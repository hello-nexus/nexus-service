using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// When each frame of a GIF or video clip goes to a fan screen. Every frame is due at the sum of
/// the delays before it, from the clip's start; after a push the next frame is the one still on
/// screen at the earliest moment the screen may take another, so a slow push, or a screen slower
/// than the clip, skips frames and keeps the clip's speed instead of stretching it.
/// </summary>
internal static class FanLcdClipTiming
{
    /// <summary>A frame with no usable delay shows for at least this long.</summary>
    public const int MinDelayMs = 10;

    /// <summary>Each frame's start in ms from the clip's start, plus the clip's length as the last entry.</summary>
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
    /// clip is over at the returned time.
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
        // Frames that are already over by then are skipped.
        while (next + 1 < frames && starts[next + 1] <= due)
        {
            next++;
        }
        if (starts[frames] <= due)
        {
            return (frames, due);
        }
        return (next, due);
    }
}

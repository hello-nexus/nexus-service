using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.ScheduleFollow;

/// <summary>Device keys the brightness schedule is followed under. A display uses its own stable display id.</summary>
public static class ScheduleFollowKeys
{
    public const string Y70 = "y70";
    public const string CorsairLcd = "corsair-lcd";
    public const string KrakenLcd = "nzxt-kraken-lcd";
    /// <summary>Followed by "panel-lcd:" plus the panel device id of a streamed-panel LCD.</summary>
    public const string PanelLcdPrefix = "panel-lcd:";
    /// <summary>Followed by "slv3-aio:" plus the upper-case hex MAC of a HydroShift II screen.</summary>
    public const string Slv3AioPrefix = "slv3-aio:";
    /// <summary>The Xeneon Edge is a display: its key is its display id, like any monitor.</summary>
}

/// <summary>Pure follow math, kept apart from the state so it tests without a store.</summary>
public static class ScheduleFollowMath
{
    /// <summary>Per-second change of the multiplier for fast surfaces (Y70, LCDs).</summary>
    public const double FastRatePerSecond = 0.05;

    /// <summary>Per-second change for DDC monitors, slower so a write lands on a small step.</summary>
    public const double SlowRatePerSecond = 0.02;

    /// <summary>The slider is the ceiling: the level it shows is the slider times the multiplier.</summary>
    public static int Scale(int slider, double multiplier) =>
        (int)Math.Round(Math.Clamp(slider, 0, 100) * Math.Clamp(multiplier, 0.0, 1.0), MidpointRounding.AwayFromZero);

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
    public static double Ramp(double current, double target, double maxDelta)
    {
        var diff = target - current;
        if (Math.Abs(diff) <= maxDelta)
        {
            return target;
        }
        return current + Math.Sign(diff) * maxDelta;
    }

    /// <summary>The multiplier a device aims at: the schedule level while the master switch is on and the device follows, else 1 (the plain slider).</summary>
    public static double Desired(bool scheduleEnabled, bool follows, double level) =>
        scheduleEnabled && follows ? Math.Clamp(level, 0.0, 1.0) : 1.0;
}

/// <summary>
/// The ramped schedule multiplier of each followed device. Writers read
/// <see cref="Scale"/> to turn the user's slider into the level to apply; the
/// stored slider is never touched. <see cref="Advance"/> is driven by one worker tick.
/// </summary>
public sealed class ScheduleFollowState
{
    private sealed class Slot
    {
        public double Multiplier = 1.0;
        public double Rate;
        public bool Primed;
    }

    private readonly IConfigStore _store;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    public ScheduleFollowState(IConfigStore store, Func<DateTime>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTime.Now);
        Track(ScheduleFollowKeys.Y70, ScheduleFollowMath.FastRatePerSecond);
        Track(ScheduleFollowKeys.CorsairLcd, ScheduleFollowMath.FastRatePerSecond);
        Track(ScheduleFollowKeys.KrakenLcd, ScheduleFollowMath.FastRatePerSecond);
    }

    private int _advances;

    /// <summary>Registers a device key; a repeat call keeps the existing slot. A key first seen at startup (or with
    /// <paramref name="snap"/>) takes the current level at once, since its hardware may already wear it; one that
    /// appears later starts from the plain slider and ramps.</summary>
    public void Track(string key, double ratePerSecond, bool snap = false)
    {
        if (!_slots.ContainsKey(key))
        {
            _slots.TryAdd(key, new Slot { Rate = ratePerSecond, Primed = !snap && Volatile.Read(ref _advances) > 1 });
        }
    }

    public IReadOnlyCollection<string> TrackedKeys => (IReadOnlyCollection<string>)_slots.Keys;

    public bool Follows(NexusSettings settings, string key) =>
        !settings.Devices.ScheduleUnfollowed.Contains(key);

    /// <summary>Current multiplier; 1 for a key never tracked.</summary>
    public double Multiplier(string key) => _slots.TryGetValue(key, out var slot) ? Volatile.Read(ref slot.Multiplier) : 1.0;

    /// <summary>The level to apply for <paramref name="slider"/> right now.</summary>
    public int Scale(string key, int slider) => ScheduleFollowMath.Scale(slider, Multiplier(key));

    /// <summary>The target multiplier right now, read from one settings snapshot.</summary>
    public double Target(NexusSettings settings, string key)
    {
        var schedule = settings.Lighting.BrightnessSchedule;
        var enabled = schedule is { Enabled: true };
        return ScheduleFollowMath.Desired(enabled, Follows(settings, key), enabled ? Lighting.MasterBrightness.ScheduleLevel(schedule, _clock()) : 1f);
    }

    /// <summary>Steps every tracked key toward its target by the elapsed time; the first step of a key snaps, so a restart does not replay a ramp.
    /// Returns the keys whose multiplier changed.</summary>
    public List<string> Advance(double elapsedSeconds)
    {
        var changed = new List<string>();
        Interlocked.Increment(ref _advances);
        var settings = _store.Load();
        foreach (var (key, slot) in _slots)
        {
            var target = Target(settings, key);
            var next = slot.Primed
                ? ScheduleFollowMath.Ramp(slot.Multiplier, target, slot.Rate * Math.Max(elapsedSeconds, 0.0))
                : target;
            slot.Primed = true;
            if (next != slot.Multiplier)
            {
                Volatile.Write(ref slot.Multiplier, next);
                changed.Add(key);
            }
        }
        return changed;
    }
}

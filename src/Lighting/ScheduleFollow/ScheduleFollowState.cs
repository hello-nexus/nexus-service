using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    /// <summary>True for a key that names a display (a monitor or the Xeneon Edge) rather than a fixed device or a prefixed one.</summary>
    public static bool IsDisplayId(string key) =>
        key.Length > 0
        && key is not (Y70 or CorsairLcd or KrakenLcd)
        && !key.StartsWith(PanelLcdPrefix, StringComparison.Ordinal)
        && !key.StartsWith(Slv3AioPrefix, StringComparison.Ordinal);
}

/// <summary>Pure follow math, kept apart from the state so it tests without a store.</summary>
public static class ScheduleFollowMath
{
    /// <summary>The schedule is a cap, like the lighting master brightness: the level shown is the slider limited to
    /// the cap, so a slider above the cap changes nothing and one below it applies as set.</summary>
    public static int Scale(int slider, double cap) =>
        Math.Min(Math.Clamp(slider, 0, 100), (int)Math.Round(Math.Clamp(cap, 0.0, 1.0) * 100, MidpointRounding.AwayFromZero));

    /// <summary>The cap a device wears: the schedule level for now while the master switch is on and the device follows, else 1 (the plain slider).</summary>
    public static double Desired(bool scheduleEnabled, bool follows, double level) =>
        scheduleEnabled && follows ? Math.Clamp(level, 0.0, 1.0) : 1.0;
}

/// <summary>
/// The schedule cap (0..1) of each followed device, read live from the settings and the clock, so a settings
/// change shows in the next read with no tick between. Writers use <see cref="Scale"/> to limit the user's slider to
/// the cap; the stored slider is never touched.
/// </summary>
public sealed class ScheduleFollowState
{
    private readonly IConfigStore _store;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    public ScheduleFollowState(IConfigStore store, Func<DateTime>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTime.Now);
        Track(ScheduleFollowKeys.Y70);
        Track(ScheduleFollowKeys.CorsairLcd);
        Track(ScheduleFollowKeys.KrakenLcd);
    }

    /// <summary>Registers a device key so the devices list reports it; a repeat call is a no-op.</summary>
    public void Track(string key) => _keys.TryAdd(key, 0);

    private readonly ConcurrentDictionary<string, byte> _restoreOnSight = new(StringComparer.Ordinal);

    /// <summary>Keys of devices that left while off their slider; each needs one write when it is seen again, so a scan must look for it.</summary>
    public IReadOnlyCollection<string> RestoreOnSight => (IReadOnlyCollection<string>)_restoreOnSight.Keys;

    public bool NeedsRestore(string key) => _restoreOnSight.ContainsKey(key);
    public void RequireRestore(string key) => _restoreOnSight.TryAdd(key, 0);
    public void RestoreDone(string key) => _restoreOnSight.TryRemove(key, out _);

    public IReadOnlyCollection<string> TrackedKeys => (IReadOnlyCollection<string>)_keys.Keys;

    public bool Follows(NexusSettings settings, string key) =>
        settings.Devices.ScheduleFollowing.Contains(key);

    /// <summary>The cap right now from one settings snapshot.</summary>
    public double Target(NexusSettings settings, string key)
    {
        var schedule = settings.Lighting.BrightnessSchedule;
        var enabled = schedule is { Enabled: true };
        return ScheduleFollowMath.Desired(enabled, Follows(settings, key), enabled ? Lighting.MasterBrightness.ScheduleLevel(schedule, _clock()) : 1f);
    }

    /// <summary>Current cap, 0..1; 1 while the schedule is off or the device does not follow.</summary>
    public double Multiplier(string key) => Target(_store.Load(), key);

    /// <summary>The level to apply for <paramref name="slider"/> right now.</summary>
    public int Scale(string key, int slider) => ScheduleFollowMath.Scale(slider, Multiplier(key));
}

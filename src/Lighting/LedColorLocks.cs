using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>One LED held on a colour.</summary>
public readonly record struct LockedLed(int Index, byte R, byte G, byte B);

/// <summary>
/// Per-LED colour locks, keyed by lighting-device id. Blackout and the wake ramp
/// still apply on top. The engine reads <see cref="TryGet"/> every tick, so the
/// map is swapped whole on change and read without a lock.
/// </summary>
public sealed class LedColorLockTracker
{
    private readonly object _lock = new();
    private readonly IConfigStore? _store;
    private Dictionary<string, LockedLed[]> _locks = new(StringComparer.Ordinal);

    public LedColorLockTracker(IConfigStore? store = null)
    {
        _store = store;
        if (store is null) return;
        Hydrate();
        // A profile switch replaces LightingSettings whole, locks included.
        store.OnChanged += Hydrate;
    }

    // Reads under _lock so it cannot interleave with Set's persist. Lock order
    // is always _lock then the store's; the store raises OnChanged after
    // releasing its own.
    private void Hydrate()
    {
        var store = _store;
        if (store is null) return;
        lock (_lock)
        {
            var rebuilt = new Dictionary<string, LockedLed[]>(StringComparer.Ordinal);
            foreach (var (id, leds) in store.Load().Lighting.LedColorLocks)
            {
                var list = Build(leds);
                if (list.Length > 0) rebuilt[id] = list;
            }
            if (SameAs(rebuilt)) return;
            Volatile.Write(ref _locks, rebuilt);
        }
    }

    private bool SameAs(Dictionary<string, LockedLed[]> other)
    {
        var mine = _locks;
        if (mine.Count != other.Count) return false;
        foreach (var (id, leds) in other)
        {
            if (!mine.TryGetValue(id, out var current)) return false;
            if (!current.AsSpan().SequenceEqual(leds)) return false;
        }
        return true;
    }

    private static LockedLed[] Build(Dictionary<int, string>? leds)
    {
        if (leds is null || leds.Count == 0) return Array.Empty<LockedLed>();
        var list = new List<LockedLed>(leds.Count);
        foreach (var (index, hex) in leds)
        {
            if (index >= 0 && StaticColorHex.TryParse(hex, out var r, out var g, out var b))
                list.Add(new LockedLed(index, r, g, b));
        }
        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list.ToArray();
    }

    /// <summary>The device's locked LEDs, ordered by index.</summary>
    public bool TryGet(string id, out LockedLed[] leds)
    {
        if (!string.IsNullOrEmpty(id) && Volatile.Read(ref _locks).TryGetValue(id, out var found))
        {
            leds = found;
            return true;
        }
        leds = Array.Empty<LockedLed>();
        return false;
    }

    /// <summary>Locks <paramref name="indices"/> to <paramref name="color"/>
    /// ("#rrggbb"), or unlocks them when the colour is empty. False when the
    /// colour is neither.</summary>
    public bool Set(string id, IReadOnlyCollection<int> indices, string? color)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var clear = string.IsNullOrEmpty(color);
        if (!clear && !StaticColorHex.TryParse(color, out _, out _, out _)) return false;
        var hex = clear ? "" : Normalize(color!);
        var leds = new Dictionary<int, string>();
        lock (_lock)
        {
            var next = new Dictionary<string, LockedLed[]>(_locks, StringComparer.Ordinal);
            if (next.TryGetValue(id, out var current))
            {
                foreach (var led in current) leds[led.Index] = ToHex(led);
            }
            foreach (var index in indices)
            {
                if (index < 0) continue;
                if (clear) leds.Remove(index);
                else leds[index] = hex;
            }
            var built = Build(leds);
            if (built.Length > 0) next[id] = built;
            else next.Remove(id);
            Volatile.Write(ref _locks, next);
            // Under the lock, so concurrent Sets persist in the order they swapped.
            _store?.Update(s =>
            {
                if (leds.Count > 0) s.Lighting.LedColorLocks[id] = leds;
                else s.Lighting.LedColorLocks.Remove(id);
            });
        }
        return true;
    }

    /// <summary>Unlocks every LED on the device.</summary>
    public void ClearDevice(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock)
        {
            if (!_locks.ContainsKey(id)) return;
            var next = new Dictionary<string, LockedLed[]>(_locks, StringComparer.Ordinal);
            next.Remove(id);
            Volatile.Write(ref _locks, next);
            _store?.Update(s => s.Lighting.LedColorLocks.Remove(id));
        }
    }

    private static string Normalize(string color) => color[0] == '#' ? color.ToLowerInvariant() : "#" + color.ToLowerInvariant();

    private static string ToHex(LockedLed led) => $"#{led.R:x2}{led.G:x2}{led.B:x2}";
}

/// <summary>
/// Locks as the card's one-row strip shows them: the frame split into columns
/// by LED x position, each wearing the lock colour most of its LEDs hold when
/// at least half of them are locked, else "" so the look shows through.
/// </summary>
public static class LedColorStrip
{
    public const int Columns = 24;

    /// <summary><paramref name="u"/> is each LED's x position (0-1); null means index order along a line.</summary>
    public static List<string>? Build(LockedLed[] locks, int ledCount, float[]? u, bool[]? disabled)
    {
        if (locks.Length == 0 || ledCount <= 0) return null;
        var colorOf = new Dictionary<int, uint>(locks.Length);
        foreach (var led in locks) colorOf[led.Index] = (uint)(led.R << 16 | led.G << 8 | led.B);
        var total = new int[Columns];
        var counts = new Dictionary<uint, int>?[Columns];
        for (var i = 0; i < ledCount; i++)
        {
            if (disabled is not null && i < disabled.Length && disabled[i]) continue;
            var x = u is not null && i < u.Length ? u[i] : ledCount > 1 ? i / (ledCount - 1f) : 0.5f;
            var col = Math.Clamp((int)(x * Columns), 0, Columns - 1);
            total[col]++;
            if (!colorOf.TryGetValue(i, out var rgb)) continue;
            var bin = counts[col] ??= new Dictionary<uint, int>();
            bin[rgb] = bin.GetValueOrDefault(rgb) + 1;
        }
        List<string>? strip = null;
        for (var col = 0; col < Columns; col++)
        {
            var bin = counts[col];
            if (bin is null) continue;
            var locked = 0;
            uint top = 0;
            var topCount = 0;
            foreach (var (rgb, n) in bin)
            {
                locked += n;
                if (n > topCount) { top = rgb; topCount = n; }
            }
            if (locked * 2 < total[col]) continue;
            strip ??= new List<string>(new string[Columns].Select(_ => ""));
            strip[col] = $"#{top:x6}";
        }
        return strip;
    }
}


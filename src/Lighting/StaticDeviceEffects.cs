using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// One device's Static assignment: the effect key plus the exact look it was
/// assigned with. Stored whole rather than reduced to a colour, so patterns
/// (gradients, two-tone, spectrum) and the tint controls (hue shift, warmth,
/// contrast) reach the hardware instead of being flattened to one swatch.
/// </summary>
public sealed class StaticDeviceAssignment
{
    public string Effect { get; init; } = "";
    /// <summary>
    /// A flat colour, "#rrggbb". Palette picks carry no shader and no controls,
    /// so the engine paints this straight onto the device and every field below
    /// is inert. Empty means the look is the effect above.
    /// </summary>
    public string Color { get; init; } = "";
    public float Intensity { get; init; } = 1f;
    public float Hue { get; init; }
    public float Colorize { get; init; }
    public float Saturation { get; init; } = 1f;
    public float Contrast { get; init; } = 1f;
    public IReadOnlyDictionary<string, float>? Params { get; init; }
    /// <summary>Template slot the look was picked from. Carried through so the
    /// UI can rebuild the pick; Key() ignores it - two devices on the same look
    /// still share one render whatever slot each came from.</summary>
    public int Slot { get; init; }
    /// <summary>Held across every mode and refused to Set/Clear until unlocked.
    /// Not part of Key(): the render is the same either way.</summary>
    public bool Locked { get; init; }

    /// <summary>
    /// Identity of the rendered look. Two devices sharing this share one render,
    /// and a change to any control invalidates the cache.
    /// </summary>
    public string Key()
    {
        var sb = new System.Text.StringBuilder(Effect);
        sb.Append('|').Append(Color);
        sb.Append('|').Append(Intensity.ToString("R")).Append('|').Append(Hue.ToString("R"))
          .Append('|').Append(Colorize.ToString("R")).Append('|').Append(Saturation.ToString("R"))
          .Append('|').Append(Contrast.ToString("R"));
        if (Params is not null)
        {
            // Ordered, so two equal looks never hash differently.
            var keys = new List<string>(Params.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys) sb.Append('|').Append(k).Append('=').Append(Params[k].ToString("R"));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Per-device Static assignments.
///
/// Static mode lets a device wear its own effect instead of the shared canvas.
/// The engine is the only place that can honour that for every contributor at
/// once (OpenRGB zones, first-party hubs and smart lights all funnel through
/// its sampling step), so assignments land here and the engine reads them.
/// Same shape as the identify trackers: a shared singleton, so no DI cycle
/// forms between the routes and the engine.
///
/// Assignments apply while Static is running - every other mode drives all
/// devices from one source, so <see cref="Enabled"/> gates them off - except a
/// locked one, which the device wears in every mode until the user unlocks it.
/// </summary>
public sealed class StaticDeviceEffectTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StaticDeviceAssignment> _assignments = new(StringComparer.Ordinal);
    private readonly IConfigStore? _store;

    public StaticDeviceEffectTracker(IConfigStore? store = null)
    {
        _store = store;
        // An assignment is what the hardware is meant to show, so it has to
        // survive a restart: without this the UI still lists every pick (it
        // keeps its own copy) while the devices fall back to the shared canvas.
        if (store is null) return;
        Hydrate();
        // A profile switch (and a cloud profile pull) replaces the whole
        // LightingSettings object, StaticDeviceLooks included. Hydrating only in
        // the ctor left the engine applying the PREVIOUS profile's assignments
        // while settings said otherwise, with nothing to reconcile them.
        store.OnChanged += Hydrate;
    }

    /// <summary>
    /// Rebuild from the store, but only when the stored content actually
    /// differs. Set/Clear write through the store and so re-enter here; a blind
    /// rebuild would bump <see cref="Version"/> every time and throw away the
    /// engine's render cache on every assignment.
    /// </summary>
    private void Hydrate()
    {
        var store = _store;
        if (store is null) return;
        var looks = store.Load().Lighting.StaticDeviceLooks;
        var rebuilt = new Dictionary<string, StaticDeviceAssignment>(StringComparer.Ordinal);
        foreach (var (id, look) in looks)
        {
            if (look is null || string.IsNullOrEmpty(look.Effect)) continue;
            rebuilt[id] = new StaticDeviceAssignment
            {
                Effect = look.Effect,
                Color = look.Color ?? "",
                Intensity = look.Intensity,
                Hue = look.Hue,
                Colorize = look.Colorize,
                Saturation = look.Saturation,
                Contrast = look.Contrast,
                Params = look.Params is { Count: > 0 } ? new Dictionary<string, float>(look.Params) : null,
                Slot = look.Slot,
                Locked = look.Locked,
            };
        }
        lock (_lock)
        {
            if (SameAs(rebuilt)) return;
            _assignments.Clear();
            foreach (var (id, a) in rebuilt) _assignments[id] = a;
            Version++;
        }
    }

    /// <summary>Content compare by look identity; caller holds the lock.</summary>
    private bool SameAs(Dictionary<string, StaticDeviceAssignment> other)
    {
        if (_assignments.Count != other.Count) return false;
        foreach (var (id, a) in other)
        {
            if (!_assignments.TryGetValue(id, out var mine)) return false;
            if (!string.Equals(mine.Key(), a.Key(), StringComparison.Ordinal)) return false;
            // Same look, different lock is still a change: a preset that
            // restores a lock must land it even when the colours match.
            if (mine.Locked != a.Locked) return false;
        }
        return true;
    }

    /// <summary>True while Static owns the output; false in every other mode.
    /// A locked assignment ignores this.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bumped on every change, so the engine can drop stale renders.</summary>
    public int Version { get; private set; }

    /// <summary>Assigns a look. False when the device is locked: the lock is
    /// the user's, so a pick cannot replace it, only an unlock can.</summary>
    public bool Set(string id, StaticDeviceAssignment assignment)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(assignment.Effect)) return false;
        lock (_lock)
        {
            if (IsLockedUnderLock(id)) return false;
            _assignments[id] = assignment;
            Version++;
        }
        _store?.Update(s => s.Lighting.StaticDeviceLooks[id] = ToLook(assignment));
        return true;
    }

    /// <summary>Returns the device to the shared canvas. False when locked.</summary>
    public bool Clear(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock)
        {
            if (IsLockedUnderLock(id)) return false;
            if (_assignments.Remove(id)) Version++;
        }
        _store?.Update(s => s.Lighting.StaticDeviceLooks.Remove(id));
        return true;
    }

    public void ClearAll()
    {
        lock (_lock) { if (_assignments.Count > 0) { _assignments.Clear(); Version++; } }
        _store?.Update(s => s.Lighting.StaticDeviceLooks.Clear());
    }

    /// <summary>
    /// Locks or unlocks the device's current assignment. False when there is
    /// nothing assigned: a lock holds a look, so it needs one to hold.
    /// </summary>
    public bool SetLocked(string id, bool locked)
    {
        if (string.IsNullOrEmpty(id)) return false;
        StaticDeviceAssignment next;
        lock (_lock)
        {
            if (!_assignments.TryGetValue(id, out var current)) return false;
            if (current.Locked == locked) return true;
            next = new StaticDeviceAssignment
            {
                Effect = current.Effect,
                Color = current.Color,
                Intensity = current.Intensity,
                Hue = current.Hue,
                Colorize = current.Colorize,
                Saturation = current.Saturation,
                Contrast = current.Contrast,
                Params = current.Params,
                Slot = current.Slot,
                Locked = locked,
            };
            // No Version bump: the render is unchanged, and the engine reads
            // the lock through TryGet on every tick anyway.
            _assignments[id] = next;
        }
        _store?.Update(s =>
        {
            if (s.Lighting.StaticDeviceLooks.TryGetValue(id, out var look) && look is not null) look.Locked = locked;
        });
        return true;
    }

    public bool IsLocked(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock) return IsLockedUnderLock(id);
    }

    private bool IsLockedUnderLock(string id) =>
        _assignments.TryGetValue(id, out var a) && a.Locked;

    private static StaticDeviceLook ToLook(StaticDeviceAssignment assignment) => new()
    {
        Effect = assignment.Effect,
        Color = assignment.Color,
        Intensity = assignment.Intensity,
        Hue = assignment.Hue,
        Colorize = assignment.Colorize,
        Saturation = assignment.Saturation,
        Contrast = assignment.Contrast,
        Params = assignment.Params is null
            ? new Dictionary<string, float>()
            : new Dictionary<string, float>(assignment.Params),
        Slot = assignment.Slot,
        Locked = assignment.Locked,
    };

    /// <summary>The assignment the engine should paint: any while Static owns
    /// the output, only locked ones otherwise.</summary>
    public bool TryGet(string id, out StaticDeviceAssignment assignment)
    {
        if (string.IsNullOrEmpty(id)) { assignment = null!; return false; }
        lock (_lock)
        {
            if (!_assignments.TryGetValue(id, out assignment!)) return false;
            if (Enabled || assignment.Locked) return true;
            assignment = null!;
            return false;
        }
    }

    public bool Any
    {
        get { if (!Enabled) return false; lock (_lock) return _assignments.Count > 0; }
    }
}

/// <summary>
/// "#rrggbb" (or "rrggbb") to bytes. Palette picks travel as hex because that
/// is what the swatch is; anything unparseable falls back to the shader path.
/// </summary>
public static class StaticColorHex
{
    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex)) return false;
        var span = hex.AsSpan();
        if (span[0] == '#') span = span[1..];
        if (span.Length != 6) return false;
        if (!byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out r)) return false;
        if (!byte.TryParse(span.Slice(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)) return false;
        if (!byte.TryParse(span.Slice(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b)) return false;
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>Identity and defaults of a static pin's render.</summary>
public static class DevicePinLook
{
    /// <summary>
    /// Identity of a static pin's rendered look. Two devices sharing this share
    /// one render, and a change to any control invalidates the cache.
    /// </summary>
    public static string StaticKey(DevicePin pin)
    {
        var sb = new System.Text.StringBuilder(pin.Effect ?? "");
        sb.Append('|').Append(pin.Color);
        sb.Append('|').Append(Intensity(pin).ToString("R")).Append('|').Append((pin.Hue ?? 0f).ToString("R"))
          .Append('|').Append((pin.Colorize ?? 0f).ToString("R")).Append('|').Append(Saturation(pin).ToString("R"))
          .Append('|').Append(Contrast(pin).ToString("R"));
        if (pin.Params is not null)
        {
            // Ordered, so two equal looks never hash differently.
            var keys = new List<string>(pin.Params.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys) sb.Append('|').Append(k).Append('=').Append(pin.Params[k].ToString("R"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The key an animate pin's render runs under. Pins on one effect share a render, except that a pin carrying
    /// sensor picks gets its own, since two picks cannot share one canvas. A sweep never reads a sensor.
    /// </summary>
    public static string AnimateRenderKey(DevicePin pin)
    {
        var effect = pin.Effect ?? "";
        // Only picks the shader would act on key the render; an unusable one falls back to the default anyway.
        Dictionary<string, SensorBinding>? usable = null;
        if (pin.Sensors is not null)
        {
            foreach (var kv in pin.Sensors)
            {
                if (kv.Value is { IsUsable: true })
                {
                    (usable ??= new Dictionary<string, SensorBinding>(StringComparer.Ordinal))[kv.Key] = kv.Value;
                }
            }
        }
        var picks = SensorBinding.MapKey(usable);
        return picks.Length == 0 || Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.IsSweepEffect(effect)
            ? effect
            : effect + "\u001f" + picks;
    }

    public static float Intensity(DevicePin pin) => pin.Intensity is > 0f ? pin.Intensity.Value : 1f;
    public static float Saturation(DevicePin pin) => pin.Saturation ?? 1f;
    public static float Contrast(DevicePin pin) => pin.Contrast is > 0f ? pin.Contrast.Value : 1f;
}

/// <summary>
/// Per-device pins. A pinned device shows its own source in every mode (a
/// static look, an Animate shader, or the screen capture); an unpinned one
/// follows the global mode. The engine is the only place that can honour a pin
/// for every contributor at once (OpenRGB zones, first-party hubs and smart
/// lights all funnel through its sampling step), so pins land here and the
/// engine reads them.
///
/// A shared singleton hydrated from the store and re-hydrated on every store
/// change, so a profile switch, cloud pull or preset activate replaces the pins.
/// The snapshot is replaced wholesale, never mutated: the engine and routes read
/// it lock-free.
/// </summary>
public sealed class DevicePinTracker
{
    private readonly object _lock = new();
    private readonly IConfigStore? _store;
    private volatile Dictionary<string, DevicePin> _pins = new(StringComparer.Ordinal);
    private int _version;

    public DevicePinTracker(IConfigStore? store = null)
    {
        _store = store;
        if (store is null) return;
        Hydrate();
        store.OnChanged += Hydrate;
    }

    /// <summary>Bumped when the set or content of pins changes.</summary>
    public int Version => Volatile.Read(ref _version);

    public bool Any => _pins.Count > 0;

    /// <summary>Current device id to pin map. Treat as read-only.</summary>
    public Dictionary<string, DevicePin> Snapshot() => _pins;

    public bool TryGet(string id, out DevicePin pin)
    {
        pin = null!;
        if (string.IsNullOrEmpty(id)) return false;
        return _pins.TryGetValue(id, out pin!);
    }

    public bool IsPinned(string id) => !string.IsNullOrEmpty(id) && _pins.ContainsKey(id);

    /// <summary>True for an Animate shader key: one with a template bundle that is
    /// not a Static fill or pattern. Keys are lower case, as StartAnimate stores them.</summary>
    public static bool IsAnimateEffect(string? key) =>
        !string.IsNullOrEmpty(key)
        && ((AnimateTemplateDefaults.All.ContainsKey(key) && !StaticEffectCatalog.Contains(key))
            || Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.IsSweepEffect(key)
            || Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.IsSensorEffect(key)
            || (Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.IsCustomKey(key)
                && Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.IsKnownKey(key)));

    /// <summary>Null when the pin is acceptable, else why not.</summary>
    public static string? Validate(DevicePin? pin)
    {
        if (pin is null || !DevicePinKinds.IsKnown(pin.Kind)) return "unknown pin kind";
        switch (pin.Kind)
        {
            case DevicePinKinds.Animate:
                return IsAnimateEffect(pin.Effect) ? null : "not an animate effect";
            case DevicePinKinds.Static:
                if (StaticColorHex.TryParse(pin.Color, out _, out _, out _)) return null;
                return StaticEffectCatalog.Contains(pin.Effect ?? "") ? null : "unknown static effect";
            case DevicePinKinds.Media:
                return HasNoFields(pin) ? null : "a media pin takes no fields";
            default:
                // One universal mirror: it is configured by the global Mirror
                // settings, so a mirror pin carries nothing of its own.
                return HasNoFields(pin) ? null : "a mirror pin takes no fields";
        }
    }

    private static bool HasNoFields(DevicePin pin) =>
        pin.Effect is null && pin.Color is null && pin.Intensity is null && pin.Hue is null
        && pin.Colorize is null && pin.Saturation is null && pin.Contrast is null
        && pin.Params is null && pin.Sensors is null && pin.Slot is null;

    /// <summary>
    /// A copy holding only the fields the pin's kind reads: animate keeps its
    /// effect, mirror and media keep nothing, static keeps its look with the
    /// numeric controls clamped to the ranges its shader accepts. Unknown kinds
    /// pass through as a plain copy.
    /// </summary>
    public static DevicePin Normalize(DevicePin pin)
    {
        switch (pin.Kind)
        {
            case DevicePinKinds.Animate:
                return new DevicePin { Kind = pin.Kind, Effect = pin.Effect, Sensors = SensorBinding.CopyMap(pin.Sensors) };
            case DevicePinKinds.Mirror:
            case DevicePinKinds.Media:
                return new DevicePin { Kind = pin.Kind };
            case DevicePinKinds.Static:
                var copy = pin.Copy();
                // A static look has no sensor uniform; a stray field must not split identical pins.
                copy.Sensors = null;
                if (string.IsNullOrEmpty(copy.Effect)
                    || !Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.AllEffectKeys.Contains(copy.Effect))
                {
                    return copy;
                }
                var specs = Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.Params(copy.Effect);
                copy.Intensity = ClampField(specs, "u_intensity", copy.Intensity);
                copy.Hue = ClampField(specs, "u_hue", copy.Hue);
                copy.Colorize = ClampField(specs, "u_colorize", copy.Colorize);
                copy.Saturation = ClampField(specs, "u_saturation", copy.Saturation);
                copy.Contrast = ClampField(specs, "u_contrast", copy.Contrast);
                if (copy.Params is not null)
                {
                    foreach (var key in new List<string>(copy.Params.Keys))
                    {
                        if (specs.TryGetValue(key, out var spec))
                        {
                            copy.Params[key] = Nexus.Service.Lighting.Engine.Gpu.ShaderParamSpec.Clamp(spec, copy.Params[key]);
                        }
                    }
                }
                return copy;
            default:
                return pin.Copy();
        }
    }

    private static float? ClampField(
        IReadOnlyDictionary<string, Nexus.Service.Lighting.Engine.Gpu.ShaderParamSpec> specs, string name, float? value) =>
        value is { } v && specs.TryGetValue(name, out var spec)
            ? Nexus.Service.Lighting.Engine.Gpu.ShaderParamSpec.Clamp(spec, v)
            : value;

    /// <summary>Pins every id to a normalized copy of <paramref name="pin"/>, replacing any pin it held; a null pin clears them.</summary>
    public void SetMany(IEnumerable<string> ids, DevicePin? pin)
    {
        var list = new List<string>();
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id)) list.Add(id);
        }
        if (list.Count == 0) return;
        var normalized = pin is null ? null : Normalize(pin);
        lock (_lock)
        {
            var next = new Dictionary<string, DevicePin>(_pins, StringComparer.Ordinal);
            foreach (var id in list)
            {
                if (normalized is null) next.Remove(id);
                else next[id] = normalized.Copy();
            }
            _pins = next;
            Interlocked.Increment(ref _version);
        }
        _store?.Update(s =>
        {
            foreach (var id in list)
            {
                if (normalized is null) s.Lighting.DevicePins.Remove(id);
                else s.Lighting.DevicePins[id] = normalized.Copy();
            }
        });
    }

    public void Set(string id, DevicePin pin) => SetMany(new[] { id }, pin);

    public void Clear(string id) => SetMany(new[] { id }, null);

    /// <summary>Rebuild from the store, only when the content differs: SetMany
    /// writes through the store and re-enters here.</summary>
    private void Hydrate()
    {
        var store = _store;
        if (store is null) return;
        var stored = store.Load().Lighting.DevicePins;
        var rebuilt = new Dictionary<string, DevicePin>(StringComparer.Ordinal);
        if (stored is not null)
        {
            foreach (var (id, pin) in stored)
            {
                if (!string.IsNullOrEmpty(id) && pin is not null && DevicePinKinds.IsKnown(pin.Kind)) rebuilt[id] = Normalize(pin);
            }
        }
        lock (_lock)
        {
            if (SameAs(rebuilt)) return;
            _pins = rebuilt;
            Interlocked.Increment(ref _version);
        }
    }

    private bool SameAs(Dictionary<string, DevicePin> other)
    {
        var mine = _pins;
        if (mine.Count != other.Count) return false;
        foreach (var (id, pin) in other)
        {
            if (!mine.TryGetValue(id, out var m)) return false;
            if (m.Kind != pin.Kind) return false;
            if (!string.Equals(m.Effect, pin.Effect, StringComparison.Ordinal)) return false;
            if (pin.Kind == DevicePinKinds.Static && DevicePinLook.StaticKey(m) != DevicePinLook.StaticKey(pin)) return false;
            if (m.Slot != pin.Slot) return false;
            if (pin.Kind == DevicePinKinds.Animate && !SensorBinding.MapEquals(m.Sensors, pin.Sensors)) return false;
        }
        return true;
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

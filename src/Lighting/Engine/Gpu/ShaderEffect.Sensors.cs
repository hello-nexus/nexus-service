using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>How a sensor uniform glides toward its reading between the feed's polls.</summary>
internal static class SensorSmoothing
{
    /// <summary>Time constant of the exponential glide.</summary>
    public const double TauMs = 400.0;

    /// <summary>A gap longer than this (a paused engine) jumps instead of gliding.</summary>
    public const double SnapAfterMs = 1500.0;

    public static float Step(float current, float target, double dtMs)
    {
        if (dtMs <= 0.0)
        {
            return current;
        }
        if (dtMs >= SnapAfterMs)
        {
            return target;
        }
        var alpha = 1.0 - Math.Exp(-dtMs / TauMs);
        return current + (float)((target - current) * alpha);
    }
}

public sealed partial class ShaderEffect
{
    private sealed class SensorSlot
    {
        public SensorBinding? Binding;
        public SensorFeed.Channel? Channel;
        public float Smoothed;
        public double LastTick;
        public bool Seeded;
    }

    private IReadOnlyList<SensorUniformSpec>? _sensorSpecs;
    private SensorSlot[]? _sensorSlots;
    private volatile IReadOnlyDictionary<string, SensorBinding>? _sensorOverrides;

    /// <summary>The sensor uniforms the source declares, in source order.</summary>
    internal IReadOnlyList<SensorUniformSpec> SensorSpecs => _sensorSpecs ??= SensorUniformSpec.Parse(_fragSource);

    /// <summary>True when the source declares a sensor uniform, so a frame depends on more than time.</summary>
    public bool ReadsSensors => SensorSpecs.Count > 0;

    /// <summary>Where readings come from; null feeds every sensor uniform 0.</summary>
    public SensorFeed? SensorFeed { get; set; }

    /// <summary>Per sensor uniform, the user's pick over the shader's default binding. Replaced whole, never edited in place.</summary>
    public IReadOnlyDictionary<string, SensorBinding>? SensorOverrides
    {
        get => _sensorOverrides;
        set => _sensorOverrides = value;
    }

    /// <summary>When set, every sensor uniform takes this 0..1 value and the feed is not consulted (thumbnails).</summary>
    public float? FixedSensorValue { get; set; }

    private void PushSensors(GL gl, Dictionary<string, int> cache, uint program, double tickMs)
    {
        var specs = SensorSpecs;
        for (var i = 0; i < specs.Count; i++)
        {
            var name = specs[i].Name;
            int loc;
            if (!cache.TryGetValue(name, out loc))
            {
                loc = gl.GetUniformLocation(program, name);
                cache[name] = loc;
            }
            // Optimized out: nothing reads it, so nothing asks the feed.
            if (loc >= 0)
            {
                gl.Uniform1(loc, NextSensorValue(i, tickMs));
            }
        }
    }

    /// <summary>The binding a sensor uniform follows: the user's pick when usable, else the shader's own.</summary>
    internal static SensorBinding ResolveBinding(SensorUniformSpec spec, IReadOnlyDictionary<string, SensorBinding>? overrides) =>
        overrides is not null && overrides.TryGetValue(spec.Name, out var picked) && picked is { IsUsable: true }
            ? picked
            : spec.Default;

    /// <summary>The smoothed 0..1 value of the index-th sensor uniform for this frame. Render thread.</summary>
    internal float NextSensorValue(int index, double tickMs)
    {
        var specs = SensorSpecs;
        var slots = _sensorSlots;
        if (slots is null)
        {
            slots = new SensorSlot[specs.Count];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = new SensorSlot();
            }
            _sensorSlots = slots;
        }
        var slot = slots[index];
        if (FixedSensorValue is { } pinned)
        {
            slot.Smoothed = pinned;
            return pinned;
        }
        var binding = ResolveBinding(specs[index], _sensorOverrides);
        if (!ReferenceEquals(slot.Binding, binding))
        {
            slot.Binding = binding;
            slot.Channel = SensorFeed?.Acquire(binding);
        }
        var raw = slot.Channel?.Read() ?? float.NaN;
        var target = binding.Normalize(raw);
        if (!slot.Seeded)
        {
            // Nothing to glide from until a real reading lands; until then the uniform is 0.
            if (float.IsFinite(raw))
            {
                slot.Smoothed = target;
                slot.Seeded = true;
            }
        }
        else
        {
            slot.Smoothed = SensorSmoothing.Step(slot.Smoothed, target, tickMs - slot.LastTick);
        }
        slot.LastTick = tickMs;
        return slot.Smoothed;
    }
}

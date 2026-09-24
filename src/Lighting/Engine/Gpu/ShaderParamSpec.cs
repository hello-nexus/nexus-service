using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// One tunable shader uniform's authored range, parsed from a Godot-style
/// hint_range comment in the GLSL (see Shaders/_prelude.frag's header for the
/// exact syntax). Declared marks a line that actually declares the uniform
/// (`uniform float u_x;`) versus one that only narrows an inherited range.
/// </summary>
public readonly partial record struct ShaderParamSpec(string Name, float Min, float Max, float Step, float Default, bool Declared)
{
    /// <summary>Clamp a value into [Min, Max]; a non-finite value falls back to Default.</summary>
    public static float Clamp(ShaderParamSpec spec, float value) =>
        float.IsFinite(value) ? Math.Clamp(value, spec.Min, spec.Max) : spec.Default;

    [GeneratedRegex(@"\bhint_range\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*(?:,\s*(-?[\d.]+))?\s*\)(?:\s*=\s*(-?[\d.]+))?")]
    private static partial Regex HintRangeRegex();
    [GeneratedRegex(@"\bu_\w+\b")]
    private static partial Regex NameRegex();

    /// <summary>
    /// Parse every hint_range annotation out of composed GLSL source (prelude
    /// plus effect body), line by line. A `uniform` line declares the param; a
    /// comment-only line only narrows a range/default already declared
    /// upstream. A later line for the same name overrides an earlier one, so a
    /// shader body can narrow a range the prelude declared.
    /// </summary>
    public static Dictionary<string, ShaderParamSpec> Parse(string source)
    {
        var result = new Dictionary<string, ShaderParamSpec>(StringComparer.Ordinal);
        foreach (var line in source.Split('\n'))
        {
            var hint = HintRangeRegex().Match(line);
            if (!hint.Success)
            {
                continue;
            }
            // The name is the LAST u_ identifier before hint_range on the
            // line, so a free-text comment mentioning an earlier uniform
            // doesn't steal the annotation meant for the one next to it.
            Match? nameMatch = null;
            foreach (Match m in NameRegex().Matches(line[..hint.Index]))
            {
                nameMatch = m;
            }
            if (nameMatch is null)
            {
                continue;
            }
            var name = nameMatch.Value;
            var min = ParseFloat(hint.Groups[1].Value);
            var max = ParseFloat(hint.Groups[2].Value);
            var step = hint.Groups[3].Success ? ParseFloat(hint.Groups[3].Value) : 0.01f;
            var def = hint.Groups[4].Success ? ParseFloat(hint.Groups[4].Value) : min;
            var declaresHere = line.TrimStart().StartsWith("uniform", StringComparison.Ordinal);
            var declaredBefore = result.TryGetValue(name, out var prev) && prev.Declared;
            result[name] = new ShaderParamSpec(name, min, max, step, def, declaresHere || declaredBefore);
        }
        return result;
    }

    private static float ParseFloat(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static float ClampIfSpecified(IReadOnlyDictionary<string, ShaderParamSpec> specs, string name, float value) =>
        specs.TryGetValue(name, out var spec) ? Clamp(spec, value) : value;

    /// <summary>
    /// Clamp every render-affecting field on a stored animate/static state
    /// against the effect's parsed spec, so an imported profile or a saved
    /// template can never carry a value outside the shader's authored range.
    /// Unknown effect keys (a foreign build's data) are left untouched. Speed
    /// is stored in wire units; it is converted to shader units the same way
    /// a headless start converts it before clamping against u_speed's range.
    /// </summary>
    public static void Sanitize(string effect, AnimateEffectState state)
    {
        if (!ShaderLibrary.AllEffectKeys.Contains(effect))
        {
            return;
        }
        var specs = ShaderLibrary.Params(effect);
        if (specs.TryGetValue("u_speed", out var speedSpec))
        {
            var shaderSpeed = Clamp(speedSpec, state.Speed / 50f);
            state.Speed = (int)MathF.Round(shaderSpeed * 50f);
        }
        state.Intensity = ClampIfSpecified(specs, "u_intensity", state.Intensity);
        state.Hue = ClampIfSpecified(specs, "u_hue", state.Hue);
        state.Colorize = ClampIfSpecified(specs, "u_colorize", state.Colorize);
        state.Saturation = ClampIfSpecified(specs, "u_saturation", state.Saturation);
        state.Contrast = ClampIfSpecified(specs, "u_contrast", state.Contrast);
        if (state.Params is null)
        {
            return;
        }
        var keys = new List<string>(state.Params.Keys);
        foreach (var key in keys)
        {
            if (specs.TryGetValue(key, out var spec))
            {
                state.Params[key] = Clamp(spec, state.Params[key]);
            }
        }
    }
}

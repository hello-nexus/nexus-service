using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// A sensor uniform a fragment declares with a trailing annotation:
/// <c>uniform float u_x_s; // sensor {"category":"quick","sensor":"summary/cpu-usage","min":0,"max":100}</c>.
/// The service feeds it the smoothed 0..1 reading every frame; it is not a slider param.
/// Default is the binding the author picked; a user override replaces it per applied effect.
/// </summary>
public readonly partial record struct SensorUniformSpec(string Name, SensorBinding Default)
{
    [GeneratedRegex(@"^\s*uniform\s+float\s+([A-Za-z_]\w*)\s*;\s*//\s*sensor\s+(\{.*\})\s*$")]
    private static partial Regex LineRegex();

    private static readonly IReadOnlyList<SensorUniformSpec> None = Array.Empty<SensorUniformSpec>();

    /// <summary>Whether any line could carry the annotation, so the common case skips the per-line scan.</summary>
    private static bool MayHaveAnnotation(string source) => source.Contains("// sensor", StringComparison.Ordinal);

    /// <summary>The lines left once block comments are gone; a line comment stays, since the annotation is one.</summary>
    private static List<string> LiveLines(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                sb.Append(source, i, end - i);
                i = end - 1;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? source.Length : end + 2;
                for (var k = i; k < stop; k++)
                {
                    if (source[k] == '\n')
                    {
                        sb.Append('\n');
                    }
                }
                i = stop - 1;
            }
            else
            {
                sb.Append(c);
            }
        }
        var lines = new List<string>(sb.ToString().Split('\n'));
        return lines;
    }

    /// <summary>Every well-formed annotation in source order. A line whose JSON is malformed or names no usable binding is skipped, so its uniform stays an ordinary one.</summary>
    public static IReadOnlyList<SensorUniformSpec> Parse(string source)
    {
        if (!MayHaveAnnotation(source))
        {
            return None;
        }
        List<SensorUniformSpec>? found = null;
        foreach (var line in LiveLines(source))
        {
            var m = LineRegex().Match(line.TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }
            SensorBinding? binding;
            try
            {
                binding = JsonSerializer.Deserialize(m.Groups[2].Value, PersistenceJsonContext.Default.SensorBinding);
            }
            catch (JsonException)
            {
                continue;
            }
            if (binding is null || !binding.IsUsable)
            {
                continue;
            }
            (found ??= new List<SensorUniformSpec>()).Add(new SensorUniformSpec(m.Groups[1].Value, binding));
        }
        return found is null ? None : found;
    }
}

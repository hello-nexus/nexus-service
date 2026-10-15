using System;
using System.Text.RegularExpressions;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Every shader the service compiles is authored as GLSL 3.30 core. ES backends
/// (ANGLE) take 3.00 es, which differs in the version line and in requiring a
/// default float precision in fragment shaders; the rest of the sources already
/// stay inside the common subset. One rewrite at the compile site keeps a single
/// copy of each source, and the rewrite never adds a line so driver error
/// positions still match the authored text.
/// </summary>
internal static class GlslDialect
{
    private static readonly Regex VersionLine = new(
        @"\A(\s*)#version[ \t]+330([ \t]+core)?[ \t]*(\r?\n|\z)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const string Precision = "precision highp float; precision highp int; ";

    /// <summary>The source for an OpenGL ES 3.00 context. Text that is not 3.30 core passes through so the driver reports its own error.</summary>
    public static string ToEs300(string source)
    {
        var m = VersionLine.Match(source);
        if (!m.Success)
        {
            return source;
        }
        var rest = source.AsSpan(m.Length).ToString();
        var head = m.Groups[1].Value + "#version 300 es" + (m.Groups[3].Length == 0 ? "\n" : m.Groups[3].Value);
        // A directive must start its line, so precision cannot share a line with one;
        // that rare case pays one extra line instead.
        var directiveNext = rest.TrimStart(' ', '\t').StartsWith('#');
        return head + Precision + (directiveNext ? "\n" : "") + rest;
    }

    /// <summary>The source for the given context: ES gets the rewrite, desktop GL is untouched.</summary>
    public static string For(bool es, string source) => es ? ToEs300(source) : source;
}

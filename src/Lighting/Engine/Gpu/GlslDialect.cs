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

    // ES 3.00 keeps no implicit int to float conversion, which 3.30 allows.
    // Lines that declare or compute with integers are left alone.
    private static readonly Regex IntegerTypedLine = new(
        @"\b(?:int|uint|ivec[234]|uvec[234]|for|while|switch|case|layout|const\s+int)\b|%|<<|>>",
        RegexOptions.CultureInvariant);

    private static readonly Regex IntLiteral = new(@"(?<![\w.])(?<![\d.][eE][+-])\d+(?![\w.])", RegexOptions.CultureInvariant);

    /// <summary>
    /// A conservative rewrite of integer literals to floats (<c>pow(x, 2)</c>,
    /// <c>max(v, 0)</c>, <c>float k = 1;</c>, <c>x * 2</c>) for the ES retry. It skips
    /// preprocessor lines, any line that mentions an integer type, loop or
    /// integer operator, and anything inside brackets, so index and loop-bound
    /// code is untouched. The caller keeps the result only if it compiles.
    /// </summary>
    public static string FixIntLiteralsForEs(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            var code = comment >= 0 ? line[..comment] : line;
            if (code.TrimStart().StartsWith('#') || IntegerTypedLine.IsMatch(code))
            {
                continue;
            }
            var fixedCode = IntLiteral.Replace(code, m =>
            {
                var depth = 0;
                for (var k = 0; k < m.Index; k++)
                {
                    if (code[k] == '[')
                    {
                        depth++;
                    }
                    else if (code[k] == ']')
                    {
                        depth--;
                    }
                }
                return depth > 0 ? m.Value : m.Value + ".0";
            });
            lines[i] = comment >= 0 ? fixedCode + line[comment..] : fixedCode;
        }
        return string.Join('\n', lines);
    }

    private static readonly Regex EsUnsupported = new(
        @"\b(?:sampler1D\w*|sampler2DRect\w*|sampler2DMS\w*|texture1D\w*|texture2D|gl_FragColor|gl_FragData|double|dvec[234]|dmat\w+|bitCount|bitfieldExtract|bitfieldInsert|bitfieldReverse|findLSB|findMSB|noise[1-4]|gl_ClipDistance|imageLoad|imageStore|subroutine)\b|#\s*extension\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex FmaCall = new(@"\bfma\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex FmaDefinition = new(
        @"\b(?:float|vec[234]|int|uint|bool|void)\s+fma\s*\(", RegexOptions.CultureInvariant);

    /// <summary>The first desktop-only construct in a user-authored source that an ES 3.00 context cannot compile, or null. Checked wherever user effects are validated so every platform refuses what Windows cannot run. Comments are ignored, and a source that defines its own fma() is fine.</summary>
    public static string? EsIncompatibility(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        foreach (var line in BlockComment.Replace(source, " ").Split('\n'))
        {
            var c = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(c >= 0 ? line[..c] : line).Append('\n');
        }
        var code = sb.ToString();
        var m = EsUnsupported.Match(code);
        if (m.Success)
        {
            return $"'{m.Value}' is not available in OpenGL ES 3.00, which the lighting renderer uses on Windows";
        }
        return FmaCall.IsMatch(code) && !FmaDefinition.IsMatch(code)
            ? "'fma' is not available in OpenGL ES 3.00, which the lighting renderer uses on Windows"
            : null;
    }
}

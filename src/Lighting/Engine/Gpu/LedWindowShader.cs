using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Turns an effect's fragment source into one that evaluates at chosen pixel centers, and names the constructs that make a pixel depend on its neighbors.</summary>
internal static class LedWindowShader
{
    /// <summary>Rows (frames) one LED-window draw covers at most; sizes the clock table in the rewritten source.</summary>
    public const int MaxFrames = 32;

    private static readonly Regex MainDecl = new(@"\bvoid\s+main\s*\(\s*(?:void)?\s*\)", RegexOptions.CultureInvariant);
    private static readonly Regex TimeDecl = new(@"^[ \t]*uniform[ \t]+float[ \t]+u_time[ \t]*;", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex OutDecl = new(@"^[ \t]*out[ \t]+vec4[ \t]+fragColor[ \t]*;", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex FragCoord = new(@"\bgl_FragCoord\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The fragment source, evaluated at a pixel center read from a tap texture (texel x of the first row) with
    /// the clock read from a per-row table, so one draw can cover many taps and many frames. Output column is the
    /// tap index, output row is the frame. Keeps every line count and every other uniform as authored. Null when
    /// the source has no single main to wrap or lacks the declarations the rewrite hooks onto.
    /// </summary>
    public static string? Rewrite(string source, int windowFrames = MaxFrames)
    {
        if (MainDecl.Matches(source).Count != 1 || !TimeDecl.IsMatch(source) || !OutDecl.IsMatch(source))
        {
            return null;
        }
        var s = FragCoord.Replace(source, "ledFC");
        s = TimeDecl.Replace(s, "float u_time;");
        s = OutDecl.Replace(
            s,
            "out vec4 fragColor; vec4 ledFC; uniform highp sampler2D u_ledTap; uniform float u_ledT["
            + windowFrames.ToString(System.Globalization.CultureInfo.InvariantCulture) + "];");
        s = MainDecl.Replace(s, "void ledInnerMain()");
        var sb = new StringBuilder(s);
        sb.Append('\n');
        sb.Append("void main() {\n");
        sb.Append("    ivec2 ledP = ivec2(gl_FragCoord.xy);\n");
        sb.Append("    ledFC = vec4(texelFetch(u_ledTap, ivec2(ledP.x, 0), 0).xy, 0.0, 1.0);\n");
        sb.Append("    u_time = u_ledT[ledP.y];\n");
        sb.Append("    ledInnerMain();\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.CultureInvariant);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex Neighbor = new(@"\b(?:dFdx\w*|dFdy\w*|fwidth\w*)\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex TextureRead = new(@"\b(?:sampler\w+|texture\w*|texelFetch\w*)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Discard = new(@"\bdiscard\b", RegexOptions.CultureInvariant);
    private static readonly Regex ResolutionLoop = new(@"\bfor\s*\([^)]*u_resolution", RegexOptions.CultureInvariant);
    private const string BodyMarker = "// ---- effect body ----";

    /// <summary>
    /// Source constructs that make a pixel depend on something other than its coordinate, the clock and
    /// uniforms. Comments are ignored; the shared prelude is skipped so only the effect's own text counts.
    /// </summary>
    public static List<string> CanvasDependencyFlags(string source)
    {
        var flags = new List<string>();
        var cut = source.IndexOf(BodyMarker, StringComparison.Ordinal);
        var body = cut >= 0 ? source[cut..] : source;
        var code = LineComment.Replace(BlockComment.Replace(body, " "), string.Empty);
        if (Neighbor.IsMatch(code))
        {
            flags.Add("derivative");
        }
        if (TextureRead.IsMatch(code))
        {
            flags.Add("texture");
        }
        if (Discard.IsMatch(code))
        {
            flags.Add("discard");
        }
        if (ResolutionLoop.IsMatch(code))
        {
            flags.Add("resolution-loop");
        }
        if (FragCoord.IsMatch(code))
        {
            flags.Add("direct-fragcoord");
        }
        return flags;
    }
}

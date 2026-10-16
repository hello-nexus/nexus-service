using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Lighting.Bench;

/// <summary>Pixels one device reads from the canvas, as flat tap lists in LED order.</summary>
internal sealed class LedTapLayout
{
    public int Leds { get; init; }
    public int[] TapStart { get; init; } = Array.Empty<int>();
    public int[] TapCount { get; init; } = Array.Empty<int>();

    /// <summary>Canvas column of each tap, top-left origin.</summary>
    public int[] TapX { get; init; } = Array.Empty<int>();

    /// <summary>Canvas row of each tap, top-left origin.</summary>
    public int[] TapY { get; init; } = Array.Empty<int>();

    public int TotalTaps => TapX.Length;
}

/// <summary>
/// Pure pieces of the LED-space probe: which canvas pixels an LED reads, how those reads reduce to
/// one color, how a fragment source is rewritten to evaluate at chosen pixel centers, and which
/// source constructs make an effect depend on neighbouring pixels. Probe only; the live engine keeps
/// its own copy of the sampling rules.
/// </summary>
internal static class LedSpaceCore
{
    /// <summary>Spot side as a fraction of the canvas width, copied from the engine's sampling rule.</summary>
    private const float SpotSideOfCanvasWidth = 3f / 160f;

    public const int StrimerColumns = 22;
    public const int StrimerRows = 6;

    /// <summary>The 24-pin cable's map: rows of columns, centered in each grid cell, rounded as the shipped data rounds them.</summary>
    public static (float[] U, float[] V) StrimerMap()
    {
        var n = StrimerColumns * StrimerRows;
        var u = new float[n];
        var v = new float[n];
        for (var i = 0; i < n; i++)
        {
            u[i] = (float)Math.Round((i % StrimerColumns + 0.5) / StrimerColumns, 5);
            v[i] = (float)Math.Round((i / StrimerColumns + 0.5) / StrimerRows, 5);
        }
        return (u, v);
    }

    /// <summary>The shader clock for a tick, as the render path pushes it to u_time.</summary>
    public static float UTime(double tickMs) => (float)(tickMs % 86_400_000 / 1000.0);

    /// <summary>
    /// The taps the engine's spot read touches for a UV-mapped device that is not full-frame, not in
    /// the 3D scene and not stacked: the same cell, spot and clamp rules, evaluated once per LED.
    /// </summary>
    public static LedTapLayout BuildTaps(DeviceFrame dev, int cw, int ch)
    {
        var ledU = dev.LedU ?? throw new ArgumentException("device has no LedU");
        var ledV = dev.LedV ?? throw new ArgumentException("device has no LedV");
        var ledCount = dev.LedCount;
        const float CW = LightingEngine.CanvasUnitsW;
        const float CH = LightingEngine.CanvasUnitsH;
        var rectX = dev.X;
        var rectY = dev.Y;
        var rectW = dev.W;
        var rectH = dev.H;
        var frameCx = rectX + rectW * 0.5f;
        var frameCy = rectY + rectH * 0.5f;
        var kx = cw / CW;
        var ky = ch / CH;
        var rad = (((dev.Rotation % 360) + 360) % 360) * (MathF.PI / 180f);
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var absCos = MathF.Abs(cos);
        var absSin = MathF.Abs(sin);
        var aspect = rectH > 0.001f ? rectW / rectH : 1f;
        var cols = Math.Max(1, (int)MathF.Round(MathF.Sqrt(ledCount * aspect)));
        var rows = Math.Max(1, (ledCount + cols - 1) / cols);
        var cellHalfW = Math.Max(1f / kx, rectW / cols) * 0.5f;
        var cellHalfH = Math.Max(1f / ky, rectH / rows) * 0.5f;
        var uvHalfW = (cellHalfW * absCos + cellHalfH * absSin) * kx;
        var uvHalfH = (cellHalfW * absSin + cellHalfH * absCos) * ky;
        var spotHalf = Math.Max(0.5f, cw * SpotSideOfCanvasWidth * 0.5f);

        var start = new int[ledCount];
        var count = new int[ledCount];
        var xs = new List<int>(ledCount * 9);
        var ys = new List<int>(ledCount * 9);
        for (var i = 0; i < ledCount; i++)
        {
            start[i] = xs.Count;
            if (dev.LedDisabled is not null && i < dev.LedDisabled.Length && dev.LedDisabled[i])
            {
                continue;
            }
            var lx = rectX + ledU[i] * rectW - frameCx;
            var ly = rectY + ledV[i] * rectH - frameCy;
            var sx = (frameCx + lx * cos - ly * sin) * kx;
            var sy = (frameCy + lx * sin + ly * cos) * ky;
            var hx = Math.Min(uvHalfW, spotHalf);
            var hy = Math.Min(uvHalfH, spotHalf);
            var cx = MathF.Floor(sx) + 0.5f;
            var cy = MathF.Floor(sy) + 0.5f;
            var ix0 = Math.Clamp((int)MathF.Floor(cx - hx), 0, cw - 1);
            var iy0 = Math.Clamp((int)MathF.Floor(cy - hy), 0, ch - 1);
            var ix1 = Math.Clamp((int)MathF.Ceiling(cx + hx) - 1, ix0, cw - 1);
            var iy1 = Math.Clamp((int)MathF.Ceiling(cy + hy) - 1, iy0, ch - 1);
            for (var py = iy0; py <= iy1; py++)
            {
                for (var px = ix0; px <= ix1; px++)
                {
                    xs.Add(px);
                    ys.Add(py);
                }
            }
            count[i] = xs.Count - start[i];
        }
        return new LedTapLayout { Leds = ledCount, TapStart = start, TapCount = count, TapX = xs.ToArray(), TapY = ys.ToArray() };
    }

    /// <summary>One LED color per LED from per-tap RGB bytes: the integer mean of each LED's taps, black when it has none.</summary>
    public static void Resolve(LedTapLayout layout, ReadOnlySpan<byte> tapRgb, Span<byte> ledRgb)
    {
        for (var i = 0; i < layout.Leds; i++)
        {
            var n = layout.TapCount[i];
            if (n == 0)
            {
                ledRgb[i * 3] = 0;
                ledRgb[i * 3 + 1] = 0;
                ledRgb[i * 3 + 2] = 0;
                continue;
            }
            long r = 0, g = 0, b = 0;
            var o = layout.TapStart[i] * 3;
            for (var k = 0; k < n; k++)
            {
                r += tapRgb[o];
                g += tapRgb[o + 1];
                b += tapRgb[o + 2];
                o += 3;
            }
            ledRgb[i * 3] = (byte)(r / n);
            ledRgb[i * 3 + 1] = (byte)(g / n);
            ledRgb[i * 3 + 2] = (byte)(b / n);
        }
    }

    /// <summary>Expected tap bytes straight from a rendered canvas.</summary>
    public static void TapsFromCanvas(LedTapLayout layout, CanvasBuffer canvas, Span<byte> tapRgb)
    {
        var px = canvas.Pixels;
        for (var t = 0; t < layout.TotalTaps; t++)
        {
            var o = (layout.TapY[t] * canvas.Width + layout.TapX[t]) * 3;
            tapRgb[t * 3] = px[o];
            tapRgb[t * 3 + 1] = px[o + 1];
            tapRgb[t * 3 + 2] = px[o + 2];
        }
    }

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
    public static string? Rewrite(string source, int windowFrames)
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
    /// uniforms. Comments are ignored; the shared prelude is skipped so only the effect's own text counts,
    /// apart from texture and discard, which are checked everywhere.
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

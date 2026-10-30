using System;
using System.Linq;
using System.Text.RegularExpressions;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// Pins the brightness contract: the shared tonemap compresses by the brightest channel (keeping hue and saturation), and every
/// effect that tonemaps declares a constant outGain() that finalize() and the
/// direct tonemap call sites apply after it, clamped to range. Source-level
/// checks; they cannot see a rendered frame.
/// </summary>
public class ShaderOutputGainTests
{
    private static readonly Regex GainDef = new(@"float\s+outGain\s*\(\s*\)\s*\{\s*return\s+([0-9.]+)\s*;\s*\}", RegexOptions.CultureInvariant);
    private static readonly Regex Comment = new(@"//[^\n]*", RegexOptions.CultureInvariant);

    private static string Code(string source) => Comment.Replace(source, "");

    // Get composes prelude + newline + body, so the prefix two bodies share, cut at its last
    // newline, is the prelude.
    private static string Prelude
    {
        get
        {
            var a = ShaderLibrary.Get("simple");
            var b = ShaderLibrary.Get("corners");
            var n = 0;
            while (n < a.Length && n < b.Length && a[n] == b[n])
            {
                n++;
            }
            return a[..a.LastIndexOf('\n', n - 1)];
        }
    }

    private static string BodyOf(string key) => Code(ShaderLibrary.Get(key)[Prelude.Length..]);

    public static TheoryData<string> TonemappedKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in ShaderLibrary.AllEffectKeys.Where(k => !ShaderLibrary.IsSensorEffect(k)).Distinct())
        {
            var body = BodyOf(key);
            if (body.Contains("finalize(", StringComparison.Ordinal) || body.Contains("tonemap(", StringComparison.Ordinal))
            {
                data.Add(key);
            }
        }
        data.Add("reactiveglow");
        return data;
    }

    [Theory]
    [MemberData(nameof(TonemappedKeys))]
    public void TonemappedEffectDeclaresInRangeGain(string key)
    {
        var m = GainDef.Match(ShaderLibrary.Get(key));
        Assert.True(m.Success, $"{key} calls finalize/tonemap but defines no outGain()");
        var gain = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(gain, 0.9f, 4.0f);
        // The unity gain ShaderLibrary appends for gainless bodies is not a tonemapped effect's own.
        if (key != "reactiveglow")
        {
            Assert.NotEqual(1.0f, gain);
        }
    }

    [Fact]
    public void ScreenMirrorGlowKeepsUnityGain()
    {
        var m = GainDef.Match(ShaderLibrary.Get("reactiveglow"));
        Assert.True(m.Success);
        Assert.Equal(1.0f, float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static TheoryData<string> AllComposedKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in ShaderLibrary.AllEffectKeys.Where(k => !ShaderLibrary.IsSensorEffect(k)).Distinct())
        {
            data.Add(key);
        }
        data.Add("reactiveglow");
        return data;
    }

    // The prelude's gainOut() calls outGain(), and GLSL ES compilers (ANGLE) reject a program
    // whose defined function calls an undefined one, so every composed source must define it once.
    // Source-level only: a real compile needs a GL context.
    [Theory]
    [MemberData(nameof(AllComposedKeys))]
    public void ComposedSourceDefinesOutGainExactlyOnce(string key)
    {
        Assert.Single(GainDef.Matches(Code(ShaderLibrary.Get(key))));
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("corners")]
    [InlineData("gradientlinear")]
    public void UntonemappedEffectsGetUnityGain(string key)
    {
        var m = GainDef.Match(ShaderLibrary.Get(key));
        Assert.True(m.Success);
        Assert.Equal(1.0f, float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void GainIsAppliedAfterTonemapAndClamped()
    {
        var prelude = Code(Prelude);
        // Color-keeping: one divisor from the brightest channel, applied to all three.
        Assert.Contains("return p / (1.0 + max(max(p.r, p.g), p.b));", prelude, StringComparison.Ordinal);
        Assert.DoesNotContain("c / (1.0 + c)", prelude, StringComparison.Ordinal);
        Assert.Contains("return clamp(c * outGain(), 0.0, 1.0);", prelude, StringComparison.Ordinal);
        Assert.Contains("return gainOut(tonemap(max(blended, 0.0)));", prelude, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("breathing")]
    [InlineData("clockfill")]
    [InlineData("aurora")]
    [InlineData("ball")]
    [InlineData("sweepbrush")]
    public void DirectTonemapCallSitesApplyGain(string key)
    {
        var body = BodyOf(key);
        Assert.Matches(new Regex(@"gainOut\(tonemap\("), body);
        Assert.DoesNotMatch(new Regex(@"(?<!gainOut\()tonemap\("), body);
    }
}

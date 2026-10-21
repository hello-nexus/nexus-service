using System;
using Nexus.Service.Lighting.Bench;
using Nexus.Service.Lighting.Engine;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Bench;

public class LedSpaceCoreTests
{
    private static CanvasBuffer Noise(int seed)
    {
        var c = new CanvasBuffer(160, 90);
        var rng = new Random(seed);
        for (var y = 0; y < 90; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                c.SetPixel(x, y, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            }
        }
        return c;
    }

    [Theory]
    [InlineData(280, 200, 440, 120, 0)]
    [InlineData(450, 260, 110, 30, 0)]
    [InlineData(280, 200, 440, 120, 33)]
    [InlineData(10, 5, 980, 590, 90)]
    public void Taps_resolve_to_the_engine_sample(float x, float y, float w, float h, int rot)
    {
        var (u, v) = LedSpaceCore.StrimerMap();
        var dev = new DeviceFrame(0, "d", u.Length, x, y, w, h, rot) { LedU = u, LedV = v };
        var canvas = Noise(7);
        using var engine = new LightingEngine();
        engine.SampleForBench(new[] { dev }, canvas);
        dev.Publish();
        var want = dev.LedBytes.ToArray();

        var layout = LedSpaceCore.BuildTaps(dev, 160, 90);
        var taps = new byte[layout.TotalTaps * 3];
        LedSpaceCore.TapsFromCanvas(layout, canvas, taps);
        var got = new byte[u.Length * 3];
        LedSpaceCore.Resolve(layout, taps, got);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Rewrite_replaces_coordinate_and_clock_and_wraps_main()
    {
        const string src = "#version 330 core\nout vec4 fragColor;\nuniform float u_time;\nvoid main() { vec2 p = gl_FragCoord.xy; fragColor = vec4(p, u_time, 1.0); }\n";
        var s = LedSpaceCore.Rewrite(src, 30);
        Assert.NotNull(s);
        Assert.DoesNotContain("uniform float u_time", s);
        Assert.Contains("void ledInnerMain()", s);
        Assert.Contains("ledFC.xy", s);
        Assert.Equal(src.Split('\n').Length - 1, s!.Split('\n').Length - 1 - 7 + 0);
    }

    [Fact]
    public void Rewrite_refuses_a_source_without_main()
    {
        Assert.Null(LedSpaceCore.Rewrite("#version 330 core\nout vec4 fragColor;\nuniform float u_time;\n", 30));
    }

    [Theory]
    [InlineData("void main() { float a = dFdx(x); }", "derivative")]
    [InlineData("uniform sampler2D t; void main() {}", "texture")]
    [InlineData("void main() { if (a) discard; }", "discard")]
    public void Flags_name_canvas_dependent_constructs(string body, string flag)
    {
        Assert.Contains(flag, LedSpaceCore.CanvasDependencyFlags(body));
    }

    [Fact]
    public void Flags_ignore_comments()
    {
        Assert.Empty(LedSpaceCore.CanvasDependencyFlags("// dFdx discard sampler\nvoid main() {}"));
    }
}

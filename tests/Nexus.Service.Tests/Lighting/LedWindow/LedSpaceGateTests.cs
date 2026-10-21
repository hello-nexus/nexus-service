using System;
using System.IO;
using System.Linq;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.LedWindow;

[Collection("LedSpaceGate")]
public class LedSpaceGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ledgate-" + Guid.NewGuid().ToString("N"));

    public LedSpaceGateTests()
    {
        LedSpaceGate.DiskOverride = new DiskBlobCache(_dir, 1024 * 1024, () => true);
        Assert.True(LedSpaceGate.DiskOverride.WaitReady(TimeSpan.FromSeconds(10)));
    }

    public void Dispose()
    {
        LedSpaceGate.DiskOverride = null;
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Key_changes_with_every_input_and_is_stable()
    {
        var k = LedSpaceGate.Key("src", "NVIDIA RTX 3070 D3D11-32.0.15.9186", "ANGLE 2.1 hash a");
        Assert.Equal(k, LedSpaceGate.Key("src", "NVIDIA RTX 3070 D3D11-32.0.15.9186", "ANGLE 2.1 hash a"));
        Assert.NotEqual(k, LedSpaceGate.Key("src2", "NVIDIA RTX 3070 D3D11-32.0.15.9186", "ANGLE 2.1 hash a"));
        Assert.NotEqual(k, LedSpaceGate.Key("src", "NVIDIA RTX 3070 D3D11-32.0.15.9999", "ANGLE 2.1 hash a"));
        Assert.NotEqual(k, LedSpaceGate.Key("src", "NVIDIA RTX 3080 D3D11-32.0.15.9186", "ANGLE 2.1 hash a"));
        Assert.NotEqual(k, LedSpaceGate.Key("src", "NVIDIA RTX 3070 D3D11-32.0.15.9186", "ANGLE 2.1 hash b"));
        Assert.True(DiskBlobCache.IsCacheName(k));
    }

    [Fact]
    public void A_runtime_rejection_is_remembered_and_read_back_for_that_key_only()
    {
        using var gpu = new GpuContext(160, 90);
        using var fx = new ShaderEffect("probe-a", gpu, "void main() {}");
        var key = LedSpaceGate.Key(fx.FragSource, gpu.Renderer, gpu.GlVersion);
        Assert.Null(LedSpaceGate.ReadCached(LedSpaceGate.DiskOverride!, key));
        LedSpaceGate.Reject(fx, "test mismatch");
        Assert.Equal(LedVerdict.Rejected, LedSpaceGate.Peek(key));
        // The write is queued; wait for it to land.
        var end = Environment.TickCount64 + 5000;
        LedVerdict? read = null;
        while (read is null && Environment.TickCount64 < end)
        {
            read = LedSpaceGate.ReadCached(LedSpaceGate.DiskOverride!, key);
        }
        Assert.Equal(LedVerdict.Rejected, read);
        Assert.Null(LedSpaceGate.ReadCached(LedSpaceGate.DiskOverride!, LedSpaceGate.Key("other", gpu.Renderer, gpu.GlVersion)));
    }

    [Fact]
    public void Every_built_in_shader_rewrites_and_none_reads_neighbouring_pixels()
    {
        foreach (var key in ShaderLibrary.AllEffectKeys)
        {
            var src = ShaderLibrary.Get(key);
            Assert.NotNull(LedWindowShader.Rewrite(src));
            var blockers = LedWindowShader.CanvasDependencyFlags(src).Where(static f => f is "derivative" or "texture" or "discard").ToList();
            Assert.True(blockers.Count == 0, $"{key}: {string.Join(',', blockers)}");
        }
    }

    [Fact]
    public void Rewrite_keeps_line_count_for_the_authored_text()
    {
        var src = ShaderLibrary.Get("plasma");
        var rewritten = LedWindowShader.Rewrite(src)!;
        Assert.Contains("ledFC", rewritten);
        Assert.DoesNotContain("gl_FragCoord.xy / u_resolution", rewritten);
        Assert.StartsWith(src.Split('\n')[0], rewritten);
    }
}

[CollectionDefinition("LedSpaceGate", DisableParallelization = true)]
public class LedSpaceGateCollection;

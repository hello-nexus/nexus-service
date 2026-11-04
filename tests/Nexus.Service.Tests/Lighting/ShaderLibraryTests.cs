using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Coverage for the embedded-shader registry. The registry is populated via
/// MSBuild &lt;EmbeddedResource Include="...\Shaders\**\*.frag"/&gt;, so it's easy
/// for a new effect's .frag to land in source while a key isn't wired into
/// AllEffectKeys, or for AllEffectKeys to list a key whose .frag is missing.
/// Both cases must fail loud here, not at first user click.
/// </summary>
public class ShaderLibraryTests
{
    [Fact]
    public void AllEffectKeys_Are_Loadable()
    {
        // Each registered key must concatenate prelude + body without throwing.
        // ShaderLibrary.Get is a strict resource lookup, so a missing .frag
        // surfaces as FileNotFoundException straight from the manifest stream.
        foreach (var key in ShaderLibrary.AllEffectKeys)
        {
            var src = ShaderLibrary.Get(key);
            Assert.False(string.IsNullOrWhiteSpace(src), $"shader '{key}' returned empty source");
            Assert.Contains("void main", src);
        }
    }

    [Theory]
    [InlineData("prismwave")]
    [InlineData("crystaltunnel")]
    [InlineData("ribbonflow")]
    [InlineData("ringtunnel")]
    [InlineData("vortextunnel")]
    [InlineData("helixtunnel")]
    [InlineData("boxtunnel")]
    [InlineData("meshgradient")]
    [InlineData("tide")]
    [InlineData("vapor")]
    [InlineData("satinflow")]
    [InlineData("ridgeline")]
    [InlineData("chevron")]
    [InlineData("terrace")]
    [InlineData("harlequin")]
    [InlineData("mosaic")]
    [InlineData("sharplines")]
    [InlineData("breathing")]
    [InlineData("clockfill")]
    [InlineData("spectrumaurora")]
    [InlineData("neonwaveform")]
    [InlineData("liquidbeat")]
    [InlineData("beatburst")]
    [InlineData("lavaribbons")]
    [InlineData("lavahaze")]
    [InlineData("constellation")]
    [InlineData("cybertunnel")]
    [InlineData("hyperspace")]
    [InlineData("synthwave")]
    [InlineData("retropetals")]
    [InlineData("contourbands")]
    [InlineData("sweepbrush")]
    [InlineData("sweepcomet")]
    [InlineData("sweepliquid")]
    [InlineData("sweepcycle")]
    public void NewShaders_Are_Registered(string key)
    {
        Assert.Contains(key, ShaderLibrary.AllEffectKeys);
        var src = ShaderLibrary.Get(key);
        // Spot-check: every shader uses the shared finalize() post-process so
        // the user's hue / saturation / contrast sliders actually do anything.
        Assert.Contains("finalize(", src);
    }

    [Theory]
    [InlineData("spectrumaurora")]
    [InlineData("neonwaveform")]
    [InlineData("liquidbeat")]
    [InlineData("beatburst")]
    [InlineData("lavaribbons")]
    [InlineData("lavahaze")]
    public void FullscreenAudioShaders_Are_AudioEffects(string key)
    {
        // A key missing here renders fine but never gets capture started, so it
        // animates its idle form while music plays.
        Assert.True(ShaderLibrary.IsAudioEffect(key));
        Assert.Contains("u_audio", ShaderLibrary.Get(key));
    }

    [Fact]
    public void SourceTag_FollowsTheShaderSource()
    {
        // The tag is part of the thumbnail ETag: stable for one source, distinct
        // across sources, and shared by keys that alias one .frag.
        Assert.Equal(ShaderLibrary.SourceTag("sharplines"), ShaderLibrary.SourceTag("sharplines"));
        Assert.NotEqual(ShaderLibrary.SourceTag("sharplines"), ShaderLibrary.SourceTag("sweepbrush"));
    }

    [Fact]
    public void SweepKeys_MatchTheRegisteredSet()
    {
        // The sweep list is duplicated into AllEffectKeys (the shader-fetch and
        // thumbnail gate); a key in one and not the other renders nowhere.
        foreach (var key in ShaderLibrary.SweepEffectKeys)
        {
            Assert.Contains(key, ShaderLibrary.AllEffectKeys);
            Assert.True(ShaderLibrary.IsSweepEffect(key));
        }
        Assert.False(ShaderLibrary.IsSweepEffect("plasma"));
        Assert.Equal(7, ShaderLibrary.SweepEffectKeys.Count);
        foreach (var retired in new[] { "sweeprainbow", "sweepbars", "sweepbreathing" })
        {
            Assert.DoesNotContain(retired, ShaderLibrary.AllEffectKeys);
        }
    }

    [Fact]
    public void EveryFullFrameEffect_HasFourPresetSlots_AndItsParamsResolve()
    {
        foreach (var key in ShaderLibrary.SweepEffectKeys)
        {
            Assert.True(AnimateTemplateDefaults.All.TryGetValue(key, out var bundle), key);
            Assert.Equal(AnimateTemplateDefaults.SlotCount, bundle.Slots.Count);
            Assert.True(DevicePinTracker.IsAnimateEffect(key), key);
            var specs = ShaderLibrary.Params(key);
            foreach (var slot in bundle.Slots)
            {
                Assert.All(slot!.Params.Keys, name => Assert.True(specs.ContainsKey(name), $"{key} slot param {name}"));
            }
        }
    }

    [Fact]
    public void Jellyfish_does_not_wrap_the_master_clock()
    {
        // A wrapped clock jumps every sin phase and re-rolls the noise field at each wrap.
        // The noise terms crossfade two offset copies instead.
        var src = ShaderLibrary.Get("jellyfish");
        Assert.DoesNotContain("mod(u_time", src);
        Assert.DoesNotMatch(@"mod\(\s*t\s*,\s*\d", src);
        Assert.Contains("haloCol = tintedPalette(fi * 0.14 + 0.35) * halo * seam;", src);
    }
}

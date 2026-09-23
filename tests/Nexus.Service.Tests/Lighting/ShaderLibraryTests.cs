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
    [InlineData("spectrumaurora")]
    [InlineData("neonwaveform")]
    [InlineData("liquidbeat")]
    [InlineData("beatburst")]
    [InlineData("constellation")]
    [InlineData("cybertunnel")]
    [InlineData("hyperspace")]
    [InlineData("synthwave")]
    [InlineData("retropetals")]
    [InlineData("contourbands")]
    [InlineData("sweeprainbow")]
    [InlineData("sweepbreathing")]
    [InlineData("sweepbars")]
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
        Assert.Equal(ShaderLibrary.SourceTag("sweepbars"), ShaderLibrary.SourceTag("sweepbars"));
        Assert.NotEqual(ShaderLibrary.SourceTag("sweepbars"), ShaderLibrary.SourceTag("sweepbrush"));
        Assert.Equal(ShaderLibrary.SourceTag("breathing"), ShaderLibrary.SourceTag("sweepbreathing"));
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
        Assert.False(ShaderLibrary.IsSweepEffect("rainbow"));
    }
}

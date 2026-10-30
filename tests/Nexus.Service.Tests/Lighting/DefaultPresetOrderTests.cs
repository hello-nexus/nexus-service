using Nexus.Service.Lighting;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>Pins the order of the default preset slots the 1/2/3/4 buttons show.</summary>
public class DefaultPresetOrderTests
{
    [Theory]
    [InlineData("gradientwave", 0, 0.08f, 0.75f)] // Silk: reddish first
    [InlineData("gradientwave", 1, 0f, 0f)]       // then the full rainbow
    [InlineData("sweepbrush", 0, 0.80f, 0.50f)]   // Brush: violet first
    [InlineData("sweepbrush", 1, 0f, 0f)]         // rainbow
    [InlineData("sweepbrush", 2, 0.04f, 0.75f)]   // red-orange
    [InlineData("sweepbrush", 3, 0.58f, 0.72f)]   // blue
    public void Slot_look_matches_its_position(string effect, int slot, float hue, float colorize)
    {
        var look = AnimateTemplateDefaults.Slot(effect, slot)!;
        Assert.Equal(hue, look.Hue, 3);
        Assert.Equal(colorize, look.Colorize, 3);
    }

    // Color Cycle: the orange fade (a narrow red-to-orange swing) leads and is among the fastest.
    [Theory]
    [InlineData(0, 0.15f, 60)]
    [InlineData(1, 1.00f, 50)]
    [InlineData(2, 0.20f, 35)]
    [InlineData(3, 0.25f, 60)]
    public void Color_cycle_slots_match_their_position(int slot, float span, int speed)
    {
        var look = AnimateTemplateDefaults.Slot("sweepcycle", slot)!;
        Assert.Equal(span, look.Params["u_span"], 3);
        Assert.Equal(speed, look.Speed);
        Assert.True(AnimateTemplateDefaults.Slot("sweepcycle", 0)!.Speed >= look.Speed);
    }

    [Fact]
    public void Color_cycle_phase_rate_is_pinned()
    {
        Assert.Contains("u_time * u_speed * 0.24", Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.Get("sweepcycle"));
    }
}

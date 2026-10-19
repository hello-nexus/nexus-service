using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexRandomizedEffectsTests
{
    [Theory]
    [InlineData("voice")]
    [InlineData("meteorShower")]
    [InlineData("kaleidoscope")]
    public void The_composed_package_matches_the_recorded_frame_count_interval_and_led_count(string key)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Combine(System.AppContext.BaseDirectory, "LianLiFlex", "Data", "flex-tl-extended-vectors.json")));
        JsonElement recorded = default;
        foreach (var r in doc.RootElement.GetProperty("recorded").EnumerateArray())
        {
            if (r.GetProperty("key").GetString() == key)
            {
                recorded = r;
            }
        }
        var zones = new Dictionary<string, FlexZoneLook>
        {
            ["all"] = new FlexZoneLook(key, 3, 0, 4, new List<Nexus.Service.Peripherals.Hyte.Np50.RgbColor>()),
        };
        var anim = FlexAnimationComposer.Compose(FlexFamily.TlFlex, 1, zones, false);
        Assert.Equal(recorded.GetProperty("frames").GetInt32(), anim.Frames);
        Assert.Equal(recorded.GetProperty("leds").GetInt32(), anim.Leds);
        Assert.Equal(recorded.GetProperty("intervalMs").GetDouble(), anim.IntervalMs, 6);
        Assert.Equal(anim.Frames * anim.Leds * 3, anim.Raw.Length);
    }

    [Fact]
    public void An_sl_inf_attach_flag_of_zero_changes_the_output_of_the_effects_that_read_it()
    {
        var differing = 0;
        foreach (var key in FlexSlInfEffects.Keys)
        {
            var zones = new Dictionary<string, FlexZoneLook>
            {
                ["all"] = new FlexZoneLook(key, 3, 0, 4, new List<Nexus.Service.Peripherals.Hyte.Np50.RgbColor>()),
            };
            var a = FlexAnimationComposer.Compose(FlexFamily.SlInfFlex, 1, zones, false, 1);
            var b = FlexAnimationComposer.Compose(FlexFamily.SlInfFlex, 1, zones, false, 0);
            if (!System.MemoryExtensions.SequenceEqual<byte>(a.Raw, b.Raw))
            {
                differing++;
            }
        }
        Assert.InRange(differing, 1, FlexSlInfEffects.Keys.Count - 1);
    }
}

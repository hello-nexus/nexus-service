using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralSensorBuilderTests
{
    [Fact]
    public void AppendLevelSense_emits_the_four_angles_under_the_gpu()
    {
        var result = new List<HardwareSensor>();

        AstralSensorBuilder.AppendLevelSense("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", 0.5f, -1.25f, 0.75f, -0.25f, result);

        Assert.Collection(result,
            x =>
            {
                Assert.Equal("/gpu-nvidia/0/astral/level/x", x.Id);
                Assert.Equal("GPU Level Sense X", x.Name);
                Assert.Equal("Angle", x.Type);
                Assert.Equal("°", x.Units);
                Assert.Equal(0.5f, x.Value);
                Assert.Equal($"{0.5f:F2}°", x.Formatted);
                Assert.Equal("/gpu-nvidia/0", x.Parent.Id);
            },
            y =>
            {
                Assert.Equal("GPU Level Sense Y", y.Name);
                Assert.Equal($"{-1.25f:F2}°", y.Formatted);
            },
            sag =>
            {
                Assert.Equal("/gpu-nvidia/0/astral/level/sag", sag.Id);
                Assert.Equal("GPU Level Sense Sag", sag.Name);
                Assert.Equal(0.75f, sag.Value);
            },
            lean =>
            {
                Assert.Equal("/gpu-nvidia/0/astral/level/lean", lean.Id);
                Assert.Equal("GPU Level Sense Lean", lean.Name);
                Assert.Equal(-0.25f, lean.Value);
            });
    }

    [Fact]
    public void AppendLevelSense_shows_noise_that_rounds_to_zero_without_a_minus_sign()
    {
        var result = new List<HardwareSensor>();

        AstralSensorBuilder.AppendLevelSense("/gpu-nvidia/0", "GPU", -0.004f, -0.006f, 0f, 0f, result);

        Assert.Equal($"{0f:F2}°", result[0].Formatted);
        Assert.Equal(-0.004f, result[0].Value);
        Assert.Equal($"{-0.006f:F2}°", result[1].Formatted);
    }
}

using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GpuBackendTests
{
    [Theory]
    [InlineData(null, true, "angle")]
    [InlineData("", true, "angle")]
    [InlineData("angle", true, "angle")]
    [InlineData("ANGLE", true, "angle")]
    [InlineData("wgl", true, "wgl")]
    [InlineData("glfw", true, "angle")]
    [InlineData("nonsense", true, "angle")]
    [InlineData(null, false, "wgl")]
    [InlineData("angle", false, "wgl")]
    [InlineData("glfw", false, "wgl")]
    public void Resolve_honors_explicit_fallbacks_and_defaults_to_angle_only_where_it_ships(
        string? requested, bool angleAvailable, string expected)
    {
        Assert.Equal(expected, GpuBackend.Resolve(requested, angleAvailable));
    }

    [Theory]
    [InlineData("software", true)]
    [InlineData(" Software ", true)]
    [InlineData("auto", false)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSoftwareChoice_matches_only_the_software_value(string? renderGpu, bool expected)
    {
        Assert.Equal(expected, GpuBackend.IsSoftwareChoice(renderGpu));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("angle", true, true)]
    [InlineData("wgl", true, false)]
    [InlineData("glfw", true, true)]
    [InlineData(null, false, false)]
    [InlineData("angle", false, false)]
    public void SoftwareRenderingAvailable_needs_the_angle_backend(
        string? backend, bool angleAvailable, bool expected)
    {
        Assert.Equal(expected, GpuBackend.SoftwareRenderingAvailable(backend, angleAvailable));
    }

    [Theory]
    [InlineData("software")]
    [InlineData("Software")]
    public void Choose_resolves_software_to_warp_even_with_hardware_present(string choice)
    {
        var adapters = new[] { new Nexus.Service.Sensors.GpuAdapterLuids.Adapter("0:1", "Discrete GPU", 0x10DE, 8192) };
        var target = AngleAdapterSelector.Choose(adapters, choice, null);
        Assert.True(target is { Warp: true });
        Assert.True(AngleAdapterSelector.Choose(System.Array.Empty<Nexus.Service.Sensors.GpuAdapterLuids.Adapter>(), choice, null) is { Warp: true });
    }
}

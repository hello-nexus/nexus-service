using System.Collections.Generic;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class AngleAdapterSelectorTests
{
    private static GpuAdapterLuids.Adapter Intel => new("0:100", "Intel(R) Graphics", 0x8086, 128);
    private static GpuAdapterLuids.Adapter Nvidia => new("0:200", "NVIDIA GeForce RTX 5070", 0x10DE, 12000);
    private static GpuAdapterLuids.Adapter Basic => new("0:300", "Microsoft Basic Render Driver", 0x1414, 0);

    private static string? Luid(AngleTarget? t) => t?.Luid;

    [Theory]
    [InlineData("auto", "0:100")]
    [InlineData("", "0:100")]
    [InlineData(null, "0:100")]
    [InlineData("Intel(R) Graphics", "0:100")]
    [InlineData("NVIDIA GeForce RTX 5070", "0:200")]
    [InlineData("nvidia geforce rtx 5070", "0:200")]
    [InlineData("Some Card No Longer Installed", "0:200")]
    public void Render_gpu_setting_maps_to_an_adapter(string? setting, string expected)
    {
        var picked = AngleAdapterSelector.Choose(new[] { Intel, Nvidia }, setting, null);

        Assert.Equal(expected, Luid(picked));
        Assert.False(picked!.Value.Warp);
    }

    [Theory]
    [InlineData(AngleAdapterSelector.ClassIntegrated, "0:100")]
    [InlineData(AngleAdapterSelector.ClassDiscrete, "0:200")]
    public void Class_wins_over_the_setting_and_picks_least_or_most_memory(int cls, string expected)
    {
        var picked = AngleAdapterSelector.Choose(new[] { Nvidia, Intel }, "NVIDIA GeForce RTX 5070", cls);

        Assert.Equal(expected, Luid(picked));
    }

    [Fact]
    public void Single_adapter_serves_both_classes()
    {
        var only = new[] { Nvidia };

        Assert.Equal("0:200", Luid(AngleAdapterSelector.Choose(only, "auto", AngleAdapterSelector.ClassIntegrated)));
        Assert.Equal("0:200", Luid(AngleAdapterSelector.Choose(only, "auto", AngleAdapterSelector.ClassDiscrete)));
    }

    [Fact]
    public void Microsoft_adapters_are_never_a_target()
    {
        Assert.Equal("0:100", Luid(AngleAdapterSelector.Choose(new[] { Basic, Intel }, "auto", AngleAdapterSelector.ClassDiscrete)));
    }

    [Fact]
    public void No_hardware_adapter_gives_null_so_the_caller_can_fall_back_to_warp()
    {
        Assert.Null(AngleAdapterSelector.Choose(new[] { Basic }, "auto", null));
        Assert.Null(AngleAdapterSelector.Choose(new List<GpuAdapterLuids.Adapter>(), "auto", null));
    }

    [Theory]
    [InlineData("0:100", true, 0, 100u)]
    [InlineData("-5:4294967295", true, -5, 4294967295u)]
    [InlineData("100", false, 0, 0u)]
    [InlineData("a:b", false, 0, 0u)]
    [InlineData(null, false, 0, 0u)]
    public void Luid_text_parses_to_the_two_egl_attributes(string? text, bool ok, int high, uint low)
    {
        Assert.Equal(ok, AngleAdapterSelector.TryParseLuid(text, out var h, out var l));
        Assert.Equal(high, h);
        Assert.Equal(low, l);
    }
}

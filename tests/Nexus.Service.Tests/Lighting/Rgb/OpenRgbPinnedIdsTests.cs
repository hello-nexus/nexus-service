using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class OpenRgbPinnedIdsTests
{
    private static RgbDevice Gpu(int index) => new() { Index = index, Name = "NVIDIA GeForce RTX 5080 FE", LedCount = 3 };

    [Fact]
    public void First_sight_pins_the_id_the_device_has_now()
    {
        var gpu = Gpu(2);
        var added = OpenRgbPinnedIds.Assign(new[] { new RgbDevice { Index = 0, Name = "Mouse", Serial = "S1" }, gpu }, new Dictionary<string, string>());

        Assert.Equal("openrgb-2", gpu.StableId);
        Assert.Equal(new KeyValuePair<string, string>("NVIDIA GeForce RTX 5080 FE#0", "openrgb-2"), Assert.Single(added));
    }

    [Fact]
    public void A_pinned_device_keeps_its_id_when_its_position_moves()
    {
        var pins = new Dictionary<string, string> { ["NVIDIA GeForce RTX 5080 FE#0"] = "openrgb-2" };
        var gpu = Gpu(1);

        var added = OpenRgbPinnedIds.Assign(new[] { gpu }, pins);

        Assert.Empty(added);
        Assert.Equal("openrgb-2", gpu.StableId);
    }

    [Fact]
    public void A_new_device_never_takes_a_number_another_pin_holds()
    {
        var pins = new Dictionary<string, string> { ["NVIDIA GeForce RTX 5080 FE#0"] = "openrgb-1" };
        var gpu = Gpu(3);
        var other = new RgbDevice { Index = 1, Name = "Some Strip", LedCount = 10 };

        var added = OpenRgbPinnedIds.Assign(new[] { other, gpu }, pins);

        Assert.Equal("openrgb-1", gpu.StableId);
        Assert.Equal("openrgb-2", other.StableId);
        Assert.Equal("openrgb-2", Assert.Single(added).Value);
    }

    [Fact]
    public void Identical_devices_pin_by_ordinal()
    {
        var a = Gpu(1);
        var b = Gpu(2);

        OpenRgbPinnedIds.Assign(new[] { a, b }, new Dictionary<string, string>());

        Assert.Equal("openrgb-1", a.StableId);
        Assert.Equal("openrgb-2", b.StableId);
    }
}

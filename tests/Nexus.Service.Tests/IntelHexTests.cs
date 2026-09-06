using Nexus.Service.Devices.Firmware;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Service.Tests;

public class IntelHexTests
{
    private readonly ITestOutputHelper _out;
    public IntelHexTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Parses_a_simple_data_record()
    {
        // Canonical Intel HEX example line: 16 data bytes at 0x0100, checksum 0x40.
        const string hex = ":10010000214601360121470136007EFE09D2190140\n:00000001FF\n";
        var img = IntelHex.Parse(hex);
        Assert.Equal(0x0100u, img.BaseAddress);
        Assert.Equal(16, img.Data.Length);
        Assert.Equal(0x21, img.Data[0]);
    }

    [Fact]
    public void Applies_extended_linear_address_and_fills_gaps_with_0xFF()
    {
        // ELA sets upper 16 bits to 0x0800; two data records with a gap between.
        const string hex =
            ":020000040800F2\n" +              // upper = 0x0800
            ":04C0000000A000207C\n" +          // 4 bytes at 0x0800C000
            ":040400000102030EE4\n" +          // 4 bytes at 0x08000400  -> lower min
            ":00000001FF\n";
        var img = IntelHex.Parse(hex);
        Assert.Equal(0x08000400u, img.BaseAddress);
        // Gap between 0x08000404 and 0x0800C000 is 0xFF-filled.
        Assert.Equal(0xFF, img.Data[10]);
    }

    [Fact]
    public void Rejects_a_bad_checksum()
    {
        Assert.ThrowsAny<System.IO.InvalidDataException>(
            () => IntelHex.Parse(":10010000214601360121470136007EFE09D2190140A8\n"));
    }

    [Theory]
    [InlineData("cnvs-v1", "1.0.2.2")]
    [InlineData("q60", "2.0.9.1")]
    [InlineData("np50", "2.0.5.1")]
    [InlineData("fan-hub", "1.0.1.1")]
    [InlineData("y70-infinite", "1.0.3.1")]
    public void Bundled_images_parse_and_target_the_app_base(string deviceId, string version)
    {
        var catalog = new BundledFirmwareCatalog();
        if (catalog.DeviceIds.Count == 0)
        {
            // A public clone builds without the vendor images (they live outside
            // the repository); there is nothing to parse, and the build that
            // ships is gated on their presence at publish time.
            _out.WriteLine("no bundled firmware in this build; nothing to check");
            return;
        }
        using var stream = catalog.OpenFirmware(deviceId, version);
        Assert.NotNull(stream);
        var img = IntelHex.Parse(stream!);

        // Every in-scope HYTE STM32 image flashes at the app base 0x0800C000.
        Assert.Equal(0x0800C000u, img.BaseAddress);

        // Surface the content range so we know whether it extends past the
        // boot-flag sector at 0x0801FFF0 (decides if the flag-erase shim is
        // needed before flashing). Logged, not asserted - it's informational.
        _out.WriteLine($"{deviceId}/{version}: 0x{img.BaseAddress:X8}..0x{img.EndAddress:X8} " +
                       $"({img.Data.Length} bytes); pastFlag={(img.EndAddress > 0x0801FFF0)}");
    }
}

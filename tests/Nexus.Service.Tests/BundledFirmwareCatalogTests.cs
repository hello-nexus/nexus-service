using System;
using System.IO;
using System.Linq;
using Nexus.Service.Devices.Firmware;
using Xunit;

namespace Nexus.Service.Tests;

// The real firmware images live outside this repository, so the catalog is
// exercised against fixture images embedded in this test assembly under the
// same firmware/<deviceId>/<version>.hex logical names the service build uses.
public class BundledFirmwareCatalogTests
{
    private readonly BundledFirmwareCatalog _catalog = new(typeof(BundledFirmwareCatalogTests).Assembly);

    [Fact]
    public void Scans_the_embedded_firmware_for_every_bundled_device()
    {
        Assert.Equal(new[] { "np50", "q60" }, _catalog.DeviceIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void GetLatestVersion_picks_the_newest_bundled_version()
    {
        Assert.Equal("2.0.5.1", _catalog.GetLatestVersion("np50"));
        Assert.Equal("2.0.9.1", _catalog.GetLatestVersion("q60"));
        Assert.Equal(new[] { "2.0.5.1", "2.0.3.1" }, _catalog.GetAvailableVersions("np50"));
    }

    [Fact]
    public void GetLatestVersion_is_empty_for_unbundled_devices()
    {
        // The bare handler ids ("y70" / "qseries") have no bundle - they're the
        // "variant not yet identified" sentinels.
        Assert.Equal("", _catalog.GetLatestVersion("y70"));
        Assert.Equal("", _catalog.GetLatestVersion("qseries"));
        Assert.Equal("", _catalog.GetLatestVersion("cnvs"));
        Assert.Equal("", _catalog.GetLatestVersion(""));
    }

    [Fact]
    public void OpenFirmware_returns_a_readable_stream_for_a_bundled_image()
    {
        using var stream = _catalog.OpenFirmware("np50", "2.0.5.1");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var firstLine = reader.ReadLine();
        // Intel HEX records start with ':'.
        Assert.StartsWith(":", firstLine);
    }

    [Fact]
    public void OpenFirmware_returns_null_for_a_missing_version()
    {
        Assert.Null(_catalog.OpenFirmware("np50", "9.9.9.9"));
    }

    [Theory]
    [InlineData("2.0.5.1", "2.0.0.1", true)]
    [InlineData("2.0.5.1", "1.9.9.9", true)]
    [InlineData("2.0.5.1", "2.0.5.1", false)]
    [InlineData("2.0.0.1", "2.0.5.1", false)]
    [InlineData("2.0.5.1", "", false)]
    [InlineData("", "2.0.5.1", false)]
    public void IsNewer_compares_dotted_versions(string available, string current, bool expected)
    {
        Assert.Equal(expected, BundledFirmwareCatalog.IsNewer(available, current));
    }
}

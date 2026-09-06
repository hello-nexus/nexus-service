using System.IO;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// detector-map.json parsing. The file is written by the bundled daemon, so
/// anything unreadable must degrade to "no mapping" rather than throw - the
/// caller then falls back to the device name.
/// </summary>
public class OpenRgbDetectorMapTests
{
    [Fact]
    public void Parses_device_to_detector_entries()
    {
        var map = OpenRgbDetectorMap.Parse("""
            {"version": 1, "devices": {"Corsair Vengeance RGB DDR5": "Corsair DRAM", "Corsair K55 RGB": "Corsair K55 RGB"}}
            """);

        Assert.Equal(2, map.Count);
        Assert.Equal("Corsair DRAM", map["Corsair Vengeance RGB DDR5"]);
        Assert.Equal("Corsair K55 RGB", map["Corsair K55 RGB"]);
    }

    [Theory]
    [InlineData("""{"version": 1}""")]
    [InlineData("""{"version": 1, "devices": []}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Malformed_documents_parse_to_an_empty_map(string json)
    {
        Assert.Empty(OpenRgbDetectorMap.Parse(json));
    }

    [Fact]
    public void Non_string_and_empty_values_are_dropped()
    {
        var map = OpenRgbDetectorMap.Parse("""
            {"devices": {"A": 7, "B": "", "C": null, "D": "Real Detector"}}
            """);

        Assert.Equal("Real Detector", Assert.Single(map).Value);
    }

    [Fact]
    public void Load_returns_empty_for_an_absent_or_unreadable_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Empty(OpenRgbDetectorMap.Load(dir));
            File.WriteAllText(Path.Combine(dir, "detector-map.json"), "{ not json");
            Assert.Empty(OpenRgbDetectorMap.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_falls_back_to_the_device_name()
    {
        Assert.Equal("Corsair K70 RGB", OpenRgbDetectorMap.Resolve(null, "Corsair K70 RGB"));
        Assert.Equal("Corsair K70 RGB", OpenRgbDetectorMap.Resolve(OpenRgbDetectorMap.Parse("{}"), "Corsair K70 RGB"));
    }
}

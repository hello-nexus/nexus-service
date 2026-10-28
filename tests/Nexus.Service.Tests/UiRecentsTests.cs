using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests;

public class UiRecentsTests
{
    [Theory]
    [InlineData("#FF8800", true)]
    [InlineData("#ff8800", true)]
    [InlineData("FF8800", false)]
    [InlineData("#FF880", false)]
    [InlineData("#FF88000", false)]
    [InlineData("#FF88ZZ", false)]
    public void Recent_colors_accept_only_seven_character_hex(string value, bool valid)
    {
        Assert.Equal(valid ? 1 : 0, UiRecents.SanitizeColors(new[] { value }).Count);
    }

    [Fact]
    public void Recent_lists_survive_the_persisted_settings_round_trip()
    {
        var doc = new NexusSettings();
        doc.Ui.RecentStaticColors = new List<string> { "#FF8800" };
        doc.Ui.RecentAnimations = new List<string> { "fire", "custom:abc" };
        var json = JsonSerializer.Serialize(doc, PersistenceJsonContext.Default.NexusSettings);
        var back = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;
        Assert.Equal(new[] { "#FF8800" }, back.Ui.RecentStaticColors);
        Assert.Equal(new[] { "fire", "custom:abc" }, back.Ui.RecentAnimations);
    }

    [Fact]
    public void Recent_lists_drop_duplicates_keeping_the_first()
    {
        Assert.Equal(new[] { "#FF8800" }, UiRecents.SanitizeColors(new[] { "#FF8800", "#ff8800" }));
        Assert.Equal(new[] { "a", "b" }, UiRecents.SanitizeKeys(new[] { "a", "b", "a" }));
    }

    [Fact]
    public void Keys_with_control_characters_are_dropped()
    {
        Assert.Equal(new[] { "ok" }, UiRecents.SanitizeKeys(new[] { "bad\nkey", "ok" }));
    }
}

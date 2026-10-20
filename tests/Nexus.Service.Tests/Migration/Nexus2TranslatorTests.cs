using System.Text.Json;
using Nexus.Service.Migration;
using Nexus.Service.Models.Displays;
using Xunit;

namespace Nexus.Service.Tests.Migration;

public sealed class Nexus2TranslatorTests
{
    [Theory]
    [InlineData("portrait", DisplayOrientations.Portrait)]
    [InlineData("flipped", DisplayOrientations.PortraitFlipped)]
    public void TranslateRotation_MapsKnownValues(string n2Value, string expected)
    {
        using var doc = JsonDocument.Parse($$"""{ "q60-rotation": "{{n2Value}}" }""");
        Assert.Equal(expected, Nexus2Translator.TranslateRotation(doc.RootElement));
    }

    [Fact]
    public void TranslateRotation_MissingKeyIsNull()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Null(Nexus2Translator.TranslateRotation(doc.RootElement));
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("PT-BR", "pt-BR")]
    [InlineData("CS", "cs")]
    public void TranslateLanguage_MapsSupportedLocales(string n2Value, string expected)
    {
        using var doc = JsonDocument.Parse($$"""{ "settings": { "general": { "language": "{{n2Value}}" } } }""");
        Assert.Equal(expected, Nexus2Translator.TranslateLanguage(doc.RootElement));
    }

    [Fact]
    public void TranslateLanguage_UnsupportedLocaleIsNull()
    {
        using var doc = JsonDocument.Parse("""{ "settings": { "general": { "language": "xx" } } }""");
        Assert.Null(Nexus2Translator.TranslateLanguage(doc.RootElement));
    }

    [Fact]
    public void FindActiveProfile_PicksTheOneMarkedActive()
    {
        using var doc = JsonDocument.Parse("""
        { "profiles": [
          { "id": "a", "name": "Other", "active": false },
          { "id": "b", "name": "Active One", "active": true }
        ]}
        """);
        var profile = Nexus2Translator.FindActiveProfile(doc.RootElement);
        Assert.NotNull(profile);
        Assert.Equal("Active One", profile!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void FindActiveProfile_NoneActiveReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{ "profiles": [{ "id": "a", "name": "Other", "active": false }] }""");
        Assert.Null(Nexus2Translator.FindActiveProfile(doc.RootElement));
    }
}

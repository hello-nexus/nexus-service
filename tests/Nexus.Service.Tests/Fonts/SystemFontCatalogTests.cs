using Nexus.Service.Fonts;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Fonts;

public class SystemFontCatalogTests
{
    [Fact]
    public void Clean_DedupesCaseInsensitivelyAndSorts()
    {
        var result = SystemFontCatalog.Clean(["zeta", "Alpha", "alpha", " beta ", "", null, "   "], null);

        Assert.Equal(["Alpha", "beta", "zeta"], result);
    }

    [Theory]
    [InlineData('@', "@Vertical Font")]
    [InlineData('.', ".SF Hidden")]
    public void Clean_DropsHiddenPrefix(char prefix, string hidden)
    {
        var result = SystemFontCatalog.Clean([hidden, "Visible"], prefix);

        Assert.Equal(["Visible"], result);
    }

    [Fact]
    public void Clean_KeepsPrefixedNamesWhenNoPrefixIsHidden()
    {
        Assert.Equal([".Odd", "@Odd"], SystemFontCatalog.Clean(["@Odd", ".Odd"], null));
    }

    [Fact]
    public void Clean_DropsNamesThatCannotBeStoredAsAPanelFont()
    {
        var result = SystemFontCatalog.Clean([new string('a', 129), "Bad\nName", "Ok"], null);

        Assert.Equal(["Ok"], result);
    }

    [Fact]
    public void ParseFcList_TakesTheFirstAliasPerLine()
    {
        var parsed = SystemFontCatalog.ParseFcList("DejaVu Sans,DejaVu Sans Condensed\nNoto Serif\n\n").ToList();

        Assert.Equal(["DejaVu Sans", "Noto Serif"], parsed);
        Assert.Equal(["DejaVu Sans", "Noto Serif"], SystemFontCatalog.Clean(parsed, null));
    }

    [Fact]
    public void Families_NeverThrowsAndIsSorted()
    {
        var families = SystemFontCatalog.Families;

        Assert.Equal(families.OrderBy(f => f, StringComparer.OrdinalIgnoreCase), families);
    }

    [Theory]
    [InlineData("oswald", true, "oswald")]
    [InlineData("not-a-font", true, "not-a-font")]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("system:Arial", true, "system:Arial")]
    [InlineData("system:  Helvetica Neue ", true, "system:Helvetica Neue")]
    [InlineData("system:", false, null)]
    [InlineData("system:   ", false, null)]
    [InlineData("system:a\tb", false, null)]
    public void TryNormalize_HandlesEmbeddedAndSystemValues(string input, bool accepted, string? expected)
    {
        Assert.Equal(accepted, PanelFontValue.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalize_CapsFamilyLength()
    {
        Assert.True(PanelFontValue.TryNormalize("system:" + new string('a', 128), out _));
        Assert.False(PanelFontValue.TryNormalize("system:" + new string('a', 129), out _));
    }
}

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

    [Theory]
    [InlineData("A'B")]
    [InlineData("A\"B")]
    [InlineData("A\\B")]
    [InlineData("A;B")]
    [InlineData("A{B")]
    [InlineData("A}B")]
    [InlineData("A<B")]
    [InlineData("A>B")]
    public void CssSignificantCharactersAreRejectedEverywhere(string name)
    {
        Assert.Empty(SystemFontCatalog.Clean([name], null));
        Assert.False(PanelFontValue.TryNormalize("system:" + name, out _));
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
        var families = SystemFontCatalog.Shared.Families;
#if MACOS || WINDOWS
        Assert.NotEmpty(families);
#endif

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

    [Fact]
    public void Peek_BeforeLoadNeverEnumerates()
    {
        var calls = 0;
        var catalog = new SystemFontCatalog(() => { calls++; return ["Arial"]; });

        var (families, loaded) = catalog.Peek();

        Assert.False(loaded);
        Assert.Empty(families);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Load_SetsTheFlagAndPeekThenReturnsTheListWithoutEnumeratingAgain()
    {
        var calls = 0;
        var catalog = new SystemFontCatalog(() => { calls++; return ["Arial", "Menlo"]; });

        Assert.Equal(["Arial", "Menlo"], catalog.Load());
        var (families, loaded) = catalog.Peek();

        Assert.True(loaded);
        Assert.Equal(["Arial", "Menlo"], families);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FailedFirstLoad_LeavesTheCatalogUnloaded()
    {
        var catalog = new SystemFontCatalog(() => []);

        Assert.Empty(catalog.Load());
        var (families, loaded) = catalog.Peek();

        Assert.False(loaded);
        Assert.Empty(families);
    }

    [Fact]
    public void FailedReload_KeepsTheLastGoodListForPeek()
    {
        var results = new Queue<string[]>([["Arial"], []]);
        var now = DateTime.UtcNow;
        var catalog = new SystemFontCatalog(() => results.Dequeue(), () => now);

        Assert.Equal(["Arial"], catalog.Load());
        now += TimeSpan.FromHours(1);

        Assert.Equal(["Arial"], catalog.Load());
        var (families, loaded) = catalog.Peek();

        Assert.True(loaded);
        Assert.Equal(["Arial"], families);
    }
}

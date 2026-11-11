using System;
using System.Linq;
using Nexus.Service.Common;
using Xunit;

namespace Nexus.Service.Tests.Common;

public class NaturalStringComparerTests
{
    private static string[] Sort(params string[] items) =>
        items.OrderBy(s => s, NaturalStringComparer.Instance).ToArray();

    [Fact]
    public void NumericNames_SortByValue()
    {
        var sorted = Sort("1001.mp4", "100.mp4", "10.mp4", "0.mp4", "101.mp4", "2.mp4", "1.mp4", "1000.mp4", "9.mp4");

        Assert.Equal(
            new[] { "0.mp4", "1.mp4", "2.mp4", "9.mp4", "10.mp4", "100.mp4", "101.mp4", "1000.mp4", "1001.mp4" },
            sorted);
    }

    [Fact]
    public void MixedText_SortsDigitRunsNumerically()
    {
        Assert.Equal(new[] { "img2.png", "img10.png" }, Sort("img10.png", "img2.png"));
        Assert.Equal(new[] { "a1b2", "a1b10", "a2b1" }, Sort("a2b1", "a1b10", "a1b2"));
    }

    [Fact]
    public void LeadingZeros_CompareByValueThenOrdinal()
    {
        Assert.Equal(new[] { "a2", "a003", "a10" }, Sort("a10", "a003", "a2"));
        Assert.True(NaturalStringComparer.Instance.Compare("a01", "a1") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a1", "a01") > 0);
    }

    [Fact]
    public void Letters_AreCaseInsensitive()
    {
        Assert.Equal(new[] { "apple", "Banana", "cherry" }, Sort("cherry", "Banana", "apple"));
        Assert.Equal(new[] { "IMG2", "img10" }, Sort("img10", "IMG2"));
    }

    [Fact]
    public void CaseOnlyDifference_IsStillTotalOrder()
    {
        var c = NaturalStringComparer.Instance;
        Assert.NotEqual(0, c.Compare("a", "A"));
        Assert.Equal(-c.Compare("a", "A"), c.Compare("A", "a"));
        Assert.Equal(0, c.Compare("a", "a"));
    }

    [Fact]
    public void VeryLongDigitRuns_DoNotOverflow()
    {
        var big = "x" + new string('9', 400);
        var bigger = "x1" + new string('0', 400);
        Assert.Equal(new[] { "x5", big, bigger }, Sort(bigger, big, "x5"));
    }

    [Fact]
    public void PrefixSortsBeforeLonger_AndNullsFirst()
    {
        var c = NaturalStringComparer.Instance;
        Assert.True(c.Compare("img", "img1") < 0);
        Assert.True(c.Compare(null, "a") < 0);
        Assert.True(c.Compare("a", null) > 0);
        Assert.Equal(0, c.Compare(null, null));
    }

    [Fact]
    public void FoldersGroupBeforeTheirContent()
    {
        Assert.Equal(
            new[] { "/p/a/2.png", "/p/a/10.png", "/p/b/1.png" },
            Sort("/p/b/1.png", "/p/a/10.png", "/p/a/2.png"));
    }
}

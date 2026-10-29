using System.Collections.Generic;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class WindowClassificationPreClassifyTests
{
    private struct FakeReads : WindowClassification.IWindowReads
    {
        public bool Visible, Tool, OnScreen;
        public IntPtr OwnerValue;
        public List<string> Calls;

        public bool IsVisible() { Calls.Add("vis"); return Visible; }
        public IntPtr Owner() { Calls.Add("owner"); return OwnerValue; }
        public bool IsToolWindow() { Calls.Add("style"); return Tool; }
        public bool HasOnScreenBounds() { Calls.Add("rect"); return OnScreen; }
    }

    // Every input combination: PreClassify followed by the cloak/title terms must
    // equal the original all-inputs IsCountableWindow.
    [Fact]
    public void PreClassify_MatchesIsCountableWindow_ForEveryInputCombination()
    {
        var bools = new[] { false, true };
        foreach (var vis in bools)
        foreach (var owned in bools)
        foreach (var tool in bools)
        foreach (var onScreen in bools)
        foreach (var fg in bools)
        foreach (var cloaked in bools)
        foreach (var covers in bools)
        foreach (var title in bools)
        foreach (var uipi in bools)
        {
            var owner = owned ? new IntPtr(7) : IntPtr.Zero;
            var expected = WindowClassification.IsCountableWindow(
                vis, owner, tool, cloaked, title, onScreen, covers, uipi, fg);

            var reads = new FakeReads { Visible = vis, Tool = tool, OnScreen = onScreen, OwnerValue = owner, Calls = new() };
            var pre = WindowClassification.PreClassify(ref reads, fg);
            var actual = pre switch
            {
                WindowClassification.PreResult.Rejected => false,
                WindowClassification.PreResult.Accepted => true,
                _ => WindowClassification.IsCountableWindow(vis, owner, tool, cloaked, title, onScreen, covers, uipi, fg),
            };
            if (pre == WindowClassification.PreResult.NeedsCloakAndTitle)
            {
                // Pins the contract: only a window passing all four cheap terms
                // reaches the cloak/title stage, so a future early return fails here.
                Assert.True(owner == IntPtr.Zero && onScreen && !fg && vis && !tool,
                    $"NeedsCloakAndTitle returned for vis={vis} owned={owned} tool={tool} onScreen={onScreen} fg={fg}");
            }
            Assert.True(expected == actual,
                $"vis={vis} owned={owned} tool={tool} onScreen={onScreen} fg={fg} cloaked={cloaked} covers={covers} title={title} uipi={uipi}");
        }
    }

    [Fact]
    public void PreClassify_RejectsAnInvisibleBackgroundWindowAfterTheVisibilityReadAlone()
    {
        var reads = new FakeReads { Visible = false, OnScreen = true, Calls = new() };
        Assert.Equal(WindowClassification.PreResult.Rejected, WindowClassification.PreClassify(ref reads, false));
        Assert.Equal(new[] { "vis" }, reads.Calls);
    }

    [Fact]
    public void PreClassify_ForegroundWindowNeverReadsVisibilityOrStyle()
    {
        var reads = new FakeReads { Visible = false, Tool = true, OnScreen = true, Calls = new() };
        Assert.Equal(WindowClassification.PreResult.Accepted, WindowClassification.PreClassify(ref reads, true));
        Assert.Equal(new[] { "owner", "rect" }, reads.Calls);
    }

    [Fact]
    public void PreClassify_ReadsTheRectLast()
    {
        var reads = new FakeReads { Visible = true, OnScreen = true, Calls = new() };
        Assert.Equal(WindowClassification.PreResult.NeedsCloakAndTitle, WindowClassification.PreClassify(ref reads, false));
        Assert.Equal(new[] { "vis", "owner", "style", "rect" }, reads.Calls);
    }

    [Fact]
    public void PreClassify_OwnedWindowSkipsStyleAndRect()
    {
        var reads = new FakeReads { Visible = true, OwnerValue = new IntPtr(3), OnScreen = true, Calls = new() };
        Assert.Equal(WindowClassification.PreResult.Rejected, WindowClassification.PreClassify(ref reads, false));
        Assert.Equal(new[] { "vis", "owner" }, reads.Calls);
    }
}

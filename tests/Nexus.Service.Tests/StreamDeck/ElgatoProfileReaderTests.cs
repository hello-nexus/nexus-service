using System;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Reads the synthetic fixture under Fixtures/ElgatoStore/ProfilesV3 (see build_elgato_fixture.py provenance in the task notes).</summary>
public sealed class ElgatoProfileReaderTests
{
    private static string ProfilesV3Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ElgatoStore", "ProfilesV3");

    [Fact]
    public void ReadProfiles_FindsTheOneBundle()
    {
        var profiles = ElgatoProfileReader.ReadProfiles(ProfilesV3Root);
        Assert.Single(profiles);
        Assert.Equal("testbundle", profiles[0].Id);
        Assert.Equal("Test Profile", profiles[0].Name);
        Assert.Equal("20GAA9901", profiles[0].Model);
    }

    [Fact]
    public void ReadProfiles_OnMissingRoot_ReturnsEmpty()
    {
        var profiles = ElgatoProfileReader.ReadProfiles(Path.Combine(ProfilesV3Root, "does-not-exist"));
        Assert.Empty(profiles);
    }

    [Fact]
    public void ReadBundle_ResolvesAllThreeTopPagesInOrder()
    {
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        Assert.Equal(new[] { "page-one", "page-two", "page-empty" }, profile.TopPageIds);
    }

    [Fact]
    public void ReadBundle_CaseFoldsUppercaseDirsAgainstLowercaseRefs()
    {
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        // Pages.Pages holds lowercase uuids; the on-disk dirs are UPPERCASE
        // (PAGE-ONE, PAGE-TWO, ...). Resolving them at all proves the fold.
        Assert.True(profile.PagesById.ContainsKey("page-one"));
        Assert.True(profile.PagesById.ContainsKey("page-two"));
    }

    [Fact]
    public void ReadBundle_ResolvesOpenchildFolderChainIncludingACycle()
    {
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        // folder-a and folder-b are only reachable via openchild refs, never
        // listed in Pages.Pages - resolving them proves the recursive walk.
        Assert.True(profile.PagesById.ContainsKey("folder-a"));
        Assert.True(profile.PagesById.ContainsKey("folder-b"));
    }

    [Fact]
    public void ReadBundle_EmptyPageHasNoActions()
    {
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        Assert.Empty(profile.PagesById["page-empty"].Actions);
    }

    [Fact]
    public void ReadBundle_InfersGridFromObservedMaxColRowWhenNeeded()
    {
        // 20GAA9901 is a known catalog code (5x3), but the reader still tracks
        // MaxColSeen/MaxRowSeen unconditionally for the inference fallback.
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        Assert.Equal(4, profile.MaxColSeen);
        Assert.Equal(2, profile.MaxRowSeen);
    }

    [Fact]
    public void CountKeys_CountsAcrossTopPagesAndFolders()
    {
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        // 14 (page-one, minus 1 tutorial tile) + 11 (page-two) + 2 (folder-a,
        // minus its backtoparent) + 2 (folder-b, minus its backtoparent).
        Assert.Equal(29, ElgatoProfileReader.CountKeys(profile));
    }

    [Fact]
    public void ReadPageAction_ResolvesImagePathUnderThePageDir()
    {
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        var website = profile.PagesById["page-one"].Actions[(3, 1)];
        var imagePath = website.States[0].ImagePath;
        Assert.NotNull(imagePath);
        Assert.True(File.Exists(imagePath));
        Assert.EndsWith(Path.Combine("PAGE-ONE", "Images", "website.png"), imagePath);
    }

    [Fact]
    public void ReadPageAction_ParsesRoutine2TopLevelActionsGroup()
    {
        var profile = ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;
        var multi = profile.PagesById["page-two"].Actions[(1, 0)];
        Assert.Equal("com.elgato.streamdeck.multiactions.routine2", multi.Uuid);
        Assert.NotNull(multi.ActionsGroup);
        Assert.Equal(1, multi.ActionsGroup!.Value.GetArrayLength());
    }

    [Fact]
    public void BackToParent_IsSkippable()
    {
        Assert.True(ElgatoActionTypes.IsSkippable("com.elgato.streamdeck.profile.backtoparent"));
    }

    [Fact]
    public void TutorialTiles_AreSkippable()
    {
        Assert.True(ElgatoActionTypes.IsSkippable("com.elgato.tutorial.welcome"));
    }

    [Fact]
    public void OrdinaryActions_AreNotSkippable()
    {
        Assert.False(ElgatoActionTypes.IsSkippable("com.elgato.streamdeck.system.hotkey"));
    }
}

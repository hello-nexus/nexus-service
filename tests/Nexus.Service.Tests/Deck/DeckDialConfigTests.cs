using System.Linq;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// The dial half of the DeckConfig wire contract (plans/streamdeck-dials.md
/// section 3.1): DeckConfigConverter's hand-written page IO, folder dials,
/// FitToGrid/DeepCopy carry, and the walkers that otherwise only visit slots.
/// </summary>
public sealed class DeckDialConfigTests
{
    private static DeckConfig Read(string json) => JsonSerializer.Deserialize(json, AppJsonContext.Default.DeckConfig)!;

    private static string Write(DeckConfig config) => JsonSerializer.Serialize(config, AppJsonContext.Default.DeckConfig);

    private const string FullDials =
        "[{\"label\":\"Vol\",\"color\":\"#22d3ee\",\"icon\":{\"kind\":\"lucide\",\"value\":\"Volume2\"},\"action\":{\"type\":\"volume\",\"deviceId\":\"out-1\",\"step\":3}}," +
        "{\"action\":{\"type\":\"appVolume\",\"appId\":\"spotify\",\"appName\":\"Spotify\"}}," +
        "{\"action\":{\"type\":\"monitoring\",\"category\":\"cpu\",\"sensor\":\"cpu.temp\",\"press\":\"taskManager\",\"labelText\":\"CPU\"}}," +
        "{\"stack\":[{\"action\":{\"type\":\"displayBrightness\",\"displayId\":\"d1\"}},{\"action\":{\"type\":\"micVolume\"}}]}]";

    [Fact]
    public void PageDials_RoundTripEveryActionShape()
    {
        var config = Read("{\"pages\":[{\"slots\":[],\"dials\":" + FullDials + "}]}");

        var dials = config.Pages[0].Dials!;
        Assert.Equal(4, dials.Count);
        Assert.Equal("Vol", dials[0].Label);
        Assert.Equal("#22d3ee", dials[0].Color);
        Assert.Equal("Volume2", dials[0].Icon!.Value);
        Assert.Equal("volume", dials[0].Action!.Type);
        Assert.Equal("out-1", dials[0].Action!.DeviceId);
        Assert.Equal(3, dials[0].Action!.Step);
        Assert.Equal("Spotify", dials[1].Action!.AppName);
        Assert.Equal("taskManager", dials[2].Action!.Press);
        Assert.Equal(2, dials[3].Stack!.Count);
        Assert.Equal("d1", dials[3].Stack![0].Action!.DisplayId);

        var again = Read(Write(config));
        Assert.Equal(Write(config), Write(again));
    }

    [Fact]
    public void CustomDial_RoundTripsItsFourKeyActions()
    {
        var json = "{\"pages\":[{\"slots\":[],\"dials\":[{\"action\":{\"type\":\"custom\"," +
            "\"turnRight\":{\"type\":\"system\",\"action\":{\"op\":\"volumeUp\"}}," +
            "\"turnLeft\":{\"type\":\"system\",\"action\":{\"op\":\"volumeDown\"}}," +
            "\"push\":{\"type\":\"power\",\"action\":\"lock\"}," +
            "\"touch\":{\"type\":\"hotkey\",\"keys\":\"ctrl+m\"}}}]}]}";

        var action = Read(Write(Read(json))).Pages[0].Dials![0].Action!;

        Assert.Equal("volumeUp", action.TurnRight!.SystemAction!.Op);
        Assert.Equal("volumeDown", action.TurnLeft!.SystemAction!.Op);
        Assert.Equal("lock", action.Push!.PowerAction);
        Assert.Equal("ctrl+m", action.Touch!.Keys);
    }

    [Fact]
    public void FractionalStep_IsTolerated()
    {
        var config = Read("{\"pages\":[{\"slots\":[],\"dials\":[{\"action\":{\"type\":\"volume\",\"step\":2.5}}]}]}");

        Assert.Equal(2.5, config.Pages[0].Dials![0].Action!.Step);
    }

    [Fact]
    public void PageWithoutDials_WritesNoDialsKey_AndKeepsNull()
    {
        var config = Read("{\"pages\":[{\"slots\":[{\"label\":\"a\"}]}]}");

        Assert.Null(config.Pages[0].Dials);
        Assert.DoesNotContain("dials", Write(config));
    }

    [Fact]
    public void MalformedDialEntry_BecomesAnEmptyDial_KeepingPositions()
    {
        var config = Read("{\"pages\":[{\"slots\":[],\"dials\":[{\"label\":\"a\"},\"junk\",{\"action\":5},{\"label\":\"d\"}]}]}");

        var dials = config.Pages[0].Dials!;
        Assert.Equal(4, dials.Count);
        Assert.Equal("a", dials[0].Label);
        Assert.Null(dials[1].Label);
        Assert.Null(dials[2].Action);
        Assert.Equal("d", dials[3].Label);
    }

    [Fact]
    public void MalformedFolderDialEntry_BecomesAnEmptyDial_AndNeverFailsTheLoad()
    {
        var config = Read(
            "{\"pages\":[{\"slots\":[{\"folder\":{\"slots\":[],\"dials\":[{\"label\":\"a\"},\"junk\",{\"action\":5},{\"label\":\"d\"}]}}," +
            "{\"folder\":{\"slots\":[],\"dials\":\"nope\"}}]}]}");

        var dials = config.Pages[0].Slots[0].Folder!.Dials!;
        Assert.Equal(4, dials.Count);
        Assert.Equal("a", dials[0].Label);
        Assert.Null(dials[1].Label);
        Assert.Null(dials[2].Action);
        Assert.Equal("d", dials[3].Label);
        Assert.Null(config.Pages[0].Slots[1].Folder!.Dials);
    }

    [Fact]
    public void FitToGrid_GivesEachChunkAndFolderItsOwnDialsList()
    {
        var preset = Read("{\"pages\":[{\"slots\":[{\"label\":\"0\"},{\"label\":\"1\"},{\"label\":\"2\"},{\"label\":\"3\"}]," +
            "\"dials\":[{\"label\":\"d\"}]}]}");

        var fitted = DeckConfigNavigation.FitToGrid(2, 2, preset, 1, 3, DeckTargetKind.Physical);

        Assert.Equal(2, fitted.Pages.Count);
        Assert.NotSame(fitted.Pages[0].Dials, fitted.Pages[1].Dials);
        Assert.NotSame(preset.Pages[0].Dials, fitted.Pages[0].Dials);
        Assert.Equal("d", fitted.Pages[1].Dials![0].Label);
    }

    [Fact]
    public void FolderDials_RoundTrip_AndAbsentMeansInheritPageDials()
    {
        var config = Read(
            "{\"pages\":[{\"slots\":[" +
            "{\"folder\":{\"slots\":[],\"dials\":[{\"label\":\"in\"}]}}," +
            "{\"folder\":{\"slots\":[]}}],\"dials\":[{\"label\":\"page\"}]}]}");

        var again = Read(Write(config));
        Assert.Equal("in", again.Pages[0].Slots[0].Folder!.Dials![0].Label);
        Assert.Null(again.Pages[0].Slots[1].Folder!.Dials);
        Assert.Equal("page", again.Pages[0].Dials![0].Label);
    }

    [Fact]
    public void DeepCopyConfig_CarriesPageAndFolderDials()
    {
        var config = Read("{\"pages\":[{\"slots\":[{\"folder\":{\"slots\":[],\"dials\":[{\"label\":\"in\"}]}}],\"dials\":[{\"label\":\"page\"}]}]}");

        var copy = DeckConfigNavigation.DeepCopyConfig(config);

        Assert.NotSame(config.Pages[0].Dials, copy.Pages[0].Dials);
        Assert.Equal("page", copy.Pages[0].Dials![0].Label);
        Assert.Equal("in", copy.Pages[0].Slots[0].Folder!.Dials![0].Label);
    }

    [Fact]
    public void DeckDials_FlattenYieldsStackEntries_AndAllActionsOnlyCustomOnes()
    {
        var config = Read("{\"pages\":[{\"slots\":[],\"dials\":[" +
            "{\"stack\":[{\"action\":{\"type\":\"custom\",\"push\":{\"type\":\"hotkey\",\"keys\":\"a\"}}},{\"action\":{\"type\":\"volume\"}}]}," +
            "{\"action\":{\"type\":\"custom\",\"turnLeft\":{\"type\":\"text\",\"text\":\"x\"}}}]}]}");

        var dials = config.Pages[0].Dials;

        Assert.Equal(4, DeckDials.Flatten(dials).Count());
        Assert.Equal(new[] { "hotkey", "text" }, DeckDials.AllActions(dials).Select(a => a.Type).ToArray());
    }

    [Fact]
    public void LayoutPolicy_SeesPrivilegedActionsHiddenInDials()
    {
        var plain = Read("{\"pages\":[{\"slots\":[]}]}");
        var onPage = Read("{\"pages\":[{\"slots\":[],\"dials\":[{\"action\":{\"type\":\"custom\",\"push\":{\"type\":\"hotkey\",\"keys\":\"ctrl+m\"}}}]}]}");
        var inStack = Read("{\"pages\":[{\"slots\":[],\"dials\":[{\"stack\":[{\"action\":{\"type\":\"custom\",\"touch\":{\"type\":\"openFile\",\"path\":\"/x\"}}}]}]}]}");
        var inFolder = Read("{\"pages\":[{\"slots\":[{\"folder\":{\"slots\":[],\"dials\":[{\"action\":{\"type\":\"custom\",\"turnRight\":{\"type\":\"text\",\"text\":\"t\"}}}]}}]}]}");
        var benign = Read("{\"pages\":[{\"slots\":[],\"dials\":[{\"action\":{\"type\":\"custom\",\"push\":{\"type\":\"openUrl\",\"url\":\"https://hellonexus.com\"}}}]}]}");

        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(onPage, plain));
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(inStack, plain));
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(inFolder, plain));
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(benign, plain));
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(onPage, onPage));
    }

    [Fact]
    public void PlatformKeys_FoldsKeysMacInsideDialActions()
    {
        var config = Read("{\"pages\":[{\"slots\":[],\"dials\":[{\"action\":{\"type\":\"custom\",\"push\":{\"type\":\"hotkey\",\"keys\":\"ctrl+c\",\"keysMac\":\"cmd+c\"}}}]}]}");

        DeckPlatformKeys.ApplyHost(config, mac: true);

        var push = config.Pages[0].Dials![0].Action!.Push!;
        Assert.Equal("cmd+c", push.Keys);
        Assert.Null(push.KeysMac);
    }

    [Theory]
    [InlineData("screenshot", "Camera")]
    [InlineData("screenRecord", "Monitor")]
    public void ScreenCaptureOps_RoundTripAndGetOwnIcons(string op, string icon)
    {
        var action = JsonSerializer.Deserialize(
            "{\"type\":\"system\",\"action\":{\"op\":\"" + op + "\"}}", AppJsonContext.Default.DeckAction)!;

        Assert.Equal(op, action.SystemAction!.Op);
        Assert.Equal(icon, DeckIconDefaults.AutoIconName(action));
        Assert.Equal(DeckCategory.Open, DeckIconDefaults.Category(action));
    }
}

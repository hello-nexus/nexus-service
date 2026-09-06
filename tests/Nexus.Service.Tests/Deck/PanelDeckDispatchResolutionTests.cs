using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// POST /panel/deck/dispatch names a slot; the service resolves the stored
/// action from the panel's layout. Nothing in the request can substitute one.
/// </summary>
public sealed class PanelDeckDispatchResolutionTests
{
    private const string DeckJson =
        "{\"pages\":[" +
        "{\"slots\":[" +
        "{\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+shift+m\"}}," +
        "{\"folder\":{\"slots\":[{\"action\":{\"type\":\"text\",\"text\":\"nested\"}}]}}," +
        "{\"action\":{\"type\":\"toggle\",\"on\":{\"type\":\"openUrl\",\"url\":\"https://on\"},\"off\":{\"type\":\"openFile\",\"path\":\"C:\\\\off.exe\"}}}," +
        "{}" +
        "]}," +
        "{\"slots\":[{\"action\":{\"type\":\"power\",\"action\":\"lock\"}}]}" +
        "]}";

    private static PanelLayoutDto Layout(string widgetType = "deck")
    {
        using var doc = JsonDocument.Parse(DeckJson);
        return new PanelLayoutDto
        {
            Pages =
            {
                new PanelPageDto
                {
                    Id = "p1",
                    Widgets =
                    {
                        new PanelWidgetDto { Id = "clock", Type = "clock" },
                        new PanelWidgetDto
                        {
                            Id = "deck1",
                            Type = widgetType,
                            Config = new Dictionary<string, JsonElement> { ["deck"] = doc.RootElement.Clone() },
                        },
                    },
                },
            },
        };
    }

    private static PanelDeckDispatchBody Press(int slot, int page = 0, List<int>? folderPath = null, string? branch = null, string widgetId = "deck1")
        => new() { DeviceId = "dev", WidgetId = widgetId, Page = page, FolderPath = folderPath, Slot = slot, Branch = branch };

    [Fact]
    public void Resolves_a_root_slot_a_folder_slot_and_a_second_page()
    {
        var layout = Layout();
        Assert.Equal("ctrl+shift+m", PanelDeckRoutes.ResolveAction(layout, Press(0))?.Keys);
        Assert.Equal("nested", PanelDeckRoutes.ResolveAction(layout, Press(0, folderPath: new List<int> { 1 }))?.Text);
        Assert.Equal("lock", PanelDeckRoutes.ResolveAction(layout, Press(0, page: 1))?.PowerAction);
    }

    [Fact]
    public void A_toggle_resolves_to_the_requested_branch_or_stays_a_toggle()
    {
        var layout = Layout();
        Assert.Equal("https://on", PanelDeckRoutes.ResolveAction(layout, Press(2, branch: "on"))?.Url);
        Assert.Equal("openFile", PanelDeckRoutes.ResolveAction(layout, Press(2, branch: "off"))?.Type);
        Assert.Equal("toggle", PanelDeckRoutes.ResolveAction(layout, Press(2))?.Type);
        Assert.Equal("toggle", PanelDeckRoutes.ResolveAction(layout, Press(2, branch: "sideways"))?.Type);
    }

    [Fact]
    public void Nothing_resolves_outside_the_stored_layout()
    {
        var layout = Layout();
        Assert.Null(PanelDeckRoutes.ResolveAction(null, Press(0)));
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(0, widgetId: "missing")));
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(0, widgetId: "clock")));
        Assert.Null(PanelDeckRoutes.ResolveAction(Layout(widgetType: "clock"), Press(0)));
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(3)));                        // empty slot
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(1)));                        // a folder, not an action
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(9)));                        // out of range
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(-1)));
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(0, page: 5)));
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(0, folderPath: new List<int> { 0 }))); // slot 0 is not a folder
        Assert.Null(PanelDeckRoutes.ResolveAction(layout, Press(0, folderPath: new List<int> { 7 })));
    }
}

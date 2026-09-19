using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// A paired panel session may trigger the deck keys stored in its layout but
/// not author the ones that open a file, send a chord, type text or play an
/// audio file: those are desktop-authored (POST /panel/devices/{id} refuses
/// them from the panel lane) and panel-triggered (POST /panel/deck/dispatch).
/// </summary>
public sealed class PanelDeckPolicyRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public PanelDeckPolicyRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(_ => { });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private HttpClient PanelClient() => TestPhoneSession.CreateClient(_factory);

    private static PanelDevicePatch LayoutWith(params string[] slotJson)
    {
        using var doc = JsonDocument.Parse("{\"pages\":[{\"slots\":[" + string.Join(",", slotJson) + "]}]}");
        return new PanelDevicePatch
        {
            Layout = new PanelLayoutDto
            {
                LayoutSchemaVersion = 2,
                Surface = "phone",
                Pages =
                {
                    new PanelPageDto
                    {
                        Id = "p1",
                        Widgets =
                        {
                            new PanelWidgetDto
                            {
                                Id = "deck1",
                                Type = "deck",
                                Size = "4x4",
                                Config = new Dictionary<string, JsonElement> { ["deck"] = doc.RootElement.Clone() },
                            },
                        },
                    },
                },
            },
        };
    }

    private const string Hotkey = "{\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+shift+m\"}}";
    private const string OpenUrl = "{\"action\":{\"type\":\"openUrl\",\"url\":\"https://hellonexus.com\"}}";
    private const string Empty = "{}";

    private static async Task<string> CreateDeviceAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/panel/devices", new PanelDeviceCreateBody { DisplayName = "Phone" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var record = await res.Content.ReadFromJsonAsync<PanelDeviceRecord>();
        Assert.False(string.IsNullOrEmpty(record?.Id));
        return record!.Id;
    }

    [Fact]
    public async Task Panel_cannot_author_a_hotkey_but_desktop_can_and_the_panel_may_then_move_it()
    {
        var panel = PanelClient();
        var desktop = DesktopClient();
        var id = await CreateDeviceAsync(panel);

        var refused = await panel.PostAsJsonAsync($"/panel/devices/{id}", LayoutWith(Hotkey, Empty));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("deck_action_requires_desktop", await refused.Content.ReadAsStringAsync());

        var authored = await desktop.PostAsJsonAsync($"/panel/devices/{id}", LayoutWith(Hotkey, Empty));
        Assert.Equal(HttpStatusCode.OK, authored.StatusCode);

        var moved = await panel.PostAsJsonAsync($"/panel/devices/{id}", LayoutWith(Empty, Hotkey));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var retargeted = await panel.PostAsJsonAsync($"/panel/devices/{id}",
            LayoutWith("{\"action\":{\"type\":\"hotkey\",\"keys\":\"meta+r\"}}"));
        Assert.Equal(HttpStatusCode.Forbidden, retargeted.StatusCode);
    }

    [Fact]
    public async Task Panel_can_author_unprivileged_keys_freely()
    {
        var panel = PanelClient();
        var id = await CreateDeviceAsync(panel);

        var res = await panel.PostAsJsonAsync($"/panel/devices/{id}", LayoutWith(OpenUrl, Empty));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Dispatch_is_panel_reachable_and_names_only_stored_slots()
    {
        var panel = PanelClient();
        var id = await CreateDeviceAsync(panel);
        await panel.PostAsJsonAsync($"/panel/devices/{id}", LayoutWith(Empty, Empty));

        var unknownDevice = await panel.PostAsJsonAsync("/panel/deck/dispatch",
            new PanelDeckDispatchBody { DeviceId = "nope", WidgetId = "deck1", Slot = 0 });
        Assert.Equal(HttpStatusCode.NotFound, unknownDevice.StatusCode);

        var emptySlot = await panel.PostAsJsonAsync("/panel/deck/dispatch",
            new PanelDeckDispatchBody { DeviceId = id, WidgetId = "deck1", Slot = 0 });
        Assert.Equal(HttpStatusCode.NotFound, emptySlot.StatusCode);

        var missingIds = await panel.PostAsJsonAsync("/panel/deck/dispatch", new PanelDeckDispatchBody());
        Assert.Equal(HttpStatusCode.BadRequest, missingIds.StatusCode);

        var anonymous = await _factory.CreateClient().PostAsJsonAsync("/panel/deck/dispatch",
            new PanelDeckDispatchBody { DeviceId = id, WidgetId = "deck1", Slot = 0 });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ───────────────────────── PUT /deck/instances/{id} and copyOfPresetId ─────────────────────────
    // /panel/deck/dispatch trusts whatever a widget's resolved preset holds
    // with no dispatch-time policy check of its own, so retargeting an
    // instance at an existing preset - or cloning one via copyOfPresetId -
    // is the only remaining gate against a panel session reaching a
    // desktop-authored privileged key it never authored itself.

    [Fact]
    public async Task Panel_cannot_retarget_a_physical_streamdeck_instance()
    {
        var panel = PanelClient();

        var res = await panel.PutAsJsonAsync("/deck/instances/streamdeck:SERIAL-1", new { mode = "fixed" });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("deck_action_requires_desktop", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Desktop_can_retarget_a_physical_streamdeck_instance()
    {
        var desktop = DesktopClient();

        var res = await desktop.PutAsJsonAsync("/deck/instances/streamdeck:SERIAL-1", new { mode = "fixed" });

        Assert.True(res.IsSuccessStatusCode);
    }

    private static async Task<string> CreatePresetAsync(HttpClient client, string name, string slotJson)
    {
        using var doc = JsonDocument.Parse($$"""{"pages":[{"slots":[{{slotJson}}]}]}""");
        var body = new { name, cols = 3, rows = 2, deck = doc.RootElement };
        var res = await client.PostAsJsonAsync("/deck/presets", body);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        using var parsed = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return parsed.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Panel_cannot_point_its_widget_at_a_preset_with_privileged_keys()
    {
        var desktop = DesktopClient();
        var panel = PanelClient();
        var privilegedId = await CreatePresetAsync(desktop, "Privileged " + Guid.NewGuid().ToString("n")[..8], Hotkey);

        var res = await panel.PutAsJsonAsync("/deck/instances/widget:w1", new { activePresetId = privilegedId });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("deck_action_requires_desktop", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Desktop_can_point_a_widget_at_a_preset_with_privileged_keys()
    {
        var desktop = DesktopClient();
        var privilegedId = await CreatePresetAsync(desktop, "Privileged " + Guid.NewGuid().ToString("n")[..8], Hotkey);

        var res = await desktop.PutAsJsonAsync("/deck/instances/widget:w1", new { activePresetId = privilegedId });

        Assert.True(res.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Panel_can_point_its_widget_at_an_unprivileged_preset()
    {
        var desktop = DesktopClient();
        var panel = PanelClient();
        var plainId = await CreatePresetAsync(desktop, "Plain " + Guid.NewGuid().ToString("n")[..8], OpenUrl);

        var res = await panel.PutAsJsonAsync("/deck/instances/widget:w1", new { activePresetId = plainId });

        Assert.True(res.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Panel_cannot_copy_a_preset_with_privileged_keys()
    {
        var desktop = DesktopClient();
        var panel = PanelClient();
        var privilegedId = await CreatePresetAsync(desktop, "Source " + Guid.NewGuid().ToString("n")[..8], Hotkey);

        var res = await panel.PostAsJsonAsync("/deck/presets", new
        {
            name = "Copy " + Guid.NewGuid().ToString("n")[..8],
            cols = 3,
            rows = 2,
            copyOfPresetId = privilegedId,
        });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("deck_action_requires_desktop", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Desktop_can_copy_a_preset_with_privileged_keys()
    {
        var desktop = DesktopClient();
        var privilegedId = await CreatePresetAsync(desktop, "Source2 " + Guid.NewGuid().ToString("n")[..8], Hotkey);

        var res = await desktop.PostAsJsonAsync("/deck/presets", new
        {
            name = "Copy2 " + Guid.NewGuid().ToString("n")[..8],
            cols = 3,
            rows = 2,
            copyOfPresetId = privilegedId,
        });

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }
}

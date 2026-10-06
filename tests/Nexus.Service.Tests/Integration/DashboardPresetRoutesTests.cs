using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class DashboardPresetRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public DashboardPresetRoutesTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string LayoutJson(string widgetType) =>
        $$"""{"layoutSchemaVersion":2,"surface":"desktop","pages":[{"id":"p","widgets":[{"id":"w","type":"{{widgetType}}","size":"2x2","col":0,"row":0}]}]}""";

    private static PanelLayoutDto Layout(string widgetType) => new()
    {
        LayoutSchemaVersion = 2,
        Surface = "desktop",
        Pages = { new PanelPageDto { Id = "p", Widgets = { new PanelWidgetDto { Id = "w", Type = widgetType } } } },
    };

    private static string? WidgetType(PanelLayoutDto? layout) => layout?.Pages.SingleOrDefault()?.Widgets.SingleOrDefault()?.Type;

    private async Task<JsonElement> ReadJson(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private Task<HttpResponseMessage> SeedAsync(params (string Name, string Type)[] presets)
    {
        var items = string.Join(",", presets.Select(p => $$"""{"name":"{{p.Name}}","layout":{{LayoutJson(p.Type)}}}"""));
        return _client.PostAsync("/dashboard/presets/seed", Json($$"""{"presets":[{{items}}]}"""));
    }

    [Fact]
    public async Task Get_reports_unseeded_on_a_fresh_store()
    {
        var root = await ReadJson(await _client.GetAsync("/dashboard/presets"));

        Assert.False(root.GetProperty("seeded").GetBoolean());
        Assert.Equal(0, root.GetProperty("presets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("activeId").ValueKind);
    }

    [Fact]
    public async Task Seed_stores_the_set_once_with_the_first_active()
    {
        var root = await ReadJson(await SeedAsync(("Default", "clock"), ("Monitoring", "monitoring")));

        Assert.True(root.GetProperty("seeded").GetBoolean());
        var presets = root.GetProperty("presets");
        Assert.Equal(2, presets.GetArrayLength());
        Assert.Equal(presets[0].GetProperty("id").GetString(), root.GetProperty("activeId").GetString());

        var again = await ReadJson(await SeedAsync(("Other", "timer")));
        Assert.Equal(2, again.GetProperty("presets").GetArrayLength());
        Assert.Equal("Default", Store.Load().Panel.DashboardPresets![0].Name);
    }

    [Fact]
    public async Task Activate_captures_the_live_layout_and_loads_the_target()
    {
        var seeded = await ReadJson(await SeedAsync(("Default", "clock"), ("Monitoring", "monitoring")));
        var defaultId = seeded.GetProperty("presets")[0].GetProperty("id").GetString()!;
        var monitoringId = seeded.GetProperty("presets")[1].GetProperty("id").GetString()!;
        Store.Update(s => s.Panel.DashboardLayout = Layout("weather"));

        var root = await ReadJson(await _client.PostAsync($"/dashboard/presets/{monitoringId}/activate", Json("{}")));

        Assert.Equal(monitoringId, root.GetProperty("activeId").GetString());
        var panel = Store.Load().Panel;
        Assert.Equal("monitoring", WidgetType(panel.DashboardLayout));
        Assert.Equal("weather", WidgetType(panel.DashboardPresets!.Single(p => p.Id == defaultId).Layout));
    }

    [Fact]
    public async Task Live_edits_do_not_leak_into_the_stored_preset()
    {
        var seeded = await ReadJson(await SeedAsync(("Default", "clock"), ("Monitoring", "monitoring")));
        var monitoringId = seeded.GetProperty("presets")[1].GetProperty("id").GetString()!;
        await ReadJson(await _client.PostAsync($"/dashboard/presets/{monitoringId}/activate", Json("{}")));

        Store.Update(s => s.Panel.DashboardLayout!.Pages[0].Widgets[0].Type = "timer");

        Assert.Equal("monitoring", WidgetType(Store.Load().Panel.DashboardPresets!.Single(p => p.Id == monitoringId).Layout));
    }

    [Fact]
    public async Task Create_saves_the_live_layout_as_the_active_preset()
    {
        Store.Update(s => s.Panel.DashboardLayout = Layout("calendar"));

        var root = await ReadJson(await _client.PostAsync("/dashboard/presets", Json("""{"name":" Work "}""")));

        var created = root.GetProperty("presets")[0];
        Assert.Equal("Work", created.GetProperty("name").GetString());
        Assert.Equal(created.GetProperty("id").GetString(), root.GetProperty("activeId").GetString());
        Assert.Equal("calendar", WidgetType(Store.Load().Panel.DashboardPresets!.Single().Layout));
    }

    [Fact]
    public async Task Create_refuses_past_the_cap()
    {
        for (var i = 0; i < Nexus.Service.Panel.DashboardPresets.Cap; i++)
            await ReadJson(await _client.PostAsync("/dashboard/presets", Json($$"""{"name":"P{{i}}"}""")));

        var res = await _client.PostAsync("/dashboard/presets", Json("""{"name":"Overflow"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(Nexus.Service.Panel.DashboardPresets.Cap, Store.Load().Panel.DashboardPresets!.Count);
    }

    [Fact]
    public async Task Deleting_the_active_preset_loads_the_one_that_takes_its_place()
    {
        var seeded = await ReadJson(await SeedAsync(("Default", "clock"), ("Monitoring", "monitoring"), ("Gaming", "deck")));
        var monitoringId = seeded.GetProperty("presets")[1].GetProperty("id").GetString()!;
        var gamingId = seeded.GetProperty("presets")[2].GetProperty("id").GetString()!;
        Store.Update(s => s.Panel.DashboardLayout = Layout("clock"));
        await ReadJson(await _client.PostAsync($"/dashboard/presets/{monitoringId}/activate", Json("{}")));

        var root = await ReadJson(await _client.DeleteAsync($"/dashboard/presets/{monitoringId}"));

        Assert.Equal(gamingId, root.GetProperty("activeId").GetString());
        Assert.Equal("deck", WidgetType(Store.Load().Panel.DashboardLayout));

        root = await ReadJson(await _client.DeleteAsync($"/dashboard/presets/{gamingId}"));

        Assert.Equal("Default", root.GetProperty("presets")[0].GetProperty("name").GetString());
        Assert.Equal(root.GetProperty("presets")[0].GetProperty("id").GetString(), root.GetProperty("activeId").GetString());
        Assert.Equal("clock", WidgetType(Store.Load().Panel.DashboardLayout));
    }

    [Fact]
    public async Task Deleting_an_inactive_preset_leaves_the_live_layout_alone()
    {
        var seeded = await ReadJson(await SeedAsync(("Default", "clock"), ("Monitoring", "monitoring")));
        var defaultId = seeded.GetProperty("presets")[0].GetProperty("id").GetString()!;
        var monitoringId = seeded.GetProperty("presets")[1].GetProperty("id").GetString()!;
        Store.Update(s => s.Panel.DashboardLayout = Layout("weather"));

        var root = await ReadJson(await _client.DeleteAsync($"/dashboard/presets/{monitoringId}"));

        Assert.Equal(defaultId, root.GetProperty("activeId").GetString());
        Assert.Equal("weather", WidgetType(Store.Load().Panel.DashboardLayout));
    }

    [Fact]
    public async Task Deleting_the_last_preset_keeps_the_live_layout()
    {
        var seeded = await ReadJson(await SeedAsync(("Default", "clock")));
        var id = seeded.GetProperty("activeId").GetString()!;
        Store.Update(s => s.Panel.DashboardLayout = Layout("clock"));

        var root = await ReadJson(await _client.DeleteAsync($"/dashboard/presets/{id}"));

        Assert.Equal(0, root.GetProperty("presets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("activeId").ValueKind);
        Assert.True(root.GetProperty("seeded").GetBoolean());
        Assert.Equal("clock", WidgetType(Store.Load().Panel.DashboardLayout));
    }

    [Fact]
    public async Task Rename_and_activate_return_404_for_an_unknown_preset()
    {
        var rename = await _client.PutAsync("/dashboard/presets/missing", Json("""{"name":"X"}"""));
        var activate = await _client.PostAsync("/dashboard/presets/missing/activate", Json("{}"));

        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, activate.StatusCode);
    }

    [Fact]
    public void Resetting_the_dashboard_category_unseeds_the_presets()
    {
        var settings = new NexusSettings();
        Nexus.Service.Panel.DashboardPresets.Seed(settings.Panel, new[] { new DashboardPreset { Name = "Default" } });

        ProfileSharing.ResetCategory(settings, ProfileSharing.Dashboard);

        Assert.Null(settings.Panel.DashboardPresets);
        Assert.Null(settings.Panel.DashboardActivePresetId);
    }
}

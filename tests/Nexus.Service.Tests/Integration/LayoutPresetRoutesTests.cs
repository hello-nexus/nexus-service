using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class LayoutPresetRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public LayoutPresetRoutesTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        // xUnit builds this class once per test; the host is shared, so the
        // store is what has to go back to defaults between them.
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private IConfigStore Store =>
        _factory.Services.GetRequiredService<IConfigStore>();

    // ---- GET ----

    [Fact]
    public async Task Get_returns_empty_list_and_null_activeId_on_fresh_store()
    {
        var res = await _client.GetAsync("/devices/lighting-devices/layout-presets");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("presets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("activeId").ValueKind);
    }

    // ---- POST create ----

    [Fact]
    public async Task Create_appends_preset_and_sets_activeId()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-1"] = new DeviceLayout { X = 5, Y = 10, W = 80, H = 40 };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Gaming"}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var preset = doc.RootElement.GetProperty("preset");
        var id = preset.GetProperty("id").GetString();
        Assert.Equal("Gaming", preset.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(id, doc.RootElement.GetProperty("activeId").GetString());

        var stored = Store.Load();
        Assert.Single(stored.Lighting.LayoutPresets, p => p.Id == id);
        Assert.Equal(id, stored.Lighting.ActiveLayoutPresetId);
        Assert.True(stored.Lighting.LayoutPresets[0].Layouts.ContainsKey("dev-1"));
    }

    [Fact]
    public async Task Create_cap_returns_400_when_at_10_presets()
    {
        for (var i = 0; i < 10; i++)
        {
            var r = await _client.PostAsync(
                "/devices/lighting-devices/layout-presets",
                Json("{\"name\":\"Preset " + i + "\"}"));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Overflow"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(10, Store.Load().Lighting.LayoutPresets.Count);
    }

    // ---- PUT update ----

    [Fact]
    public async Task Put_saveCurrent_overwrites_preset_layouts()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-a"] = new DeviceLayout { X = 1, Y = 2, W = 50, H = 30 };
        });
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Original"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        // Change the live layouts before overwriting the preset.
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-b"] = new DeviceLayout { X = 9, Y = 9, W = 80, H = 40 };
        });

        var putRes = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var stored = Store.Load();
        var p = stored.Lighting.LayoutPresets.Find(x => x.Id == id)!;
        Assert.True(p.Layouts.ContainsKey("dev-b"));
    }

    [Fact]
    public async Task Put_name_renames_preset_only()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Old Name"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        var putRes = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"name":"New Name","saveCurrent":false}"""));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var stored = Store.Load();
        Assert.Equal("New Name", stored.Lighting.LayoutPresets[0].Name);
    }

    [Fact]
    public async Task Put_returns_404_for_unknown_id()
    {
        var res = await _client.PutAsync(
            "/devices/lighting-devices/layout-presets/nonexistent",
            Json("""{"name":"X"}"""));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- DELETE ----

    [Fact]
    public async Task Delete_active_preset_nulls_activeId()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P1"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        var res = await _client.DeleteAsync($"/devices/lighting-devices/layout-presets/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("activeId").ValueKind);

        var stored = Store.Load();
        Assert.Empty(stored.Lighting.LayoutPresets);
        Assert.Null(stored.Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public async Task Delete_non_active_preset_leaves_activeId()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P1"}"""));
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P2"}"""));

        var presets = Store.Load().Lighting.LayoutPresets;
        var firstId = presets[0].Id;
        var secondId = presets[1].Id;

        // Activate the second preset, then delete the first.
        await _client.PutAsync(
            "/devices/lighting-devices/layout-presets/active",
            Json("{\"id\":\"" + secondId + "\"}"));
        var res = await _client.DeleteAsync($"/devices/lighting-devices/layout-presets/{firstId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(secondId, doc.RootElement.GetProperty("activeId").GetString());

        var stored = Store.Load();
        Assert.Single(stored.Lighting.LayoutPresets);
        Assert.Equal(secondId, stored.Lighting.ActiveLayoutPresetId);
    }

    // ---- PUT /active ----

    [Fact]
    public async Task Put_active_sets_id_without_changing_device_layouts()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-x"] = new DeviceLayout { X = 3, Y = 4, W = 60, H = 30 };
        });
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        // Clear active then set it back via /active.
        Store.Update(s => s.Lighting.ActiveLayoutPresetId = null);

        var res = await _client.PutAsync(
            "/devices/lighting-devices/layout-presets/active",
            Json("{\"id\":\"" + id + "\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(id, stored.Lighting.ActiveLayoutPresetId);
        // DeviceLayouts must not have changed.
        Assert.True(stored.Lighting.DeviceLayouts.ContainsKey("dev-x"));
    }

    // ---- POST /layouts (batch apply) ----

    [Fact]
    public async Task Post_layouts_replaces_device_layouts()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["old-dev"] = new DeviceLayout { X = 1, Y = 1, W = 50, H = 20 };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layouts",
            Json("""{"layouts":{"new-dev":{"x":7,"y":8,"w":60,"h":30,"rotation":0}}}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.False(stored.Lighting.DeviceLayouts.ContainsKey("old-dev"));
        Assert.True(stored.Lighting.DeviceLayouts.TryGetValue("new-dev", out var layout));
        Assert.Equal(7f, layout.X);
        Assert.Equal(8f, layout.Y);
        Assert.Equal(60f, layout.W);
    }

    // ---- POST /{id}/activate ----

    [Fact]
    public async Task Activate_copies_preset_layouts_and_sets_activeId()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["original"] = new DeviceLayout { X = 1, Y = 1, W = 50, H = 20 };
        });
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Saved"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        // Change live layouts.
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts.Clear();
            s.Lighting.DeviceLayouts["changed"] = new DeviceLayout { X = 9, Y = 9, W = 80, H = 40 };
        });

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(id, stored.Lighting.ActiveLayoutPresetId);
        Assert.True(stored.Lighting.DeviceLayouts.ContainsKey("original"));
        Assert.False(stored.Lighting.DeviceLayouts.ContainsKey("changed"));
    }

    [Fact]
    public async Task Activate_returns_404_for_unknown_id()
    {
        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/nonexistent/activate",
            null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- power state capture and restore ----

    [Fact]
    public async Task Create_captures_disabled_devices_into_preset()
    {
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-a", "dev-b" };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"WithPower"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        var preset = stored.Lighting.LayoutPresets[0];
        Assert.Equal(new List<string> { "dev-a", "dev-b" }, preset.DisabledDevices);
    }

    [Fact]
    public async Task SaveCurrent_captures_disabled_devices_into_preset()
    {
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-x" };
        });
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-y", "dev-z" };
        });

        var putRes = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var stored = Store.Load();
        var preset = stored.Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(new List<string> { "dev-y", "dev-z" }, preset.DisabledDevices);
    }

    [Fact]
    public async Task Activate_restores_disabled_devices_from_preset()
    {
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-a" };
        });
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P1"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        // Change global disabled list after the preset was saved.
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(new List<string> { "dev-a" }, stored.Devices.DisabledLightingDevices);
    }

    [Fact]
    public async Task Activate_legacy_preset_without_power_leaves_global_disabled_untouched()
    {
        // A preset saved before per-preset power has DisabledDevices = null;
        // activating it must not wipe the user's current global disabled list.
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Add(new Nexus.Service.Persistence.LayoutPreset
            {
                Id = "legacy",
                Name = "Legacy",
            });
            s.Devices.DisabledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/legacy/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(new List<string> { "dev-b" }, stored.Devices.DisabledLightingDevices);
    }

    // ---- ignore (uncontrolled) state capture and restore ----

    [Fact]
    public async Task Create_captures_uncontrolled_devices_into_preset()
    {
        Store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-ignored" };
        });

        var id = await CreatePreset("Ignoring");

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(new List<string> { "dev-ignored" }, preset.UncontrolledDevices);
    }

    [Fact]
    public async Task Activate_restores_uncontrolled_devices_from_preset()
    {
        Store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-a" };
        });
        var id = await CreatePreset("P1");

        Store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(new List<string> { "dev-a" }, Store.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public async Task Activate_legacy_preset_without_ignore_state_leaves_the_live_list_untouched()
    {
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Add(new Nexus.Service.Persistence.LayoutPreset
            {
                Id = "legacy",
                Name = "Legacy",
            });
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/legacy/activate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(new List<string> { "dev-b" }, Store.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public async Task Toggling_ignore_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/devices/lighting-devices/controlled",
            Json("""{"id":"dev-x","controlled":false}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(new List<string> { "dev-x" }, preset.UncontrolledDevices);
    }

    [Fact]
    public async Task Toggling_power_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/devices/lighting-devices/power",
            Json("""{"id":"dev-x","on":false}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Contains("dev-x", preset.DisabledDevices!);
    }

    // ---- mode + effect capture and restore ----

    private async Task<string> CreatePreset(string name)
    {
        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json($$"""{"name":"{{name}}"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
    }

    private void SetLiveLook(string effect, float hue)
    {
        Store.Update(s =>
        {
            s.Lighting.Sync = effect;
            s.Lighting.Animate.Effect = effect;
            s.Lighting.Animate.States[effect] = new AnimateEffectState
            {
                Speed = 50,
                Intensity = 1f,
                Hue = hue,
                Colorize = 0f,
                Saturation = 1f,
                Contrast = 1f,
            };
        });
    }

    [Fact]
    public async Task Create_captures_the_live_mode_and_effect()
    {
        SetLiveLook("jellyfish", 0.25f);

        var id = await CreatePreset("Gaming");

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.NotNull(preset.Look);
        Assert.Equal("jellyfish", preset.Look.Sync);
        Assert.Equal("jellyfish", preset.Look.AnimateEffect);
        Assert.Equal(0.25f, preset.Look.AnimateState!.Hue);
    }

    [Fact]
    public async Task SaveCurrent_recaptures_the_live_effect()
    {
        SetLiveLook("jellyfish", 0.25f);
        var id = await CreatePreset("P");

        SetLiveLook("aurora", 0.5f);
        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal("aurora", preset.Look!.AnimateEffect);
    }

    [Fact]
    public async Task Activate_restores_the_preset_effect()
    {
        SetLiveLook("jellyfish", 0.25f);
        var first = await CreatePreset("Jelly");
        SetLiveLook("aurora", 0.5f);
        await CreatePreset("Aurora");

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{first}/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal("jellyfish", stored.Lighting.Sync);
        Assert.Equal("jellyfish", stored.Lighting.Animate.Effect);
    }

    // NEX-51: two presets on one effect differing only by colour. States is
    // keyed by effect, so the preset must carry its own copy.
    [Fact]
    public async Task Activate_restores_the_colour_of_a_shared_effect()
    {
        SetLiveLook("jellyfish", 0.2f);
        var blue = await CreatePreset("Blue");
        SetLiveLook("jellyfish", 0.8f);
        var orange = await CreatePreset("Orange");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{blue}/activate", null);
        Assert.Equal(0.2f, Store.Load().Lighting.Animate.States["jellyfish"].Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{orange}/activate", null);
        Assert.Equal(0.8f, Store.Load().Lighting.Animate.States["jellyfish"].Hue);
    }

    [Fact]
    public async Task Activate_restores_the_master_brightness()
    {
        Store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        var dim = await CreatePreset("Dim");
        Store.Update(s => s.Lighting.GlobalBrightness = 1f);
        var bright = await CreatePreset("Bright");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{dim}/activate", null);
        Assert.Equal(0.3f, Store.Load().Lighting.GlobalBrightness);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{bright}/activate", null);
        Assert.Equal(1f, Store.Load().Lighting.GlobalBrightness);
    }

    [Fact]
    public async Task Setting_master_brightness_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/lighting/global-brightness", Json("""{"value":0.42}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(0.42f, preset.Look!.GlobalBrightness);
    }

    [Fact]
    public async Task Activate_restores_the_screen_filter()
    {
        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.2f);
        var cool = await CreatePreset("Cool");
        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.8f);
        var warm = await CreatePreset("Warm");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{cool}/activate", null);
        Assert.Equal(0.2f, Store.Load().Lighting.ScreenEffect.Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{warm}/activate", null);
        Assert.Equal(0.8f, Store.Load().Lighting.ScreenEffect.Hue);
    }

    // The running Mirror effect samples the provider's holder, not the store, so
    // a preset that only persisted the filter would keep rendering the old one.
    [Fact]
    public async Task Activate_pushes_the_screen_filter_into_the_live_holder()
    {
        var provider = (Nexus.Service.Lighting.LightingProvider)
            _factory.Services.GetRequiredService<Nexus.Service.Lighting.ILightingProvider>();

        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.2f);
        var cool = await CreatePreset("Cool");
        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.8f);
        var warm = await CreatePreset("Warm");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{cool}/activate", null);
        Assert.Equal(0.2f, provider.ScreenPostProcess.Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{warm}/activate", null);
        Assert.Equal(0.8f, provider.ScreenPostProcess.Hue);
    }

    [Fact]
    public async Task Activate_pushes_the_media_filter_into_the_live_holder()
    {
        var provider = (Nexus.Service.Lighting.LightingProvider)
            _factory.Services.GetRequiredService<Nexus.Service.Lighting.ILightingProvider>();

        Store.Update(s => s.Lighting.MediaEffect.Hue = 0.15f);
        var first = await CreatePreset("First");
        Store.Update(s => s.Lighting.MediaEffect.Hue = 0.75f);
        var second = await CreatePreset("Second");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{first}/activate", null);
        Assert.Equal(0.15f, provider.MediaPostProcess.Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{second}/activate", null);
        Assert.Equal(0.75f, provider.MediaPostProcess.Hue);
    }

    [Fact]
    public async Task Setting_the_screen_filter_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/lighting/screen/effect",
            Json("""{"hue":0.45,"colorize":0,"saturation":1,"contrast":1,"persist":true}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(0.45f, preset.Look!.ScreenEffect!.Hue);
    }

    [Fact]
    public async Task Activate_legacy_preset_without_a_look_leaves_the_live_effect_untouched()
    {
        SetLiveLook("jellyfish", 0.25f);
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Add(new Nexus.Service.Persistence.LayoutPreset
            {
                Id = "legacy",
                Name = "Legacy",
            });
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/legacy/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal("jellyfish", Store.Load().Lighting.Sync);
    }

    // ---- Per-device Static assignments ----

    private static StaticDeviceLook Look(string effect, string color, int slot = 0) =>
        new() { Effect = effect, Color = color, Slot = slot };

    [Fact]
    public async Task Create_captures_static_device_looks_into_preset()
    {
        Store.Update(s =>
        {
            s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#ff0000", 2);
            s.Devices.LightingDevicePrefs["dev-a"] = new LightingDevicePreference { Brightness = 42, Hue = 0.25f, Saturation = 0.5f };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"WithColours"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets[0];
        Assert.Equal("#ff0000", preset.StaticDeviceLooks!["dev-a"].Color);
        Assert.Equal(2, preset.StaticDeviceLooks["dev-a"].Slot);
        Assert.Equal(42, preset.DevicePrefs!["dev-a"].Brightness);
        Assert.Equal(0.25f, preset.DevicePrefs["dev-a"].Hue);
    }

    // Colour tuning rides on the same per-device preference the preset copies.
    // The first cut of the copy dropped the trim fields, so saving a trim wrote
    // a neutral copy into the active preset and the next activate reverted it.
    [Fact]
    public async Task Colour_trims_survive_a_preset_capture_and_activate()
    {
        var write = await _client.PostAsync(
            "/devices/lighting-devices/color-adjust",
            Json("""{"ids":["dev-a"],"red":1.3,"green":0.9,"blue":0.75,"temperature":0.4,"saturation":1.25}"""));
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Tuned"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        Assert.Equal(1.3f, Store.Load().Lighting.LayoutPresets[0].DevicePrefs!["dev-a"].AdjustRed);

        // Drift away, then load the preset back over it. Written straight to
        // the store: the route would mirror the drift into the ACTIVE preset
        // (CaptureDeviceStateIntoActive), which is this preset, so the activate
        // would restore the drift rather than what was captured.
        Store.Update(s => s.Devices.LightingDevicePrefs["dev-a"] = new LightingDevicePreference());
        var activate = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var pref = Store.Load().Devices.LightingDevicePrefs["dev-a"];
        Assert.Equal(1.3f, pref.AdjustRed);
        Assert.Equal(0.9f, pref.AdjustGreen);
        Assert.Equal(0.75f, pref.AdjustBlue);
        Assert.Equal(0.4f, pref.AdjustTemperature);
        Assert.Equal(1.25f, pref.AdjustSaturation);
    }

    // A partial body leaves the fields it omits alone. Without this a drag on
    // one slider would rewrite the other four, flattening values that differ
    // between the devices in a multi-device scope.
    [Fact]
    public async Task Colour_trim_write_only_touches_the_fields_it_carries()
    {
        Store.Update(s => s.Devices.LightingDevicePrefs["dev-a"] = new LightingDevicePreference
        {
            AdjustRed = 1.4f,
            AdjustSaturation = 0.6f,
            Brightness = 55,
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/color-adjust",
            Json("""{"ids":["dev-a"],"blue":0.8}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var pref = Store.Load().Devices.LightingDevicePrefs["dev-a"];
        Assert.Equal(0.8f, pref.AdjustBlue);
        Assert.Equal(1.4f, pref.AdjustRed);
        Assert.Equal(0.6f, pref.AdjustSaturation);
        Assert.Equal(55, pref.Brightness);
    }

    [Fact]
    public async Task Activate_restores_static_device_looks_over_the_live_ones()
    {
        Store.Update(s => s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#00ff00"));
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Green"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        // Drift away from what the preset captured, then load it back.
        Store.Update(s =>
        {
            s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#0000ff");
            s.Lighting.StaticDeviceLooks["dev-b"] = Look("flat", "#ffffff");
        });

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var looks = Store.Load().Lighting.StaticDeviceLooks;
        Assert.Equal("#00ff00", looks["dev-a"].Color);
        // Replaced wholesale, so a device the preset never knew about is gone.
        Assert.False(looks.ContainsKey("dev-b"));
    }

    [Fact]
    public async Task Activate_leaves_looks_alone_on_a_preset_saved_before_they_existed()
    {
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Legacy"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Find(x => x.Id == id)!.StaticDeviceLooks = null;
            s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#123456");
        });

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));

        Assert.Equal("#123456", Store.Load().Lighting.StaticDeviceLooks["dev-a"].Color);
    }

    [Fact]
    public async Task Static_looks_route_returns_every_assignment_with_its_slot()
    {
        Store.Update(s =>
        {
            s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#ff00ff", 3);
            // An empty effect is a cleared device and must not be listed.
            s.Lighting.StaticDeviceLooks["dev-b"] = Look("", "");
        });

        var res = await _client.GetAsync("/devices/lighting-devices/static-looks");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var looks = doc.RootElement.GetProperty("looks");
        Assert.Equal("#ff00ff", looks.GetProperty("dev-a").GetProperty("color").GetString());
        Assert.Equal(3, looks.GetProperty("dev-a").GetProperty("slot").GetInt32());
        Assert.False(looks.TryGetProperty("dev-b", out _));
    }

    [Fact]
    public async Task Static_lock_route_locks_a_look_and_the_colour_route_then_refuses_it()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/color",
            Json("""{"id":"dev-a","hue":0.5,"saturation":1,"effect":"flat","color":"#abcdef"}"""));

        var lockRes = await _client.PostAsync(
            "/devices/lighting-devices/static-lock",
            Json("""{"id":"dev-a","locked":true}"""));
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);
        Assert.True(Store.Load().Lighting.StaticDeviceLooks["dev-a"].Locked);

        var res = await _client.GetAsync("/devices/lighting-devices/static-looks");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("looks").GetProperty("dev-a").GetProperty("locked").GetBoolean());

        // Locked: a new pick is refused and the stored look is untouched.
        var repick = await _client.PostAsync(
            "/devices/lighting-devices/color",
            Json("""{"id":"dev-a","hue":0.1,"saturation":1,"effect":"flat","color":"#000000"}"""));
        Assert.Equal(HttpStatusCode.Conflict, repick.StatusCode);
        Assert.Equal("#abcdef", Store.Load().Lighting.StaticDeviceLooks["dev-a"].Color);

        // Unlocked: the same pick lands.
        await _client.PostAsync(
            "/devices/lighting-devices/static-lock",
            Json("""{"id":"dev-a","locked":false}"""));
        var again = await _client.PostAsync(
            "/devices/lighting-devices/color",
            Json("""{"id":"dev-a","hue":0.1,"saturation":1,"effect":"flat","color":"#000000"}"""));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("#000000", Store.Load().Lighting.StaticDeviceLooks["dev-a"].Color);
    }

    [Fact]
    public async Task Static_lock_route_needs_a_look_to_lock()
    {
        var res = await _client.PostAsync(
            "/devices/lighting-devices/static-lock",
            Json("""{"id":"dev-none","locked":true}"""));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_preset_round_trips_the_lock()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/color",
            Json("""{"id":"dev-a","hue":0.5,"saturation":1,"effect":"flat","color":"#abcdef"}"""));
        await _client.PostAsync(
            "/devices/lighting-devices/static-lock",
            Json("""{"id":"dev-a","locked":true}"""));
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Locked"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
        Assert.True(Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!.StaticDeviceLooks!["dev-a"].Locked);

        // A second preset takes the live state, so unlocking there leaves the
        // first one's lock alone; activating the first brings the lock back.
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Open"}"""));
        await _client.PostAsync(
            "/devices/lighting-devices/static-lock",
            Json("""{"id":"dev-a","locked":false}"""));
        Assert.False(Store.Load().Lighting.StaticDeviceLooks["dev-a"].Locked);
        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.True(Store.Load().Lighting.StaticDeviceLooks["dev-a"].Locked);
    }

    [Fact]
    public async Task Colour_post_mirrors_the_assignment_into_the_active_preset()
    {
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Live"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        var res = await _client.PostAsync(
            "/devices/lighting-devices/color",
            Json("""{"id":"dev-a","hue":0.5,"saturation":1,"effect":"flat","color":"#abcdef","slot":1}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal("#abcdef", preset.StaticDeviceLooks!["dev-a"].Color);
        Assert.Equal(1, preset.StaticDeviceLooks["dev-a"].Slot);
    }

    [Fact]
    public async Task SaveCurrent_without_saveDeviceLooks_keeps_the_stored_assignments()
    {
        Store.Update(s => s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#00ff00"));
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Keep"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        // The undo/redo reconcile: live colours now belong to another preset.
        Store.Update(s => s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#0000ff"));
        var putRes = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true,"saveDeviceLooks":false}"""));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal("#00ff00", preset.StaticDeviceLooks!["dev-a"].Color);
    }

    [Fact]
    public async Task SaveCurrent_defaults_to_capturing_the_assignments()
    {
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Plain"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        Store.Update(s => s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#0000ff"));
        // No saveDeviceLooks in the body: an older client keeps the old behaviour.
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal("#0000ff", preset.StaticDeviceLooks!["dev-a"].Color);
    }

    // ---- PUT apps (per-app auto-activation) ----

    [Fact]
    public async Task Apps_are_stored_and_returned_by_GET()
    {
        var id = await CreatePreset("Gaming");

        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[{"id":"proc:chrome.exe","name":"Chrome"}]}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Single(stored.Apps!);
        Assert.Equal("proc:chrome.exe", stored.Apps![0].Id);
        // A running-app pick carries the process name in its id.
        Assert.Equal("chrome", stored.Apps![0].ProcessName);

        var getRes = await _client.GetAsync("/devices/lighting-devices/layout-presets");
        using var doc = JsonDocument.Parse(await getRes.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("presets")[0].GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("Chrome", apps[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Binding_an_app_another_preset_already_uses_is_refused()
    {
        var first = await CreatePreset("First");
        var second = await CreatePreset("Second");

        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{first}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"},{"id":"proc:code","name":"Code"}]}"""));

        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{second}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"}]}"""));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("Chrome", doc.RootElement.GetProperty("appName").GetString());
        Assert.Equal("First", doc.RootElement.GetProperty("presetName").GetString());

        // Neither side moved: the first preset keeps both, the second stays empty.
        var settings = Store.Load();
        Assert.Equal(2, settings.Lighting.LayoutPresets.Find(p => p.Id == first)!.Apps!.Count);
        Assert.Empty(settings.Lighting.LayoutPresets.Find(p => p.Id == second)!.Apps ?? new List<PresetAppBinding>());
    }

    [Fact]
    public async Task Different_apps_on_different_presets_both_bind()
    {
        var first = await CreatePreset("First");
        var second = await CreatePreset("Second");

        var a = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{first}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"}]}"""));
        var b = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{second}/apps",
            Json("""{"apps":[{"id":"proc:code","name":"Code"}]}"""));

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        var settings = Store.Load();
        Assert.Single(settings.Lighting.LayoutPresets.Find(p => p.Id == first)!.Apps!);
        Assert.Single(settings.Lighting.LayoutPresets.Find(p => p.Id == second)!.Apps!);
    }

    [Fact]
    public async Task Re_saving_a_presets_own_apps_is_not_a_conflict()
    {
        var id = await CreatePreset("Gaming");
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"}]}"""));

        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"},{"id":"proc:code","name":"Code"}]}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!.Apps!.Count);
    }

    [Fact]
    public async Task Empty_list_clears_the_bindings()
    {
        var id = await CreatePreset("Gaming");
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"}]}"""));

        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[]}"""));

        Assert.Empty(Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!.Apps!);
    }

    [Fact]
    public async Task Duplicate_ids_in_one_request_are_collapsed()
    {
        var id = await CreatePreset("Gaming");
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"},{"id":"proc:chrome","name":"Chrome"}]}"""));

        Assert.Single(Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!.Apps!);
    }

    [Fact]
    public async Task Apps_on_an_unknown_preset_is_404()
    {
        var res = await _client.PutAsync(
            "/devices/lighting-devices/layout-presets/nope/apps",
            Json("""{"apps":[]}"""));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task The_same_app_picked_two_ways_is_still_one_app()
    {
        var first = await CreatePreset("First");
        var second = await CreatePreset("Second");

        // Bound off the installed list on one preset, with a resolved process
        // name...
        Store.Update(s =>
        {
            var p = s.Lighting.LayoutPresets.Find(x => x.Id == second)!;
            p.Apps = new List<PresetAppBinding>
            {
                new() { Id = "Google Chrome", Name = "Google Chrome", ProcessName = "chrome" },
            };
        });

        // ...then picked off the running list on another. Different id, same
        // process - still a conflict, not a second binding.
        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{first}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"chrome"}]}"""));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("Second", doc.RootElement.GetProperty("presetName").GetString());
    }

    [Fact]
    public async Task An_unresolved_binding_is_not_matched_by_empty_process_name()
    {
        var first = await CreatePreset("First");
        var second = await CreatePreset("Second");

        // Two different UWP-style picks, neither resolvable to a process name.
        Store.Update(s =>
        {
            var p = s.Lighting.LayoutPresets.Find(x => x.Id == second)!;
            p.Apps = new List<PresetAppBinding>
            {
                new() { Id = "Some.Uwp.App!App", Name = "Some UWP App", ProcessName = "" },
            };
        });

        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{first}/apps",
            Json("""{"apps":[{"id":"Other.Uwp.App!App","name":"Other UWP App"}]}"""));

        // An empty process name must not collapse every unresolved binding
        // into one another, so this is not a conflict.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(Store.Load().Lighting.LayoutPresets.Find(p => p.Id == second)!.Apps!);
    }

    // ---- bindings live on the preset, like its other captured config ----

    [Fact]
    public async Task Apps_survive_a_saveCurrent_that_recaptures_the_preset()
    {
        var id = await CreatePreset("Gaming");
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"}]}"""));

        // The usual "save the live look into this preset" path.
        Store.Update(s => s.Lighting.StaticDeviceLooks["dev-a"] = Look("flat", "#00ff00"));
        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Single(preset.Apps!);
        Assert.Equal("chrome", preset.Apps![0].ProcessName);
    }

    [Fact]
    public async Task Apps_survive_a_rename()
    {
        var id = await CreatePreset("Gaming");
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"}]}"""));

        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"name":"Renamed"}"""));

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal("Renamed", preset.Name);
        Assert.Single(preset.Apps!);
    }

    [Fact]
    public async Task Activating_a_preset_leaves_every_presets_bindings_alone()
    {
        var first = await CreatePreset("First");
        var second = await CreatePreset("Second");
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{first}/apps",
            Json("""{"apps":[{"id":"proc:chrome","name":"Chrome"}]}"""));
        await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{second}/apps",
            Json("""{"apps":[{"id":"proc:code","name":"Code"}]}"""));

        await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{first}/activate", Json("{}"));

        var settings = Store.Load();
        Assert.Single(settings.Lighting.LayoutPresets.Find(p => p.Id == first)!.Apps!);
        Assert.Single(settings.Lighting.LayoutPresets.Find(p => p.Id == second)!.Apps!);
    }

}

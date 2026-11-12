using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for the panel MCP tools against a real PanelDeviceRegistry backed by a
/// temp-file JsonConfigStore, with one Y70 record holding a known two-widget layout.
/// </summary>
public sealed class PanelToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-panel-tools-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;
    private readonly PanelBgLibrary _media;
    private readonly MultiplexHub _hub = new();
    private readonly string _y70;

    public PanelToolsTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _registry = new PanelDeviceRegistry(_store) { SeeThroughHost = false };
        _media = new PanelBgLibrary(Path.Combine(_tempDir, "panel-bg"));
        var record = _registry.Allocate("Case Screen", new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70, Orientation = "portrait" });
        _y70 = record.Id;
        _registry.Patch(_y70, new PanelDevicePatch
        {
            Layout = new PanelLayoutDto
            {
                LayoutSchemaVersion = 2,
                ActivePageId = "p1",
                Pages =
                {
                    new PanelPageDto
                    {
                        Id = "p1",
                        Widgets =
                        {
                            new PanelWidgetDto { Id = "clock1", Type = "clock", Size = "4x4", Col = 0, Row = 0 },
                            new PanelWidgetDto
                            {
                                Id = "mon1", Type = "monitoring", Size = "4x4", Col = 0, Row = 4,
                                Config = new Dictionary<string, JsonElement>
                                {
                                    ["slot1_design"] = JsonSerializer.SerializeToElement("arc270"),
                                    ["slot2_design"] = JsonSerializer.SerializeToElement("bar"),
                                },
                            },
                        },
                    },
                },
            },
        });
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private PanelDeviceRecord Y70 => _registry.Get(_y70)!;

    private List<PanelWidgetDto> Widgets => Y70.Layout!.Pages.SelectMany(p => p.Widgets).ToList();

    [Fact]
    public void Reads_are_gated_by_telemetry_and_writes_by_panel_consent()
    {
        IMcpTool[] reads = { new ListPanelsTool(_registry), new GetPanelTool(_registry, _media), new ListPanelPresetsTool(_registry) };
        IMcpTool[] writes =
        {
            new UpdatePanelLookTool(_registry, _media, _hub), new SetPanelLayoutTool(_registry, _hub),
            new UpdatePanelWidgetTool(_registry, _hub), new CreatePanelPresetTool(_registry),
            new RenamePanelPresetTool(_registry), new DeletePanelPresetTool(_registry), new ActivatePanelPresetTool(_registry, _hub),
        };

        Assert.All(reads, t => Assert.True(t.ReadOnly && t.Capability == McpCapability.Telemetry, t.Name));
        Assert.All(writes, t => Assert.True(!t.ReadOnly && t.Capability == McpCapability.Panels, t.Name));
        Assert.All(reads.Concat(writes), t => JsonDocument.Parse(t.InputSchemaJson).Dispose());
    }

    [Fact]
    public async Task Panel_consent_off_refuses_a_write_and_audits_it()
    {
        _store.Update(s => s.AiIntegration.AllowPanels = false);
        var audit = new List<McpAuditEntry>();
        var registry = new McpToolRegistry(new IMcpTool[] { new UpdatePanelLookTool(_registry, _media, _hub) }, _store, new ListSink(audit));
        registry.TryGetTool("update_panel_look", out var tool);

        var result = await registry.CallAsync(tool, Args(new { panel = _y70, accentColor = "#ff0000" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Allow panel editing", result.Text, StringComparison.Ordinal);
        Assert.Null(Y70.AccentColor);
        var entry = Assert.Single(audit);
        Assert.False(entry.Success);
    }

    [Fact]
    public async Task A_successful_write_is_audited()
    {
        var audit = new List<McpAuditEntry>();
        var registry = new McpToolRegistry(new IMcpTool[] { new UpdatePanelLookTool(_registry, _media, _hub) }, _store, new ListSink(audit));
        registry.TryGetTool("update_panel_look", out var tool);

        var result = await registry.CallAsync(tool, Args(new { panel = _y70, accentColor = "#00ff88" }), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var entry = Assert.Single(audit);
        Assert.True(entry.Success);
        Assert.Equal("update_panel_look", entry.ToolName);
    }

    [Fact]
    public async Task ListPanels_reports_the_y70_with_its_widget_count()
    {
        var result = await new ListPanelsTool(_registry).ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        var panel = Assert.Single(doc.RootElement.GetProperty("panels").EnumerateArray());
        Assert.Equal(_y70, panel.GetProperty("id").GetString());
        Assert.Equal("y70", panel.GetProperty("surface").GetString());
        Assert.Equal(2, panel.GetProperty("widgets").GetInt32());
    }

    [Fact]
    public async Task GetPanel_by_name_returns_grid_look_layout_and_media()
    {
        var mediaId = AddMedia("animated");
        _registry.Patch(_y70, new PanelDevicePatch { AccentColor = "#123456" });

        var result = await new GetPanelTool(_registry, _media).ExecuteAsync(Args(new { panel = "case screen" }), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        using var doc = JsonDocument.Parse(result.Text);
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("grid").GetProperty("cols").GetInt32());
        Assert.Equal(16, root.GetProperty("grid").GetProperty("rows").GetInt32());
        Assert.Equal("#123456", root.GetProperty("look").GetProperty("accentColor").GetString());
        var widgets = root.GetProperty("layout").GetProperty("pages")[0].GetProperty("widgets");
        Assert.Equal("arc270", widgets[1].GetProperty("config").GetProperty("slot1_design").GetString());
        Assert.Equal(mediaId, Assert.Single(root.GetProperty("backgroundMedia").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetPanel_unknown_panel_lists_known_panels()
    {
        var result = await new GetPanelTool(_registry, _media).ExecuteAsync(Args(new { panel = "nope" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Case Screen (" + _y70 + ")", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPanel_serves_the_starter_layout_for_a_never_edited_panel()
    {
        var phone = _registry.Allocate("Phone", new PanelDeviceCapabilities { Surface = PanelSurfaces.Phone });

        var result = await new GetPanelTool(_registry, _media).ExecuteAsync(Args(new { panel = phone.Id }), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.False(doc.RootElement.TryGetProperty("grid", out _));
        Assert.NotEqual(0, doc.RootElement.GetProperty("layout").GetProperty("pages").GetArrayLength());
    }

    [Fact]
    public async Task UpdateLook_applies_look_fields()
    {
        var tool = new UpdatePanelLookTool(_registry, _media, _hub);

        var result = await tool.ExecuteAsync(Args(new
        {
            panel = _y70,
            themeMode = "dark",
            accentColor = "#FF8800",
            accentSyncWithDesktop = false,
            backgroundMode = "shader",
            backgroundEffect = "aurora",
            widgetOpacity = 0.6,
            font = "oswald",
            gaugeGradient = new object[] { new { at = 1, color = "#ff0000" }, new { at = 0, color = "accent" } },
        }), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var r = Y70;
        Assert.Equal("dark", r.ThemeMode);
        Assert.Equal("#ff8800", r.AccentColor);
        Assert.False(r.AccentSyncWithDesktop);
        Assert.Equal("shader", r.BackgroundMode);
        Assert.Equal("aurora", r.BackgroundEffect);
        Assert.Equal(0, r.BackgroundTemplate);
        Assert.Equal(0.6, r.WidgetOpacity);
        Assert.Equal("oswald", r.Font);
        Assert.Equal(new[] { "accent", "#ff0000" }, r.GaugeGradient!.Select(s => s.Color));
    }

    [Fact]
    public async Task UpdateLook_template_is_remembered_per_effect_like_the_editor()
    {
        var tool = new UpdatePanelLookTool(_registry, _media, _hub);
        await tool.ExecuteAsync(Args(new { panel = _y70, backgroundEffect = "nebula", backgroundTemplate = 2 }), CancellationToken.None);
        await tool.ExecuteAsync(Args(new { panel = _y70, backgroundEffect = "plasma" }), CancellationToken.None);
        Assert.Equal(0, Y70.BackgroundTemplate);

        await tool.ExecuteAsync(Args(new { panel = _y70, backgroundEffect = "nebula" }), CancellationToken.None);

        Assert.Equal(2, Y70.BackgroundTemplate);
        Assert.Equal(2, Y70.BackgroundTemplates!["nebula"]);
    }

    [Fact]
    public async Task UpdateLook_with_any_invalid_field_changes_nothing()
    {
        var tool = new UpdatePanelLookTool(_registry, _media, _hub);

        var result = await tool.ExecuteAsync(Args(new
        {
            panel = _y70,
            accentColor = "#00ff00",
            backgroundEffect = "not-an-effect",
            backgroundMode = "video",
            widgetOpacity = 2,
            font = "comic",
        }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("backgroundEffect", result.Text, StringComparison.Ordinal);
        Assert.Contains("backgroundMode", result.Text, StringComparison.Ordinal);
        Assert.Contains("widgetOpacity", result.Text, StringComparison.Ordinal);
        Assert.Contains("font", result.Text, StringComparison.Ordinal);
        Assert.Null(Y70.AccentColor);
    }

    [Theory]
    [InlineData("restrictedMode", "true")]
    [InlineData("layout", "{}")]
    [InlineData("lcdBrightness", "10")]
    public async Task UpdateLook_refuses_fields_outside_the_look(string field, string value)
    {
        var tool = new UpdatePanelLookTool(_registry, _media, _hub);

        var result = await tool.ExecuteAsync(Json($"{{\"panel\":\"{_y70}\",\"{field}\":{value}}}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains($"'{field}' is not a look field", result.Text, StringComparison.Ordinal);
        Assert.Null(Y70.RestrictedMode);
    }

    [Fact]
    public async Task UpdateLook_media_id_must_exist_and_selects_its_type()
    {
        var tool = new UpdatePanelLookTool(_registry, _media, _hub);
        var missing = await tool.ExecuteAsync(Args(new { panel = _y70, backgroundMediaId = Guid.NewGuid().ToString("N") }), CancellationToken.None);
        Assert.True(missing.IsError);

        var id = AddMedia("animated");
        var result = await tool.ExecuteAsync(Args(new { panel = _y70, backgroundMode = "media", backgroundMediaId = id }), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.Equal(id, Y70.BackgroundMediaId);
        Assert.Equal("animated", Y70.BackgroundMediaType);
    }

    [Fact]
    public async Task SetLayout_adds_a_widget_with_a_new_id_and_keeps_existing_config()
    {
        var tool = new SetPanelLayoutTool(_registry, _hub);

        var result = await tool.ExecuteAsync(Json($$$"""
            {"panel":"{{{_y70}}}","pages":[{"id":"p1","widgets":[
              {"id":"mon1","type":"monitoring","size":"4x4","col":0,"row":0,"config":{"slot1_design":"dial"}},
              {"type":"media","size":"4x2","col":0,"row":4},
              {"type":"weather","size":"2x2round","col":0,"row":6},
              {"type":"gallery","size":"4x6","col":0,"row":8}
            ]}]}
            """), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(3, doc.RootElement.GetProperty("assignedIds").GetArrayLength());
        var widgets = Widgets;
        Assert.Equal(new[] { "monitoring", "media", "weather", "gallery" }, widgets.Select(w => w.Type));
        Assert.Equal("dial", widgets[0].Config!["slot1_design"].GetString());
        Assert.All(widgets, w => Assert.False(string.IsNullOrEmpty(w.Id)));
        Assert.Equal("p1", Y70.Layout!.ActivePageId);
    }

    [Theory]
    [InlineData("{\"type\":\"clock\",\"size\":\"4x4\",\"col\":0,\"row\":14}", "runs off the 4x16 grid")]
    [InlineData("{\"type\":\"clock\",\"size\":\"4x4\",\"col\":2,\"row\":4}", "runs off the 4x16 grid")]
    [InlineData("{\"type\":\"clock\",\"size\":\"2x2\",\"col\":0,\"row\":3}", "overlaps widget")]
    [InlineData("{\"type\":\"mixer\",\"size\":\"2x2\",\"col\":0,\"row\":4}", "Sizes: 4x4")]
    [InlineData("{\"type\":\"clock\",\"size\":\"4x6\",\"col\":0,\"row\":4}", "is not offered")]
    [InlineData("{\"type\":\"toaster\",\"size\":\"2x2\",\"col\":0,\"row\":4}", "unknown widget type")]
    [InlineData("{\"type\":\"clock\",\"size\":\"big\",\"col\":0,\"row\":4}", "is not \"<cols>x<rows>\"")]
    [InlineData("{\"type\":\"clock\",\"col\":0,\"row\":4}", "'size' is required")]
    [InlineData("{\"type\":\"clock\",\"size\":\"2x2\",\"col\":-1,\"row\":4}", "0 or more")]
    [InlineData("{\"id\":\"clock1\",\"type\":\"clock\",\"size\":\"2x2\",\"col\":0,\"row\":4}", "id is used twice")]
    public async Task SetLayout_rejects_an_invalid_layout_and_saves_nothing(string extraWidget, string expected)
    {
        var tool = new SetPanelLayoutTool(_registry, _hub);
        var before = JsonSerializer.Serialize(Y70.Layout);

        var result = await tool.ExecuteAsync(Json($$$"""
            {"panel":"{{{_y70}}}","pages":[{"id":"p1","widgets":[
              {"id":"clock1","type":"clock","size":"4x4","col":0,"row":0},
              {{{extraWidget}}}
            ]}]}
            """), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(Y70.Layout));
    }

    [Fact]
    public async Task SetLayout_bounds_follow_the_records_orientation()
    {
        _registry.Patch(_y70, new PanelDevicePatch { Capabilities = new PanelDeviceCapabilities { Orientation = "landscape" } });
        var tool = new SetPanelLayoutTool(_registry, _hub);

        var wide = await tool.ExecuteAsync(Json($$$"""
            {"panel":"{{{_y70}}}","pages":[{"id":"p1","widgets":[{"type":"clock","size":"4x4","col":12,"row":0}]}]}
            """), CancellationToken.None);
        var tall = await tool.ExecuteAsync(Json($$$"""
            {"panel":"{{{_y70}}}","pages":[{"id":"p1","widgets":[{"type":"clock","size":"2x4","col":0,"row":2}]}]}
            """), CancellationToken.None);

        Assert.False(wide.IsError, wide.Text);
        Assert.True(tall.IsError);
        Assert.Contains("16x4 grid", tall.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetLayout_refuses_new_privileged_deck_keys()
    {
        var tool = new SetPanelLayoutTool(_registry, _hub);

        var result = await tool.ExecuteAsync(DeckLayout("{\"type\":\"hotkey\",\"keys\":\"meta+r\"}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Nexus app", result.Text, StringComparison.Ordinal);
        Assert.Equal(new[] { "clock1", "mon1" }, Widgets.Select(w => w.Id));
    }

    [Fact]
    public async Task SetLayout_allows_unprivileged_deck_keys()
    {
        var tool = new SetPanelLayoutTool(_registry, _hub);

        var result = await tool.ExecuteAsync(DeckLayout("{\"type\":\"openUrl\",\"url\":\"https://hellonexus.com\"}"), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task An_edit_does_not_fail_on_a_problem_the_stored_layout_already_has()
    {
        var stored = Y70.Layout!;
        stored.Pages[0].Widgets.Add(new PanelWidgetDto { Id = "old", Type = "future-widget", Size = "2x2", Col = 0, Row = 3 });
        _registry.Patch(_y70, new PanelDevicePatch { Layout = stored });
        var tool = new UpdatePanelWidgetTool(_registry, _hub);

        var result = await tool.ExecuteAsync(Args(new { panel = _y70, widget = "mon1", config = new { slot1_design = "dial" } }), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task UpdateWidget_merges_config_and_removes_null_keys()
    {
        var tool = new UpdatePanelWidgetTool(_registry, _hub);

        var result = await tool.ExecuteAsync(Json($$$"""
            {"panel":"{{{_y70}}}","widget":"mon1","config":{"slot1_design":"dial","slot2_design":null,"slot3_sensor":"cpu-temp"}}
            """), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var mon = Widgets.Single(w => w.Id == "mon1");
        Assert.Equal("dial", mon.Config!["slot1_design"].GetString());
        Assert.False(mon.Config.ContainsKey("slot2_design"));
        Assert.Equal("cpu-temp", mon.Config["slot3_sensor"].GetString());
        Assert.Equal("4x4", mon.Size);
    }

    [Fact]
    public async Task UpdateWidget_moves_and_resizes_within_the_grid()
    {
        var tool = new UpdatePanelWidgetTool(_registry, _hub);

        var ok = await tool.ExecuteAsync(Args(new { panel = _y70, widget = "mon1", size = "4x2", row = 8 }), CancellationToken.None);
        var overlap = await tool.ExecuteAsync(Args(new { panel = _y70, widget = "mon1", row = 2 }), CancellationToken.None);

        Assert.False(ok.IsError, ok.Text);
        Assert.True(overlap.IsError);
        Assert.Contains("overlaps", overlap.Text, StringComparison.Ordinal);
        var mon = Widgets.Single(w => w.Id == "mon1");
        Assert.Equal(("4x2", 8), (mon.Size, mon.Row));
    }

    [Fact]
    public async Task UpdateWidget_unknown_widget_lists_the_panels_widgets()
    {
        var result = await new UpdatePanelWidgetTool(_registry, _hub)
            .ExecuteAsync(Args(new { panel = _y70, widget = "zzz", size = "2x2" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("clock1 (clock)", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presets_create_rename_activate_and_delete_by_name()
    {
        var look = new UpdatePanelLookTool(_registry, _media, _hub);
        await look.ExecuteAsync(Args(new { panel = _y70, accentColor = "#111111" }), CancellationToken.None);
        var created = await new CreatePanelPresetTool(_registry).ExecuteAsync(Args(new { panel = _y70, name = "Day" }), CancellationToken.None);
        Assert.False(created.IsError, created.Text);
        await new CreatePanelPresetTool(_registry).ExecuteAsync(Args(new { panel = _y70, name = "Night" }), CancellationToken.None);
        await look.ExecuteAsync(Args(new { panel = _y70, accentColor = "#222222" }), CancellationToken.None);

        var renamed = await new RenamePanelPresetTool(_registry).ExecuteAsync(Args(new { panel = _y70, preset = "day", name = "Morning" }), CancellationToken.None);
        var activated = await new ActivatePanelPresetTool(_registry, _hub).ExecuteAsync(Args(new { panel = _y70, preset = "Morning" }), CancellationToken.None);

        Assert.False(renamed.IsError, renamed.Text);
        Assert.False(activated.IsError, activated.Text);
        Assert.Equal("#111111", Y70.AccentColor);
        var list = await new ListPanelPresetsTool(_registry).ExecuteAsync(Args(new { panel = _y70 }), CancellationToken.None);
        using (var doc = JsonDocument.Parse(list.Text))
        {
            var presets = doc.RootElement.GetProperty("presets").EnumerateArray().ToList();
            Assert.Equal(new[] { "Morning", "Night" }, presets.Select(p => p.GetProperty("name").GetString()));
            Assert.True(presets[0].GetProperty("active").GetBoolean());
        }

        await new ActivatePanelPresetTool(_registry, _hub).ExecuteAsync(Args(new { panel = _y70, preset = "Night" }), CancellationToken.None);
        Assert.Equal("#222222", Y70.AccentColor);

        var deleted = await new DeletePanelPresetTool(_registry).ExecuteAsync(Args(new { panel = _y70, preset = "Morning" }), CancellationToken.None);
        Assert.False(deleted.IsError, deleted.Text);
        Assert.Equal("Night", Assert.Single(_registry.ListPresets(_y70)!.Presets).Name);
    }

    [Fact]
    public async Task Presets_unknown_preset_lists_known_presets()
    {
        await new CreatePanelPresetTool(_registry).ExecuteAsync(Args(new { panel = _y70, name = "Day" }), CancellationToken.None);

        var result = await new ActivatePanelPresetTool(_registry, _hub).ExecuteAsync(Args(new { panel = _y70, preset = "Evening" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Day (", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePreset_reports_the_cap()
    {
        var tool = new CreatePanelPresetTool(_registry);
        for (var i = 0; i < PanelPresets.Cap; i++)
        {
            await tool.ExecuteAsync(Args(new { panel = _y70, name = $"P{i}" }), CancellationToken.None);
        }

        var result = await tool.ExecuteAsync(Args(new { panel = _y70, name = "One more" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("delete_panel_preset", result.Text, StringComparison.Ordinal);
    }

    private JsonElement DeckLayout(string action) => Json(
        "{\"panel\":\"" + _y70 + "\",\"pages\":[{\"id\":\"p1\",\"widgets\":[{\"type\":\"deck\",\"size\":\"4x2\",\"col\":0,\"row\":0," +
        "\"config\":{\"deck\":{\"pages\":[{\"slots\":[{\"action\":" + action + "}]}]}}}]}]}");

    private string AddMedia(string type)
    {
        var id = Guid.NewGuid().ToString("N");
        var dir = _media.GetItemDir(_y70, id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "media.mp4"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(dir, "thumb.jpg"), new byte[] { 0 });
        _media.SaveMeta(_y70, new PanelBgItem { Id = id, Name = "Loop", Type = type });
        return id;
    }

    private sealed class ListSink(List<McpAuditEntry> entries) : IMcpAuditSink
    {
        public void Record(McpAuditEntry entry) => entries.Add(entry);
    }
}

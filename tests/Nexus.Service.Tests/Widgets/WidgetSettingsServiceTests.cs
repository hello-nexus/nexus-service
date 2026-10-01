using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

/// <summary>
/// Per-instance marketplace widget settings storage. Each placement carries
/// its own <see cref="PanelWidgetDto.Config"/> dictionary in the layout;
/// the service merges manifest defaults on read and writes overrides on
/// apply. Tests wire up a real <see cref="AppRegistry"/> against a
/// fixture manifest folder so registry behavior (schema gate, folder/id
/// match) is exercised end-to-end.
/// </summary>
public class WidgetSettingsServiceTests : IDisposable
{
    private const string AppId = "com.hellonexus.test";
    private const string InstanceId = "test-instance";
    private const string MarketplaceType = "app:" + AppId;

    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly JsonConfigStore _store;
    private readonly AppRegistry _registry;

    public WidgetSettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-widget-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "widgets-root"));
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new JsonConfigStore(_settingsPath);
        var widgetsRoot = Path.Combine(_tempDir, "widgets-root");
        _registry = new AppRegistry(() => new[]
        {
            new AppInstallPaths.Root(widgetsRoot, AppInstallPaths.Source.User),
        });
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Write a manifest fixture and seed a single placement in the dashboard
    /// layout. Returns a fresh <see cref="WidgetSettingsService"/> ready to use.
    /// </summary>
    private WidgetSettingsService SetupWidget(params (string Key, string DefaultJson)[] settings)
    {
        var widgetDir = Path.Combine(_tempDir, "widgets-root", AppId);
        Directory.CreateDirectory(widgetDir);
        File.WriteAllText(Path.Combine(widgetDir, "manifest.json"), BuildManifestJson(settings));
        File.WriteAllText(Path.Combine(widgetDir, "widget.mjs"), "export const mount = () => {};");

        _store.Update(s =>
        {
            s.Panel.DashboardLayout = new PanelLayoutDto
            {
                Pages =
                {
                    new PanelPageDto
                    {
                        Id = "page-1",
                        Widgets =
                        {
                            new PanelWidgetDto { Id = InstanceId, Type = MarketplaceType, Size = "2x2" },
                        },
                    },
                },
            };
        });

        _registry.Refresh();
        return new WidgetSettingsService(_store, _registry);
    }

    private static string BuildManifestJson((string Key, string DefaultJson)[] settings)
    {
        var settingsJson = new System.Text.StringBuilder();
        for (var i = 0; i < settings.Length; i++)
        {
            if (i > 0) settingsJson.Append(',');
            settingsJson.Append($"{{\"key\":\"{settings[i].Key}\",\"type\":\"string\",\"default\":{settings[i].DefaultJson}}}");
        }
        return $$"""
        {
          "schema": "nexus.app/1",
          "id": "{{AppId}}",
          "name": "Test Widget",
          "version": "1.0.0",
          "min_nexus_version": "0.0.0",
          "surfaces": ["dashboard"],
          "sizes": ["2x2"],
          "runtime": "sdk",
          "settings": [{{settingsJson}}]
        }
        """;
    }

    [Fact]
    public void Playlist_stand_in_reads_and_writes_the_devices_remembered_config()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""), ("scale", "1"));
        using var remembered = JsonDocument.Parse("\"#123456\"");
        _store.Update(s =>
        {
            s.PanelDevices["q60dev"] = new PanelDeviceRecord
            {
                Id = "q60dev",
                Layout = new PanelLayoutDto
                {
                    Surface = "q60",
                    SingleWidgetConfigs = new Dictionary<string, Dictionary<string, JsonElement>>
                    {
                        [MarketplaceType] = new() { ["color"] = remembered.RootElement.Clone() },
                    },
                },
            };
        });
        var standIn = $"playlist:q60dev:{MarketplaceType}";

        var doc = svc.Get(standIn);
        Assert.Equal("#123456", doc.Values["color"].GetString());
        Assert.Equal(1, doc.Values["scale"].GetInt32());

        using var scale = JsonDocument.Parse("5");
        svc.Apply(standIn, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["scale"] = scale.RootElement.Clone() },
        });
        var stored = _store.Load().PanelDevices["q60dev"].Layout!.SingleWidgetConfigs![MarketplaceType];
        Assert.Equal("#123456", stored["color"].GetString());
        Assert.Equal(5, stored["scale"].GetInt32());

        Assert.Empty(svc.Get($"playlist:missing:{MarketplaceType}").Values);
    }

    [Fact]
    public void Get_returns_manifest_defaults_when_no_overrides()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""), ("scale", "1"));

        var doc = svc.Get(InstanceId);

        Assert.Equal(2, doc.Values.Count);
        Assert.Equal("#ff8800", doc.Values["color"].GetString());
        Assert.Equal(1, doc.Values["scale"].GetInt32());
    }

    [Fact]
    public void Apply_persists_overrides_and_round_trips_through_reload()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""), ("scale", "1"));

        using var colorDoc = JsonDocument.Parse("\"#00ffaa\"");
        svc.Apply(InstanceId, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = colorDoc.RootElement.Clone() },
        });
        _store.FlushNow();

        // Reload via a fresh store + registry pointing at the same fixtures
        // (simulates a nexus-service restart).
        using var reloaded = new JsonConfigStore(_settingsPath);
        var widgetsRoot = Path.Combine(_tempDir, "widgets-root");
        var registry2 = new AppRegistry(() => new[]
        {
            new AppInstallPaths.Root(widgetsRoot, AppInstallPaths.Source.User),
        });
        var reloadedSvc = new WidgetSettingsService(reloaded, registry2);

        var got = reloadedSvc.Get(InstanceId);
        Assert.Equal("#00ffaa", got.Values["color"].GetString());
        Assert.Equal(1, got.Values["scale"].GetInt32());
    }

    [Fact]
    public void Apply_reset_drops_back_to_manifest_default()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""));

        using var override1 = JsonDocument.Parse("\"#123456\"");
        svc.Apply(InstanceId, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = override1.RootElement.Clone() },
        });
        Assert.Equal("#123456", svc.Get(InstanceId).Values["color"].GetString());

        svc.Apply(InstanceId, new WidgetSettingsPatch { Reset = new List<string> { "color" } });
        Assert.Equal("#ff8800", svc.Get(InstanceId).Values["color"].GetString());
    }

    [Fact]
    public void Apply_set_then_reset_overlap_reset_wins()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""));

        using var doc = JsonDocument.Parse("\"#ABCDEF\"");
        svc.Apply(InstanceId, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = doc.RootElement.Clone() },
            Reset = new List<string> { "color" },
        });

        Assert.Equal("#ff8800", svc.Get(InstanceId).Values["color"].GetString());
    }

    [Fact]
    public void Apply_empty_patch_does_not_insert_a_hollow_config_block()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""));

        svc.Apply(InstanceId, new WidgetSettingsPatch());
        _store.FlushNow();

        var loaded = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement;
        var widget = FindWidgetJson(loaded, InstanceId);
        Assert.False(widget.TryGetProperty("config", out _));
    }

    [Fact]
    public void Reset_only_removes_the_config_block_when_no_overrides_remain()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""));

        using var doc = JsonDocument.Parse("\"#abcdef\"");
        svc.Apply(InstanceId, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = doc.RootElement.Clone() },
        });
        svc.Apply(InstanceId, new WidgetSettingsPatch { Reset = new List<string> { "color" } });
        _store.FlushNow();

        var loaded = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement;
        var widget = FindWidgetJson(loaded, InstanceId);
        Assert.False(widget.TryGetProperty("config", out _));
    }

    [Fact]
    public void Settings_round_trip_handles_quoted_strings_unicode_and_large_blobs()
    {
        var svc = SetupWidget(("blob", "\"\""));

        var weird = "He said \"hi\" - 🎉 " + new string('x', 4096);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(weird));
        svc.Apply(InstanceId, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["blob"] = doc.RootElement.Clone() },
        });
        _store.FlushNow();

        using var reloaded = new JsonConfigStore(_settingsPath);
        var widgetsRoot = Path.Combine(_tempDir, "widgets-root");
        var registry2 = new AppRegistry(() => new[]
        {
            new AppInstallPaths.Root(widgetsRoot, AppInstallPaths.Source.User),
        });
        var got = new WidgetSettingsService(reloaded, registry2).Get(InstanceId);
        Assert.Equal(weird, got.Values["blob"].GetString());
    }

    [Fact]
    public void Apply_silently_drops_keys_not_in_manifest()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""));

        using var stray = JsonDocument.Parse("\"hostile\"");
        using var color = JsonDocument.Parse("\"#abcdef\"");
        svc.Apply(InstanceId, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement>
            {
                ["color"] = color.RootElement.Clone(),
                ["unknown.key"] = stray.RootElement.Clone(),
            },
        });
        var doc = svc.Get(InstanceId);
        Assert.False(doc.Values.ContainsKey("unknown.key"));
        Assert.Equal("#abcdef", doc.Values["color"].GetString());
    }

    [Fact]
    public void Get_returns_empty_doc_for_unknown_instance_id()
    {
        var svc = SetupWidget(("color", "\"#ff8800\""));
        var doc = svc.Get("does-not-exist");
        Assert.Empty(doc.Values);
    }

    [Fact]
    public void Get_returns_empty_doc_for_non_marketplace_widget_type()
    {
        SetupWidget(("color", "\"#ff8800\""));
        _store.Update(s =>
        {
            s.Panel.DashboardLayout!.Pages[0].Widgets.Add(new PanelWidgetDto
            {
                Id = "native-clock",
                Type = "clock", // not the app: prefix
                Size = "1x1",
            });
        });

        var svc = new WidgetSettingsService(_store, _registry);
        var doc = svc.Get("native-clock");
        Assert.Empty(doc.Values);
    }

    // ── Helpers ──

    /// <summary>
    /// Walk the persisted settings JSON to the placement entry with this id.
    /// Mirrors the way the service's FindWidget locates placements at runtime.
    /// </summary>
    private static JsonElement FindWidgetJson(JsonElement root, string instanceId)
    {
        var pages = root.GetProperty("panel").GetProperty("dashboardLayout").GetProperty("pages");
        foreach (var page in pages.EnumerateArray())
        {
            foreach (var w in page.GetProperty("widgets").EnumerateArray())
            {
                if (w.GetProperty("id").GetString() == instanceId) return w;
            }
        }
        throw new InvalidOperationException($"Widget {instanceId} not found in persisted settings.");
    }
}

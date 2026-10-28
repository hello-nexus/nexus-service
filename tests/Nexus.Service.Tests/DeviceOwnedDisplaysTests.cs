using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class DeviceOwnedDisplaysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-owneddisp-" + Guid.NewGuid().ToString("N")[..8]);

    public DeviceOwnedDisplaysTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeProvider : IDisplayTopologyProvider
    {
        public List<RawDisplayInfo>? Displays = new();
        public bool PositionsAvailable => true;
        public IReadOnlyList<RawDisplayInfo>? Enumerate() => Displays;
    }

    private static RawDisplayInfo Monitor(string id, string mfg, string model, int w = 1920, int h = 1080, bool primary = false, string evidence = "")
        => new()
        {
            Id = id,
            Name = mfg + " " + model,
            Manufacturer = mfg,
            Model = model,
            X = primary ? 0 : 1920,
            Y = 0,
            Width = w,
            Height = h,
            ResolutionWidth = w,
            ResolutionHeight = h,
            IsPrimary = primary,
            RawHardwareId = $@"\\?\DISPLAY#{mfg}{model}#5&abc#{{guid}}",
            Fingerprint = new DisplayFingerprint { Manufacturer = mfg, Product = model },
            AdapterEvidence = evidence,
        };

    [Theory]
    [InlineData("NXS", "0001", true)]
    [InlineData("nxs", "0001", true)]
    [InlineData("TUR", "0000", false)]
    [InlineData("DEL", "41B7", false)]
    [InlineData("CRX", "ED00", false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void IsOwned_matches_edid_identity(string? mfg, string? product, bool expected)
    {
        Assert.Equal(expected, DeviceOwnedDisplays.IsOwned(mfg, product));
    }

    [Fact]
    public void IsOwned_ignores_resolution()
    {
        // Size never decides ownership.
        Assert.False(DeviceOwnedDisplays.IsOwned(Monitor("DEL41B7-1", "DEL", "41B7", 1920, 480)));
        Assert.True(DeviceOwnedDisplays.IsOwned(Monitor("NXS0001-1", "NXS", "0001", 2560, 1440)));
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#TUR0000#5&512482b&0&UID256#{guid}", false)]
    [InlineData("NXS0001-1-10f369c6-0-UID256", true)]
    [InlineData("TUR0000-a-512482b-0-UID256", false)]
    [InlineData(@"\\?\DISPLAY#DEL41B7#5&abc#{guid}", false)]
    [InlineData("", false)]
    public void IsOwnedHardwareId_matches_pnp_path_and_display_id(string id, bool expected)
    {
        Assert.Equal(expected, DeviceOwnedDisplays.IsOwnedHardwareId(id));
    }

    [Fact]
    public void Topology_omits_owned_displays_and_keeps_others_including_plain_TUR()
    {
        var provider = new FakeProvider
        {
            Displays = new List<RawDisplayInfo>
            {
                Monitor("DEL41B7-1", "DEL", "41B7", primary: true),
                Monitor("NXS0001-1-10f369c6-0-UID256", "NXS", "0001", 1920, 480),
                Monitor("TUR0000-a-512482b-0-UID256", "TUR", "0000", 480, 480),
                Monitor("TUR0000-b-9-0-UID1", "TUR", "0000", 480, 480, evidence: "LIANLI USB Secondary Display-60hz USB\\VID_1A86&PID_AD20\\5"),
            },
        };
        using var store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        var service = new DisplayTopologyService(provider, new PanelDeviceRegistry(store));

        var topo = service.GetTopology();

        Assert.Equal(new[] { "DEL41B7-1", "TUR0000-a-512482b-0-UID256" }, topo.Displays.Select(d => d.Id).ToArray());
        Assert.Null(service.FindDisplay("NXS0001-1-10f369c6-0-UID256"));
    }

    [Theory]
    [InlineData("LIANLI USB Secondary Display-60hz", true)]
    [InlineData("Lian Li USB Secondary Display", true)]
    [InlineData("LIAN LI Display", true)]
    [InlineData(@"USB\VID_1A86&PID_AD20\5&1", true)]
    [InlineData("Generic PnP Monitor", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Shared_usb_identity_is_owned_only_with_lianli_evidence(string? evidence, bool expected)
    {
        Assert.Equal(expected, DeviceOwnedDisplays.IsOwned("TUR", "0000", evidence));
        Assert.False(DeviceOwnedDisplays.IsOwned("DEL", "41B7", evidence));
    }

    [Fact]
    public void Lianli_usb_display_is_hidden_and_its_stale_record_pruned_while_plain_TUR_record_stays()
    {
        const string lianLiId = "TUR0000-b-9-0-UID1";
        const string plainId = "TUR0000-a-512482b-0-UID256";
        var provider = new FakeProvider
        {
            Displays = new List<RawDisplayInfo>
            {
                Monitor(lianLiId, "TUR", "0000", 480, 480, evidence: "LIANLI USB Secondary Display-60hz"),
                Monitor(plainId, "TUR", "0000", 480, 480),
            },
        };
        using var store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        store.Update(s =>
        {
            s.PanelDevices["lianli"] = new PanelDeviceRecord { Id = "lianli", DisplayId = lianLiId };
            s.PanelDevices["plain"] = new PanelDeviceRecord { Id = "plain", DisplayId = plainId };
        });
        var registry = new PanelDeviceRegistry(store);
        var service = new DisplayTopologyService(provider, registry) { RemoveOwnedRecord = registry.Remove };

        var topo = service.GetTopology();

        Assert.Equal(new[] { plainId }, topo.Displays.Select(d => d.Id).ToArray());
        Assert.Equal(new[] { "plain" }, store.Load().PanelDevices.Keys.ToArray());
    }

    [Fact]
    public void Auto_promotion_and_widget_targets_never_see_owned_displays()
    {
        var provider = new FakeProvider
        {
            Displays = new List<RawDisplayInfo>
            {
                Monitor("NXS0001-1-10f369c6-0-UID256", "NXS", "0001", 1920, 480),
            },
        };
        using var store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        var registry = new PanelDeviceRegistry(store);
        var service = new DisplayTopologyService(provider, registry);

        var promoted = new PanelAutoPromotion(registry, store).Reconcile(service.GetTopology());

        Assert.Empty(promoted);
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void PruneRecords_removes_owned_records_by_fingerprint_or_display_id_only()
    {
        var settings = new NexusSettings();
        settings.PanelDevices["a"] = new PanelDeviceRecord
        {
            Id = "a",
            DisplayId = "NXS0001-1-10f369c6-0-UID256",
            DisplayFingerprint = new DisplayFingerprint { Manufacturer = "NXS", Product = "0001" },
        };
        settings.PanelDevices["b"] = new PanelDeviceRecord { Id = "b", DisplayId = "NXS0001-2-aa-0-UID1" };
        settings.PanelDevices["t"] = new PanelDeviceRecord
        {
            Id = "t",
            DisplayId = "TUR0000-a-512482b-0-UID256",
            DisplayFingerprint = new DisplayFingerprint { Manufacturer = "TUR", Product = "0000" },
        };
        settings.PanelDevices["c"] = new PanelDeviceRecord
        {
            Id = "c",
            DisplayId = "CRXED00-2",
            DisplayFingerprint = new DisplayFingerprint { Manufacturer = "CRX", Product = "ED00" },
        };
        settings.PanelDevices["d"] = new PanelDeviceRecord { Id = "d" };

        Assert.Equal(new[] { "a", "b" }, DeviceOwnedDisplays.PruneRecords(settings).OrderBy(k => k).ToArray());

        Assert.Equal(new[] { "c", "d", "t" }, settings.PanelDevices.Keys.OrderBy(k => k).ToArray());
        Assert.Empty(DeviceOwnedDisplays.PruneRecords(settings));
    }

    [Fact]
    public void Load_drops_stale_owned_records_and_persists()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "panelDevices": {
            "1xJ3LyCG8eJF": { "id": "1xJ3LyCG8eJF", "displayId": "NXS0001-1-10f369c6-0-UID256",
              "displayFingerprint": { "manufacturer": "NXS", "product": "0001" } },
            "plainTur": { "id": "plainTur", "displayId": "TUR0000-a-512482b-0-UID256",
              "displayFingerprint": { "manufacturer": "TUR", "product": "0000" } },
            "keep": { "id": "keep", "displayId": "DEL41B7-1" }
          }
        }
        """);

        using (var store = new JsonConfigStore(path))
        {
            Assert.Equal(new[] { "keep", "plainTur" }, store.Load().PanelDevices.Keys.OrderBy(k => k).ToArray());
            store.FlushNow();
        }

        using var reloaded = new JsonConfigStore(path);
        Assert.Equal(new[] { "keep", "plainTur" }, reloaded.Load().PanelDevices.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void Load_with_null_panelDevices_keeps_the_config()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
        { "schemaVersion": {{NexusSettings.CurrentSchemaVersion}}, "theme": { "accentColor": "#aabbcc" }, "panelDevices": null }
        """);

        using var store = new JsonConfigStore(path);

        Assert.Equal("#aabbcc", store.Load().Theme.AccentColor);
    }

    [Fact]
    public void Load_with_null_record_value_keeps_the_config()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "theme": { "accentColor": "#aabbcc" },
          "panelDevices": {
            "nul": null,
            "stale": { "id": "stale", "displayId": "NXS0001-1-10f369c6-0-UID256" }
          }
        }
        """);

        using var store = new JsonConfigStore(path);
        var loaded = store.Load();

        Assert.Equal("#aabbcc", loaded.Theme.AccentColor);
        Assert.False(loaded.PanelDevices.ContainsKey("stale"));
    }

    [Fact]
    public void Prune_tolerates_null_collection_and_null_entries()
    {
        var withNull = new NexusSettings { PanelDevices = null! };
        Assert.Empty(DeviceOwnedDisplays.PruneRecords(withNull));

        var settings = new NexusSettings();
        settings.PanelDevices["x"] = null!;
        Assert.Empty(DeviceOwnedDisplays.PruneRecords(settings));
    }

    [Fact]
    public void Load_survives_a_failing_persist_of_the_pruned_document()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "theme": { "accentColor": "#aabbcc" },
          "panelDevices": { "stale": { "id": "stale", "displayId": "NXS0001-1-10f369c6-0-UID256" } }
        }
        """);
        // A directory squatting on the temp file name makes the atomic write fail.
        Directory.CreateDirectory(AtomicJsonFile.TempPathFor(path));

        using var store = new JsonConfigStore(path);
        var loaded = store.Load();

        Assert.Equal("#aabbcc", loaded.Theme.AccentColor);
        Assert.Empty(loaded.PanelDevices);
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Scan_with_nothing_to_remove_writes_nothing_and_raises_no_change()
    {
        var provider = new FakeProvider
        {
            Displays = new List<RawDisplayInfo>
            {
                Monitor("NXS0001-1-10f369c6-0-UID256", "NXS", "0001", 1920, 480),
                Monitor("DEL41B7-1", "DEL", "41B7", primary: true),
            },
        };
        using var store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        var service = new DisplayTopologyService(provider, new PanelDeviceRegistry(store));
        service.GetTopology();
        var changes = 0;
        store.OnChanged += () => changes++;

        service.GetTopology();
        service.GetTopology();

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Owned_record_is_removed_through_the_remover_hook()
    {
        const string nxsId = "NXS0001-1-10f369c6-0-UID256";
        var provider = new FakeProvider { Displays = new List<RawDisplayInfo> { Monitor(nxsId, "NXS", "0001", 1920, 480) } };
        using var store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        store.Update(s => s.PanelDevices["rec"] = new PanelDeviceRecord { Id = "rec", DisplayId = nxsId });
        var removed = new List<string>();
        var registry = new PanelDeviceRegistry(store);
        var service = new DisplayTopologyService(provider, registry)
        {
            RemoveOwnedRecord = id => { removed.Add(id); return registry.Remove(id); },
        };

        service.GetTopology();

        Assert.Equal(new[] { "rec" }, removed.ToArray());
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void Load_deletes_pruned_record_media_inside_the_store_root_only()
    {
        var path = Path.Combine(_dir, "settings.json");
        var mediaDir = Path.Combine(_dir, "devices", "panel-backgrounds", "stale");
        Directory.CreateDirectory(mediaDir);
        File.WriteAllText(Path.Combine(mediaDir, "bg.jpg"), "x");
        var keepDir = Path.Combine(_dir, "devices", "panel-backgrounds", "keep");
        Directory.CreateDirectory(keepDir);
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "panelDevices": {
            "stale": { "id": "stale", "displayId": "NXS0001-1-10f369c6-0-UID256" },
            "keep": { "id": "keep", "displayId": "DEL41B7-1" }
          }
        }
        """);

        using var store = new JsonConfigStore(path);
        store.Load();

        Assert.False(Directory.Exists(mediaDir));
        Assert.True(Directory.Exists(keepDir));
    }
}

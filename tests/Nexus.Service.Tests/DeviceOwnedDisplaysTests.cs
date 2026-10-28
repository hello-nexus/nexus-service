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

    private static RawDisplayInfo Monitor(string id, string mfg, string model, int w = 1920, int h = 1080, bool primary = false)
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
        };

    [Theory]
    [InlineData("NXS", "0001", true)]
    [InlineData("nxs", "0001", true)]
    [InlineData("TUR", "0000", true)]
    [InlineData("TUR", "0001", false)]
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
        // 1920x480 is the Universal Screen's size; an ordinary monitor of that size is not owned.
        Assert.False(DeviceOwnedDisplays.IsOwned(Monitor("DEL41B7-1", "DEL", "41B7", 1920, 480)));
        Assert.True(DeviceOwnedDisplays.IsOwned(Monitor("NXS0001-1", "NXS", "0001", 2560, 1440)));
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#TUR0000#5&512482b&0&UID256#{guid}", true)]
    [InlineData("NXS0001-1-10f369c6-0-UID256", true)]
    [InlineData("TUR0000-a-512482b-0-UID256", true)]
    [InlineData(@"\\?\DISPLAY#DEL41B7#5&abc#{guid}", false)]
    [InlineData("", false)]
    public void IsOwnedHardwareId_matches_pnp_path_and_display_id(string id, bool expected)
    {
        Assert.Equal(expected, DeviceOwnedDisplays.IsOwnedHardwareId(id));
    }

    [Fact]
    public void Topology_omits_owned_displays_and_keeps_others()
    {
        var provider = new FakeProvider
        {
            Displays = new List<RawDisplayInfo>
            {
                Monitor("DEL41B7-1", "DEL", "41B7", primary: true),
                Monitor("NXS0001-1-10f369c6-0-UID256", "NXS", "0001", 1920, 480),
                Monitor("TUR0000-a-512482b-0-UID256", "TUR", "0000", 480, 480),
            },
        };
        using var store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        var service = new DisplayTopologyService(provider, new PanelDeviceRegistry(store));

        var topo = service.GetTopology();

        Assert.Equal(new[] { "DEL41B7-1" }, topo.Displays.Select(d => d.Id).ToArray());
        Assert.Null(service.FindDisplay("NXS0001-1-10f369c6-0-UID256"));
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
        settings.PanelDevices["b"] = new PanelDeviceRecord { Id = "b", DisplayId = "TUR0000-a-512482b-0-UID256" };
        settings.PanelDevices["c"] = new PanelDeviceRecord
        {
            Id = "c",
            DisplayId = "CRXED00-2",
            DisplayFingerprint = new DisplayFingerprint { Manufacturer = "CRX", Product = "ED00" },
        };
        settings.PanelDevices["d"] = new PanelDeviceRecord { Id = "d" };

        Assert.True(DeviceOwnedDisplays.PruneRecords(settings));

        Assert.Equal(new[] { "c", "d" }, settings.PanelDevices.Keys.OrderBy(k => k).ToArray());
        Assert.False(DeviceOwnedDisplays.PruneRecords(settings));
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
            "ekTI0p_1XnZD": { "id": "ekTI0p_1XnZD", "displayId": "TUR0000-a-512482b-0-UID256",
              "displayFingerprint": { "manufacturer": "TUR", "product": "0000" } },
            "keep": { "id": "keep", "displayId": "DEL41B7-1" }
          }
        }
        """);

        using (var store = new JsonConfigStore(path))
        {
            Assert.Equal(new[] { "keep" }, store.Load().PanelDevices.Keys.ToArray());
            store.FlushNow();
        }

        using var reloaded = new JsonConfigStore(path);
        Assert.Equal(new[] { "keep" }, reloaded.Load().PanelDevices.Keys.ToArray());
    }
}

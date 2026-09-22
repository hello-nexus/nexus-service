using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Topology merge-layer stamping (Y70 detection, bounds mapping, assignment
/// join) plus the Linux EDID/mode parsing vectors. Hardware-free.
/// </summary>
public sealed class DisplayTopologyTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "nexus-displaytopo-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;

    public DisplayTopologyTests()
    {
        _store = new JsonConfigStore(_path);
        _registry = new PanelDeviceRegistry(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private sealed class FakeProvider : IDisplayTopologyProvider
    {
        public List<RawDisplayInfo>? Displays = new();
        public bool Positions = true;
        public bool PositionsAvailable => Positions;
        public IReadOnlyList<RawDisplayInfo>? Enumerate() => Displays;
    }

    private static RawDisplayInfo Monitor(string id, int x = 0, int y = 0, string rawHw = "")
        => new()
        {
            Id = id,
            Name = "DEL 41B7",
            Manufacturer = "DEL",
            Model = "41B7",
            X = x,
            Y = y,
            Width = 2560,
            Height = 1440,
            ResolutionWidth = 3840,
            ResolutionHeight = 2160,
            Scale = 1.5,
            IsPrimary = x == 0 && y == 0,
            RawHardwareId = rawHw,
        };

    [Fact]
    public void Y70_display_is_flagged_and_not_hostable()
    {
        var provider = new FakeProvider
        {
            Displays = new List<RawDisplayInfo>
            {
                Monitor("DEL41B7-1", rawHw: @"\\?\DISPLAY#DEL41B7#5&abc#{guid}"),
                Monitor("RTK0004-2", x: 2560, rawHw: @"\\?\DISPLAY#RTK0004#5&def#{guid}"),
            },
        };
        var service = new DisplayTopologyService(provider, _registry);

        var topo = service.GetTopology();

        Assert.Equal(2, topo.Displays.Count);
        Assert.False(topo.Displays[0].IsY70);
        Assert.True(topo.Displays[1].IsY70);
        Assert.False(topo.Displays[1].HostingSupported);
    }

    [Fact]
    public void Null_positions_produce_null_bounds()
    {
        var entry = Monitor("a");
        entry.X = null;
        entry.Y = null;
        var provider = new FakeProvider { Displays = new List<RawDisplayInfo> { entry }, Positions = false };
        var service = new DisplayTopologyService(provider, _registry);

        var topo = service.GetTopology();

        Assert.False(topo.PositionsAvailable);
        Assert.Null(topo.Displays[0].Bounds);
        Assert.Equal(3840, topo.Displays[0].Resolution.Width);
    }

    [Fact]
    public void Unavailable_provider_yields_hint_and_unknown_attached_ids()
    {
        var provider = new FakeProvider { Displays = null };
        var service = new DisplayTopologyService(provider, _registry);

        var topo = service.GetTopology();

        Assert.Empty(topo.Displays);
        Assert.NotEqual("", topo.Hint);
        Assert.Null(service.GetAttachedIds());
        Assert.Null(service.HasY70DisplayIfKnown());
        Assert.False(service.HasY70Display());
    }

    [Fact]
    public void Known_topology_answers_the_y70_question()
    {
        var withY70 = new DisplayTopologyService(new FakeProvider
        {
            Displays = new List<RawDisplayInfo> { Monitor("RTK0004-2", rawHw: @"\\?\DISPLAY#RTK0004#5&def#{guid}") },
        }, _registry);
        var without = new DisplayTopologyService(new FakeProvider { Displays = new List<RawDisplayInfo>() }, _registry);

        Assert.True(withY70.HasY70DisplayIfKnown());
        Assert.False(without.HasY70DisplayIfKnown());
    }

    [Fact]
    public void Assignment_is_stamped_onto_the_entry()
    {
        var provider = new FakeProvider { Displays = new List<RawDisplayInfo> { Monitor("DEL41B7-1") } };
        var service = new DisplayTopologyService(provider, _registry);
        var (record, _) = _registry.AllocateForDisplay("DEL41B7-1", "Desk monitor", new PanelDeviceCapabilities
        {
            Surface = PanelSurfaces.Monitor,
        });

        var entry = service.FindDisplay("DEL41B7-1");

        Assert.NotNull(entry);
        Assert.Equal(record.Id, entry!.AssignedPanelDeviceId);
        Assert.Equal("Desk monitor", entry.AssignedPanelName);
    }

    [Fact]
    public void Registry_round_trips_display_binding()
    {
        var (record, created) = _registry.AllocateForDisplay("DEL41B7-1", "Desk monitor", null);

        Assert.True(created);
        Assert.Equal("DEL41B7-1", _registry.Get(record.Id)?.DisplayId);
        Assert.Equal(record.Id, _registry.FindByDisplayId("DEL41B7-1")?.Id);
        Assert.Contains(_registry.ListAssignments(), a => a.DisplayId == "DEL41B7-1" && a.PanelDeviceId == record.Id);

        _registry.Remove(record.Id);
        Assert.Null(_registry.FindByDisplayId("DEL41B7-1"));
    }

    [Fact]
    public void Registry_refuses_a_second_binding_for_the_same_display()
    {
        var (first, firstCreated) = _registry.AllocateForDisplay("DEL41B7-1", "Desk monitor", null);
        var (second, secondCreated) = _registry.AllocateForDisplay("DEL41B7-1", "Desk monitor", null);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(_registry.ListAssignments());
    }

    [Fact]
    public void Attached_ids_reflect_current_enumeration()
    {
        var provider = new FakeProvider { Displays = new List<RawDisplayInfo> { Monitor("a"), Monitor("b", x: 2560) } };
        var service = new DisplayTopologyService(provider, _registry);

        var ids = service.GetAttachedIds();

        Assert.NotNull(ids);
        Assert.Contains("a", ids!);
        Assert.Contains("b", ids);
    }

    // -- Linux parsing vectors ----------------------------------------------

    [Theory]
    [InlineData("2560x1440", 2560, 1440)]
    [InlineData("3840x2160i", 3840, 2160)]
    [InlineData("", 0, 0)]
    [InlineData("garbage", 0, 0)]
    public void Linux_mode_parsing(string mode, int width, int height)
    {
        Assert.Equal((width, height), LinuxDisplayTopologyProvider.ParseMode(mode));
    }

    [Fact]
    public void Linux_edid_identity_parses_vendor_product_and_size()
    {
        var edid = BuildEdid(mfg: "DEL", product: 0x41B7, serial: 0x12345678,
            widthCm: 60, heightCm: 34, modelName: "U2723QE");

        var identity = LinuxDisplayTopologyProvider.ParseEdidIdentity(edid);

        Assert.Equal("DEL", identity.Mfg);
        Assert.Equal(0x41B7, identity.Product);
        Assert.Equal(0x12345678u, identity.Serial);
        Assert.Equal(60, identity.WidthCm);
        Assert.Equal("U2723QE", identity.ModelName);
    }

    [Fact]
    public void Linux_edid_identity_rejects_invalid_blocks()
    {
        Assert.Equal("", LinuxDisplayTopologyProvider.ParseEdidIdentity(ReadOnlySpan<byte>.Empty).Mfg);
        Assert.Equal("", LinuxDisplayTopologyProvider.ParseEdidIdentity(new byte[128]).Mfg);
    }

    // The shape WindowsDisplayIdentity reports: Name composed from the PnP
    // DeviceID segment "CRXED00"; the EDID product-name string never surfaces.
    private static RawDisplayInfo XeneonEdge(string id = "CRXED00-2") => new()
    {
        Id = id,
        Name = "CRX ED00",
        Manufacturer = "CRX",
        Model = "ED00",
        X = 3840,
        Y = 0,
        Width = 2560,
        Height = 720,
        ResolutionWidth = 2560,
        ResolutionHeight = 720,
        Scale = 1.0,
        IsTouch = true,
        Orientation = "Landscape",
    };

    [Fact]
    public void Known_display_match_reads_pnp_identity_or_edid_name()
    {
        // Windows: PnP identity from the DeviceID, no EDID name available.
        Assert.Equal("xeneon-edge", KnownPanelDisplays.Match("CRX", "ED00", "CRX ED00")?.Family);
        Assert.Equal("xeneon-edge", KnownPanelDisplays.Match("crx", "ed00", null)?.Family);
        // macOS/Linux: EDID product name in Name or Model.
        Assert.Equal("xeneon-edge", KnownPanelDisplays.Match(null, null, "XENEON EDGE")?.Family);
        Assert.Equal("xeneon-edge", KnownPanelDisplays.Match(null, null, "Corsair Xeneon Edge")?.Family);
        Assert.Equal("xeneon-edge", KnownPanelDisplays.Match(null, "xeneon edge", null)?.Family);
        Assert.Null(KnownPanelDisplays.Match("DEL", "41B7", "DEL 41B7"));
        Assert.Null(KnownPanelDisplays.Match("CRX", "1234", "CRX 1234"));
        Assert.Null(KnownPanelDisplays.Match(null, null, null));
    }

    [Fact]
    public void Promoted_capabilities_carry_density_for_known_displays()
    {
        var caps = DisplayTopologyService.BuildPromotedCapabilities(
            "CRX", "ED00", "CRX ED00", 2560, 720, 1.5, isTouch: true, orientation: "Landscape");

        Assert.Equal(PanelSurfaces.Monitor, caps.Surface);
        Assert.Equal(183, caps.Dpi);
        Assert.Equal("xeneon-edge", caps.Family);
        Assert.Equal(1707, caps.CssWidth);
        Assert.Equal(480, caps.CssHeight);
        Assert.Equal(1.5, caps.Dpr);
        Assert.True(caps.Touch);

        var generic = DisplayTopologyService.BuildPromotedCapabilities(
            "DEL", "41B7", "DEL 41B7", 3840, 2160, null, isTouch: false, orientation: "");
        Assert.Null(generic.Dpi);
        Assert.Null(generic.Family);
        Assert.Equal(3840, generic.CssWidth);
        Assert.Null(generic.Orientation);
        Assert.False(generic.Touch);
    }

    [Fact]
    public void Known_touch_display_is_touch_even_when_os_reports_no_digitizer()
    {
        // The Windows pointer-device association misses some panels; a curated
        // touch strip stays interactive regardless.
        var caps = DisplayTopologyService.BuildPromotedCapabilities(
            "CRX", "ED00", "CRX ED00", 2560, 720, 1.5, isTouch: false, orientation: "Landscape");

        Assert.True(caps.Touch);
    }

    [Fact]
    public void Topology_read_refreshes_stale_promoted_record_capabilities()
    {
        var display = XeneonEdge();
        var provider = new FakeProvider { Displays = new List<RawDisplayInfo> { display } };
        var service = new DisplayTopologyService(provider, _registry);
        // A record promoted by an older build: no dpi/family stamped.
        var (record, _) = _registry.AllocateForDisplay(display.Id, display.Name, new PanelDeviceCapabilities
        {
            Surface = PanelSurfaces.Monitor,
            Touch = true,
            CssWidth = 2560,
            CssHeight = 720,
            Dpr = 1.0,
        });
        var changedBatches = new List<IReadOnlyList<string>>();
        service.PromotedPanelCapabilitiesChanged += ids => changedBatches.Add(ids);

        service.GetTopology();

        var synced = _registry.FindByDisplayId(display.Id)!;
        Assert.Equal(183, synced.Capabilities?.Dpi);
        Assert.Equal("xeneon-edge", synced.Capabilities?.Family);
        Assert.Equal("Landscape", synced.Capabilities?.Orientation);
        Assert.Equal(new[] { record.Id }, Assert.Single(changedBatches));

        // Same facts again: no write, no event.
        service.GetTopology();
        Assert.Single(changedBatches);

        // Rotation to portrait swaps the reported mode; the record follows.
        display.ResolutionWidth = 720;
        display.ResolutionHeight = 2560;
        display.Orientation = "Portrait";
        service.GetTopology();
        var rotated = _registry.FindByDisplayId(display.Id)!;
        Assert.Equal(720, rotated.Capabilities?.CssWidth);
        Assert.Equal(2560, rotated.Capabilities?.CssHeight);
        Assert.Equal("Portrait", rotated.Capabilities?.Orientation);
        Assert.Equal(2, changedBatches.Count);
    }

    [Fact]
    public void Disabled_promoted_records_are_not_synced()
    {
        var display = XeneonEdge();
        var provider = new FakeProvider { Displays = new List<RawDisplayInfo> { display } };
        var service = new DisplayTopologyService(provider, _registry);
        _registry.AllocateForDisplay(display.Id, display.Name, new PanelDeviceCapabilities
        {
            Surface = PanelSurfaces.Monitor,
        });
        _registry.DisablePanelForDisplay(display.Id);

        service.GetTopology();

        Assert.Null(_registry.FindByDisplayId(display.Id)!.Capabilities?.Dpi);
    }

    private static byte[] BuildEdid(string mfg, ushort product, uint serial, int widthCm, int heightCm, string modelName)
    {
        var edid = new byte[128];
        // Header 00 FF FF FF FF FF FF 00
        edid[0] = 0x00;
        for (var i = 1; i <= 6; i++) edid[i] = 0xFF;
        edid[7] = 0x00;
        // PNP id: 3 x 5-bit letters, big-endian 16-bit at bytes 8-9.
        var id = ((mfg[0] - 'A' + 1) << 10) | ((mfg[1] - 'A' + 1) << 5) | (mfg[2] - 'A' + 1);
        edid[8] = (byte)(id >> 8);
        edid[9] = (byte)(id & 0xFF);
        edid[10] = (byte)(product & 0xFF);
        edid[11] = (byte)(product >> 8);
        edid[12] = (byte)(serial & 0xFF);
        edid[13] = (byte)((serial >> 8) & 0xFF);
        edid[14] = (byte)((serial >> 16) & 0xFF);
        edid[15] = (byte)((serial >> 24) & 0xFF);
        edid[21] = (byte)widthCm;
        edid[22] = (byte)heightCm;
        // Descriptor block at 54: 00 00 ?? FC 00 then 13 bytes of name, LF-terminated.
        edid[54] = 0x00;
        edid[55] = 0x00;
        edid[57] = 0xFC;
        for (var i = 0; i < modelName.Length && i < 13; i++)
            edid[59 + i] = (byte)modelName[i];
        if (modelName.Length < 13)
            edid[59 + modelName.Length] = 0x0A;
        return edid;
    }
}

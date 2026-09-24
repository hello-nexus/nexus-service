using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Models;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The assign-a-device-to-an-ARGB-port flow over the real routes: the offline
/// product picker, wiring a chain to a port, and what the editor reads back.
/// Unit tests cover the resolution rules; these cover the wire, which is where
/// a missing AppJsonContext entry or a route-level guard actually bites.
/// </summary>
public sealed class ChainRouteTests : IDisposable
{
    private const string PortId = "fakeport:1";

    private const int DefaultLedCount = 60;

    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    /// <summary>
    /// A single addressable ARGB port: one resizable segment, nothing else.
    /// The segment tracks the persisted count, as every real port provider
    /// does - a fixture pinned to the firmware number would make every
    /// partition fail to tile and self-heal back to defaults.
    /// </summary>
    private sealed class FakePortSource(IConfigStore store) : IDeviceStructureSource
    {
        public IReadOnlyList<DeviceStructure> GetStructures()
        {
            var ledCount = store.Load().Devices.ZoneLedCounts.TryGetValue(PortId, out var persisted)
                ? persisted
                : DefaultLedCount;
            var s = new DeviceStructure { DeviceId = PortId, Name = "Fake Port", Partitionable = true };
            s.Segments.Add(new StructureSegment
            {
                Index = 0,
                Name = "ARGB",
                LedCount = ledCount,
                FrameLedCount = ledCount,
                Resizable = true,
                ZoneType = "linear",
            });
            s.DefaultZones.Add(new DefaultZoneDef
            {
                Id = PortId,
                Name = "Fake Port",
                RawName = "ARGB",
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
            });
            return new[] { s };
        }
    }

    /// <summary>Two fixed segments, so the device map has a segment past the first to address.</summary>
    private sealed class TwoSegmentSource : IDeviceStructureSource
    {
        public const string Id = "fakedev:two";

        public IReadOnlyList<DeviceStructure> GetStructures()
        {
            var s = new DeviceStructure { DeviceId = Id, Name = "Two Segment", Partitionable = true };
            var whole = new DefaultZoneDef { Id = Id, Name = "Two Segment", RawName = "All", LegacyZoneIndex = -1 };
            for (int i = 0; i < 2; i++)
            {
                s.Segments.Add(new StructureSegment
                {
                    Index = i,
                    Name = $"Seg {i}",
                    LedCount = 4,
                    FrameLedCount = 4,
                    Resizable = false,
                    ZoneType = "linear",
                });
                whole.Slices.Add(new ZoneSlice { Segment = i, Start = 0, Count = 4 });
            }
            s.DefaultZones.Add(whole);
            return new[] { s };
        }
    }

    public ChainRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        // Every read must go through THIS host: WithWebHostBuilder boots a
        // second one, and its IConfigStore caches its own copy of settings.
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IDeviceStructureSource>(sp => new FakePortSource(sp.GetRequiredService<IConfigStore>()));
            s.AddSingleton<IDeviceStructureSource>(new TwoSegmentSource());
        }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private static string Catalog(string? q = null, int limit = 50)
        => $"/devices/lighting-devices/mappings/catalog?limit={limit}" + (q is null ? "" : $"&q={q}");

    private Task<HttpResponseMessage> PostChain(params SetChainEntry[] entries)
        => _client.PostAsJsonAsync($"/devices/lighting-devices/{PortId}/mappings/chain",
            new SetChainBody { Entries = entries.ToList() });

    private Task<HttpResponseMessage> PostChainPreview(params SetChainEntry[] entries)
        => _client.PostAsJsonAsync($"/devices/lighting-devices/{PortId}/mappings/chain/preview",
            new SetChainBody { Entries = entries.ToList() });

    [Fact]
    public async Task An_override_on_a_later_segment_survives_a_save()
    {
        // The device map addresses LEDs by (segment, index), where segment is
        // the segment's POSITION in the structure. A structure that reported
        // something else there unmapped every LED past the first segment, and
        // the save path then read the empty result as "no overrides" and
        // deleted the device's stored ones.
        var save = await _client.PostAsJsonAsync(
            $"/devices/lighting-devices/{TwoSegmentSource.Id}/device-map",
            new SaveDeviceMapBody
            {
                Overrides =
                {
                    new SegmentLedOverride { Segment = 1, LedIndex = 2, U = 0.25f, V = 0.75f },
                },
            });
        save.EnsureSuccessStatusCode();

        var stored = _factory.Services.GetRequiredService<IConfigStore>()
            .Load().Devices.DeviceLedOverrides[TwoSegmentSource.Id];
        var only = Assert.Single(stored);
        Assert.Equal(1, only.Segment);
        Assert.Equal(2, only.LedIndex);

        var map = await _client.GetFromJsonAsync<DeviceMapResponse>(
            $"/devices/lighting-devices/{TwoSegmentSource.Id}/device-map");
        Assert.NotNull(map);
        var second = map!.Segments[1];
        Assert.Equal(1, second.Index);
        // Every LED on the segment resolves to the zone covering it, and the
        // edited one comes back marked.
        Assert.All(second.Leds, l => Assert.NotEqual("", l.ZoneId));
        Assert.True(second.Leds[2].IsCustom);
    }

    [Fact]
    public async Task The_picker_offers_the_generics_first_and_reaches_the_network_for_nothing()
    {
        var resp = await _client.GetFromJsonAsync<BuiltInMappingsResponse>(Catalog());

        Assert.NotNull(resp);
        Assert.Equal(GenericChainArtifacts.FanKey, resp!.Items[0].Key);
        Assert.Equal(GenericChainArtifacts.StripKey, resp.Items[1].Key);
        Assert.True(resp.Items[0].Parametric);
        // Total counts what matched, which includes the rows that are not in
        // the packed file at all.
        Assert.True(resp.Total > BuiltInMappingsCatalog.All.Count);
    }

    [Fact]
    public async Task Our_own_accessories_are_in_the_picker_under_the_brand()
    {
        var resp = await _client.GetFromJsonAsync<BuiltInMappingsResponse>(Catalog("HYTE"));

        Assert.NotNull(resp);
        var byKey = resp!.Items.ToDictionary(i => i.Key);
        Assert.Equal(HyteChainArtifacts.All.Count, resp.Total);
        foreach (var product in HyteChainArtifacts.All)
        {
            Assert.True(byKey.ContainsKey(product.Key), product.Key);
            Assert.Equal(product.LedCount, byKey[product.Key].LedCount);
            Assert.Equal("HYTE", byKey[product.Key].Brand);
            Assert.False(byKey[product.Key].Parametric);
        }
    }

    [Fact]
    public async Task An_unchained_port_reads_back_as_one_editable_row()
    {
        var resp = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");

        Assert.NotNull(resp);
        Assert.True(resp!.Chainable);
        Assert.True(resp.IsDefaultPartition);
        var only = Assert.Single(resp.Chain);
        // A port with nothing declared reads as a generic strip the user can
        // resize, so the editor renders the same list either way.
        Assert.Equal(GenericChainArtifacts.StripKey, only.Key);
        Assert.Equal(60, only.LedCount);
        Assert.True(only.EditableCount);
    }

    [Fact]
    public async Task Wiring_a_chain_turns_one_port_into_one_card_per_product()
    {
        var post = await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12-trio" },
            new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 20 },
            new SetChainEntry { Key = "product:hyte-y50-solo" });

        post.EnsureSuccessStatusCode();
        var result = await post.Content.ReadFromJsonAsync<SetChainResponse>();
        Assert.NotNull(result);
        Assert.False(result!.Error);
        Assert.Equal(68 + 20 + 8, result.LedCount);
        Assert.Equal(new[] { $"{PortId}:z0", $"{PortId}:z1", $"{PortId}:z2" }, result.ZoneIds.ToArray());

        var structure = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");
        Assert.NotNull(structure);
        Assert.False(structure!.IsDefaultPartition);
        Assert.Equal(3, structure.Chain.Count);
        // No ordinal suffix: three identical fans read as three of the same
        // product, and their position in the chain is what tells them apart.
        Assert.Equal(new[] { "FR12 Trio", "Generic Strip", "Y50 Solo Fan" },
            structure.Chain.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { 68, 20, 8 }, structure.Chain.Select(c => c.LedCount).ToArray());
        // A product fixes its own count; only the generic's is the user's.
        Assert.Equal(new[] { false, true, false }, structure.Chain.Select(c => c.EditableCount).ToArray());
        Assert.Equal(96, structure.Segments[0].LedCount);
    }

    [Fact]
    public async Task Each_product_lands_on_its_own_zone_with_its_own_mapping()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-trio" })).EnsureSuccessStatusCode();

        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        var applied = settings.Devices.AppliedMappings;

        Assert.Equal("product:hyte-fr12", applied[$"{PortId}:z0"].MappingId);
        Assert.Equal("product:hyte-y50-trio", applied[$"{PortId}:z1"].MappingId);
        // The geometry rides along, so the canvas has the rings without ever
        // asking the registry for them.
        Assert.Equal(33, applied[$"{PortId}:z0"].Artifact.Zones[0].Leds.Count);
        Assert.Equal(24, applied[$"{PortId}:z1"].Artifact.Zones[0].Leds.Count);
    }

    [Fact]
    public async Task Clearing_the_chain_returns_the_port_to_one_zone()
    {
        (await PostChain(new SetChainEntry { Key = "product:hyte-fr12" })).EnsureSuccessStatusCode();
        (await PostChain()).EnsureSuccessStatusCode();

        var structure = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");

        Assert.NotNull(structure);
        Assert.True(structure!.IsDefaultPartition);
        Assert.Single(structure.Chain);
        // The declared total stays; the user has only stopped naming the parts.
        Assert.Equal(33, structure.Chain[0].LedCount);
    }

    [Fact]
    public async Task Resetting_the_zones_forgets_the_chain_too()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" })).EnsureSuccessStatusCode();

        var reset = await _client.DeleteAsync($"/devices/lighting-devices/{PortId}/zones");
        reset.EnsureSuccessStatusCode();

        var structure = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");
        Assert.NotNull(structure);
        Assert.True(structure!.IsDefaultPartition);
        // A chain record left behind would keep naming products the zones no
        // longer match, and keep rule 2 lifted for a segment nothing owns.
        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        Assert.DoesNotContain(ZoneResolution.ChainKey(PortId, 0), settings.Devices.PortChains.Keys);
    }

    [Fact]
    public async Task An_unknown_product_is_refused_rather_than_silently_dropped()
    {
        var post = await PostChain(new SetChainEntry { Key = "product:does-not-exist" });
        var body = await post.Content.ReadFromJsonAsync<ApiResponse>();

        Assert.NotNull(body);
        Assert.True(body!.Error);
    }

    [Fact]
    public async Task A_chain_longer_than_a_port_can_carry_is_refused()
    {
        // Six generics at the per-link maximum: each is legal on its own, and
        // the sum is what has to be caught.
        var links = Enumerable.Range(0, 6)
            .Select(_ => new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 4096 })
            .ToArray();

        var post = await PostChain(links);
        var body = await post.Content.ReadFromJsonAsync<ApiResponse>();

        Assert.NotNull(body);
        Assert.True(body!.Error);
        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        Assert.DoesNotContain(ZoneResolution.ChainKey(PortId, 0), settings.Devices.PortChains.Keys);
    }

    [Fact]
    public async Task A_generic_needs_a_count_and_a_product_ignores_one()
    {
        var noCount = await PostChain(new SetChainEntry { Key = GenericChainArtifacts.FanKey });
        Assert.True((await noCount.Content.ReadFromJsonAsync<ApiResponse>())!.Error);

        // A client cannot resize a product: its artifact decides.
        var post = await PostChain(new SetChainEntry { Key = "product:hyte-fr12", LedCount = 999 });
        post.EnsureSuccessStatusCode();
        var result = await post.Content.ReadFromJsonAsync<SetChainResponse>();
        Assert.Equal(33, result!.LedCount);
    }

    [Fact]
    public async Task Preview_matches_what_a_real_chain_POST_then_produces()
    {
        var preview = await PostChainPreview(
            new SetChainEntry { Key = "product:hyte-fr12-trio" },
            new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 20 },
            new SetChainEntry { Key = "product:hyte-y50-solo" });
        preview.EnsureSuccessStatusCode();
        var previewBody = await preview.Content.ReadFromJsonAsync<ChainPreviewResponse>();
        Assert.NotNull(previewBody);
        Assert.False(previewBody!.Error);
        Assert.NotNull(previewBody.Structure);
        Assert.NotNull(previewBody.Map);

        var post = await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12-trio" },
            new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 20 },
            new SetChainEntry { Key = "product:hyte-y50-solo" });
        post.EnsureSuccessStatusCode();

        var structure = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");
        Assert.NotNull(structure);

        Assert.Equal(structure!.Chain.Select(c => c.Name), previewBody.Structure!.Chain.Select(c => c.Name));
        Assert.Equal(structure.Chain.Select(c => c.LedCount), previewBody.Structure.Chain.Select(c => c.LedCount));
        Assert.Equal(structure.Zones.Select(z => z.Id), previewBody.Structure.Zones.Select(z => z.Id));
        Assert.Equal(structure.Segments[0].LedCount, previewBody.Structure.Segments[0].LedCount);
        Assert.Equal(structure.Segments[0].LedCount, previewBody.Map!.Segments[0].LedCount);

        var map = await _client.GetFromJsonAsync<DeviceMapResponse>(
            $"/devices/lighting-devices/{PortId}/device-map");
        Assert.NotNull(map);
        var realLeds = map!.Segments[0].Leds;
        var previewLeds = previewBody.Map.Segments[0].Leds;
        Assert.Equal(realLeds.Count, previewLeds.Count);
        for (int i = 0; i < realLeds.Count; i++)
        {
            Assert.Equal(realLeds[i].ZoneId, previewLeds[i].ZoneId);
            Assert.Equal(realLeds[i].U, previewLeds[i].U);
            Assert.Equal(realLeds[i].V, previewLeds[i].V);
            Assert.Equal(realLeds[i].IsCustom, previewLeds[i].IsCustom);
            Assert.Equal(realLeds[i].Disabled, previewLeds[i].Disabled);
        }
    }

    [Fact]
    public async Task Preview_writes_nothing()
    {
        var preview = await PostChainPreview(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<ChainPreviewResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Error);

        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        Assert.Empty(settings.Devices.PortChains);
        Assert.Empty(settings.Devices.ZonePartitions);
        Assert.Empty(settings.Devices.AppliedMappings);
        Assert.Empty(settings.Devices.ZoneLedCounts);
    }

    [Fact]
    public async Task Preview_rejects_exactly_what_the_post_rejects()
    {
        var unknown = await PostChainPreview(new SetChainEntry { Key = "product:does-not-exist" });
        Assert.True((await unknown.Content.ReadFromJsonAsync<ChainPreviewResponse>())!.Error);

        var emptyKey = await PostChainPreview(new SetChainEntry { Key = "" });
        Assert.True((await emptyKey.Content.ReadFromJsonAsync<ChainPreviewResponse>())!.Error);

        // Six generics at the per-link maximum: each is legal on its own, and
        // the sum is what has to be caught.
        var overCeilingLinks = Enumerable.Range(0, 6)
            .Select(_ => new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 4096 })
            .ToArray();
        var overCeiling = await PostChainPreview(overCeilingLinks);
        Assert.True((await overCeiling.Content.ReadFromJsonAsync<ChainPreviewResponse>())!.Error);

        var noCount = await PostChainPreview(new SetChainEntry { Key = GenericChainArtifacts.FanKey });
        Assert.True((await noCount.Content.ReadFromJsonAsync<ChainPreviewResponse>())!.Error);

        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        Assert.Empty(settings.Devices.PortChains);
    }

    [Fact]
    public async Task The_previewed_map_carries_the_products_real_geometry()
    {
        var preview = await PostChainPreview(new SetChainEntry { Key = "product:hyte-fr12" });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<ChainPreviewResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Error);

        var leds = body!.Map!.Segments[0].Leds;
        Assert.Equal(33, leds.Count);
        // Every LED sharing one position would mean the seeded linear default
        // never got overlaid with the FR12 artifact's real per-LED positions.
        Assert.True(leds.Select(l => (l.U, l.V)).Distinct().Count() > 1);
    }

    [Fact]
    public async Task A_renamed_link_keeps_its_name_when_the_chain_is_reordered()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" })).EnsureSuccessStatusCode();
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        store.Update(s => s.Lighting.DeviceNames[ZoneResolution.CustomZoneId(PortId, 1)] = "YOOOO");

        (await PostChain(
            new SetChainEntry { Key = "product:hyte-y50-solo", FromOrdinal = 1 },
            new SetChainEntry { Key = "product:hyte-fr12", FromOrdinal = 0 })).EnsureSuccessStatusCode();

        var names = store.Load().Lighting.DeviceNames;
        Assert.Equal("YOOOO", names[ZoneResolution.CustomZoneId(PortId, 0)]);
        Assert.DoesNotContain(ZoneResolution.CustomZoneId(PortId, 1), names.Keys);
    }

    [Fact]
    public async Task A_slot_that_changed_product_does_not_inherit_the_old_name()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" })).EnsureSuccessStatusCode();
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        store.Update(s => s.Lighting.DeviceNames[ZoneResolution.CustomZoneId(PortId, 1)] = "YOOOO");

        // The second link is removed and a different product put in its place,
        // so the client sends no origin for it.
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12", FromOrdinal = 0 },
            new SetChainEntry { Key = "product:hyte-ln80" })).EnsureSuccessStatusCode();

        var names = store.Load().Lighting.DeviceNames;
        Assert.DoesNotContain(ZoneResolution.CustomZoneId(PortId, 1), names.Keys);
    }

    [Fact]
    public async Task A_moved_link_keeps_its_brightness_canvas_rect_and_control_state()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" })).EnsureSuccessStatusCode();
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var slot1 = ZoneResolution.CustomZoneId(PortId, 1);
        store.Update(s =>
        {
            s.Devices.LightingDevicePrefs[slot1] = new LightingDevicePreference { Brightness = 42 };
            s.Lighting.DeviceLayouts[slot1] = new DeviceLayout { X = 11, Y = 22, W = 33, H = 44 };
            s.Devices.UncontrolledLightingDevices = new List<string> { slot1 };
        });

        // Appending a third link must not reset the two already wired: only
        // their ordinal is the route's to change, not the device at it.
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12", FromOrdinal = 0 },
            new SetChainEntry { Key = "product:hyte-y50-solo", FromOrdinal = 1 },
            new SetChainEntry { Key = "product:hyte-ln80" })).EnsureSuccessStatusCode();

        var after = store.Load();
        Assert.Equal(42, after.Devices.LightingDevicePrefs[slot1].Brightness);
        Assert.Equal(11, after.Lighting.DeviceLayouts[slot1].X);
        Assert.Contains(slot1, after.Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public async Task A_replaced_link_does_not_inherit_the_old_devices_state()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" })).EnsureSuccessStatusCode();
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var slot1 = ZoneResolution.CustomZoneId(PortId, 1);
        store.Update(s => s.Devices.LightingDevicePrefs[slot1] = new LightingDevicePreference { Brightness = 42 });

        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12", FromOrdinal = 0 },
            new SetChainEntry { Key = "product:hyte-ln80" })).EnsureSuccessStatusCode();

        Assert.DoesNotContain(slot1, store.Load().Devices.LightingDevicePrefs.Keys);
    }

    [Fact]
    public async Task A_chain_longer_than_the_port_declares_is_refused()
    {
        // The fixture port declares no ceiling, so this pins the generic cap's
        // behaviour; MiniHub and Nollie now declare their firmware's own.
        var links = Enumerable.Range(0, 6)
            .Select(_ => new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 1024 })
            .ToArray();
        var post = await PostChain(links);
        var body = await post.Content.ReadFromJsonAsync<ApiResponse>();
        Assert.True(body!.Error);
        Assert.Contains("carries", body.Msg);
    }

    [Fact]
    public async Task Nexus_Control_moves_the_whole_chain_not_one_link()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" })).EnsureSuccessStatusCode();

        // Off on one link: the links share one wire, so handing the port over
        // while Nexus still drives a link means neither owns it.
        var off = await _client.PostAsJsonAsync("/devices/lighting-devices/controlled",
            new SetLightingDeviceControlledBody { Id = ZoneResolution.CustomZoneId(PortId, 0), Controlled = false });
        off.EnsureSuccessStatusCode();

        var uncontrolled = _factory.Services.GetRequiredService<IConfigStore>()
            .Load().Devices.UncontrolledLightingDevices;
        Assert.Contains(ZoneResolution.CustomZoneId(PortId, 0), uncontrolled);
        Assert.Contains(ZoneResolution.CustomZoneId(PortId, 1), uncontrolled);
    }

    [Fact]
    public async Task The_ports_own_rename_survives_a_chain_write()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        store.Update(s => s.Lighting.DeviceNames[PortId] = "ARGB Port #2");

        (await PostChain(new SetChainEntry { Key = "product:hyte-fr12" })).EnsureSuccessStatusCode();

        // The port's rename is device-level state, and an unchained port's
        // only zone id IS the device id - dropping it with the outgoing slots
        // would un-name the header on the first assign.
        Assert.Equal("ARGB Port #2", store.Load().Lighting.DeviceNames[PortId]);
    }
}

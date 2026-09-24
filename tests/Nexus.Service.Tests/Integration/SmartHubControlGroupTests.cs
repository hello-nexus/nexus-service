using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Nexus Control on a SmartHub port is the whole hub's: the firmware animation
/// has one activity flag for all four ports, so one controlled port keeps the
/// hub streaming and every other port can only go black.
/// </summary>
public sealed class SmartHubControlGroupTests : IDisposable
{
    private const string HubId = "smarthub:SH01";
    private static string Port(int channel) => $"{HubId}:port{channel}";

    /// <summary>The provider's own structures for a four-port hub, collapsing to the mirror device when the composition says so.</summary>
    private sealed class FourPortSource(IConfigStore store) : IDeviceStructureSource
    {
        public IReadOnlyList<DeviceStructure> GetStructures()
        {
            var settings = store.Load();
            if (SmartHubLightingDeviceProvider.ReadMirror(settings, HubId))
                return new[] { MirrorStructure(settings) };
            var list = new List<DeviceStructure>(4);
            for (var ch = 1; ch <= 4; ch++)
                list.Add(SmartHubLightingDeviceProvider.BuildPortStructure(settings, HubId, ch, 60));
            return list;
        }

        private static DeviceStructure MirrorStructure(NexusSettings settings)
        {
            var s = SmartHubLightingDeviceProvider.BuildPortStructure(settings, HubId, 1, 60);
            var id = SmartHubLightingDeviceProvider.MirrorId(HubId);
            s.DeviceId = id;
            s.DefaultZones[0].Id = id;
            return s;
        }
    }

    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SmartHubControlGroupTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IDeviceStructureSource>(sp => new FourPortSource(sp.GetRequiredService<IConfigStore>()))));
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

    private List<string> Uncontrolled()
        => _factory.Services.GetRequiredService<IConfigStore>().Load().Devices.UncontrolledLightingDevices;

    [Fact]
    public async Task Off_on_one_port_hands_over_every_port_of_the_hub()
    {
        var off = await _client.PostAsJsonAsync("/devices/lighting-devices/controlled",
            new SetLightingDeviceControlledBody { Id = Port(4), Controlled = false });
        off.EnsureSuccessStatusCode();

        var uncontrolled = Uncontrolled();
        for (var ch = 1; ch <= 4; ch++) Assert.Contains(Port(ch), uncontrolled);
    }

    [Fact]
    public async Task On_for_one_port_takes_the_whole_hub_back()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
            s.Devices.UncontrolledLightingDevices = new List<string> { Port(1), Port(2), Port(3), Port(4) });

        var on = await _client.PostAsJsonAsync("/devices/lighting-devices/controlled",
            new SetLightingDeviceControlledBody { Id = Port(2), Controlled = true });
        on.EnsureSuccessStatusCode();

        Assert.Empty(Uncontrolled());
    }

    [Fact]
    public async Task A_partitioned_port_hands_over_by_its_live_zone_ids()
    {
        var chain = await _client.PostAsJsonAsync($"/devices/lighting-devices/{Port(4)}/mappings/chain",
            new SetChainBody { Entries = [new SetChainEntry { Key = "product:hyte-fr12" }, new SetChainEntry { Key = "product:hyte-y50-solo" }] });
        chain.EnsureSuccessStatusCode();

        var off = await _client.PostAsJsonAsync("/devices/lighting-devices/controlled",
            new SetLightingDeviceControlledBody { Id = Port(1), Controlled = false });
        off.EnsureSuccessStatusCode();

        var uncontrolled = Uncontrolled();
        Assert.Contains(ZoneResolution.CustomZoneId(Port(4), 0), uncontrolled);
        Assert.Contains(ZoneResolution.CustomZoneId(Port(4), 1), uncontrolled);
        Assert.DoesNotContain(Port(4), uncontrolled);
        for (var ch = 1; ch <= 3; ch++) Assert.Contains(Port(ch), uncontrolled);
    }

    [Fact]
    public async Task The_mirror_device_is_its_own_group()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
            s.Devices.LightingComposition[HubId] = new HubCompositionSettings { Mirror = true });
        var mirror = SmartHubLightingDeviceProvider.MirrorId(HubId);

        var off = await _client.PostAsJsonAsync("/devices/lighting-devices/controlled",
            new SetLightingDeviceControlledBody { Id = mirror, Controlled = false });
        off.EnsureSuccessStatusCode();

        Assert.Equal(new[] { mirror }, Uncontrolled());
    }

    [Theory]
    [InlineData("smarthub:SH01:port1", "smarthub:SH01")]
    [InlineData("smarthub:SH01:mirror", "smarthub:SH01")]
    [InlineData("smarthub:SH01", null)]
    [InlineData("smarthub:", null)]
    [InlineData("np50:SH01:port1", null)]
    [InlineData("smarthub:SH01:port", null)]
    public void HubIdOf_accepts_only_port_and_mirror_ids(string id, string? expected)
        => Assert.Equal(expected, SmartHubLightingDeviceProvider.HubIdOf(id));
}

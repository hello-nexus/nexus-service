using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Strimer;

public class StrimerLightingDeviceProviderTests
{
    private readonly StrimerHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly StrimerLightingDeviceProvider _provider;

    public StrimerLightingDeviceProviderTests()
    {
        _provider = new StrimerLightingDeviceProvider(new StrimerUnits(_hub), _store, new Np50IdentifyTracker());
    }

    private void Connect() => _hub.State.IsConnected = true;

    [Fact]
    public void GetAll_returns_empty_when_disconnected()
    {
        var result = _provider.GetAll();
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void GetAll_returns_12_zones_when_connected()
    {
        Connect();
        var result = _provider.GetAll();
        Assert.Equal(12, result.Devices.Count);
        Assert.All(result.Devices, d => Assert.Null(d.DevicePageReason));
    }

    [Fact]
    public void Argb_sync_keeps_marked_cards_but_no_structures_or_frames()
    {
        Connect();
        _store.Update(s => s.Devices.StrimerLighting.ArgbSync = true);

        var result = _provider.GetAll();

        Assert.Equal(12, result.Devices.Count);
        Assert.All(result.Devices, d => Assert.Equal("argbInput", d.DevicePageReason));
        Assert.Empty(_provider.GetStructures());
        Assert.Empty(_provider.BuildFrames(0));
    }

    [Fact]
    public void GetAll_first_6_zones_belong_to_atx_structure()
    {
        Connect();
        var devices = _provider.GetAll().Devices;
        for (var i = 0; i < 6; i++)
        {
            Assert.StartsWith("strimer:atx:", devices[i].Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GetAll_last_6_zones_belong_to_gpu_structure()
    {
        Connect();
        var devices = _provider.GetAll().Devices;
        for (var i = 6; i < 12; i++)
        {
            Assert.StartsWith("strimer:gpu:", devices[i].Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GetStructures_returns_empty_when_disconnected()
    {
        Assert.Empty(_provider.GetStructures());
    }

    [Fact]
    public void GetStructures_returns_2_structures_when_connected()
    {
        Connect();
        Assert.Equal(2, _provider.GetStructures().Count);
    }
}

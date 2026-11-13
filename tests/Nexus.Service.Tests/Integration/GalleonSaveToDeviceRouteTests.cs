using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>POST /devices/lighting-devices/save-to-device accepts only the Galleon keyboard's device id.</summary>
public sealed class GalleonSaveToDeviceRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();
    private readonly HttpClient _client;

    public GalleonSaveToDeviceRouteTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private List<string> Saved() => _factory.Services.GetRequiredService<IConfigStore>().Load().Devices.SaveToDeviceLightingDevices;

    [Theory]
    [InlineData("corsair-galleon:dials:SN1")]
    [InlineData("openrgb-s-1")]
    [InlineData("")]
    public async Task Rejects_a_dials_id_and_a_foreign_id(string id)
    {
        var resp = await _client.PostAsJsonAsync("/devices/lighting-devices/save-to-device",
            new SetLightingDeviceSaveToDeviceBody { Id = id, Enabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(Saved());
    }

    [Fact]
    public async Task Accepts_the_keyboard_id_and_clears_it_again()
    {
        var on = await _client.PostAsJsonAsync("/devices/lighting-devices/save-to-device",
            new SetLightingDeviceSaveToDeviceBody { Id = "corsair-galleon:SN1", Enabled = true });
        on.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "corsair-galleon:SN1" }, Saved());

        var off = await _client.PostAsJsonAsync("/devices/lighting-devices/save-to-device",
            new SetLightingDeviceSaveToDeviceBody { Id = "corsair-galleon:SN1", Enabled = false });
        off.EnsureSuccessStatusCode();
        Assert.Empty(Saved());
    }
}

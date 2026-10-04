using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>A lighting provider with one card, so GET /all has a device to report the mirror on.</summary>
public sealed class OneCardHostFactory : NexusAppFactory
{
    public const string CardId = "card-1";

    private sealed class OneCardProvider : ILightingDeviceProvider
    {
        public bool IsConnected => true;
        public GetLightingDevicesResponse GetAll() => new()
        {
            IsInit = true,
            Devices = new List<LightingDevice> { new() { Id = CardId, Name = "Strip", LedCount = 4 } },
        };
        public void SetDisabled(IReadOnlyList<string> ids) { }
        public void SetPower(string id, bool on) { }
        public void SetBrightness(string id, int brightness) { }
        public void SetHue(string id, float hue) { }
        public void SetSaturation(string id, float saturation) { }
        public void SetZoneLedCount(string id, int count) { }
        public void Identify(string id, int durationMs) { }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILightingDeviceProvider>();
            services.AddSingleton<ILightingDeviceProvider>(new OneCardProvider());
        });
    }
}

public sealed class DeviceLayoutFlipRoutesTests : IClassFixture<OneCardHostFactory>
{
    private const string CardId = OneCardHostFactory.CardId;
    private readonly OneCardHostFactory _factory;
    private readonly HttpClient _client;
    private readonly DeviceFrame _frame = new(0, CardId, 4);

    public DeviceLayoutFlipRoutesTests(OneCardHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        Engine.UpdateDevices(new[] { _frame });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();
    private LightingEngine Engine => _factory.Services.GetRequiredService<LightingEngine>();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostLayout(string flipField) => _client.PostAsync(
        "/devices/lighting-devices/layout",
        Json($$"""{"id":"{{CardId}}","x":10,"y":20,"w":200,"h":40,"rotation":90{{flipField}}}"""));

    [Fact]
    public async Task Layout_post_stores_the_flip_and_sets_it_on_the_engine_frame()
    {
        var res = await PostLayout(""","flip":true""");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var stored = Store.Load().Lighting.DeviceLayouts[CardId];
        Assert.True(stored.Flip);
        Assert.Equal(90, stored.Rotation);
        Assert.True(_frame.Flip);
        Assert.Equal(90, _frame.Rotation);
    }

    [Fact]
    public async Task Layout_post_without_flip_keeps_the_stored_one_and_false_clears_it()
    {
        await PostLayout(""","flip":true""");

        await PostLayout("");
        Assert.True(Store.Load().Lighting.DeviceLayouts[CardId].Flip);
        Assert.True(_frame.Flip);

        await PostLayout(""","flip":false""");
        Assert.False(Store.Load().Lighting.DeviceLayouts[CardId].Flip);
        Assert.False(_frame.Flip);
    }

    [Fact]
    public async Task Layout_post_without_flip_on_a_new_device_stores_no_flip()
    {
        await PostLayout("");

        Assert.False(Store.Load().Lighting.DeviceLayouts[CardId].Flip);
        Assert.False(_frame.Flip);
    }

    [Fact]
    public async Task Get_all_reports_the_stored_flip_and_false_without_a_layout()
    {
        async Task<bool> CanvasFlip()
        {
            using var doc = JsonDocument.Parse(await _client.GetStringAsync("/devices/lighting-devices/all"));
            var card = doc.RootElement.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == CardId);
            return card.GetProperty("canvasFlip").GetBoolean();
        }

        Assert.False(await CanvasFlip());
        await PostLayout(""","flip":true""");
        Assert.True(await CanvasFlip());
    }

    [Fact]
    public async Task Batch_layouts_carry_the_flip_and_one_without_it_keeps_the_stored_flip()
    {
        Task<HttpResponseMessage> PostBatch(string flipField) => _client.PostAsync("/devices/lighting-devices/layouts",
            Json("{\"layouts\":{\"card-1\":{\"x\":1,\"y\":2,\"w\":30,\"h\":40,\"rotation\":180" + flipField + "}}}"));

        var res = await PostBatch(",\"flip\":true");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(Store.Load().Lighting.DeviceLayouts[CardId].Flip);
        Assert.True(_frame.Flip);

        await PostBatch("");
        Assert.True(Store.Load().Lighting.DeviceLayouts[CardId].Flip);
        Assert.Equal(180, Store.Load().Lighting.DeviceLayouts[CardId].Rotation);
        Assert.True(_frame.Flip);

        await PostBatch(",\"flip\":false");
        Assert.False(Store.Load().Lighting.DeviceLayouts[CardId].Flip);
        Assert.False(_frame.Flip);
    }

    [Fact]
    public async Task Layout_reset_clears_the_flip_on_the_engine_frame()
    {
        await PostLayout(""","flip":true""");

        var res = await _client.DeleteAsync("/devices/lighting-devices/layouts");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(_frame.Flip);
    }
}

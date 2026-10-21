using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Xunit;

namespace Nexus.Service.Tests.Integration;

public class GlacialLcdRoutesTests
{
    private static HttpClient Client(NexusAppFactory factory, bool nexusPanel = true)
    {
        factory.Services.GetRequiredService<Nexus.Service.Peripherals.BulkPanels.GlacialLcdLink>().NexusPanelEnabled = nexusPanel;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task With_the_nexus_panel_off_the_state_says_so_and_the_panel_mode_is_refused()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory, nexusPanel: false);

        var state = await client.GetAsync("/devices/asus-glacial-lcd/state");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        using var doc = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("panelModeAvailable").GetBoolean());

        var put = await client.PutAsync("/devices/asus-glacial-lcd/settings",
            new StringContent("{\"mode\":\"panel\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Theory]
    [InlineData("quick", "summary/cpu-usage", HttpStatusCode.OK)]
    [InlineData("network", "Network Total", HttpStatusCode.OK)]
    [InlineData("fps", "fps/current", HttpStatusCode.OK)]
    [InlineData("nonsense", "x", HttpStatusCode.BadRequest)]
    public async Task Any_picker_category_is_accepted_for_a_widget(string category, string sensor, HttpStatusCode expected)
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var put = await client.PutAsync("/devices/asus-glacial-lcd/settings",
            new StringContent($"{{\"slots\":[{{\"category\":\"{category}\",\"sensor\":\"{sensor}\",\"label\":\"W\"}}]}}", Encoding.UTF8, "application/json"));
        Assert.Equal(expected, put.StatusCode);
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_tuning_api_reports_the_proven_defaults_and_the_stats_shape()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var res = await client.GetAsync("/devices/asus-glacial-lcd/tuning");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var settings = doc.RootElement.GetProperty("settings");
        Assert.Equal(19, settings.GetProperty("pacePercent").GetInt32());
        Assert.Equal(4, settings.GetProperty("chunkKb").GetInt32());
        Assert.Equal(65, settings.GetProperty("jpegQuality").GetInt32());
        Assert.Equal(100, settings.GetProperty("scalePercent").GetInt32());
        Assert.Equal("send", settings.GetProperty("scaleMode").GetString());
        Assert.False(settings.GetProperty("doubleBuffer").GetBoolean());
        Assert.False(settings.GetProperty("replyDriven").GetBoolean());
        Assert.Equal(30, settings.GetProperty("maxFps").GetDouble());
        var stats = doc.RootElement.GetProperty("stats");
        foreach (string name in new[] { "uploadsOk", "uploadsFailed", "lastUploadMs", "uploadsPerSec", "lastFrameBytes", "lastEncodeMs", "stalled" })
        {
            Assert.True(stats.TryGetProperty(name, out _), name);
        }
        Assert.Equal(JsonValueKind.Null, stats.GetProperty("lastError").ValueKind);
        Assert.Equal(JsonValueKind.Null, stats.GetProperty("lastErrorAt").ValueKind);
    }

    [Fact]
    public async Task A_partial_put_is_validated_persisted_and_returned()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var put = await client.PutAsync("/devices/asus-glacial-lcd/tuning", Body("{\"pacePercent\":50,\"chunkKb\":8,\"scaleMode\":\"upscale\"}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var doc = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.Equal(50, doc.RootElement.GetProperty("settings").GetProperty("pacePercent").GetInt32());

        var get = await client.GetAsync("/devices/asus-glacial-lcd/tuning");
        using var again = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var settings = again.RootElement.GetProperty("settings");
        Assert.Equal(8, settings.GetProperty("chunkKb").GetInt32());
        Assert.Equal("upscale", settings.GetProperty("scaleMode").GetString());
        Assert.Equal(65, settings.GetProperty("jpegQuality").GetInt32());
    }

    [Theory]
    [InlineData("{\"maxFps\":0.1}")]
    [InlineData("{\"maxFps\":31}")]
    [InlineData("{\"pacePercent\":101}")]
    [InlineData("{\"scalePercent\":10}")]
    [InlineData("{\"scaleMode\":\"x\"}")]
    [InlineData("{\"jpegQuality\":10}")]
    [InlineData("{\"chunkKb\":17}")]
    public async Task An_out_of_range_tuning_value_is_refused_and_changes_nothing(string json)
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var put = await client.PutAsync("/devices/asus-glacial-lcd/tuning", Body(json));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var get = await client.GetAsync("/devices/asus-glacial-lcd/tuning");
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(19, doc.RootElement.GetProperty("settings").GetProperty("pacePercent").GetInt32());
    }

    [Fact]
    public async Task Reset_stats_and_resume_are_mapped_and_clear_their_state()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);
        var tuning = factory.Services.GetRequiredService<Nexus.Service.Peripherals.BulkPanels.GlacialLcdTuning>();
        tuning.UploadFailed("x");
        tuning.Stall("silent");

        var resume = await client.PostAsync("/devices/asus-glacial-lcd/tuning/resume", null);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        Assert.False(tuning.Stalled);

        var reset = await client.PostAsync("/devices/asus-glacial-lcd/tuning/reset-stats", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(0, tuning.Stats().UploadsFailed);
    }

    [Fact]
    public async Task With_the_nexus_panel_off_every_tuning_route_says_so_and_nothing_is_created()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory, nexusPanel: false);

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/devices/asus-glacial-lcd/tuning")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("/devices/asus-glacial-lcd/tuning", Body("{\"chunkKb\":8}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/devices/asus-glacial-lcd/tuning/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/devices/asus-glacial-lcd/tuning/reset-stats", null)).StatusCode);
        var store = factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>();
        Assert.Null(store.Load().Devices.GlacialLcdTuning);
    }
}

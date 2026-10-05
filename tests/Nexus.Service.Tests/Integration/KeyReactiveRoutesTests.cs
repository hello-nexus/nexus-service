using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>/lighting/key-reactive* through the real pipeline and source-generated JSON, against one injected keyboard card.</summary>
public class KeyReactiveRoutesTests
{
    private const string Card = "kb:dev:z0";
    private const string Device = "kb:dev";

    private static (WebApplicationFactory<Program> Factory, HttpClient Client) Boot()
    {
        var factory = new NexusAppFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        var n = 40;
        var u = new float[n];
        var v = new float[n];
        var names = new string?[n];
        for (var i = 0; i < n; i++)
        {
            u[i] = (i % 10) / 9f;
            v[i] = (i / 10) / 3f;
            names[i] = "Key: " + "1234567890QWERTYUIOPASDFGHJKL;ZXCVBNM,./"[i];
        }
        factory.Services.GetRequiredService<LightingEngine>().UpdateDevices(new[]
        {
            new DeviceFrame(7, Card, n) { Archetype = "keyboard", LedU = u, LedV = v, LedKeys = names, DeviceId = Device },
        });
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Lists_the_card_and_stores_its_config_under_the_device()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var list = await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync();
            Assert.Contains($"\"id\":\"{Card}\"", list);
            Assert.Contains($"\"deviceId\":\"{Device}\"", list);
            Assert.Contains("\"frameIndex\":7", list);
            Assert.Contains("\"namedKeys\":40", list);

            var put = await client.PutAsync($"/lighting/key-reactive/{Card}",
                Json("{\"enabled\":true,\"effect\":\"sparks\",\"colorMode\":\"random\",\"color\":\"#00ff00\",\"speed\":9,\"size\":1,\"background\":\"dim\"}"));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var saved = await put.Content.ReadAsStringAsync();
            Assert.Contains("\"speed\":3", saved);
            Assert.Contains("\"effect\":\"sparks\"", saved);

            var stored = factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.KeyReactions;
            Assert.True(stored.ContainsKey(Device));
            Assert.False(stored.ContainsKey(Card));
        }
    }

    [Fact]
    public async Task Preview_and_press_answer_for_a_keyboard_and_404_otherwise()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var body = "{\"enabled\":true,\"effect\":\"ripple\",\"colorMode\":\"rainbow\",\"color\":\"#ff0000\",\"speed\":1,\"size\":1,\"background\":\"effect\"}";
            var preview = await client.PostAsync($"/lighting/key-reactive/{Card}/preview", Json(body));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var text = await preview.Content.ReadAsStringAsync();
            Assert.Contains("\"frameCount\":", text);
            Assert.Contains("\"frames\":\"", text);

            var press = await client.PostAsync($"/lighting/key-reactive/{Card}/press", Json("{\"key\":\"G\"}"));
            Assert.Equal(HttpStatusCode.OK, press.StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/lighting/key-reactive/nope", Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/lighting/key-reactive/nope/press", Json("{}"))).StatusCode);
        }
    }
}

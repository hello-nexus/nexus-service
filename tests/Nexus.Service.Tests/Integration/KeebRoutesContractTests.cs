using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Contract test pinning every /keeb URL nexus-web (src/api/keeb.ts) calls to
/// a mapped, validated route. The Key Assignment tab shipped against routes
/// the service had never implemented - every write 404'd and only a live
/// browser session could see it; this suite fails the build instead.
///
/// Runs against the stub HID enumerator (no keyboard), so device-requiring
/// writes must fail with a 4xx + error body - never 404/405 (unmapped) and
/// never a fake success.
/// </summary>
[Collection("NexusHost")]
public class KeebRoutesContractTests
{
    private static (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Every_web_called_keeb_route_is_mapped()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            // (method, url, body-or-null) for every wrapper in nexus-web src/api/keeb.ts.
            var calls = new (string Method, string Url, string? Body)[]
            {
                ("GET", "/keeb/state?layer=0", null),
                ("GET", "/keeb/settings", null),
                ("GET", "/keeb/rotary/functions", null),
                ("GET", "/keeb/macro/0", null),
                ("POST", "/keeb/rotary", "{\"left\":\"VolumeAdjustment\",\"right\":\"ScrollY\"}"),
                ("POST", "/keeb/firmware/lighting", "{\"animationMode\":\"Static\",\"speed\":\"Standard\",\"direction\":\"LeftToRight\",\"brightness\":50}"),
                ("POST", "/keeb/passive-lighting", "{\"keyReactive\":false,\"keyReactiveMask\":false,\"keyReactiveMode\":\"SingleKey\",\"keyReactiveColor\":{\"r\":0,\"g\":0,\"b\":0,\"a\":255}}"),
                ("POST", "/keeb/game-mode", "{\"altF4\":false,\"altTab\":false,\"shiftTab\":false,\"windowsKey\":false}"),
                ("POST", "/keeb/macro/0", "{\"keys\":[]}"),
                ("POST", "/keeb/layer/0/key", "{\"x\":2,\"y\":0,\"func\":\"A\",\"mode\":\"StandardKey\",\"input\":null}"),
                ("POST", "/keeb/layer/0/reset", "{}"),
            };
            foreach (var (method, url, body) in calls)
            {
                var res = method == "GET"
                    ? await client.GetAsync(url)
                    : await client.PostAsync(url, Json(body!));
                Assert.True(res.StatusCode != HttpStatusCode.NotFound
                    && res.StatusCode != HttpStatusCode.MethodNotAllowed,
                    $"{method} {url} is not mapped ({(int)res.StatusCode})");
            }
        }
    }

    [Fact]
    public async Task Macro_save_round_trips_and_reports_no_device()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var save = await client.PostAsync("/keeb/macro/2", Json(
                "{\"keys\":[{\"key\":\"ControlLeft\",\"duration\":10,\"type\":\"Make\"}," +
                "{\"key\":\"KeyC\",\"duration\":10,\"type\":\"Make\"}," +
                "{\"key\":\"KeyC\",\"duration\":10,\"type\":\"Break\"}," +
                "{\"key\":\"ControlLeft\",\"duration\":10,\"type\":\"Break\"}]}"));
            Assert.True(save.IsSuccessStatusCode);
            var saveText = await save.Content.ReadAsStringAsync();
            Assert.Contains("\"wroteDevice\":false", saveText);
            Assert.Contains("\"truncated\":false", saveText);

            var read = await client.GetAsync("/keeb/macro/2");
            var readText = await read.Content.ReadAsStringAsync();
            Assert.Contains("ControlLeft", readText);
            Assert.Contains("KeyC", readText);
        }
    }

    [Fact]
    public async Task Macro_index_and_body_are_validated()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsync("/keeb/macro/16", Json("{\"keys\":[]}"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsync("/keeb/macro/-1", Json("{\"keys\":[]}"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsync("/keeb/macro/0", Json("{}"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync("/keeb/macro/99")).StatusCode);
        }
    }

    [Fact]
    public async Task Layer_key_write_without_a_device_fails_honestly()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/keeb/layer/0/key",
                Json("{\"x\":2,\"y\":0,\"func\":\"A\",\"mode\":\"StandardKey\"}"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"error\":true", text);
            Assert.Contains("connected", text);
        }
    }

    [Fact]
    public async Task Layer_key_write_validates_coordinates_and_function()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var badCell = await client.PostAsync("/keeb/layer/0/key",
                Json("{\"x\":99,\"y\":99,\"func\":\"A\",\"mode\":\"StandardKey\"}"));
            Assert.Equal(HttpStatusCode.BadRequest, badCell.StatusCode);
            Assert.Contains("no key at", await badCell.Content.ReadAsStringAsync());

            var badFn = await client.PostAsync("/keeb/layer/0/key",
                Json("{\"x\":2,\"y\":0,\"func\":\"Bogus\",\"mode\":\"StandardKey\"}"));
            Assert.Equal(HttpStatusCode.BadRequest, badFn.StatusCode);
            Assert.Contains("unknown function", await badFn.Content.ReadAsStringAsync());

            var badLayer = await client.PostAsync("/keeb/layer/9/key",
                Json("{\"x\":2,\"y\":0,\"func\":\"A\",\"mode\":\"StandardKey\"}"));
            Assert.Equal(HttpStatusCode.BadRequest, badLayer.StatusCode);
        }
    }

    [Fact]
    public async Task Layer_reset_with_nothing_to_reset_is_a_successful_noop()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/keeb/layer/1/reset", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);
            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"wroteDevice\":false", text);
        }
    }
}

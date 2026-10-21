using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Weather;
using Nexus.Service.Platform.Weather;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Weather;

public sealed class OpenMeteoWeatherProviderErrorTests
{
    private const string GoodForecast =
        "{\"timezone\":\"UTC\",\"current\":{\"time\":\"2026-09-12T19:30\",\"temperature_2m\":20.0,\"weather_code\":0}}";

    [Fact]
    public void Classify_maps_exceptions_to_wire_reasons()
    {
        Assert.Equal("timeout", OpenMeteoWeatherProvider.Classify(new TaskCanceledException("t", new TimeoutException())));
        Assert.Equal("timeout", OpenMeteoWeatherProvider.Classify(new TimeoutException()));
        Assert.Equal("unreachable", OpenMeteoWeatherProvider.Classify(new HttpRequestException("dns")));
        Assert.Equal("invalid", OpenMeteoWeatherProvider.Classify(new JsonException("bad")));
    }

    [Fact]
    public void Describe_includes_type_and_inner_message()
    {
        var text = OpenMeteoWeatherProvider.Describe(new HttpRequestException("outer", new System.Net.Sockets.SocketException(11001)));
        Assert.Contains("HttpRequestException: outer", text);
        Assert.Contains("SocketException", text);
    }

    [Fact]
    public async Task Success_has_no_error()
    {
        var p = Provider(_ => Task.FromResult(Json(GoodForecast)));
        var snap = await p.GetCurrentAsync(1, 2);
        Assert.Null(snap.Error);
        Assert.NotEqual("", snap.AsOf);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "http")]
    [InlineData(HttpStatusCode.OK, "invalid")]
    public async Task Empty_with_error_when_nothing_cached(HttpStatusCode status, string expected)
    {
        var p = Provider(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") }));
        var snap = await p.GetCurrentAsync(1, 2);
        Assert.Equal(expected, snap.Error);
        Assert.Equal("", snap.AsOf);
    }

    [Fact]
    public async Task Timeout_and_unreachable_are_reported()
    {
        var timeout = Provider(_ => throw new TaskCanceledException("t", new TimeoutException()));
        Assert.Equal("timeout", (await timeout.GetCurrentAsync(1, 2)).Error);

        var down = Provider(_ => throw new HttpRequestException("no route"));
        Assert.Equal("unreachable", (await down.GetCurrentAsync(1, 2)).Error);
    }

    [Fact]
    public async Task Stale_snapshot_is_served_with_error_and_cache_stays_clean()
    {
        var fail = false;
        var offset = TimeSpan.Zero;
        var p = Provider(_ => fail
            ? throw new TaskCanceledException("t", new TimeoutException())
            : Task.FromResult(Json(GoodForecast)));
        p.Clock = () => DateTime.UtcNow + offset;
        var first = await p.GetCurrentAsync(1, 2);
        Assert.Null(first.Error);

        offset = TimeSpan.FromMinutes(30);
        fail = true;
        var stale = await p.GetCurrentAsync(1, 2);
        Assert.Equal("timeout", stale.Error);
        Assert.Equal(first.AsOf, stale.AsOf);
        Assert.Equal(20.0, stale.TemperatureC);
        Assert.Null(first.Error);

        fail = false;
        var recovered = await p.GetCurrentAsync(1, 2);
        Assert.Null(recovered.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "http")]
    [InlineData(null, "timeout")]
    public async Task Auto_location_failure_with_no_cache_is_empty_with_error(HttpStatusCode? status, string expected)
    {
        var p = Provider(_ => status is { } code
            ? Task.FromResult(new HttpResponseMessage(code))
            : throw new TaskCanceledException("t", new TimeoutException()));
        var snap = await p.GetCurrentAsync();
        Assert.Equal(expected, snap.Error);
        Assert.Equal("", snap.AsOf);
    }

    [Fact]
    public async Task Air_quality_failure_does_not_set_error()
    {
        var p = Provider(req => req.RequestUri!.Host.StartsWith("air-quality", StringComparison.Ordinal)
            ? throw new HttpRequestException("aq down")
            : Task.FromResult(Json(GoodForecast)));
        Assert.Null((await p.GetCurrentAsync(1, 2)).Error);
    }

    [Fact]
    public void Error_serializes_camelCase_and_is_omitted_when_null()
    {
        var withError = JsonSerializer.Serialize(WeatherSnapshot.Empty.WithError("timeout"), AppJsonContext.Default.WeatherSnapshot);
        Assert.Contains("\"error\":\"timeout\"", withError);
        var without = JsonSerializer.Serialize(WeatherSnapshot.Empty, AppJsonContext.Default.WeatherSnapshot);
        Assert.DoesNotContain("\"error\"", without);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static OpenMeteoWeatherProvider Provider(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new Factory(new FuncHandler(respond)));

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}

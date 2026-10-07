using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Weather;
using Nexus.Service.Platform.Weather;
using Xunit;

namespace Nexus.Service.Tests.Weather;

public sealed class WeatherUnavailableTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Task<WeatherSnapshot> Fetch(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new OpenMeteoWeatherProvider(new StubFactory(new StubHandler(respond))).GetCurrentAsync(37.77, -122.42, "SF", "US");

    [Fact]
    public async Task AnErrorReplyFromTheProvider_IsTheWeatherService()
    {
        var snap = await Fetch(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"reason\":\"The service is overloaded\",\"error\":true}"),
        });
        Assert.Equal(WeatherUnavailable.Service, snap.Unavailable);
        Assert.Null(snap.TemperatureC);
    }

    [Fact]
    public async Task NoReplyAtAll_IsTheConnection()
    {
        var snap = await Fetch(_ => throw new HttpRequestException("No such host is known."));
        Assert.Equal(WeatherUnavailable.Network, snap.Unavailable);
    }

    [Fact]
    public async Task ATimeout_IsTheConnection()
    {
        var snap = await Fetch(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        Assert.Equal(WeatherUnavailable.Network, snap.Unavailable);
    }

    [Fact]
    public async Task AFailedLocationLookup_OnTheAutoPath_CarriesItsReason()
    {
        var provider = new OpenMeteoWeatherProvider(new StubFactory(new StubHandler(_ => throw new HttpRequestException("No such host is known."))));
        Assert.Equal(WeatherUnavailable.Network, (await provider.GetCurrentAsync()).Unavailable);
    }

    [Fact]
    public void AnExceptionCarryingAStatusCode_IsTheWeatherService()
    {
        Assert.Equal(WeatherUnavailable.Service, OpenMeteoWeatherProvider.Classify(new HttpRequestException("bad", null, HttpStatusCode.BadGateway)));
        Assert.Equal(WeatherUnavailable.Service, OpenMeteoWeatherProvider.Classify(new System.Text.Json.JsonException("unreadable")));
    }
}

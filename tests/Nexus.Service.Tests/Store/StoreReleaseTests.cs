using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Store;
using Xunit;

namespace Nexus.Service.Tests.Store;

/// <summary>
/// The catalog read behind the hardware auto-install and the app updater. An app
/// whose launch day is still ahead resolves to nothing, so neither installs it.
/// </summary>
public sealed class StoreReleaseTests
{
    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <param name="releaseDate">The raw JSON value, or null to leave the field out entirely.</param>
    private static Task<Nexus.Service.Models.Widgets.StoreCatalogVersion?> Latest(string? releaseDate)
    {
        var field = releaseDate is null ? "" : $"\"releaseDate\":{releaseDate},";
        var body = $$$"""{"id":"com.hellonexus.ina",{{{field}}}"latest":{"version":"1.0.0","sha256":"{{{new string('a', 64)}}}","size":10}}""";
        return StoreRelease.LatestAsync(
            new StoreCatalogProxy(new HttpClient(new JsonHandler(body))),
            "com.hellonexus.ina", "3.0.0", CancellationToken.None);
    }

    private static string Iso(DateTimeOffset at) => $"\"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}\"";

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task An_app_with_no_release_date_is_released(string? releaseDate)
    {
        var latest = await Latest(releaseDate);

        Assert.NotNull(latest);
        Assert.Equal("1.0.0", latest!.Version);
    }

    [Fact]
    public async Task An_app_before_its_launch_day_resolves_to_nothing()
    {
        Assert.Null(await Latest(Iso(DateTimeOffset.UtcNow.AddDays(7))));
    }

    [Fact]
    public async Task An_app_past_its_launch_day_resolves()
    {
        Assert.NotNull(await Latest(Iso(DateTimeOffset.UtcNow.AddMinutes(-1))));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lifecycle;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// POST /service/restart through the real pipeline. The finalizer claim is held so Begin refuses and nothing spawns or
/// stops the host: a 409 "already in progress" means the request passed validation and reached Begin.
/// </summary>
[Collection("FactoryResetLatch")]
public sealed class ServiceRestartRouteTests : IClassFixture<NexusAppFactory>, IDisposable
{
    private readonly NexusAppFactory _factory;

    public ServiceRestartRouteTests(NexusAppFactory factory)
    {
        _factory = factory;
        FactoryReset.ClaimFinalizerForTests();
    }

    public void Dispose() => FactoryReset.ResetFinalizerClaimForTests();

    private async Task<(HttpStatusCode Status, string Body)> Post(HttpContent? content)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        var response = await client.PostAsync("/service/restart", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static void AssertReachedBegin((HttpStatusCode Status, string Body) r)
    {
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Contains("already in progress", r.Body);
    }

    [Fact]
    public async Task No_body_behaves_as_before()
    {
        AssertReachedBegin(await Post(null));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"reopenRoute\":null}")]
    [InlineData("{\"reopenRoute\":\"/system/lighting\"}")]
    [InlineData("{\"reopenRoute\":\"/system/lighting?tab=effects\"}")]
    public async Task A_json_body_without_a_route_or_with_a_valid_one_reaches_begin(string body)
    {
        AssertReachedBegin(await Post(Json(body)));
    }

    [Theory]
    [InlineData("text/plain", "not json at all")]
    [InlineData("text/plain", "{\"reopenRoute\":\"https://evil.example\"}")]
    [InlineData("application/x-www-form-urlencoded", "reopenRoute=%2F%2Fevil")]
    public async Task A_non_json_body_is_ignored_exactly_as_before(string contentType, string body)
    {
        AssertReachedBegin(await Post(new StringContent(body, Encoding.UTF8, contentType)));
    }

    [Theory]
    [InlineData("{\"reopenRoute\":\"https://evil.example/x\"}")]
    [InlineData("{\"reopenRoute\":\"//evil.example\"}")]
    [InlineData("{\"reopenRoute\":\"\"}")]
    [InlineData("{\"reopenRoute\":\"/system/..\\\\x\"}")]
    [InlineData("{\"reopenRoute\":\"/other/page\"}")]
    public async Task An_invalid_route_is_a_400_before_the_in_progress_guard(string body)
    {
        var r = await Post(Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
        Assert.Contains("reopenRoute", doc.RootElement.GetProperty("msg").GetString());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    public async Task Malformed_json_is_a_400_with_the_standard_envelope(string body)
    {
        var r = await Post(Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("msg").GetString()));
    }

    private static DefaultHttpContext Context(string? contentType, string body)
    {
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.ContentType = contentType;
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return ctx;
    }

    [Fact]
    public async Task The_parsed_route_is_exactly_the_requested_one_or_none()
    {
        var valid = await ServiceControlRoutes.ReadRestartRouteAsync(Context("application/json", "{\"reopenRoute\":\"/system/lighting\"}").Request);
        var none = await ServiceControlRoutes.ReadRestartRouteAsync(Context("application/json", "{}").Request);
        var text = await ServiceControlRoutes.ReadRestartRouteAsync(Context("text/plain", "{\"reopenRoute\":\"/system/lighting\"}").Request);
        var noType = await ServiceControlRoutes.ReadRestartRouteAsync(Context(null, "{\"reopenRoute\":\"/system/lighting\"}").Request);
        var huge = await ServiceControlRoutes.ReadRestartRouteAsync(Context("application/json", "{\"reopenRoute\":\"" + new string('a', 10_000) + "\"}").Request);

        Assert.Equal((true, "/system/lighting"), (valid.Ok, valid.Route));
        Assert.Equal((true, (string?)null), (none.Ok, none.Route));
        Assert.Equal((true, (string?)null), (text.Ok, text.Route));
        Assert.Equal((true, (string?)null), (noType.Ok, noType.Route));
        Assert.False(huge.Ok);
    }
}

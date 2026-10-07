using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cloud;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Power;
using Nexus.Service.Tests.Cloud;
using Nexus.Service.Tests.Sentry;
using Xunit;
using SentryCoordinator = Nexus.Service.Sentry.SentryCoordinator;

namespace Nexus.Service.Tests.Integration;

/// <summary>A fake power provider replaces the real one: the real Lock() would lock the machine running the tests.</summary>
public sealed class SentryAppFactory : NexusAppFactory
{
    public FakePowerProvider Power { get; } = new();
    public FakeCloudApiClient Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISystemPowerProvider>();
            services.AddSingleton<ISystemPowerProvider>(Power);
            services.RemoveAll<ICloudApiClient>();
            services.AddSingleton<ICloudApiClient>(Api);
        });
    }
}

public sealed class SentryRoutesTests : IClassFixture<SentryAppFactory>
{
    private readonly SentryAppFactory _factory;
    private readonly SentryCoordinator _sentry;
    private readonly PanelPhonePairingService _pairing;

    public SentryRoutesTests(SentryAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _factory.Power.LockCalls = 0;
        _factory.Power.LockResult = true;
        _factory.Power.OnLock = null;
        _sentry = _factory.Services.GetRequiredService<SentryCoordinator>();
        _pairing = _factory.Services.GetRequiredService<PanelPhonePairingService>();
        // No watch is wired in the test host; give it one so the feature reads as supported.
        _sentry.LockInputWatch = _ => { };
        _sentry.OnLockChanged(false);
    }

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private HttpClient Phone() => TestPhoneSession.CreateClient(_factory);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private const string PushBody =
        """{"platform":"ios","token":"abc123","environment":"production","title":"Sentry","body":"Someone is typing on {pc}"}""";

    [Fact]
    public async Task Status_needs_a_token_and_a_panel_session_can_read_it()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/sentry")).StatusCode);

        var res = await Phone().GetAsync("/sentry");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Body(res);
        Assert.True(body.GetProperty("supported").GetBoolean());
        Assert.False(body.GetProperty("armed").GetBoolean());
        Assert.False(body.GetProperty("locked").GetBoolean());
        Assert.Equal(0, body.GetProperty("alertPhones").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastAlertAt").ValueKind);
        Assert.Equal((int)Nexus.Service.Sentry.SentryCoordinator.Cooldown.TotalSeconds, body.GetProperty("cooldownSeconds").GetInt32());
    }

    [Fact]
    public async Task Arming_without_a_lock_while_unlocked_is_409_not_locked()
    {
        var res = await Phone().PostAsync("/sentry/arm", Json("""{"lock":false}"""));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("not_locked", (await Body(res)).GetProperty("error").GetString());
        Assert.Equal(0, _factory.Power.LockCalls);
    }

    [Fact]
    public async Task A_phone_arms_once_the_pc_is_locked_and_can_disarm()
    {
        _sentry.OnLockChanged(true);
        var phone = Phone();

        var arm = await phone.PostAsync("/sentry/arm", Json("""{"lock":false}"""));
        Assert.Equal(HttpStatusCode.OK, arm.StatusCode);
        var armed = await Body(arm);
        Assert.True(armed.GetProperty("armed").GetBoolean());
        Assert.True(armed.GetProperty("locked").GetBoolean());
        Assert.Equal(0, _factory.Power.LockCalls);

        var disarm = await phone.PostAsync("/sentry/disarm", null);
        Assert.Equal(HttpStatusCode.OK, disarm.StatusCode);
        Assert.False((await Body(disarm)).GetProperty("armed").GetBoolean());
    }

    [Fact]
    public async Task A_phone_can_lock_and_arm_through_the_provider()
    {
        _factory.Power.OnLock = () => _sentry.OnLockChanged(true);

        var phone = await Phone().PostAsync("/sentry/arm", Json("""{"lock":true}"""));

        Assert.Equal(HttpStatusCode.OK, phone.StatusCode);
        Assert.Equal(1, _factory.Power.LockCalls);
        Assert.True((await Body(phone)).GetProperty("armed").GetBoolean());
    }

    [Fact]
    public async Task The_desktop_can_lock_and_arm_through_the_provider()
    {
        _factory.Power.OnLock = () => _sentry.OnLockChanged(true);

        var desktop = await Desktop().PostAsync("/sentry/arm", Json("""{"lock":true}"""));

        Assert.Equal(HttpStatusCode.OK, desktop.StatusCode);
        Assert.Equal(1, _factory.Power.LockCalls);
        Assert.True((await Body(desktop)).GetProperty("armed").GetBoolean());
    }

    [Fact]
    public async Task A_failed_lock_is_a_500_and_does_not_arm()
    {
        _factory.Power.LockResult = false;

        var res = await Desktop().PostAsync("/sentry/arm", Json("""{"lock":true}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("lock_failed", (await Body(res)).GetProperty("error").GetString());
        Assert.False(_sentry.GetStatus().Armed);
    }

    [Fact]
    public async Task Put_push_stores_the_target_on_the_phone_session_and_delete_removes_it()
    {
        var phone = Phone();

        var put = await phone.PutAsync("/panel/phone/push", Json(PushBody));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var target = Assert.Single(_pairing.GetPushTargets());
        Assert.Equal("abc123", target.Token);
        Assert.Equal("Someone is typing on {pc}", target.Body);
        Assert.Equal(1, (await Body(await phone.GetAsync("/sentry"))).GetProperty("alertPhones").GetInt32());

        var delete = await phone.DeleteAsync("/panel/phone/push");

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Empty(_pairing.GetPushTargets());
    }

    [Fact]
    public async Task Put_push_again_replaces_the_phones_target()
    {
        var phone = Phone();
        await phone.PutAsync("/panel/phone/push", Json(PushBody));

        await phone.PutAsync("/panel/phone/push", Json(PushBody.Replace("abc123", "def456")));

        Assert.Equal(new[] { "def456" }, _pairing.GetPushTargets().Select(t => t.Token));
    }

    [Fact]
    public async Task Revoking_the_session_removes_its_push_target()
    {
        var phone = Phone();
        await phone.PutAsync("/panel/phone/push", Json(PushBody));
        var sessionId = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>()
            .Load().Auth!.PanelPhoneSessions.Single().Id;

        Assert.True(await _pairing.RevokeSessionAsync(sessionId));

        Assert.Empty(_pairing.GetPushTargets());
    }

    [Theory]
    [InlineData("""{"platform":"windows","token":"abc","environment":"production","title":"t","body":"b"}""")]
    [InlineData("""{"platform":"ios","token":"abc","environment":"beta","title":"t","body":"b"}""")]
    [InlineData("""{"platform":"ios","token":"","environment":"production","title":"t","body":"b"}""")]
    [InlineData("""{"platform":"ios","token":"a b","environment":"production","title":"t","body":"b"}""")]
    [InlineData("""{"platform":"ios","token":"abc","environment":"production","title":"","body":"b"}""")]
    [InlineData("""{"platform":"ios","token":"abc","environment":"production","title":"t","body":""}""")]
    public async Task Put_push_rejects_a_malformed_target(string body)
    {
        var res = await Phone().PutAsync("/panel/phone/push", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(_pairing.GetPushTargets());
    }

    [Fact]
    public async Task Put_push_rejects_over_long_title_and_body()
    {
        var phone = Phone();
        var longTitle = new string('a', 65);
        var longBody = new string('a', 201);

        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PutAsync("/panel/phone/push",
            Json($$"""{"platform":"ios","token":"abc","environment":"production","title":"{{longTitle}}","body":"b"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PutAsync("/panel/phone/push",
            Json($$"""{"platform":"ios","token":"abc","environment":"production","title":"t","body":"{{longBody}}"}"""))).StatusCode);
    }

    [Fact]
    public async Task Put_push_without_a_phone_session_is_rejected()
    {
        var res = await Desktop().PutAsync("/panel/phone/push", Json(PushBody));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}

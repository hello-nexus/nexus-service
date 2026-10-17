using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class LightingCurrentRemovedNoticeTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public LightingCurrentRemovedNoticeTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private async Task<bool> NoticeFlag()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/lighting/current"));
        return doc.RootElement.GetProperty("gameSyncRemovedNotice").GetBoolean();
    }

    [Fact]
    public async Task Current_reports_the_removed_notice_from_settings()
    {
        Assert.False(await NoticeFlag());

        _factory.Services.GetRequiredService<IConfigStore>().Update(s => s.GameSyncRemovedNotice = true);

        Assert.True(await NoticeFlag());
    }
}

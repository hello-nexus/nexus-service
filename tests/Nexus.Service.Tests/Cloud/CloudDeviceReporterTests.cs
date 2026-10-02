using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Tests.Mcp;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

public sealed class CloudDeviceReporterTests
{
    private sealed record Rig(CloudDeviceReporter Reporter, FakeCloudApiClient Api, InMemoryConfigStore Config, string InstallId, List<(HttpMethod Method, string Path)> Raw);

    private static Rig Make(bool signedIn = true)
    {
        var api = new FakeCloudApiClient();
        var config = new InMemoryConfigStore();
        var accounts = new CloudAccountService(api, config);
        var raw = new List<(HttpMethod, string)>();
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            Account = new CloudAccountDto { Id = "acct-1", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        api.OnSendRaw = (method, path, _, _) =>
        {
            raw.Add((method, path));
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "" });
        };
        if (signedIn)
        {
            config.Update(s =>
            {
                s.Auth ??= new AuthSettings();
                s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh" });
                s.Auth.ActiveCloudAccountId = "acct-1";
            });
        }
        var reporter = new CloudDeviceReporter(api, accounts, new SystemSpecsCollector(new McpTestHarness.StubSensorProvider()), config, TimeProvider.System);
        return new Rig(reporter, api, config, accounts.ResolveStableInstallId(), raw);
    }

    [Fact]
    public async Task Reports_by_default()
    {
        var rig = Make();

        await rig.Reporter.ReportSafeAsync("acct-1", CancellationToken.None);

        Assert.True(rig.Config.Load().ReportSystem);
        Assert.Equal(1, rig.Api.PutDeviceCalls);
    }

    [Fact]
    public async Task Turning_reporting_off_removes_this_machine_and_stops_reports()
    {
        var rig = Make();

        Assert.True(await rig.Reporter.SetReportingAsync(false));
        await rig.Reporter.ReportSafeAsync("acct-1", CancellationToken.None);

        Assert.False(rig.Config.Load().ReportSystem);
        Assert.Equal([(HttpMethod.Delete, "/account/devices/" + Uri.EscapeDataString(rig.InstallId))], rig.Raw);
        Assert.Equal(0, rig.Api.PutDeviceCalls);
    }

    [Fact]
    public async Task A_failed_removal_keeps_reporting_on()
    {
        var rig = Make();
        // The real client answers every HTTP status as a successful send carrying that status.
        rig.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "{}" }, 429);

        Assert.False(await rig.Reporter.SetReportingAsync(false));

        Assert.True(rig.Config.Load().ReportSystem);
    }

    [Fact]
    public async Task A_row_already_gone_counts_as_removed()
    {
        var rig = Make();
        rig.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "{}" }, 404);

        Assert.True(await rig.Reporter.SetReportingAsync(false));

        Assert.False(rig.Config.Load().ReportSystem);
    }

    [Fact]
    public async Task An_unreachable_account_keeps_reporting_on()
    {
        var rig = Make();
        rig.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Fail(0, "offline", "no route");

        Assert.False(await rig.Reporter.SetReportingAsync(false));

        Assert.True(rig.Config.Load().ReportSystem);
    }

    [Fact]
    public async Task Turning_reporting_back_on_reports_at_once()
    {
        var rig = Make();
        await rig.Reporter.SetReportingAsync(false);

        Assert.True(await rig.Reporter.SetReportingAsync(true));

        Assert.True(rig.Config.Load().ReportSystem);
        Assert.Equal(1, rig.Api.PutDeviceCalls);
    }

    [Fact]
    public async Task Signed_out_the_setting_saves_without_calling_the_api()
    {
        var rig = Make(signedIn: false);

        Assert.True(await rig.Reporter.SetReportingAsync(false));

        Assert.False(rig.Config.Load().ReportSystem);
        Assert.Empty(rig.Raw);
    }
}

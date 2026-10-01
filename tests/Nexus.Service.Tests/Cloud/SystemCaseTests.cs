using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

public sealed class SystemCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-case-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "system-case.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("a", true)]
    [InlineData("case-1.v2_x:y", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("slash/x", false)]
    [InlineData("naïve", false)]
    public void IsValidCaseId_follows_the_api_rule(string? id, bool expected) =>
        Assert.Equal(expected, SystemCaseService.IsValidCaseId(id));

    [Fact]
    public void IsValidCaseId_bounds_length_at_64()
    {
        Assert.True(SystemCaseService.IsValidCaseId(new string('a', 64)));
        Assert.False(SystemCaseService.IsValidCaseId(new string('a', 65)));
    }

    [Fact]
    public void Store_missing_file_reads_empty()
    {
        var file = new MachineCaseStore(FilePath).Load();
        Assert.Null(file.CaseId);
        Assert.False(file.Pending);
    }

    [Fact]
    public void Store_survives_a_restart_and_keeps_pending()
    {
        new MachineCaseStore(FilePath).Save("case-9", pending: true);

        var reloaded = new MachineCaseStore(FilePath).Load();

        Assert.Equal("case-9", reloaded.CaseId);
        Assert.True(reloaded.Pending);
    }

    [Fact]
    public void Store_corrupt_file_reads_empty_and_next_save_repairs_it()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{not json");
        var store = new MachineCaseStore(FilePath);

        Assert.Null(store.Load().CaseId);
        store.Save("ok", pending: false);
        Assert.Equal("ok", new MachineCaseStore(FilePath).Load().CaseId);
    }

    private sealed class Rig
    {
        public required SystemCaseService Svc;
        public required FakeCloudApiClient Api;
        public required InMemoryConfigStore Config;
        public required MachineCaseStore Store;
        public required ManualTimeProvider Clock;
        public bool Held;
        public string InstallId = "";
    }

    private Rig Make(string? activeAccount = "acct-1")
    {
        var api = new FakeCloudApiClient();
        var config = new InMemoryConfigStore();
        var accounts = new CloudAccountService(api, config);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new MachineCaseStore(FilePath);
        var rig = new Rig { Svc = null!, Api = api, Config = config, Store = store, Clock = clock };
        rig.Svc = new SystemCaseService(store, accounts, api, clock, () => rig.Held);
        rig.InstallId = accounts.ResolveStableInstallId();
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            Account = new CloudAccountDto { Id = "acct-1", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        if (activeAccount is not null)
        {
            SignIn(rig, activeAccount);
        }
        return rig;
    }

    private static void SignIn(Rig rig, string accountId) =>
        rig.Config.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = accountId, RefreshToken = "refresh" });
            s.Auth.ActiveCloudAccountId = accountId;
        });

    [Fact]
    public async Task Set_signed_out_saves_locally_stays_pending_and_never_calls_the_api()
    {
        var rig = Make(activeAccount: null);

        var saved = await rig.Svc.SetAsync("case-1", CancellationToken.None);
        await rig.Svc.IdleAsync();

        Assert.Equal("case-1", saved);
        Assert.Equal(0, rig.Api.SetDeviceCaseCalls);
        Assert.Equal(0, rig.Api.ListDevicesCalls);
        Assert.True(rig.Store.Load().Pending);
        Assert.Equal("case-1", await rig.Svc.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Set_signed_in_pushes_with_the_install_id_and_clears_pending()
    {
        var rig = Make();
        string? sentInstall = null;
        string? sentCase = "unset";
        rig.Api.OnSetDeviceCase = (_, install, id) =>
        {
            sentInstall = install;
            sentCase = id;
            return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        };

        await rig.Svc.SetAsync("case-1", CancellationToken.None);
        await rig.Svc.IdleAsync();

        Assert.Equal(rig.InstallId, sentInstall);
        Assert.Equal("case-1", sentCase);
        Assert.False(rig.Store.Load().Pending);
    }

    [Fact]
    public async Task Set_null_pushes_a_null_clear()
    {
        var rig = Make();
        string? sentCase = "unset";
        rig.Api.OnSetDeviceCase = (_, _, id) =>
        {
            sentCase = id;
            return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        };
        rig.Store.Save("old", pending: false);

        await rig.Svc.SetAsync(null, CancellationToken.None);
        await rig.Svc.IdleAsync();

        Assert.Null(sentCase);
        Assert.Null(rig.Store.Load().CaseId);
        Assert.Equal(1, rig.Api.SetDeviceCaseCalls);
    }

    [Fact]
    public async Task Failed_push_keeps_the_local_value_pending_then_a_device_report_flush_clears_it()
    {
        var rig = Make();
        rig.Api.OnSetDeviceCase = (_, _, _) => CloudApiResult<CloudVoid>.Fail(404, "not_found", "no such device");

        var saved = await rig.Svc.SetAsync("case-1", CancellationToken.None);
        await rig.Svc.IdleAsync();

        Assert.Equal("case-1", saved);
        Assert.True(rig.Store.Load().Pending);

        rig.Api.OnSetDeviceCase = (_, _, _) => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        await rig.Svc.FlushPendingAsync("acct-1", CancellationToken.None);

        Assert.Equal(2, rig.Api.SetDeviceCaseCalls);
        Assert.False(rig.Store.Load().Pending);
    }

    [Fact]
    public async Task Offline_push_stays_pending_and_the_next_get_retries_it()
    {
        var rig = Make();
        rig.Api.OnSetDeviceCase = (_, _, _) => CloudApiResult<CloudVoid>.NetworkError("offline");
        await rig.Svc.SetAsync("case-1", CancellationToken.None);
        await rig.Svc.IdleAsync();
        Assert.True(rig.Store.Load().Pending);

        rig.Api.OnSetDeviceCase = (_, _, _) => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        var got = await rig.Svc.GetAsync(CancellationToken.None);
        await rig.Svc.IdleAsync();

        Assert.Equal("case-1", got);
        Assert.Equal(2, rig.Api.SetDeviceCaseCalls);
        Assert.Equal(0, rig.Api.ListDevicesCalls);
        Assert.False(rig.Store.Load().Pending);
    }

    [Fact]
    public async Task Get_adopts_the_account_value_when_nothing_is_pending()
    {
        var rig = Make();
        rig.Store.Save("local", pending: false);
        rig.Api.OnListDevices = _ => CloudApiResult<List<CloudDeviceDto>>.Ok(new List<CloudDeviceDto>
        {
            new() { InstallId = "other", CaseId = "x" },
            new() { InstallId = rig.InstallId, CaseId = "from-web" },
        });

        var got = await rig.Svc.GetAsync(CancellationToken.None);
        await rig.Svc.IdleAsync();

        Assert.Equal("local", got);
        Assert.Equal("from-web", rig.Store.Load().CaseId);
        Assert.Equal("from-web", await rig.Svc.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Get_never_adopts_a_null_account_value_and_pushes_the_local_pick_instead()
    {
        var rig = Make();
        rig.Store.Save("local", pending: false);
        string? pushed = null;
        rig.Api.OnListDevices = _ => CloudApiResult<List<CloudDeviceDto>>.Ok(new List<CloudDeviceDto>
        {
            new() { InstallId = rig.InstallId, CaseId = null },
        });
        rig.Api.OnSetDeviceCase = (_, _, id) =>
        {
            pushed = id;
            return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        };

        Assert.Equal("local", await rig.Svc.GetAsync(CancellationToken.None));
        await rig.Svc.IdleAsync();

        Assert.Equal("local", rig.Store.Load().CaseId);
        Assert.Equal("local", pushed);
        Assert.False(rig.Store.Load().Pending);
    }

    [Fact]
    public async Task Get_returns_at_once_while_the_account_read_hangs_and_adopts_after_it_finishes()
    {
        var rig = Make();
        rig.Store.Save("local", pending: false);
        using var release = new ManualResetEventSlim();
        rig.Api.OnListDevices = _ =>
        {
            release.Wait();
            return CloudApiResult<List<CloudDeviceDto>>.Ok(new List<CloudDeviceDto>
            {
                new() { InstallId = rig.InstallId, CaseId = "from-web" },
            });
        };

        var got = await rig.Svc.GetAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("local", got);
        release.Set();
        await rig.Svc.IdleAsync();
        Assert.Equal("from-web", rig.Store.Load().CaseId);
    }

    [Fact]
    public async Task Get_reads_the_account_at_most_once_a_minute()
    {
        var rig = Make();

        await rig.Svc.GetAsync(CancellationToken.None);
        await rig.Svc.IdleAsync();
        await rig.Svc.GetAsync(CancellationToken.None);
        await rig.Svc.IdleAsync();
        Assert.Equal(1, rig.Api.ListDevicesCalls);

        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        await rig.Svc.GetAsync(CancellationToken.None);
        await rig.Svc.IdleAsync();
        Assert.Equal(2, rig.Api.ListDevicesCalls);
    }

    [Fact]
    public async Task Get_ignores_an_invalid_account_value()
    {
        var rig = Make();
        rig.Store.Save("local", pending: false);
        rig.Api.OnListDevices = _ => CloudApiResult<List<CloudDeviceDto>>.Ok(new List<CloudDeviceDto>
        {
            new() { InstallId = rig.InstallId, CaseId = "bad id!" },
        });

        Assert.Equal("local", await rig.Svc.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Get_signed_out_or_offline_answers_locally_without_failing()
    {
        var signedOut = Make(activeAccount: null);
        signedOut.Store.Save("local", pending: false);
        Assert.Equal("local", await signedOut.Svc.GetAsync(CancellationToken.None));
        Assert.Equal(0, signedOut.Api.ListDevicesCalls);

        var offline = Make();
        offline.Store.Save("local", pending: false);
        offline.Api.OnListDevices = _ => CloudApiResult<List<CloudDeviceDto>>.NetworkError("offline");
        Assert.Equal("local", await offline.Svc.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Focus_network_gate_blocks_every_call()
    {
        var rig = Make();
        rig.Held = true;

        await rig.Svc.SetAsync("case-1", CancellationToken.None);
        await rig.Svc.IdleAsync();
        await rig.Svc.GetAsync(CancellationToken.None);
        await rig.Svc.IdleAsync();
        await rig.Svc.FlushPendingAsync("acct-1", CancellationToken.None);

        Assert.Equal(0, rig.Api.SetDeviceCaseCalls);
        Assert.Equal(0, rig.Api.ListDevicesCalls);
        Assert.Equal("case-1", rig.Store.Load().CaseId);
        Assert.True(rig.Store.Load().Pending);
    }

    [Fact]
    public async Task Pending_pick_is_pushed_to_the_account_that_becomes_active()
    {
        var rig = Make(activeAccount: null);
        await rig.Svc.SetAsync("case-1", CancellationToken.None);
        await rig.Svc.IdleAsync();
        SignIn(rig, "acct-2");
        string? sent = null;
        rig.Api.OnSetDeviceCase = (_, _, id) =>
        {
            sent = id;
            return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        };

        await rig.Svc.FlushPendingAsync("acct-2", CancellationToken.None);

        Assert.Equal("case-1", sent);
        Assert.False(rig.Store.Load().Pending);
    }

    [Fact]
    public async Task Flush_for_a_no_longer_active_account_does_nothing()
    {
        var rig = Make();
        rig.Store.Save("case-1", pending: true);

        await rig.Svc.FlushPendingAsync("acct-old", CancellationToken.None);

        Assert.Equal(0, rig.Api.SetDeviceCaseCalls);
        Assert.True(rig.Store.Load().Pending);
    }

    [Fact]
    public async Task A_newer_pick_made_during_a_push_stays_pending()
    {
        var rig = Make();
        rig.Api.OnSetDeviceCase = (_, _, _) =>
        {
            rig.Store.Save("case-2", pending: true);
            return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
        };

        await rig.Svc.SetAsync("case-1", CancellationToken.None);
        await rig.Svc.IdleAsync();

        var file = rig.Store.Load();
        Assert.Equal("case-2", file.CaseId);
        Assert.True(file.Pending);
    }
}

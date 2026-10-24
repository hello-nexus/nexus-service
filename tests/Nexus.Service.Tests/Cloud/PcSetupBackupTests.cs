using System.Text.Json;
using Nexus.Service.Cloud;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Cloud;

/// <summary>The shared PC setup rides the manual profile Back up as one reserved app-data document and comes back through the manual restore.</summary>
public sealed class PcSetupBackupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-pc-setup-backup-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;
    private readonly FakeCloudApiClient _api = new();
    private readonly CloudAccountService _accounts;
    private readonly AppDataStore _appData;
    private readonly LightingSceneStore _scene;
    private readonly CloudProfileSyncService _sync;
    private readonly CloudProfileLibraryService _library;

    public PcSetupBackupTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
        _accounts = new CloudAccountService(_api, _store, TimeProvider.System);
        _appData = new AppDataStore(() => Path.Combine(_tempDir, "app-data"), () => _profiles.ActiveProfileId);
        _scene = new LightingSceneStore(Path.Combine(_tempDir, "scene"));
        _sync = new CloudProfileSyncService(_api, _accounts, _profiles, _store, new StubCoolingProvider(_store), _appData, TimeProvider.System, _scene.Setup);
        _library = new CloudProfileLibraryService(_api, _accounts, _profiles, _store, _appData, _scene.Setup);
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        _api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            Account = new CloudAccountDto { Id = "acct-1", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* already gone */ }
    }

    private static PcSetup Setup(string id)
    {
        var setup = new PcSetup();
        setup.Items.Add(new PcSetupItem { Id = id, Kind = "keyboard", Custom = new PcSetupCustom { Name = "Keys" }, Place = new PcSetupPlace { X = 4f, Z = 5f, Yaw = 6f } });
        return setup;
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> BundleWith(PcSetup setup) => new()
    {
        [PcSetupBackup.AppId] = new() { [PcSetupBackup.Key] = JsonSerializer.SerializeToElement(setup, PersistenceJsonContext.Default.PcSetup) },
    };

    [Fact]
    public async Task A_manual_back_up_carries_the_saved_record_as_the_reserved_app_data_document()
    {
        _scene.Setup.TrySave(0, Setup("kb"), out _);
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        CloudPutProfileRequest? sent = null;
        _api.OnPutProfile = (_, _, _, body) =>
        {
            sent = body;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        await _sync.TriggerNowAsync(null);

        Assert.NotNull(sent);
        var doc = sent!.AppData![PcSetupBackup.AppId][PcSetupBackup.Key];
        Assert.Equal("kb", doc.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.False(doc.TryGetProperty("rev", out _));
        // The app-data store itself never holds the reserved app.
        Assert.DoesNotContain(PcSetupBackup.AppId, _appData.ReadProfile(_profiles.ActiveProfileId).Keys);
    }

    [Fact]
    public async Task A_back_up_without_a_saved_record_adds_nothing()
    {
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        CloudPutProfileRequest? sent = null;
        _api.OnPutProfile = (_, _, _, body) =>
        {
            sent = body;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        await _sync.TriggerNowAsync(null);

        Assert.NotNull(sent);
        Assert.DoesNotContain(PcSetupBackup.AppId, sent!.AppData!.Keys);
    }

    [Fact]
    public void Attach_skips_the_record_when_it_would_push_the_bundle_past_the_cap()
    {
        _scene.Setup.TrySave(0, Setup("kb"), out _);
        var filler = JsonSerializer.SerializeToElement(new string('x', AppDataStore.MaxBundleBytes - 100));
        var appData = new Dictionary<string, Dictionary<string, JsonElement>> { ["com.test.big"] = new() { ["save"] = filler } };

        var result = PcSetupBackup.Attach(appData, _scene.Setup);

        Assert.DoesNotContain(PcSetupBackup.AppId, result.Keys);
    }

    [Fact]
    public async Task A_manual_restore_of_this_machines_backup_applies_the_record_and_strips_it_from_app_data()
    {
        var own = _accounts.ResolveStableInstallId();
        var profileId = _profiles.ActiveProfileId;
        var bundle = BundleWith(Setup("restored"));
        bundle["com.test.app"] = new() { ["save"] = JsonSerializer.SerializeToElement(new { fish = 3 }) };
        _api.OnGetProfile = (_, _, _) => CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
        {
            InstallId = own,
            ProfileId = profileId,
            Name = "Default",
            Revision = 2,
            UpdatedAt = "t",
            Payload = new ProfileExport { Name = "Default", Settings = new NexusSettings() },
            AppData = bundle,
        });

        var result = await _library.ImportAsync(new CloudImportRequest { InstallId = own, ProfileId = profileId, IncludeAppData = true }, CancellationToken.None);

        Assert.True(result.Success);
        var file = _scene.Setup.Load();
        Assert.Equal(["restored"], file.Setup.Items.Select(i => i.Id));
        Assert.Equal(1, file.Rev);
        var stored = _appData.ReadProfile(profileId);
        Assert.DoesNotContain(PcSetupBackup.AppId, stored.Keys);
        Assert.Equal(3, stored["com.test.app"]["save"].GetProperty("fish").GetInt32());
    }

    [Fact]
    public async Task A_restore_with_the_app_data_toggle_off_leaves_the_record_alone()
    {
        _scene.Setup.TrySave(0, Setup("mine"), out _);
        var own = _accounts.ResolveStableInstallId();
        var profileId = _profiles.ActiveProfileId;
        _api.OnGetProfile = (_, _, _) => CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
        {
            InstallId = own,
            ProfileId = profileId,
            Name = "Default",
            Revision = 2,
            UpdatedAt = "t",
            Payload = new ProfileExport { Name = "Default", Settings = new NexusSettings() },
            AppData = BundleWith(Setup("theirs")),
        });

        await _library.ImportAsync(new CloudImportRequest { InstallId = own, ProfileId = profileId, IncludeAppData = false }, CancellationToken.None);

        Assert.Equal(["mine"], _scene.Setup.Load().Setup.Items.Select(i => i.Id));
    }

    [Fact]
    public void Detach_drops_an_invalid_record_but_still_strips_the_reserved_app()
    {
        var bad = Setup("kb");
        bad.Items[0].Place!.X = 1e30f;
        var bundle = BundleWith(bad);
        bundle["com.test.app"] = new() { ["save"] = JsonSerializer.SerializeToElement(1) };

        var rest = PcSetupBackup.Detach(bundle, out var setup);

        Assert.Null(setup);
        Assert.Equal(["com.test.app"], rest!.Keys);
    }

    [Fact]
    public void Detach_returns_a_valid_record()
    {
        var rest = PcSetupBackup.Detach(BundleWith(Setup("ok")), out var setup);

        Assert.Empty(rest!);
        Assert.Equal("ok", Assert.Single(setup!.Items).Id);
    }
}

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
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public PcSetupBackupTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
        _accounts = new CloudAccountService(_api, _store, TimeProvider.System);
        _appData = new AppDataStore(() => Path.Combine(_tempDir, "app-data"), () => _profiles.ActiveProfileId);
        _scene = new LightingSceneStore(Path.Combine(_tempDir, "scene"));
        _sync = new CloudProfileSyncService(_api, _accounts, _profiles, _store, new StubCoolingProvider(_store), _appData, _clock, _scene.Setup);
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

        var result = PcSetupBackup.Attach(appData, _scene.Setup, out var digest);

        Assert.DoesNotContain(PcSetupBackup.AppId, result.Keys);
        Assert.Null(digest);
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
        Assert.Equal("applied", result.PcSetup);
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

    private string SeedSyncedProfile(int cloudRevision = 1)
    {
        var profileId = _profiles.ActiveProfileId;
        var hash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(profileId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord { Revision = cloudRevision, LastSyncedHash = hash });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = _accounts.ResolveStableInstallId(), ProfileId = profileId, Name = "Default", Revision = cloudRevision, UpdatedAt = "t", UpdatedByInstallId = "x" },
        });
        return profileId;
    }

    [Fact]
    public async Task A_setup_only_change_is_uploaded_by_the_next_manual_back_up_and_then_not_again()
    {
        var profileId = SeedSyncedProfile();
        _scene.Setup.TrySave(0, Setup("kb"), out _);
        var puts = 0;
        CloudPutProfileRequest? sent = null;
        _api.OnPutProfile = (_, _, _, body) =>
        {
            puts++;
            sent = body;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 2 });
        };

        await _sync.TriggerNowAsync(profileId);

        Assert.Equal(1, puts);
        Assert.Equal(1, sent!.BaseRevision);
        Assert.Contains(PcSetupBackup.AppId, sent.AppData!.Keys);
        var record = _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId];
        Assert.Equal(PcSetupBackup.Digest(_scene.Setup), record.LastPushedSetupHash);

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = _accounts.ResolveStableInstallId(), ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "t", UpdatedByInstallId = "x" },
        });
        await _sync.TriggerNowAsync(profileId);
        Assert.Equal(1, puts);

        // Moving an item changes the digest, so the next manual Back up uploads again.
        var moved = Setup("kb");
        moved.Items[0].Place!.X = 99f;
        _scene.Setup.TrySave(1, moved, out _);
        await _sync.TriggerNowAsync(profileId);
        Assert.Equal(2, puts);
    }

    [Fact]
    public async Task A_background_pass_never_uploads_a_setup_only_change()
    {
        var profileId = SeedSyncedProfile();
        _scene.Setup.TrySave(0, Setup("kb"), out _);
        var puts = 0;
        _api.OnPutProfile = (_, _, _, _) =>
        {
            puts++;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 2 });
        };

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(0, puts);
        Assert.Equal("", _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId].LastPushedSetupHash);
    }

    [Fact]
    public async Task A_background_push_for_a_profile_change_carries_no_new_setup_and_clears_the_pushed_hash()
    {
        var profileId = SeedSyncedProfile();
        _scene.Setup.TrySave(0, Setup("kb"), out _);
        var pushedDigest = PcSetupBackup.Digest(_scene.Setup)!;
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId].LastPushedSetupHash = pushedDigest);
        // The setup then changes locally, and so does a profile setting.
        var moved = Setup("kb");
        moved.Items[0].Place!.X = 50f;
        _scene.Setup.TrySave(1, moved, out _);
        _store.Update(s => s.Lighting.GlobalBrightness = 0.4f);
        _store.FlushNow();
        CloudPutProfileRequest? sent = null;
        _api.OnPutProfile = (_, _, _, body) =>
        {
            sent = body;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 2 });
        };

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: false);
        Assert.Equal(0, _api.PutProfileCalls);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: false);

        Assert.NotNull(sent);
        Assert.DoesNotContain(PcSetupBackup.AppId, sent!.AppData!.Keys);
        Assert.Equal("", _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId].LastPushedSetupHash);
    }

    [Fact]
    public async Task A_restore_of_another_machines_backup_skips_the_record_and_says_so()
    {
        _scene.Setup.TrySave(0, Setup("mine"), out _);
        _api.OnListDevices = _ => CloudApiResult<List<CloudDeviceDto>>.Ok(new List<CloudDeviceDto>());
        _api.OnGetProfile = (_, _, _) => CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
        {
            InstallId = "install-other",
            ProfileId = "p-remote",
            Name = "Theirs",
            Revision = 2,
            UpdatedAt = "t",
            Payload = new ProfileExport { Name = "Theirs", Settings = new NexusSettings() },
            AppData = BundleWith(Setup("theirs")),
        });

        var result = await _library.ImportAsync(new CloudImportRequest { InstallId = "install-other", ProfileId = "p-remote", IncludeAppData = true }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("skipped_other_machine", result.PcSetup);
        Assert.Equal(["mine"], _scene.Setup.Load().Setup.Items.Select(i => i.Id));
        Assert.DoesNotContain(_profiles.GetManifest().Profiles.SelectMany(p => _appData.ReadProfile(p.Id).Keys), k => k == PcSetupBackup.AppId);
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

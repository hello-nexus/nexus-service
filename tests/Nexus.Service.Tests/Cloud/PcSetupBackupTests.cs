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

    private static PcSetup WithLayout(string id, string layoutJson)
    {
        var setup = Setup(id);
        setup.Resolved.CaseLayout = JsonDocument.Parse(layoutJson).RootElement.Clone();
        return setup;
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static PcSetup WithCatalogItem(string id, string specsJson)
    {
        var setup = Setup(id);
        setup.Items[0].Catalog = new PcSetupCatalog { Category = "monitor", PartId = "m-1", Title = "Screen", Specs = Raw(specsJson) };
        return setup;
    }

    [Fact]
    public void Attach_sends_user_choices_only_without_resolved_or_specs()
    {
        var setup = WithCatalogItem("kb", """{"widthMm":600}""");
        setup.Resolved.CaseLayout = Raw("""{"slots":[1,2,3]}""");
        setup.Resolved.Objects.Add(new SceneObject { Id = "case", Kind = "case", Source = "user", Position = [0f, 0f, 0f] });
        _scene.Setup.TrySave(0, setup, out _);

        var result = PcSetupBackup.Attach(new(), _scene.Setup, out _);

        var doc = result[PcSetupBackup.AppId][PcSetupBackup.Key];
        Assert.Equal(0, doc.GetProperty("resolved").GetProperty("objects").GetArrayLength());
        Assert.False(doc.GetProperty("resolved").TryGetProperty("caseLayout", out var layout) && layout.ValueKind != JsonValueKind.Null);
        var catalog = doc.GetProperty("items")[0].GetProperty("catalog");
        Assert.Equal("m-1", catalog.GetProperty("partId").GetString());
        Assert.False(catalog.TryGetProperty("specs", out var specs) && specs.ValueKind != JsonValueKind.Null);
        Assert.NotNull(_scene.Setup.Load().Setup.Resolved.CaseLayout);
        Assert.NotNull(_scene.Setup.Load().Setup.Items[0].Catalog!.Specs);
    }

    [Fact]
    public void The_digest_ignores_resolved_and_specs_changes_but_not_user_choices()
    {
        _scene.Setup.TrySave(0, WithCatalogItem("kb", """{"widthMm":1}"""), out _);
        var before = PcSetupBackup.Digest(_scene.Setup);

        var changed = WithCatalogItem("kb", """{"widthMm":999}""");
        changed.Resolved.CaseLayout = Raw("""{"slots":[1,2,3,4]}""");
        changed.Resolved.Objects.Add(new SceneObject { Id = "x", Kind = "box", Source = "user", Position = [0f, 0f, 0f] });
        _scene.Setup.TrySave(1, changed, out _);

        Assert.Equal(before, PcSetupBackup.Digest(_scene.Setup));
        _scene.Setup.TrySave(2, WithCatalogItem("other", """{"widthMm":999}"""), out _);
        Assert.NotEqual(before, PcSetupBackup.Digest(_scene.Setup));
    }

    [Fact]
    public void A_restore_keeps_the_local_resolved_block_and_specs_and_ignores_those_in_an_old_backup()
    {
        var local = WithCatalogItem("kb", """{"widthMm":600}""");
        local.Resolved.CaseLayout = Raw("""{"local":true}""");
        local.Resolved.Objects.Add(new SceneObject { Id = "kb", Kind = "keyboard", Source = "user", Position = [1f, 0f, 2f] });
        _scene.Setup.TrySave(0, local, out _);

        var old = WithCatalogItem("kb", """{"widthMm":1}""");
        old.Items.Add(new PcSetupItem { Id = "new", Kind = "mouse", Catalog = new PcSetupCatalog { Category = "mouse", PartId = "p", Title = "T", Specs = Raw("""{"a":1}""") } });
        old.Resolved.CaseLayout = Raw("""{"old":true}""");
        PcSetupBackup.Detach(BundleWith(old), out var fromOldBackup);
        _scene.Setup.Replace(fromOldBackup!);

        var now = _scene.Setup.Load().Setup;
        Assert.Equal("""{"local":true}""", JsonSerializer.Serialize(now.Resolved.CaseLayout!.Value));
        Assert.Equal("kb", Assert.Single(now.Resolved.Objects).Id);
        Assert.Equal("""{"widthMm":600}""", JsonSerializer.Serialize(now.Items[0].Catalog!.Specs!.Value));
        Assert.Null(now.Items[1].Catalog!.Specs);
    }

    [Fact]
    public void A_restore_with_no_local_record_leaves_resolved_empty_and_still_validates()
    {
        var skinny = WithCatalogItem("kb", """{"widthMm":1}""");
        skinny.Items[0].Catalog!.Specs = null;
        Assert.Null(PcSetupValidation.Validate(skinny));

        _scene.Setup.Replace(WithLayout("restored", """{"old":true}"""));

        var now = _scene.Setup.Load().Setup;
        Assert.Null(now.Resolved.CaseLayout);
        Assert.Empty(now.Resolved.Objects);
    }

    [Fact]
    public void A_realistic_full_setup_backs_up_in_under_8_KB()
    {
        var setup = new PcSetup { Case = new PcSetupCase { Place = new PcSetupPlace { X = -300.5f, Z = 40.25f, Yaw = 90f } } };
        var parts = string.Join(",", Enumerable.Range(0, 15).Select(i => $$"""{"slot":"part-{{i}}","partId":"catalog-part-{{i:D4}}","category":"cat{{i}}","note":"user pick {{i}}"}"""));
        setup.Parts = Raw("{\"corrections\":{\"case\":{\"widthMm\":230}},\"more\":[" + parts + "]}");
        var kinds = new[] { "keyboard", "mouse", "mousepad", "monitor", "monitor", "speaker", "headset", "desk" };
        for (var i = 0; i < 8; i++)
        {
            setup.Items.Add(new PcSetupItem
            {
                Id = "item-" + i,
                Kind = kinds[i],
                Owned = true,
                Catalog = new PcSetupCatalog { Category = kinds[i], PartId = "catalog-item-" + i.ToString("D4"), Title = "Some Vendor " + kinds[i] + " Model " + i, Specs = Raw("""{"dimensions":[600,30,350],"screenSize":27,"aspect":"16:9"}""") },
                Custom = new PcSetupCustom { Name = "My " + kinds[i], WidthMm = 440.5f, DepthMm = 140.25f },
                Detected = new PcSetupDetected { Key = "device-key-" + i },
                Place = new PcSetupPlace { X = 10.5f * i, Z = 150.25f, Yaw = 15f },
            });
            setup.Resolved.Objects.Add(new SceneObject { Id = "item-" + i, Kind = "keyboard", Source = "user", Position = [1f, 0f, 2f] });
        }
        var slots = string.Join(",", Enumerable.Range(0, 400).Select(i => "{\"k\":" + i + ",\"p\":[1.5,2.5,3.5]}"));
        setup.Resolved.CaseLayout = Raw("{\"slots\":[" + slots + "]}");
        _scene.Setup.TrySave(0, setup, out _);

        var doc = PcSetupBackup.Attach(new(), _scene.Setup, out _)[PcSetupBackup.AppId][PcSetupBackup.Key];

        var bytes = System.Text.Encoding.UTF8.GetByteCount(doc.GetRawText());
        Assert.InRange(bytes, 1, 8 * 1024 - 1);
        Assert.True(bytes * 4 < PcSetupValidation.Size(_scene.Setup.Load().Setup), $"{bytes} B is not under a quarter of the full record");
    }

    [Fact]
    public async Task A_stored_full_form_hash_still_matches_so_the_background_push_keeps_the_setup_and_rewrites_the_hash()
    {
        var profileId = SeedSyncedProfile();
        var setup = WithCatalogItem("kb", """{"widthMm":600}""");
        setup.Resolved.CaseLayout = Raw("""{"slots":[1]}""");
        _scene.Setup.TrySave(0, setup, out _);
        var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(_scene.Setup.Load().Setup, PersistenceJsonContext.Default.PcSetup))));
        Assert.NotEqual(legacy, PcSetupBackup.Digest(_scene.Setup));
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId].LastPushedSetupHash = legacy);
        _store.Update(s => s.Lighting.GlobalBrightness = 0.4f);
        _store.FlushNow();
        CloudPutProfileRequest? sent = null;
        _api.OnPutProfile = (_, _, _, body) =>
        {
            sent = body;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 2 });
        };

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: false);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: false);

        Assert.NotNull(sent);
        Assert.Contains(PcSetupBackup.AppId, sent!.AppData!.Keys);
        Assert.Equal(PcSetupBackup.Digest(_scene.Setup), _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId].LastPushedSetupHash);
    }

    [Fact]
    public void An_old_backup_with_an_invalid_resolved_block_and_oversized_specs_still_restores()
    {
        var old = WithCatalogItem("kb", """{"widthMm":1}""");
        old.Resolved.Objects.Add(new SceneObject { Id = "bad", Kind = "monitor", Source = "user", Position = [0f, 0f, 0f], Curve = new SceneCurve { RadiusMm = 5f } });
        old.Items[0].Catalog!.Specs = Raw("{\"x\":\"" + new string('a', PcSetupValidation.MaxOpaqueSpecs + 10) + "\"}");
        Assert.NotNull(PcSetupValidation.Validate(old));

        PcSetupBackup.Detach(BundleWith(old), out var setup);

        Assert.NotNull(setup);
        Assert.Empty(setup!.Resolved.Objects);
        Assert.Null(setup.Items[0].Catalog!.Specs);
    }

    [Fact]
    public void A_migrated_items_height_and_curve_radius_move_onto_the_item_in_the_backup_copy()
    {
        var setup = Setup("scr");
        setup.Items[0].Kind = "monitor";
        setup.Items[0].Custom!.SceneKind = "monitor";
        setup.Items[0].Custom!.WidthMm = 600f;
        setup.Resolved.Objects.Add(new SceneObject { Id = "scr", Kind = "monitor", Source = "user", Position = [0f, 0f, 0f], Size = [600f, 380f, 60f], Curve = new SceneCurve { RadiusMm = 1500f } });
        // A Build-made item keeps nothing extra: its object is derived.
        setup.Items.Add(new PcSetupItem { Id = "kb", Kind = "keyboard", Custom = new PcSetupCustom { Name = "Keys" } });
        setup.Resolved.Objects.Add(new SceneObject { Id = "kb", Kind = "keyboard", Source = "build", Position = [0f, 0f, 0f], Size = [440f, 35f, 140f] });
        _scene.Setup.TrySave(0, setup, out _);

        var doc = PcSetupBackup.Attach(new(), _scene.Setup, out _)[PcSetupBackup.AppId][PcSetupBackup.Key];

        var custom = doc.GetProperty("items")[0].GetProperty("custom");
        Assert.Equal(380f, custom.GetProperty("heightMm").GetSingle());
        Assert.Equal(1500f, custom.GetProperty("curveRadiusMm").GetSingle());
        Assert.False(doc.GetProperty("items")[1].GetProperty("custom").TryGetProperty("heightMm", out var h) && h.ValueKind != JsonValueKind.Null);
        // Restoring it keeps them, and they pass validation.
        PcSetupBackup.Detach(BundleWith(JsonSerializer.Deserialize(doc.GetRawText(), PersistenceJsonContext.Default.PcSetup)!), out var restored);
        Assert.Equal(1500f, restored!.Items[0].Custom!.CurveRadiusMm);
    }

    [Fact]
    public void An_old_backup_with_an_out_of_range_radius_restores_with_the_field_absent()
    {
        var old = Setup("scr");
        old.Items[0].Custom!.SceneKind = "monitor";
        old.Resolved.Objects.Add(new SceneObject { Id = "scr", Kind = "monitor", Source = "user", Position = [0f, 0f, 0f], Size = [600f, 380f, 60f], Curve = new SceneCurve { RadiusMm = 5f } });

        PcSetupBackup.Detach(BundleWith(old), out var setup);

        Assert.NotNull(setup);
        Assert.Null(setup!.Items[0].Custom!.CurveRadiusMm);
        Assert.Equal(380f, setup.Items[0].Custom!.HeightMm);
    }


    [Fact]
    public void A_restore_over_the_cap_drops_local_specs_first_keeps_the_mesh_and_validates()
    {
        string Specs() => "{\"x\":\"" + new string('a', PcSetupValidation.MaxOpaqueSpecs - 100) + "\"}";
        PcSetupItem Item(int i, bool specs) => new()
        {
            Id = "i" + i,
            Kind = "keyboard",
            Catalog = new PcSetupCatalog { Category = "c", PartId = "p" + i, Title = "T", Specs = specs ? Raw(Specs()) : null },
        };
        var local = new PcSetup();
        local.Items.AddRange(Enumerable.Range(0, 60).Select(i => Item(i, true)));
        local.Resolved.CaseLayout = Raw("{\"s\":\"" + new string('a', 60 * 1024) + "\"}");
        local.Resolved.Objects.Add(new SceneObject { Id = "i0", Kind = "keyboard", Source = "user", Position = [0f, 0f, 0f] });
        Assert.Null(PcSetupValidation.Validate(local));
        _scene.Setup.TrySave(0, local, out _);
        var incoming = new PcSetup { Parts = Raw("\"" + new string('p', PcSetupValidation.MaxOpaqueParts - 100) + "\"") };
        incoming.Items.AddRange(Enumerable.Range(0, 60).Select(i => Item(i, false)));

        var saved = _scene.Setup.Replace(incoming);

        Assert.Null(PcSetupValidation.Validate(saved.Setup));
        Assert.All(saved.Setup.Items, i => Assert.Null(i.Catalog!.Specs));
        Assert.NotNull(saved.Setup.Resolved.CaseLayout);
        Assert.Single(saved.Setup.Resolved.Objects);
    }
}

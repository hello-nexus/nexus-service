using Nexus.Service.Cloud;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

/// <summary>Real ProfileManager + JsonConfigStore against a temp dir (matching Integration/ProfileSwitchTests.cs), a FakeCloudApiClient standing in for api.hellonexus.com, and CloudProfileSyncService's internal methods invoked directly (bypassing the BackgroundService loop, which only matters for scheduling, not the sync logic itself).</summary>
public sealed class CloudProfileSyncServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;
    private readonly FakeCloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly StubCoolingProvider _fans;
    private readonly CloudProfileSyncService _sync;
    private readonly ManualTimeProvider _clock;

    public CloudProfileSyncServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-cloud-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();

        _api = new FakeCloudApiClient();
        _fans = new StubCoolingProvider(_store);
        _clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _accounts = new CloudAccountService(_api, _store, _clock);
        _sync = new CloudProfileSyncService(_api, _accounts, _profiles, _store, _fans, _clock);
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>This machine's install id. A sync pass only ever considers rows carrying it.</summary>
    private string OwnId => _accounts.ResolveStableInstallId();

    private void SeedAccount(string accountId, string refreshToken)
    {
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = accountId, RefreshToken = refreshToken });
            s.Auth.ActiveCloudAccountId = accountId;
        });
        _api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-" + accountId,
            RefreshToken = refreshToken,
            Account = new CloudAccountDto { Id = accountId, Email = "x@example.com", Username = "x", EmailVerified = true },
        });
    }

    // ── conflict resolution ──────────────────────────────────────────────

    [Fact]
    public async Task RunSyncPass_detects_a_conflict_when_local_is_dirty_and_cloud_moved_past_the_synced_base()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;

        var baseExport = _profiles.ExportProfileForSync(profileId)!;
        var baseHash = CloudProfileSyncService.HashPayload(baseExport);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord
        { Revision = 1, LastSyncedHash = baseHash, LastSyncedAt = "2026-01-01T00:00:00Z" });

        // Local edit after the last sync - now dirty relative to baseHash.
        _store.Update(s => s.Lighting.GlobalBrightness = 0.2f);
        _store.FlushNow();

        // Cloud also moved past revision 1 in the meantime.
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "2026-01-02T00:00:00Z", UpdatedByInstallId = "other-machine" },
        });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        var status = _sync.GetStatus();
        Assert.Equal("dirty", status.State);
        var conflict = Assert.Single(status.Conflicts);
        Assert.Equal(profileId, conflict.ProfileId);
        Assert.Equal(2, conflict.CloudRevision);
        Assert.Equal("other-machine", conflict.UpdatedByInstallId);

        // Neither side was clobbered - no push, no pull happened automatically.
        Assert.Equal(0, _api.PutProfileCalls);
        Assert.Equal(0, _api.GetProfileCalls);
    }

    [Fact]
    public async Task ResolveConflict_choice_local_pushes_using_the_conflicting_cloud_revision_as_base()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;
        var baseHash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(profileId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = baseHash });
        _store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        _store.FlushNow();
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "t", UpdatedByInstallId = "other" },
        });
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Single(_sync.GetStatus().Conflicts);

        int? capturedBaseRevision = null;
        _api.OnPutProfile = (_, _, _, body) =>
        {
            capturedBaseRevision = body.BaseRevision;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 3 });
        };

        var result = await _sync.ResolveConflictAsync(profileId, "local", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, capturedBaseRevision); // the CURRENT (conflicting) cloud revision, not the stale synced one.
        Assert.Empty(_sync.GetStatus().Conflicts);
        Assert.Equal(3, _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId].Revision);
    }

    [Fact]
    public async Task ResolveConflict_choice_cloud_overwrites_local_with_the_cloud_payload()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;
        var baseHash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(profileId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = baseHash });
        _store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        _store.FlushNow();
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "t", UpdatedByInstallId = "other" },
        });
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Single(_sync.GetStatus().Conflicts);

        var cloudSettings = new NexusSettings();
        cloudSettings.Lighting.GlobalBrightness = 0.9f;
        _api.OnGetProfile = (_, _, _) => CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
        {
            ProfileId = profileId,
            Name = "Default",
            Revision = 2,
            Payload = new ProfileExport { Name = "Default", Settings = cloudSettings },
        });

        var result = await _sync.ResolveConflictAsync(profileId, "cloud", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(_sync.GetStatus().Conflicts);
        Assert.Equal(0.9f, _store.Load().Lighting.GlobalBrightness);
        Assert.Equal(2, _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId].Revision);
    }

    [Fact]
    public async Task ResolveConflict_invalid_choice_is_rejected()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;
        var baseHash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(profileId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = baseHash });
        _store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "t", UpdatedByInstallId = "other" },
        });
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        var result = await _sync.ResolveConflictAsync(profileId, "sideways", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("invalid_choice", result.ErrorCode);
    }

    // ── delete propagation ───────────────────────────────────────────────

    [Fact]
    public async Task RunSyncPass_keeps_the_backup_when_the_local_profile_was_deleted()
    {
        SeedAccount("acct-1", "refresh-1");
        var defaultId = _profiles.GetActiveEntry()!.Id;
        var deletedId = _profiles.CreateProfile("ToDelete").Id;
        _profiles.SwitchProfile(defaultId);

        var hash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(deletedId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[deletedId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = hash });
        _profiles.DeleteProfile(deletedId); // local delete - a second, still-present profile exists.

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = deletedId, Name = "ToDelete", Revision = 1 },
        });
        string? deletedProfileId = null;
        _api.OnDeleteProfile = (_, _, profileId) => { deletedProfileId = profileId; return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance); };

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: true);

        // Deleting locally must not destroy the backup: the user restores from
        // it via import, or removes it deliberately from the cloud list.
        Assert.Null(deletedProfileId);
        Assert.Equal(0, _api.DeleteProfileCalls);
    }

    [Fact]
    public async Task RunSyncPass_deletes_local_when_cloud_was_deleted_on_another_machine()
    {
        SeedAccount("acct-1", "refresh-1");
        var defaultId = _profiles.GetActiveEntry()!.Id;
        var otherId = _profiles.CreateProfile("Other").Id;
        _profiles.SwitchProfile(defaultId);

        var hash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(otherId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[otherId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = hash });

        // Cloud list no longer carries "Other" (deleted from another machine).
        // "Default" has no sync record, so it decides Push, not DeleteLocal -
        // wired to fail silently so it does not interfere with the assertions.
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, _, _) => CloudApiResult<CloudPutProfileResult>.NetworkError("not wired");

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.DoesNotContain(_profiles.GetManifest().Profiles, p => p.Id == otherId);
        Assert.DoesNotContain(otherId, _store.Load().Auth!.CloudAccounts[0].ProfileSync.Keys);
        // Default (never synced, cloud-missing) is untouched by the delete path.
        Assert.Contains(_profiles.GetManifest().Profiles, p => p.Id == defaultId);
    }

    [Fact]
    public async Task RunSyncPass_repushes_instead_of_deleting_the_only_local_profile()
    {
        SeedAccount("acct-1", "refresh-1");
        var onlyId = _profiles.GetActiveEntry()!.Id;
        var hash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(onlyId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[onlyId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = hash });

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, _, _) => CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 2 });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Single(_profiles.GetManifest().Profiles);
        Assert.Equal(onlyId, _profiles.GetManifest().Profiles[0].Id);
        Assert.Equal(1, _api.PutProfileCalls);
        Assert.Equal(2, _store.Load().Auth!.CloudAccounts[0].ProfileSync[onlyId].Revision);
    }

    [Fact]
    public async Task TriggerNow_with_a_profile_id_backs_up_only_that_profile()
    {
        SeedAccount("acct-1", "refresh-1");
        var defaultId = _profiles.GetActiveEntry()!.Id;
        var secondId = _profiles.CreateProfile("Second").Id;

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        var pushed = new List<string>();
        _api.OnPutProfile = (_, _, profileId, _) =>
        {
            pushed.Add(profileId);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        await _sync.TriggerNowAsync(secondId);

        Assert.Equal(new[] { secondId }, pushed);
        var syncMap = _store.Load().Auth!.CloudAccounts[0].ProfileSync;
        Assert.Contains(secondId, syncMap.Keys);
        Assert.DoesNotContain(defaultId, syncMap.Keys);
    }

    // ── sync status profiles ─────────────────────────────────────────────

    [Fact]
    public void GetStatus_profiles_joins_local_manifest_with_sync_records_and_sorts_by_name()
    {
        SeedAccount("acct-1", "refresh-1");
        var defaultId = _profiles.GetActiveEntry()!.Id; // "Default"
        var zebraId = _profiles.CreateProfile("Zebra").Id;
        var appleId = _profiles.CreateProfile("apple").Id;
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[appleId] = new CloudProfileSyncRecord
        { Revision = 3, LastSyncedAt = "2026-01-01T00:00:00Z", LastSyncedHash = "h" });

        var profiles = _sync.GetStatus().Profiles;

        Assert.Equal(3, profiles.Count);
        Assert.Equal(new[] { "apple", "Default", "Zebra" }, profiles.Select(p => p.Name)); // OrdinalIgnoreCase: apple < Default < Zebra.

        var appleDto = profiles.Single(p => p.ProfileId == appleId);
        Assert.Equal(3, appleDto.Revision);
        Assert.Equal("2026-01-01T00:00:00Z", appleDto.LastSyncedAt);

        var zebraDto = profiles.Single(p => p.ProfileId == zebraId);
        Assert.Equal(0, zebraDto.Revision);
        Assert.Equal("", zebraDto.LastSyncedAt);

        var defaultDto = profiles.Single(p => p.ProfileId == defaultId);
        Assert.Equal(0, defaultDto.Revision);
        Assert.Equal("", defaultDto.LastSyncedAt);
    }

    [Fact]
    public void GetStatus_profiles_empty_when_logged_out()
    {
        Assert.Null(_accounts.ActiveAccountId);

        Assert.Empty(_sync.GetStatus().Profiles);
    }

    // ── account switch (archive + wholesale replace) ────────────────────

    [Fact]
    public async Task HandleSwitch_pushes_outgoing_pending_changes_archives_then_replaces_with_the_incoming_library()
    {
        SeedAccount("from-acct", "refresh-from");
        var oldDefaultId = _profiles.GetActiveEntry()!.Id;
        _profiles.CreateProfile("Work"); // 2 local-only profiles now under "from-acct", never synced.
        _profiles.SwitchProfile(oldDefaultId);

        // Seed the incoming ("to") account too so WithAuthAsync can mint a token for it.
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        var refreshTokenToAccount = new Dictionary<string, string> { ["refresh-from"] = "from-acct", ["refresh-to"] = "to-acct" };
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-for-" + refreshTokenToAccount[rt],
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = refreshTokenToAccount[rt], Email = "x@example.com", Username = "x", EmailVerified = true },
        });

        var pushedProfileIds = new List<string>();
        _api.OnPutProfile = (token, _, profileId, _) =>
        {
            Assert.Equal("access-for-from-acct", token); // the outgoing flush must authenticate as the OUTGOING account.
            pushedProfileIds.Add(profileId);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        var incomingSettings = new NexusSettings();
        incomingSettings.Lighting.GlobalBrightness = 0.42f;
        _api.OnListProfiles = token =>
        {
            Assert.Equal("access-for-to-acct", token);
            return CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
            {
                new() { InstallId = OwnId, ProfileId = "cloud-profile-1", Name = "Incoming", Revision = 5 },
            });
        };
        _api.OnGetProfile = (token, _, profileId) =>
        {
            Assert.Equal("access-for-to-acct", token);
            Assert.Equal("cloud-profile-1", profileId);
            return CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
            {
                ProfileId = profileId,
                Name = "Incoming",
                Revision = 5,
                Payload = new ProfileExport { Name = "Incoming", Settings = incomingSettings },
            });
        };

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        // Outgoing account: both local-only profiles were pushed before the swap.
        Assert.Equal(2, pushedProfileIds.Count);

        // Archive exists and holds the outgoing library.
        var archiveRoot = Path.Combine(_tempDir, "profiles-archive");
        Assert.True(Directory.Exists(archiveRoot));
        var archiveDirs = Directory.GetDirectories(archiveRoot);
        var archived = Assert.Single(archiveDirs);
        Assert.Equal(2, Directory.GetFiles(archived, "profile-*.json").Length);

        // Local library now mirrors the incoming account exactly.
        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("cloud-profile-1", manifest.Profiles[0].Id);
        Assert.Equal("cloud-profile-1", manifest.ActiveProfileId);
        Assert.Equal(0.42f, _store.Load().Lighting.GlobalBrightness);

        var toRecord = _store.Load().Auth!.CloudAccounts.Single(a => a.AccountId == "to-acct");
        Assert.Equal(5, toRecord.ProfileSync["cloud-profile-1"].Revision);

        // A queued duplicate (the OnAccountActivated pass completed the switch
        // first via its redirect) must be a no-op: re-running would flush the
        // INCOMING library to the OUTGOING account's cloud.
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);
        Assert.Equal(2, pushedProfileIds.Count);
        Assert.Single(Directory.GetDirectories(archiveRoot));
    }

    [Fact]
    public async Task HandleSwitch_with_an_empty_incoming_cloud_library_falls_back_to_a_default_profile()
    {
        SeedAccount("from-acct", "refresh-from");
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        _api.OnPutProfile = (_, _, _, _) => CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("Default", manifest.Profiles[0].Name);
    }

    [Fact]
    public async Task HandleSwitch_aborts_without_touching_the_local_library_when_the_incoming_list_call_fails()
    {
        SeedAccount("from-acct", "refresh-from");
        var originalProfileId = _profiles.GetActiveEntry()!.Id;
        _profiles.CreateProfile("Work");
        _profiles.SwitchProfile(originalProfileId);
        var originalCount = _profiles.GetManifest().Profiles.Count;

        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        // The incoming account's profile list is unreachable - must NOT be
        // treated as "this account has zero cloud profiles".
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.NetworkError("dns failure");

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        Assert.Equal("offline", _sync.GetStatus().State);
        Assert.Equal(originalCount, _profiles.GetManifest().Profiles.Count);
        Assert.Contains(_profiles.GetManifest().Profiles, p => p.Id == originalProfileId);
        // No archive was created - ArchiveLibrary is only called after the
        // incoming library is fully and successfully retrieved.
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "profiles-archive")));
    }

    [Fact]
    public async Task HandleSwitch_aborts_without_touching_the_local_library_when_a_single_profile_fetch_fails()
    {
        SeedAccount("from-acct", "refresh-from");
        var originalCount = _profiles.GetManifest().Profiles.Count;

        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = "cloud-1", Name = "One", Revision = 1 },
            new() { InstallId = OwnId, ProfileId = "cloud-2", Name = "Two", Revision = 1 },
        });
        // First profile fetches fine, second fails - a partial pull must not
        // silently drop the second profile from the resulting library.
        _api.OnGetProfile = (_, _, profileId) => profileId == "cloud-1"
            ? CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
            {
                ProfileId = "cloud-1", Name = "One", Revision = 1,
                Payload = new ProfileExport { Name = "One", Settings = new NexusSettings() },
            })
            : CloudApiResult<CloudProfileDto>.NetworkError("timeout");

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        Assert.Equal("offline", _sync.GetStatus().State);
        Assert.Equal(originalCount, _profiles.GetManifest().Profiles.Count);
    }

    [Fact]
    public async Task RunSyncPass_retries_a_stalled_switch_instead_of_an_incremental_pass()
    {
        SeedAccount("from-acct", "refresh-from");
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _store.Update(s => s.Auth!.ActiveCloudAccountId = "to-acct"); // the switch already flipped the active pointer
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.NetworkError("dns failure");
        var pushTokens = new List<string>();
        _api.OnPutProfile = (token, _, _, _) =>
        {
            pushTokens.Add(token);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        // First attempt fails and leaves a pending switch.
        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);
        Assert.Equal("offline", _sync.GetStatus().State);

        // The list call now succeeds - a plain RunSyncPassAsync tick for the
        // (already active) "to-acct" must redo the wholesale switch, not an
        // incremental pass that would upload "from-acct"'s local library
        // under "to-acct"'s identity.
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        await _sync.RunSyncPassAsync("to-acct", CancellationToken.None);

        Assert.Equal("idle", _sync.GetStatus().State);
        // FlushAccountAsync legitimately pushes from-acct's never-synced local
        // profile as part of the switch's outgoing flush - the point being
        // tested is that every such push authenticates as from-acct, never as
        // to-acct (which would mean the redirect ran an incremental pass
        // against to-acct's identity instead of retrying the wholesale switch).
        Assert.All(pushTokens, t => Assert.Equal("tok-refresh-from", t));
        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("Default", manifest.Profiles[0].Name);
    }

    // ── first login (a backup never replaces the local library) ────────

    [Fact]
    public async Task RunSyncPass_on_a_pristine_machine_does_not_adopt_the_cloud_library()
    {
        SeedAccount("acct-1", "refresh-1");
        var bootstrapId = _profiles.GetActiveEntry()!.Id;

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = "cloud-default", Name = "Default", Revision = 4 },
        });
        _api.OnGetProfile = (_, _, _) => throw new InvalidOperationException("a backup must never fetch a cloud profile");
        _api.OnPutProfile = (_, _, _, _) => CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: true);

        // Pressing "Back up now" on a fresh machine used to swap its whole
        // library for the cloud's. Adopting another library is the import flow.
        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal(bootstrapId, manifest.Profiles[0].Id);
    }

    [Fact]
    public async Task RunSyncPass_uploads_the_local_profile_and_never_pulls_the_cloud_one()
    {
        SeedAccount("acct-1", "refresh-1");
        var localId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s => s.Lighting.GlobalBrightness = 0.5f); // real user edit before first login.
        _store.FlushNow();

        var cloudSettings = new NexusSettings();
        cloudSettings.Lighting.GlobalBrightness = 0.9f;
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = OwnId, ProfileId = "cloud-default", Name = "Default", Revision = 3 },
        });
        _api.OnGetProfile = (_, _, profileId) => CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
        {
            ProfileId = profileId,
            Name = "Default",
            Revision = 3,
            Payload = new ProfileExport { Name = "Default", Settings = cloudSettings },
        });
        var putCallsForLocalId = 0;
        _api.OnPutProfile = (_, _, profileId, _) =>
        {
            Assert.Equal(localId, profileId);
            putCallsForLocalId++;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: true);

        // The local profile is backed up; the cloud-only one is NOT pulled
        // down. Backing up must never import, and a machine's library only
        // grows when the user imports on purpose.
        Assert.Equal(1, putCallsForLocalId);
        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Contains(manifest.Profiles, p => p.Id == localId);
        Assert.DoesNotContain(manifest.Profiles, p => p.Id == "cloud-default");
    }

    [Fact]
    public async Task RunSyncPass_first_login_with_an_empty_cloud_library_still_uploads_the_pristine_default()
    {
        SeedAccount("acct-1", "refresh-1");
        var bootstrapId = _profiles.GetActiveEntry()!.Id;

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, profileId, _) =>
        {
            Assert.Equal(bootstrapId, profileId);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        // The replace fast path requires a non-empty cloud library - an empty
        // one takes the ordinary Push branch (first tick only seeds the
        // debounce window, matching RunSyncPass_preserves_dirty_tracking...).
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(0, _api.PutProfileCalls);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Single(_profiles.GetManifest().Profiles);
        Assert.Equal(bootstrapId, _profiles.GetManifest().Profiles[0].Id);
        Assert.Equal(1, _api.PutProfileCalls);
    }

    [Fact]
    public async Task RunSyncPass_preserves_dirty_tracking_across_a_failed_push_so_the_next_tick_retries()
    {
        SeedAccount("acct-1", "refresh-1");
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, _, _) => CloudApiResult<CloudPutProfileResult>.NetworkError("dns failure");

        // Local-only profile, no sync record yet -> Push decision. The first
        // pass only SEEDS dirtySince (a fresh debounce window starts counting
        // from "first noticed dirty", not from any earlier clock state) - no
        // push is attempted yet.
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(0, _api.PutProfileCalls);

        // Debounce window elapses - the push is attempted and fails.
        _clock.Advance(TimeSpan.FromSeconds(61));
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(1, _api.PutProfileCalls);

        // Same instant (no further clock advance) - a fix that reset the
        // dirty-since timestamp on a failed push would make this attempt wait
        // another full 60s; it must retry immediately instead.
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(2, _api.PutProfileCalls);
    }

    [Fact]
    public async Task RunGuardedAsync_swallows_a_timeout_shaped_cancellation_instead_of_faulting_the_service_loop()
    {
        // Simulates HttpClient.Timeout: a TaskCanceledException whose own
        // CancellationToken is not the caller's ct, so ct.IsCancellationRequested
        // stays false. A filter that rethrows on any OperationCanceledException
        // regardless of ct would let this escape RunGuardedAsync and fault the
        // BackgroundService's execute task.
        Task Action() => throw new TaskCanceledException("simulated timeout", null, CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => _sync.RunGuardedAsync(Action, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunGuardedAsync_rethrows_when_the_callers_own_ct_was_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Never reached: the gate wait itself (_syncGate.WaitAsync(ct)) is the
        // first thing RunGuardedAsync does, and throws on the already-cancelled
        // token before action() is ever invoked - proving the propagation
        // happens at the earliest possible point, not just inside the action.
        Task Action() => throw new InvalidOperationException("should not run");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sync.RunGuardedAsync(Action, cts.Token));
    }

    [Fact]
    public void AnnounceSwitch_sets_pending_fields_immediately()
    {
        Assert.Equal((null, null), _sync.PendingSwitch);

        _sync.AnnounceSwitch("from-acct", "to-acct");

        Assert.Equal(("from-acct", "to-acct"), _sync.PendingSwitch);
    }

    [Fact]
    public async Task Interleaved_sync_pass_between_switch_announce_and_switch_handle_redirects_instead_of_running_incrementally()
    {
        SeedAccount("from-acct", "refresh-from");
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        // CloudAccountService flips ActiveCloudAccountId to the incoming
        // account before firing OnAccountSwitching - a racing RunSyncPassAsync
        // for "to-acct" would otherwise bail out immediately on its own
        // active-account guard, defeating the point of this test.
        _store.Update(s => s.Auth!.ActiveCloudAccountId = "to-acct");
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        var pushTokens = new List<string>();
        _api.OnPutProfile = (token, _, _, _) =>
        {
            pushTokens.Add(token);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());

        // AnnounceSwitch is exactly what the real OnAccountSwitching handler
        // runs synchronously before its Task.Run(HandleSwitchAsync) dispatch -
        // this is the earliest possible moment a second, racing
        // RunSyncPassAsync call (the sibling OnAccountActivated dispatch, or a
        // concurrent 15s tick) could interleave, before any background work
        // has run at all.
        _sync.AnnounceSwitch("from-acct", "to-acct");

        await _sync.RunSyncPassAsync("to-acct", CancellationToken.None);

        // Redirected into the wholesale switch (which pushes from-acct's
        // pending local profile as its outgoing flush) instead of an
        // incremental pass, which would read "no sync record for to-acct" and
        // upload the still-outgoing local library as new profiles under
        // to-acct's identity - the cross-account leakage this fix prevents.
        Assert.NotEmpty(pushTokens);
        Assert.All(pushTokens, t => Assert.Equal("tok-refresh-from", t));
        // The switch actually completed (not just redirected-and-stuck).
        Assert.Equal((null, null), _sync.PendingSwitch);
    }

    // ── per-machine isolation ────────────────────────────────────────────

    [Fact]
    public async Task Sync_pass_ignores_another_machines_rows()
    {
        SeedAccount("acct-1", "refresh-1");
        var localCount = _profiles.GetManifest().Profiles.Count;
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = "some-other-machine", ProfileId = "their-profile", Name = "Default", Revision = 9, UpdatedAt = "t" },
        });
        _api.OnGetProfile = (_, _, _) => throw new InvalidOperationException("must not fetch another machine's profile");

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        // No pull, no local library growth, and nothing to rename - which is
        // exactly what stopped the "Default (2) (2)" duplication loop.
        Assert.Equal(0, _api.GetProfileCalls);
        Assert.Equal(localCount, _profiles.GetManifest().Profiles.Count);
        Assert.Empty(_sync.GetStatus().Conflicts);
    }

    [Fact]
    public async Task Sync_pass_addresses_its_own_install_id_when_pushing()
    {
        SeedAccount("acct-1", "refresh-1");
        string? pushedInstallId = null;
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, installId, _, _) =>
        {
            pushedInstallId = installId;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        // First pass only starts the debounce window; the push lands on the
        // pass after it elapses.
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(90));
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(OwnId, pushedInstallId);
    }

    [Fact]
    public async Task A_manual_pass_pushes_without_waiting_out_the_debounce()
    {
        SeedAccount("acct-1", "refresh-1");
        var pushes = 0;
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, _, _) =>
        {
            pushes++;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        // Background passes no longer run, so the debounce would otherwise mean
        // "Back up now" did nothing until a minute after the last edit.
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(0, pushes);

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None, manual: true);
        Assert.True(pushes > 0);
    }
}

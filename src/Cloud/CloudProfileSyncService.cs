using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Cloud;

/// <summary>
/// Drives profile sync for the active cloud account: periodic push/pull
/// against the per-profile revision matrix (<see cref="CloudSyncDecision"/>),
/// account-switch archive+replace, and shutdown flush. Reacts to
/// <see cref="CloudAccountService"/> events rather than being called
/// directly - login/activate/logout routes return immediately and this
/// service does the (potentially slow) network work in the background.
///
/// <see cref="_syncGate"/> serializes a regular sync pass against an
/// account-switch replace: both can be triggered back-to-back (a switch
/// fires OnAccountSwitching then OnAccountActivated), and running them
/// concurrently could push the outgoing library under the incoming
/// account's name mid-swap.
/// </summary>
public sealed class CloudProfileSyncService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PushDebounce = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownFlushBudget = TimeSpan.FromSeconds(5);

    private readonly ICloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly ProfileManager _profiles;
    private readonly IConfigStore _store;
    private readonly IFanControlProvider _fans;
    private readonly TimeProvider _clock;

    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dirtySince = new();
    private readonly ConcurrentDictionary<string, CloudSyncConflictDto> _conflicts = new();
    private readonly HashSet<string> _overCapWarned = new();
    private volatile string _state = "idle";
    private volatile string _lastSyncAt = "";
    private volatile string? _syncedAccountId;

    public CloudProfileSyncService(ICloudApiClient api, CloudAccountService accounts, ProfileManager profiles, IConfigStore store, IFanControlProvider fans)
        : this(api, accounts, profiles, store, fans, TimeProvider.System)
    {
    }

    internal CloudProfileSyncService(ICloudApiClient api, CloudAccountService accounts, ProfileManager profiles, IConfigStore store, IFanControlProvider fans, TimeProvider clock)
    {
        _api = api;
        _accounts = accounts;
        _profiles = profiles;
        _store = store;
        _fans = fans;
        _clock = clock;
    }

    public CloudSyncStatusResponse GetStatus() => new()
    {
        State = _state,
        LastSyncAt = _lastSyncAt,
        Conflicts = _conflicts.Values.ToList(),
        Profiles = BuildProfileStatuses(),
    };

    /// <summary>Local manifest entries for the active account joined with that account's per-profile ProfileSync records. A profile never synced yet reports the unsynced sentinel defaults. Empty when logged out.</summary>
    private List<CloudSyncProfileDto> BuildProfileStatuses()
    {
        if (_accounts.ActiveAccountId is not { } accountId)
        {
            return new List<CloudSyncProfileDto>();
        }

        var syncMap = _store.Load().Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId)?.ProfileSync;
        var localProfiles = _profiles.GetManifest().Profiles;
        var result = new List<CloudSyncProfileDto>(localProfiles.Count);
        foreach (var entry in localProfiles)
        {
            CloudProfileSyncRecord? record = null;
            syncMap?.TryGetValue(entry.Id, out record);
            result.Add(new CloudSyncProfileDto
            {
                ProfileId = entry.Id,
                Name = entry.Name,
                LastSyncedAt = record?.LastSyncedAt ?? "",
                Revision = record?.Revision ?? 0,
            });
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Manual "sync now" trigger for POST /cloud/sync/now. No-op when logged out.</summary>
    /// <summary>The user pressed "Back up now": push straight away rather than waiting out the debounce, which exists only to batch background passes that no longer run.</summary>
    public void TriggerNow()
    {
        if (_accounts.ActiveAccountId is { } accountId)
        {
            _ = RunGuardedAsync(() => RunSyncPassAsync(accountId, CancellationToken.None, manual: true), CancellationToken.None);
        }
    }

    public async Task<CloudActionResult> ResolveConflictAsync(string profileId, string choice, CancellationToken ct)
    {
        var accountId = _accounts.ActiveAccountId;
        if (accountId is null)
        {
            return CloudActionResult.Fail("no_session", "Not logged in.", 401);
        }
        if (!_conflicts.TryGetValue(profileId, out var conflict))
        {
            return CloudActionResult.Fail("not_found", "No conflict for this profile.", 404);
        }

        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(choice, "local", StringComparison.OrdinalIgnoreCase))
            {
                return await ResolveKeepLocalAsync(accountId, profileId, conflict, ct).ConfigureAwait(false);
            }
            if (string.Equals(choice, "cloud", StringComparison.OrdinalIgnoreCase))
            {
                await PullAsync(accountId, profileId, ct).ConfigureAwait(false);
                _conflicts.TryRemove(profileId, out _);
                _dirtySince.TryRemove(profileId, out _);
                return CloudActionResult.Ok();
            }
            return CloudActionResult.Fail("invalid_choice", "choice must be 'local' or 'cloud'.", 400);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>Narrows a whole-account profile listing to the rows this machine owns.</summary>
    private List<CloudProfileSummaryDto> OwnRows(List<CloudProfileSummaryDto>? rows)
    {
        if (rows is null)
        {
            return new List<CloudProfileSummaryDto>();
        }
        var own = OwnInstallId();
        return rows.Where(r => string.Equals(r.InstallId, own, StringComparison.Ordinal)).ToList();
    }

    /// <summary>This machine's id. Every row this service reads or writes is its own; another machine's rows are reachable only through the explicit import flow.</summary>
    private string OwnInstallId() => _accounts.ResolveStableInstallId();

    private async Task<CloudActionResult> ResolveKeepLocalAsync(string accountId, string profileId, CloudSyncConflictDto conflict, CancellationToken ct)
    {
        var localExport = _profiles.ExportProfileForSync(profileId);
        if (localExport is null)
        {
            return CloudActionResult.Fail("not_found", "Local profile not found.", 404);
        }
        var hash = HashPayload(localExport);
        var request = new CloudPutProfileRequest
        {
            Name = conflict.Name,
            BaseRevision = conflict.CloudRevision,
            Payload = localExport,
        };
        var result = await _accounts.WithAuthAsync(accountId, token => _api.PutProfileAsync(token, OwnInstallId(), profileId, request, ct), ct).ConfigureAwait(false);

        if (result.StatusCode == 409 && result.Value is { CurrentRevision: not null })
        {
            RecordConflict(profileId, conflict.Name, conflict.LocalUpdatedAt, result.Value.CurrentRevision.Value,
                result.Value.UpdatedAt ?? "", result.Value.Name ?? "", result.Value.UpdatedByInstallId ?? "");
            return CloudActionResult.Fail("conflict", "The cloud copy moved again - resolve once more.", 409);
        }
        if (!result.Success || result.Value?.Revision is not { } revision)
        {
            return CloudActionResult.FromError(result);
        }

        SetSyncRecord(accountId, profileId, revision, hash);
        _conflicts.TryRemove(profileId, out _);
        _dirtySince.TryRemove(profileId, out _);
        return CloudActionResult.Ok();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Task.Run hands the event off to the thread pool immediately - the
        // firing HTTP route (login/activate) must never run any part of the
        // sync pass synchronously on its own thread, even the in-memory part
        // before the first real network await.
        // Signing in no longer uploads anything; it only clears state from the
        // previous account. Backing up is an explicit action.
        _accounts.OnAccountSwitching += (from, to) =>
        {
            // AnnounceSwitch runs BEFORE the Task.Run dispatch below,
            // synchronously, on the same call stack that fires
            // OnAccountSwitching. OnAccountActivated fires immediately after
            // this handler returns and dispatches its own
            // RunGuardedAsync(RunSyncPassAsync) for the same (now-active)
            // account - if that task wins the _syncGate race before
            // HandleSwitchAsync's task even starts, RunSyncPassAsync's
            // pending-switch check at its top must already see the announced
            // switch, or it runs an incremental pass against the still-
            // outgoing local library and pushes it under the incoming
            // account's identity.
            AnnounceSwitch(from, to);
            _ = Task.Run(() => RunGuardedAsync(() => HandleSwitchAsync(from, to, CancellationToken.None), CancellationToken.None));
        };
        _accounts.OnAccountLoggedOut += ClearLocalState;

        // No periodic pass and no pass at boot: profiles reach the cloud only
        // when the user asks (TriggerNow), so nothing is uploaded behind their
        // back. The timer still turns so the service has a cancellation-aware
        // idle loop for the hosted-service lifetime.
        using var timer = new PeriodicTimer(TickInterval, _clock);
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_accounts.ActiveAccountId is { } accountId)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ShutdownFlushBudget);
            try
            {
                await FlushAccountAsync(accountId, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[cloud-sync] exit flush failed: {ex.Message}");
            }
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Blocking best-effort flush for the Windows service fast-shutdown path (Program.cs FastServiceShutdown), which has no hosted-service StopAsync lifecycle. Bounded by <paramref name="budget"/>; skips the sync gate since the process is exiting either way.</summary>
    public void FlushPendingSyncBlocking(TimeSpan budget)
    {
        if (_accounts.ActiveAccountId is not { } accountId)
        {
            return;
        }
        using var cts = new CancellationTokenSource(budget);
        try
        {
            FlushAccountAsync(accountId, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // cts has no external caller token - any OperationCanceledException
            // here is always the internal budget timeout, never an outside
            // cancellation, so it is always caught, never rethrown.
            Console.Error.WriteLine($"[cloud-sync] shutdown flush failed: {ex.Message}");
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal async Task RunGuardedAsync(Func<Task> action, CancellationToken ct)
    {
        try
        {
            await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[cloud-sync] gate wait failed: {ex.Message}");
            return;
        }
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[cloud-sync] background task failed: {ex.GetType().Name}: {ex.Message}");
            _state = "error";
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private void ClearLocalState(string accountId)
    {
        if (accountId == _pendingSwitch?.To)
        {
            // The account a stalled switch was retrying into was just logged
            // out - forget the pending retry so RunSyncPassAsync stops trying
            // to switch into an account that no longer has a stored session.
            _pendingSwitch = null;
        }
        if (accountId != _syncedAccountId)
        {
            return;
        }
        _dirtySince.Clear();
        _conflicts.Clear();
        lock (_overCapWarned)
        {
            _overCapWarned.Clear();
        }
        _state = "idle";
        _lastSyncAt = "";
        _syncedAccountId = null;
    }

    // ── regular sync pass ────────────────────────────────────────────────

    internal async Task RunSyncPassAsync(string accountId, CancellationToken ct, bool manual = false)
    {
        if (accountId != _accounts.ActiveAccountId)
        {
            return;
        }

        // A prior HandleSwitchAsync for this account never completed (offline/
        // error abort) - the local library still belongs to the outgoing
        // account (pending.From). Retry the wholesale switch instead of
        // running an incremental pass, which would misread "no sync record
        // yet" as "push these as new profiles" and upload the outgoing
        // account's library under this one.
        if (_pendingSwitch is { } pending && pending.To == accountId)
        {
            await HandleSwitchAsync(pending.From, accountId, ct).ConfigureAwait(false);
            return;
        }

        _syncedAccountId = accountId;
        _state = "syncing";

        var listResult = await _accounts.WithAuthAsync(accountId, token => _api.ListProfilesAsync(token, ct), ct).ConfigureAwait(false);
        if (!listResult.Success)
        {
            _state = listResult.Offline ? "offline" : "error";
            return;
        }

        // The endpoint returns every machine's rows; a sync pass is a backup of
        // THIS machine only. Another machine's profiles are never pulled
        // automatically - they reach this library through the explicit import
        // flow, which is what stops two machines round-tripping renames of a
        // profile they both call "Default".
        var cloudRows = OwnRows(listResult.Value);
        if (cloudRows.Count > ProfileManager.MaxProfiles)
        {
            // The server caps an account at MaxProfiles; a row past that is an
            // anomaly rather than a case to retry every tick - ImportProfileWithId
            // would throw on it forever otherwise. Excluded rows log once per
            // profile id, not once per tick.
            foreach (var row in cloudRows.Skip(ProfileManager.MaxProfiles))
            {
                bool firstWarning;
                lock (_overCapWarned)
                {
                    firstWarning = _overCapWarned.Add(row.ProfileId);
                }
                if (firstWarning)
                {
                    Console.Error.WriteLine($"[cloud-sync] {accountId} has more than {ProfileManager.MaxProfiles} cloud profiles - {row.ProfileId} ({row.Name}) will not sync locally.");
                }
            }
            cloudRows = cloudRows.Take(ProfileManager.MaxProfiles).ToList();
        }
        var localProfiles = _profiles.GetManifest().Profiles;
        var syncMap = _store.Load().Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId)?.ProfileSync
            ?? new Dictionary<string, CloudProfileSyncRecord>();

        // First login on a fresh install: nothing has ever synced for this
        // account on this machine (syncMap empty), the account already has a
        // cloud library, and the only local profile is the untouched
        // bootstrap Default ProfileManager.Initialize created before login.
        // The regular per-profile loop below would read "local exists, cloud
        // missing, no record" for that id and Push it as a brand new profile,
        // duplicating "Default" on the account. Adopt the cloud library
        // wholesale instead, exactly like an account switch.
        // No first-login bootstrap: pressing "Back up now" on a fresh machine
        // must not replace the local library with the cloud's. Pulling another
        // library in is the explicit import flow.

        var ids = new HashSet<string>(localProfiles.Select(p => p.Id), StringComparer.Ordinal);
        ids.UnionWith(cloudRows.Select(r => r.ProfileId));

        var now = _clock.GetUtcNow();
        var anyPending = false;

        foreach (var id in ids)
        {
            var localEntry = localProfiles.FirstOrDefault(p => p.Id == id);
            var cloudRow = cloudRows.FirstOrDefault(r => r.ProfileId == id);
            syncMap.TryGetValue(id, out var syncRecord);

            string? localHash = null;
            ProfileExport? localExport = null;
            if (localEntry is not null)
            {
                localExport = _profiles.ExportProfileForSync(id);
                localHash = localExport is null ? null : HashPayload(localExport);
            }

            var action = CloudSyncDecision.Decide(
                localExists: localEntry is not null,
                localHash: localHash,
                cloudExists: cloudRow is not null,
                cloudRevision: cloudRow?.Revision ?? 0,
                hasSyncRecord: syncRecord is not null,
                syncedRevision: syncRecord?.Revision ?? 0,
                syncedHash: syncRecord?.LastSyncedHash);

            switch (action)
            {
                case CloudSyncAction.None:
                    _dirtySince.TryRemove(id, out _);
                    _conflicts.TryRemove(id, out _);
                    break;

                case CloudSyncAction.Push:
                    if (!manual && !DebounceElapsed(id, now))
                    {
                        anyPending = true;
                        break;
                    }
                    var pushed = await PushAsync(accountId, id, localEntry!.Name, localExport!, localHash!, syncRecord?.Revision ?? 0, ct).ConfigureAwait(false);
                    if (pushed)
                    {
                        _dirtySince.TryRemove(id, out _);
                    }
                    else
                    {
                        anyPending = true;
                    }
                    break;

                case CloudSyncAction.Pull:
                    await PullAsync(accountId, id, ct).ConfigureAwait(false);
                    _conflicts.TryRemove(id, out _);
                    _dirtySince.TryRemove(id, out _);
                    break;

                case CloudSyncAction.Conflict:
                    RecordConflict(id, localEntry?.Name ?? "", localEntry?.UpdatedAt ?? "",
                        cloudRow!.Revision, cloudRow.UpdatedAt, cloudRow.Name, cloudRow.UpdatedByInstallId);
                    anyPending = true;
                    break;

                case CloudSyncAction.DeleteRemote:
                    await DeleteRemoteAsync(accountId, id, ct).ConfigureAwait(false);
                    _dirtySince.TryRemove(id, out _);
                    break;

                case CloudSyncAction.DeleteLocal:
                    await DeleteLocalOrRepushAsync(accountId, id, localEntry!.Name, localExport!, localHash!, syncRecord!.Revision, ct).ConfigureAwait(false);
                    _dirtySince.TryRemove(id, out _);
                    break;
            }
        }

        _lastSyncAt = now.ToString("o");
        _accounts.MarkSynced(accountId, now);
        _state = !_conflicts.IsEmpty || anyPending ? "dirty" : "idle";
    }

    private bool DebounceElapsed(string profileId, DateTimeOffset now)
    {
        var since = _dirtySince.GetOrAdd(profileId, now);
        return now - since >= PushDebounce;
    }

    /// <returns>True only when the push actually landed a new revision. False on a conflict or an offline/server error - callers must not clear their own dirty-tracking state in that case, or a failed push stops being retried until the profile changes again.</returns>
    private async Task<bool> PushAsync(string accountId, string profileId, string name, ProfileExport payload, string localHash, int baseRevision, CancellationToken ct)
    {
        var request = new CloudPutProfileRequest
        {
            Name = name,
            BaseRevision = baseRevision,
            Payload = payload,
        };
        var result = await _accounts.WithAuthAsync(accountId, token => _api.PutProfileAsync(token, OwnInstallId(), profileId, request, ct), ct).ConfigureAwait(false);

        if (result.StatusCode == 409 && result.Value is { CurrentRevision: not null })
        {
            var cr = result.Value;
            // The comparison sheet shows both sides' timestamps, so the local
            // one has to be real rather than blank.
            var localUpdatedAt = _profiles.GetManifest().Profiles
                .FirstOrDefault(p => p.Id == profileId)?.UpdatedAt ?? "";
            RecordConflict(profileId, name, localUpdatedAt, cr.CurrentRevision!.Value, cr.UpdatedAt ?? "", cr.Name ?? "", cr.UpdatedByInstallId ?? "");
            return false;
        }
        if (!result.Success || result.Value?.Revision is not { } revision)
        {
            // Offline/server error: leave dirtySince alone so the next tick retries.
            return false;
        }

        SetSyncRecord(accountId, profileId, revision, localHash);
        return true;
    }

    private async Task PullAsync(string accountId, string profileId, CancellationToken ct)
    {
        var result = await _accounts.WithAuthAsync(accountId, token => _api.GetProfileAsync(token, OwnInstallId(), profileId, ct), ct).ConfigureAwait(false);
        if (!result.Success || result.Value?.Payload?.Settings is null)
        {
            return;
        }

        var dto = result.Value;
        var hash = HashPayload(dto.Payload!);
        var name = string.IsNullOrEmpty(dto.Name) ? (dto.Payload!.Name ?? "Imported") : dto.Name;
        try
        {
            _profiles.ImportProfileWithId(profileId, name, dto.Payload!.Settings!);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[cloud-sync] pull {profileId} skipped: {ex.Message}");
            return;
        }

        SetSyncRecord(accountId, profileId, dto.Revision, hash);
    }

    /// <summary>The profile was deleted locally after having synced - tell the cloud. Leaves the sync record in place on failure so the next tick retries the delete instead of forgetting it.</summary>
    private async Task DeleteRemoteAsync(string accountId, string profileId, CancellationToken ct)
    {
        var result = await _accounts.WithAuthAsync(accountId, token => _api.DeleteProfileAsync(token, OwnInstallId(), profileId, ct), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return;
        }
        _store.Update(s =>
        {
            var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
            rec?.ProfileSync.Remove(profileId);
        });
    }

    /// <summary>The cloud row was deleted (another machine) after having synced. Deletes the local profile via ProfileManager's own last-profile/Primary guards; when those guards refuse (only profile, or Primary-locked), re-pushes the local content instead of losing it.</summary>
    private async Task DeleteLocalOrRepushAsync(string accountId, string profileId, string name, ProfileExport localExport, string localHash, int baseRevision, CancellationToken ct)
    {
        try
        {
            _profiles.DeleteProfile(profileId);
        }
        catch (InvalidOperationException)
        {
            await PushAsync(accountId, profileId, name, localExport, localHash, baseRevision, ct).ConfigureAwait(false);
            return;
        }
        catch (KeyNotFoundException)
        {
            // Already gone locally (a manual delete raced this tick) - nothing left to reconcile.
        }
        _store.Update(s =>
        {
            var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
            rec?.ProfileSync.Remove(profileId);
        });
    }

    private void RecordConflict(string profileId, string localName, string localUpdatedAt, int cloudRevision, string cloudUpdatedAt, string cloudName, string updatedByInstallId)
    {
        _conflicts[profileId] = new CloudSyncConflictDto
        {
            ProfileId = profileId,
            Name = localName,
            LocalUpdatedAt = localUpdatedAt,
            LocalHostname = Environment.MachineName,
            CloudRevision = cloudRevision,
            CloudUpdatedAt = cloudUpdatedAt,
            CloudName = cloudName,
            // Rows are per-machine, so the writer of the cloud copy is normally
            // this same machine (two service instances, or a settings restore).
            // Anything else is left unnamed rather than guessed at.
            CloudHostname = string.Equals(updatedByInstallId, OwnInstallId(), StringComparison.Ordinal)
                ? Environment.MachineName
                : "",
            UpdatedByInstallId = updatedByInstallId,
        };
    }

    private void SetSyncRecord(string accountId, string profileId, int revision, string hash)
    {
        _store.Update(s =>
        {
            var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
            if (rec is null)
            {
                return;
            }
            rec.ProfileSync[profileId] = new CloudProfileSyncRecord
            {
                Revision = revision,
                LastSyncedAt = DateTimeOffset.UtcNow.ToString("o"),
                LastSyncedHash = hash,
            };
        });
    }

    /// <summary>Pushes every profile whose export payload no longer matches its last-synced hash, ignoring the debounce window. Used for the outgoing side of an account switch and for shutdown flush.</summary>
    private async Task FlushAccountAsync(string accountId, CancellationToken ct)
    {
        var localProfiles = _profiles.GetManifest().Profiles;
        var syncMap = _store.Load().Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId)?.ProfileSync
            ?? new Dictionary<string, CloudProfileSyncRecord>();

        foreach (var entry in localProfiles)
        {
            ct.ThrowIfCancellationRequested();
            var localExport = _profiles.ExportProfileForSync(entry.Id);
            if (localExport is null)
            {
                continue;
            }
            var hash = HashPayload(localExport);
            syncMap.TryGetValue(entry.Id, out var syncRecord);
            if (syncRecord is not null && string.Equals(syncRecord.LastSyncedHash, hash, StringComparison.Ordinal))
            {
                continue;
            }

            await PushAsync(accountId, entry.Id, entry.Name, localExport, hash, syncRecord?.Revision ?? 0, ct).ConfigureAwait(false);
        }
    }

    // ── account switch (archive + wholesale replace) ────────────────────

    /// <summary>
    /// Non-null while an account switch has been requested but has not yet
    /// completed a full archive+replace. RunSyncPassAsync checks this before
    /// doing an incremental pass for the same account - the local library
    /// still belongs to the OUTGOING account until the switch actually lands,
    /// so an incremental pass in that window would push the outgoing
    /// account's profiles under the incoming account's identity.
    /// </summary>
    private sealed record PendingSwitchPair(string From, string To);
    private volatile PendingSwitchPair? _pendingSwitch;

    internal (string? From, string? To) PendingSwitch => (_pendingSwitch?.From, _pendingSwitch?.To);

    /// <summary>Records that fromAccountId -> toAccountId is in flight. Called synchronously from the OnAccountSwitching handler before any async dispatch. One volatile write of an immutable pair, so concurrent switch requests cannot interleave a mismatched From/To.</summary>
    internal void AnnounceSwitch(string fromAccountId, string toAccountId)
    {
        _syncedAccountId = toAccountId;
        _pendingSwitch = new PendingSwitchPair(fromAccountId, toAccountId);
    }

    internal async Task HandleSwitchAsync(string fromAccountId, string toAccountId, CancellationToken ct)
    {
        // Only the announced, still-pending switch may run. A queued duplicate
        // (the OnAccountActivated sync pass won the gate first and completed
        // the switch via its redirect) or a switch superseded by a logout or a
        // newer switch would otherwise flush the INCOMING library to the
        // OUTGOING account's cloud.
        if (_pendingSwitch is not { } current || current.From != fromAccountId || current.To != toAccountId)
        {
            return;
        }
        _state = "syncing";
        try
        {
            // Read the incoming account's full library BEFORE touching anything
            // local. A failed list or a failed fetch of any single profile
            // aborts here with the local library completely untouched - a
            // partial pull must never partially wipe or partially replace the
            // library (see ProfileManager.ReplaceLibrary's empty-set fallback:
            // silently proceeding on a failed list would look identical to
            // "this account has zero cloud profiles" and reset the outgoing
            // user's active library to a blank Default).
            var listResult = await _accounts.WithAuthAsync(toAccountId, token => _api.ListProfilesAsync(token, ct), ct).ConfigureAwait(false);
            if (!listResult.Success)
            {
                _state = listResult.Offline ? "offline" : "error";
                return;
            }
            var cloudRows = OwnRows(listResult.Value);

            var pulled = await PullCloudProfilesAsync(toAccountId, cloudRows, ct).ConfigureAwait(false);
            if (pulled is null)
            {
                return;
            }

            // The incoming library is fully and successfully retrieved - safe
            // to touch local state now.
            await FlushAccountAsync(fromAccountId, ct).ConfigureAwait(false);
            ReplaceLocalLibrary(toAccountId, cloudRows, pulled);

            _pendingSwitch = null;
            _state = "idle";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[cloud-sync] account switch failed: {ex.GetType().Name}: {ex.Message}");
            _state = "error";
        }
    }

    /// <summary>Fetches every payload for the given cloud rows. Returns null (and sets _state to "offline"/"error") on any single failure - a partial pull must never be applied, so callers only touch the local library after a non-null result.</summary>
    private async Task<List<(string Id, string Name, NexusSettings Data, string Hash)>?> PullCloudProfilesAsync(
        string accountId, List<CloudProfileSummaryDto> cloudRows, CancellationToken ct)
    {
        var pulled = new List<(string Id, string Name, NexusSettings Data, string Hash)>();
        foreach (var row in cloudRows.Take(ProfileManager.MaxProfiles))
        {
            ct.ThrowIfCancellationRequested();
            var profileResult = await _accounts.WithAuthAsync(accountId, token => _api.GetProfileAsync(token, OwnInstallId(), row.ProfileId, ct), ct).ConfigureAwait(false);
            if (!profileResult.Success || profileResult.Value?.Payload?.Settings is null)
            {
                _state = profileResult.Offline ? "offline" : "error";
                return null;
            }
            // Hash BEFORE handing the object to ReplaceLibrary, which strips
            // Auth/PrimaryProfileId/SharedCategories in place - the cloud
            // payload should already arrive stripped, but hashing first
            // avoids relying on that mutation ordering for correctness.
            var hash = HashPayload(profileResult.Value.Payload);
            pulled.Add((row.ProfileId, row.Name, profileResult.Value.Payload.Settings!, hash));
        }
        return pulled;
    }

    /// <summary>Archives the current local library then replaces it wholesale with the already-pulled cloud rows, updates per-profile sync bookkeeping, and resets in-flight dirty/conflict tracking. Shared by the account-switch handler and the first-login pristine-bootstrap-Default fast path.</summary>
    private void ReplaceLocalLibrary(
        string accountId, List<CloudProfileSummaryDto> cloudRows, List<(string Id, string Name, NexusSettings Data, string Hash)> pulled)
    {
        _profiles.ArchiveLibrary();
        _profiles.ReplaceLibrary(pulled.Select(p => (p.Id, p.Name, p.Data)).ToList());

        var now = _clock.GetUtcNow();
        _store.Update(s =>
        {
            var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
            if (rec is null)
            {
                return;
            }
            rec.ProfileSync.Clear();
            foreach (var match in pulled)
            {
                rec.ProfileSync[match.Id] = new CloudProfileSyncRecord
                {
                    Revision = cloudRows.First(r => r.ProfileId == match.Id).Revision,
                    LastSyncedAt = now.ToString("o"),
                    LastSyncedHash = match.Hash,
                };
            }
        });

        _dirtySince.Clear();
        _conflicts.Clear();
        lock (_overCapWarned)
        {
            _overCapWarned.Clear();
        }
        _lastSyncAt = now.ToString("o");
        _accounts.MarkSynced(accountId, now);
    }

    /// <summary>
    /// True when settings' profile-scoped content (Lighting, Cooling, Theme,
    /// and the Dashboard-relevant fields per ProfileSharing.All - the same
    /// set LoadProfileIntoSettings round-trips on switch/pull) matches one of
    /// the two legitimately pristine states of a bootstrap Default profile:
    /// a blank NexusSettings, or one with the Silent/Balanced/Turbo/Max curves
    /// seeded by the same FanProfiles.SeedDefaultPresetCurves call
    /// AutoRestoreOnStart makes ~4s after boot - login can race that delay.
    /// Hardware-bound fields (Keeb, Y70, Devices, PanelDevices) are excluded
    /// since those vary by machine even on a profile the user never touched.
    /// </summary>
    private bool IsPristineDefaultContent(NexusSettings settings)
    {
        var candidateJson = ProfileScopedJson(settings);
        foreach (var baseline in PristineBaselines())
        {
            if (candidateJson == ProfileScopedJson(baseline))
            {
                return true;
            }
        }
        return false;
    }

    private IEnumerable<NexusSettings> PristineBaselines()
    {
        yield return new NexusSettings();

        var scratch = new ScratchConfigStore();
        FanProfiles.SeedDefaultPresetCurves(_fans, scratch);
        yield return scratch.Settings;
    }

    private static string ProfileScopedJson(NexusSettings settings)
    {
        var projection = new NexusSettings();
        foreach (var category in ProfileSharing.All)
        {
            ProfileSharing.ApplyCategory(projection, settings, category);
        }
        return JsonSerializer.Serialize(projection, PersistenceJsonContext.Default.NexusSettings);
    }

    /// <summary>Throwaway in-memory IConfigStore over one NexusSettings instance, used only to run FanProfiles.SeedDefaultPresetCurves for the pristine-baseline projection without touching the real settings file.</summary>
    private sealed class ScratchConfigStore : IConfigStore
    {
        public NexusSettings Settings { get; } = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => Settings;
        public void Update(Action<NexusSettings> mutator) => mutator(Settings);
        public void FlushNow() { }
        public void Reload() { }
        public event Action? OnChanged { add { } remove { } }
    }

    // ── shared helpers ───────────────────────────────────────────────────

    internal static string HashPayload(ProfileExport payload)
    {
        var json = JsonSerializer.Serialize(payload, PersistenceJsonContext.Default.ProfileExport);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}

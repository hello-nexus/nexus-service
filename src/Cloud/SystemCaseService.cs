using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cloud;

/// <summary>
/// This machine's case pick: the local value (MachineCaseStore) is the source
/// of truth for reads and always answers first; while a cloud account is
/// active it is mirrored to that account's device row so the website shows
/// the same case, and a pick made on the website is adopted back.
///
/// A pick is "pending" from the moment it is saved until the account accepted
/// it, so a pick made offline, signed out, or before the device report created
/// the device row still reaches the next account/connection. Pending is retried
/// on the next GET and after every successful device report.
/// </summary>
public sealed class SystemCaseService
{
    public const int MaxCaseIdLength = 64;
    private static readonly TimeSpan PullInterval = TimeSpan.FromMinutes(1);

    private readonly MachineCaseStore _store;
    private readonly CloudAccountService _accounts;
    private readonly ICloudApiClient _api;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _networkHeld;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _push = new(1, 1);
    private long _lastAttemptTicks;

    public SystemCaseService(MachineCaseStore store, CloudAccountService accounts, ICloudApiClient api)
        : this(store, accounts, api, TimeProvider.System, () => Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
    {
    }

    internal SystemCaseService(MachineCaseStore store, CloudAccountService accounts, ICloudApiClient api, TimeProvider clock, Func<bool> networkHeld)
    {
        _store = store;
        _accounts = accounts;
        _api = api;
        _clock = clock;
        _networkHeld = networkHeld;
        // A different account may carry a different website pick: let the next
        // GET compare against it instead of waiting out the pull interval. A
        // still-pending local pick is pushed by the device-report flush.
        _accounts.OnAccountActivated += _ => Volatile.Write(ref _lastAttemptTicks, 0);
    }

    /// <summary>Null, or 1-64 characters of letters, digits, '.', '_', ':' and '-'. Same rule as the cloud api.</summary>
    public static bool IsValidCaseId(string? caseId)
    {
        if (caseId is null)
        {
            return true;
        }
        if (caseId.Length is < 1 or > MaxCaseIdLength)
        {
            return false;
        }
        foreach (var c in caseId)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The local value, immediately. A due account read runs in the background
    /// (serialized with pushes, at most once a minute) and never delays the answer.
    /// </summary>
    public Task<string?> GetAsync(CancellationToken ct)
    {
        if (_accounts.ActiveAccountId is { } accountId && !_networkHeld() && DueForNetwork())
        {
            MarkAttempt();
            Launch(() => ReconcileAsync(accountId));
        }
        return Task.FromResult(_store.Load().CaseId);
    }

    /// <summary>Saves locally first (always pending) and returns; the active account is pushed in the background.</summary>
    public Task<string?> SetAsync(string? caseId, CancellationToken ct)
    {
        _store.Save(caseId, pending: true);
        if (_accounts.ActiveAccountId is { } accountId && !_networkHeld())
        {
            MarkAttempt();
            Launch(() => PushPendingAsync(accountId));
        }
        return Task.FromResult(_store.Load().CaseId);
    }

    /// <summary>Pushes a pending pick right after a device report succeeded, when the device row is known to exist.</summary>
    public async Task FlushPendingAsync(string accountId, CancellationToken ct)
    {
        if (accountId != _accounts.ActiveAccountId || _networkHeld() || !_store.Load().Pending)
        {
            return;
        }
        MarkAttempt();
        await RunSafeAsync(() => PushPendingAsync(accountId)).ConfigureAwait(false);
    }

    /// <summary>Completes when the background sync work started so far has finished.</summary>
    internal Task IdleAsync() => Volatile.Read(ref _background);

    private Task _background = Task.CompletedTask;

    // Network calls are never cancelled: cancelling a token refresh after the
    // api rotated the refresh token loses the new one and the next refresh
    // replays the old one, which the api treats as reuse and signs the user out.
    private void Launch(Func<Task> work)
    {
        lock (_sync)
        {
            _background = _background.ContinueWith(_ => RunSafeAsync(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private static async Task RunSafeAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[system-case] sync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool DueForNetwork() =>
        _clock.GetUtcNow().UtcTicks - Volatile.Read(ref _lastAttemptTicks) >= PullInterval.Ticks;

    private void MarkAttempt() => Volatile.Write(ref _lastAttemptTicks, _clock.GetUtcNow().UtcTicks);

    private async Task ReconcileAsync(string accountId)
    {
        await _push.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_store.Load().Pending)
            {
                await PushLockedAsync(accountId).ConfigureAwait(false);
                return;
            }
            await PullLockedAsync(accountId).ConfigureAwait(false);
        }
        finally
        {
            _push.Release();
        }
    }

    private async Task PushPendingAsync(string accountId)
    {
        await _push.WaitAsync().ConfigureAwait(false);
        try
        {
            await PushLockedAsync(accountId).ConfigureAwait(false);
        }
        finally
        {
            _push.Release();
        }
    }

    private async Task PushLockedAsync(string accountId)
    {
        var local = _store.Load();
        if (!local.Pending || accountId != _accounts.ActiveAccountId)
        {
            return;
        }
        var installId = _accounts.ResolveStableInstallId();
        var pushed = local.CaseId;
        var result = await _accounts.WithAuthAsync(accountId, token => _api.SetDeviceCaseAsync(token, installId, pushed, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        if (!result.Success)
        {
            return;
        }
        // A newer pick saved while the call was in flight stays pending.
        _store.Update(f => f.Pending && f.CaseId == pushed ? new MachineCaseFile { CaseId = f.CaseId, Pending = false } : f);
    }

    private async Task PullLockedAsync(string accountId)
    {
        var installId = _accounts.ResolveStableInstallId();
        var result = await _accounts.WithAuthAsync(accountId, token => _api.ListDevicesAsync(token, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        if (!result.Success || result.Value is null || accountId != _accounts.ActiveAccountId)
        {
            return;
        }
        var device = result.Value.FirstOrDefault(d => string.Equals(d.InstallId, installId, StringComparison.Ordinal));
        if (device is null)
        {
            return;
        }
        // A null (or unusable) account value never erases the machine's pick:
        // a pre-migration row or an api rollback looks the same. A local pick
        // the account lacks is pushed instead.
        if (device.CaseId is null || !IsValidCaseId(device.CaseId))
        {
            var after = _store.Update(f => f.Pending || f.CaseId is null ? f : new MachineCaseFile { CaseId = f.CaseId, Pending = true });
            if (after.Pending)
            {
                await PushLockedAsync(accountId).ConfigureAwait(false);
            }
            return;
        }
        _store.Update(f => f.Pending || f.CaseId == device.CaseId ? f : new MachineCaseFile { CaseId = device.CaseId, Pending = false });
    }
}

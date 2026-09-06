using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Security;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Cloud;

/// <summary>Outcome of a local account action that has no payload beyond success/failure.</summary>
public sealed class CloudActionResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>ISO timestamp from the upstream error body's retryAt field (e.g. username_cooldown). Null unless the server sent one.</summary>
    public string? ErrorRetryAt { get; init; }
    public bool Offline { get; init; }

    public static CloudActionResult Ok() => new() { Success = true, StatusCode = 200 };

    public static CloudActionResult Fail(string code, string message, int statusCode = 400) =>
        new() { Success = false, StatusCode = statusCode, ErrorCode = code, ErrorMessage = message };

    public static CloudActionResult FromError<T>(CloudApiResult<T> result) => new()
    {
        Success = false,
        StatusCode = result.StatusCode,
        ErrorCode = result.ErrorCode,
        ErrorMessage = result.ErrorMessage,
        ErrorRetryAt = result.ErrorRetryAt,
        Offline = result.Offline,
    };
}

/// <summary>A <see cref="CloudActionResult"/> that also carries a value on success.</summary>
public sealed class CloudActionResult<T>
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Offline { get; init; }
    public T? Value { get; init; }

    public static CloudActionResult<T> Ok(T value) => new() { Success = true, StatusCode = 200, Value = value };

    public static CloudActionResult<T> Fail(string code, string message, int statusCode = 400) =>
        new() { Success = false, StatusCode = statusCode, ErrorCode = code, ErrorMessage = message };

    public static CloudActionResult<T> FromError<TOther>(CloudApiResult<TOther> result) => new()
    {
        Success = false,
        StatusCode = result.StatusCode,
        ErrorCode = result.ErrorCode,
        ErrorMessage = result.ErrorMessage,
        Offline = result.Offline,
    };
}

public readonly record struct CloudRecoveryStatusSnapshot(string Status, bool RecoveryFresh);

/// <summary>
/// Token holder + identity/session manager for Nexus cloud accounts. Owns the
/// persisted <see cref="AuthSettings.CloudAccounts"/> list (refresh tokens,
/// account metadata) and an in-memory-only access-token cache (15 min JWTs -
/// never written to disk). Profile sync and device reporting are separate
/// services that react to <see cref="OnAccountActivated"/>,
/// <see cref="OnAccountSwitching"/>, and <see cref="OnAccountLoggedOut"/>
/// rather than being driven directly, so this class stays focused on
/// identity/session plumbing.
/// </summary>
public sealed class CloudAccountService
{
    private const int AccessTokenLifetimeMinutes = 15;
    private const int AccessTokenRefreshSkewSeconds = 45;
    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RecoveryPollCap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecoveryFreshWindow = TimeSpan.FromMinutes(30);

    private readonly ICloudApiClient _api;
    private readonly IConfigStore _store;
    private readonly TimeProvider _clock;

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _accessTokens = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new();

    private readonly object _recoveryLock = new();
    private string? _recoveryGrantId;
    private string _recoveryStatus = "idle";
    private CancellationTokenSource? _recoveryCts;
    private string? _recoveryFreshAccountId;
    private DateTimeOffset _recoveryFreshUntil;

    /// <summary>Fired whenever <paramref name="accountId"/> becomes the active account - including right after <see cref="OnAccountSwitching"/> on a switch, so a subscriber that only cares about "an account is active now" (device reporting) doesn't need to also watch the switch event.</summary>
    public event Action<string>? OnAccountActivated;

    /// <summary>Fired before the newly-active account's activation, only when a DIFFERENT account was previously active. CloudProfileSyncService uses this to archive the outgoing library and pull the incoming one wholesale.</summary>
    public event Action<string, string>? OnAccountSwitching;

    /// <summary>Fired when an account's local session is removed (logout or account deletion), regardless of whether it was the active account.</summary>
    public event Action<string>? OnAccountLoggedOut;

    public CloudAccountService(ICloudApiClient api, IConfigStore store)
        : this(api, store, TimeProvider.System)
    {
    }

    internal CloudAccountService(ICloudApiClient api, IConfigStore store, TimeProvider clock)
    {
        _api = api;
        _store = store;
        _clock = clock;
    }

    public string? ActiveAccountId => _store.Load().Auth?.ActiveCloudAccountId;

    public List<CloudAccountSummaryDto> ListAccounts()
    {
        var auth = _store.Load().Auth;
        if (auth is null)
        {
            return new List<CloudAccountSummaryDto>();
        }
        return auth.CloudAccounts.Select(a => new CloudAccountSummaryDto
        {
            AccountId = a.AccountId,
            Email = a.Email,
            Username = a.Username,
            Avatar = string.IsNullOrEmpty(a.AvatarLarge) && string.IsNullOrEmpty(a.AvatarSmall)
                ? null
                : new CloudAvatarDto { Large = a.AvatarLarge, Small = a.AvatarSmall },
            IsPrivate = a.IsPrivate,
            EmailVerified = a.EmailVerified,
            Active = a.AccountId == auth.ActiveCloudAccountId,
            LastSyncAt = a.LastSyncAt,
        }).ToList();
    }

    // ── register / login / logout ──────────────────────────────────────

    public async Task<CloudActionResult> RegisterAsync(string email, string password, string username, CancellationToken ct)
    {
        var result = await _api.RegisterAsync(new CloudRegisterRequest { Email = email, Password = password, Username = username }, ct)
            .ConfigureAwait(false);
        return result.Success ? CloudActionResult.Ok() : CloudActionResult.FromError(result);
    }

    public async Task<(CloudActionResult Result, string? AccountId)> LoginAsync(string identifier, string password, CancellationToken ct)
    {
        var installId = InstallIdentity.Resolve(_store);
        var request = new CloudLoginRequest
        {
            Identifier = identifier,
            Password = password,
            DeviceName = ResolveDeviceName(),
            InstallId = installId,
        };
        var result = await _api.LoginAsync(request, ct).ConfigureAwait(false);
        if (!result.Success || result.Value?.Account is null || string.IsNullOrEmpty(result.Value.AccessToken))
        {
            return (CloudActionResult.FromError(result), null);
        }

        var accountId = ApplySession(result.Value);
        return (CloudActionResult.Ok(), accountId);
    }

    /// <summary>Applies a fresh login/recovery session: upserts the account record, rotates the stored refresh token, caches the access token, sets it active, and fires the activation events. Returns the accountId.</summary>
    private string ApplySession(CloudAuthSession session)
    {
        var account = session.Account!;
        string? previousActiveId = null;
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            previousActiveId = s.Auth.ActiveCloudAccountId;
            var rec = s.Auth.CloudAccounts.FirstOrDefault(a => a.AccountId == account.Id);
            if (rec is null)
            {
                rec = new CloudAccountRecord { AccountId = account.Id };
                s.Auth.CloudAccounts.Add(rec);
            }
            ApplyAccountFields(rec, account);
            rec.RefreshToken = SecretProtector.Protect(session.RefreshToken);
            s.Auth.ActiveCloudAccountId = account.Id;
        });

        _accessTokens[account.Id] = (session.AccessToken, _clock.GetUtcNow().AddMinutes(AccessTokenLifetimeMinutes));
        FireActivation(previousActiveId, account.Id);
        return account.Id;
    }

    /// <summary>Switches the active pointer to an already-stored account (multi-account switcher) - no re-login, reuses the stored refresh token.</summary>
    public CloudActionResult Activate(string accountId)
    {
        string? previousActiveId = null;
        var found = false;
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            previousActiveId = s.Auth.ActiveCloudAccountId;
            found = s.Auth.CloudAccounts.Any(a => a.AccountId == accountId);
            if (found)
            {
                s.Auth.ActiveCloudAccountId = accountId;
            }
        });
        if (!found)
        {
            return CloudActionResult.Fail("not_found", "Account not found.", 404);
        }
        if (previousActiveId != accountId)
        {
            FireActivation(previousActiveId, accountId);
        }
        return CloudActionResult.Ok();
    }

    public async Task LogoutAsync(string accountId, CancellationToken ct)
    {
        var storedRefreshToken = _store.Load().Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId)?.RefreshToken;
        var refreshToken = SecretProtector.Unprotect(storedRefreshToken);
        RemoveAccountLocally(accountId);

        if (!string.IsNullOrEmpty(refreshToken))
        {
            try
            {
                await _api.LogoutAsync(refreshToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[cloud] server-side logout failed (local session already dropped): {ex.Message}");
            }
        }
    }

    public async Task<CloudActionResult> DeleteAccountAsync(string? currentPassword, CancellationToken ct)
    {
        var accountId = ActiveAccountId;
        if (accountId is null)
        {
            return CloudActionResult.Fail("no_session", "Not logged in.", 401);
        }
        var result = await WithAuthAsync(accountId,
            token => _api.DeleteAccountAsync(token, currentPassword, ct), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return CloudActionResult.FromError(result);
        }
        RemoveAccountLocally(accountId);
        return CloudActionResult.Ok();
    }

    private void RemoveAccountLocally(string accountId)
    {
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.RemoveAll(a => a.AccountId == accountId);
            if (s.Auth.ActiveCloudAccountId == accountId)
            {
                s.Auth.ActiveCloudAccountId = null;
            }
        });
        _accessTokens.TryRemove(accountId, out _);
        try
        {
            OnAccountLoggedOut?.Invoke(accountId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cloud] OnAccountLoggedOut handler failed: {ex.Message}");
        }
    }

    private void FireActivation(string? previousActiveId, string newAccountId)
    {
        if (!string.IsNullOrEmpty(previousActiveId) && previousActiveId != newAccountId)
        {
            try
            {
                OnAccountSwitching?.Invoke(previousActiveId, newAccountId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cloud] OnAccountSwitching handler failed: {ex.Message}");
            }
        }
        try
        {
            OnAccountActivated?.Invoke(newAccountId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cloud] OnAccountActivated handler failed: {ex.Message}");
        }
    }

    // ── access token / refresh ──────────────────────────────────────────

    /// <summary>Returns a usable access token for the account, refreshing first if the cached one is missing or near expiry. Null when there is no stored session (or the refresh token was rejected, in which case the local session is dropped).</summary>
    public async Task<string?> GetValidAccessTokenAsync(string accountId, CancellationToken ct)
    {
        if (_accessTokens.TryGetValue(accountId, out var cached) &&
            cached.ExpiresAt > _clock.GetUtcNow().AddSeconds(AccessTokenRefreshSkewSeconds))
        {
            return cached.Token;
        }
        return await RefreshAsync(accountId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rotates the refresh token against the cloud and caches the new access
    /// token. Serialized per account: the refresh token rotates on every use
    /// and a reuse trips the server's whole-family revocation, so two
    /// concurrent callers (CloudProfileSyncService's tick and
    /// CloudDeviceReporter's tick both deciding to refresh near-simultaneously)
    /// must never present the same pre-rotation token at once. The rotated
    /// refresh token is persisted to settings BEFORE the access token is
    /// cached/returned, so a crash in between leaves the durable session
    /// usable (the old refresh token is already dead on the server either way
    /// - rotation is one-shot).
    /// </summary>
    internal Task<string?> RefreshAsync(string accountId, CancellationToken ct)
    {
        var gate = _refreshGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        return RefreshGatedAsync(accountId, gate, ct);
    }

    private async Task<string?> RefreshGatedAsync(string accountId, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(accountId, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> RefreshCoreAsync(string accountId, CancellationToken ct)
    {
        var storedRefreshToken = _store.Load().Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId)?.RefreshToken;
        var refreshToken = SecretProtector.Unprotect(storedRefreshToken);
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        var result = await _api.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
        if (!result.Success || result.Value is null || string.IsNullOrEmpty(result.Value.AccessToken))
        {
            // A definitive 401/403 means the server rejected this refresh token
            // outright - reuse-detection revoked the whole family, or it was
            // explicitly revoked (password change, remote logout). Any other
            // failure (5xx, malformed 200 body, or offline) is transient: the
            // refresh token itself may still be good, so the session stays and
            // the caller retries later instead of forcing every machine to log
            // back in on a server blip.
            if (!result.Offline && (result.StatusCode == 401 || result.StatusCode == 403))
            {
                RemoveAccountLocally(accountId);
            }
            return null;
        }

        var session = result.Value;
        _store.Update(s =>
        {
            var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
            if (rec is null)
            {
                return;
            }
            rec.RefreshToken = SecretProtector.Protect(session.RefreshToken);
            if (session.Account is not null)
            {
                ApplyAccountFields(rec, session.Account);
            }
        });

        var expiresAt = _clock.GetUtcNow().AddMinutes(AccessTokenLifetimeMinutes);
        _accessTokens[accountId] = (session.AccessToken, expiresAt);
        return session.AccessToken;
    }

    /// <summary>Runs an authenticated call, refreshing and retrying exactly once on a 401. Shared by every /account/... proxy so the "expired token -> refresh -> retry" behavior lives in one place.</summary>
    public async Task<CloudApiResult<T>> WithAuthAsync<T>(
        string accountId, Func<string, Task<CloudApiResult<T>>> call, CancellationToken ct)
    {
        var token = await GetValidAccessTokenAsync(accountId, ct).ConfigureAwait(false);
        if (token is null)
        {
            return CloudApiResult<T>.Fail(401, "no_session", "Not logged in.");
        }

        var result = await call(token).ConfigureAwait(false);
        if (result.StatusCode != 401)
        {
            return result;
        }

        var refreshed = await RefreshAsync(accountId, ct).ConfigureAwait(false);
        if (refreshed is null)
        {
            return result;
        }
        return await call(refreshed).ConfigureAwait(false);
    }

    // ── account management proxies ──────────────────────────────────────

    public Task<CloudApiResult<CloudVoid>> ChangePasswordAsync(string? currentPassword, string newPassword, CancellationToken ct)
    {
        var accountId = ActiveAccountId;
        if (accountId is null)
        {
            return Task.FromResult(CloudApiResult<CloudVoid>.Fail(401, "no_session", "Not logged in."));
        }
        return WithAuthAsync(accountId, token =>
            _api.ChangePasswordAsync(token, new CloudChangePasswordRequest { CurrentPassword = currentPassword, NewPassword = newPassword }, ct), ct);
    }

    public async Task<CloudApiResult<CloudVoid>> ChangeUsernameAsync(string username, CancellationToken ct)
    {
        var accountId = ActiveAccountId;
        if (accountId is null)
        {
            return CloudApiResult<CloudVoid>.Fail(401, "no_session", "Not logged in.");
        }
        var result = await WithAuthAsync(accountId, token =>
            _api.ChangeUsernameAsync(token, new CloudChangeUsernameRequest { Username = username }, ct), ct).ConfigureAwait(false);
        if (result.Success)
        {
            _store.Update(s =>
            {
                var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
                rec?.Username = username;
            });
        }
        return result;
    }

    public async Task<CloudApiResult<CloudVoid>> SetPrivateAsync(bool isPrivate, CancellationToken ct)
    {
        var accountId = ActiveAccountId;
        if (accountId is null)
        {
            return CloudApiResult<CloudVoid>.Fail(401, "no_session", "Not logged in.");
        }
        var result = await WithAuthAsync(accountId, token => _api.SetPrivateAsync(token, isPrivate, ct), ct).ConfigureAwait(false);
        if (result.Success)
        {
            _store.Update(s =>
            {
                var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
                rec?.IsPrivate = isPrivate;
            });
        }
        return result;
    }

    public async Task<CloudApiResult<CloudAvatarDto>> UploadAvatarAsync(byte[] bytes, string contentType, CancellationToken ct)
    {
        var accountId = ActiveAccountId;
        if (accountId is null)
        {
            return CloudApiResult<CloudAvatarDto>.Fail(401, "no_session", "Not logged in.");
        }
        var result = await WithAuthAsync(accountId, token => _api.UploadAvatarAsync(token, bytes, contentType, ct), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return result.Offline
                ? CloudApiResult<CloudAvatarDto>.NetworkError(result.ErrorMessage ?? "")
                : CloudApiResult<CloudAvatarDto>.Fail(result.StatusCode, result.ErrorCode, result.ErrorMessage);
        }
        if (result.Value is null)
        {
            // ToResultAsync only builds a Success result from a non-null
            // parsed body, so this is unreachable through the real HTTP
            // client - kept as a real failure (not a 200 with an empty
            // avatar) in case a future caller constructs the result directly.
            return CloudApiResult<CloudAvatarDto>.Fail(result.StatusCode, "invalid_response", "Empty or malformed response body.");
        }

        var avatar = new CloudAvatarDto { Large = result.Value.Large, Small = result.Value.Small };
        _store.Update(s =>
        {
            var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
            if (rec is not null)
            {
                rec.AvatarLarge = avatar.Large;
                rec.AvatarSmall = avatar.Small;
            }
        });
        return CloudApiResult<CloudAvatarDto>.Ok(avatar, result.StatusCode);
    }

    // ── recovery (device-code style polling) ────────────────────────────

    public async Task<CloudActionResult> StartRecoveryAsync(string email, CancellationToken ct)
    {
        var grantId = Guid.NewGuid().ToString("N");
        var deviceSecret = GenerateDeviceSecret();

        // pollToken is read from the CTS created for THIS call, inside the same
        // lock, before any await - a concurrent second StartRecoveryAsync call
        // (double-click resend) must not be able to swap _recoveryCts out from
        // under this one between the await below and a later unguarded read.
        CancellationTokenSource? previousCts;
        CancellationToken pollToken;
        lock (_recoveryLock)
        {
            previousCts = _recoveryCts;
            _recoveryCts = new CancellationTokenSource();
            pollToken = _recoveryCts.Token;
            _recoveryGrantId = grantId;
            _recoveryStatus = "pending";
        }
        previousCts?.Cancel();
        previousCts?.Dispose();

        var result = await _api.RecoveryStartAsync(
            new CloudRecoveryStartRequest { Email = email, GrantId = grantId, DeviceSecret = deviceSecret }, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            // No link was sent (throttled, offline), so the flow is back where it
            // started; reporting "expired" here is what put a "link expired" page
            // in front of users who never got a link.
            SetRecoveryStatus(grantId, "idle");
            return CloudActionResult.FromError(result);
        }

        _ = Task.Run(() => PollRecoveryLoopAsync(grantId, deviceSecret, pollToken), CancellationToken.None);
        return CloudActionResult.Ok();
    }

    public CloudRecoveryStatusSnapshot GetRecoveryStatus()
    {
        lock (_recoveryLock)
        {
            var fresh = _recoveryFreshAccountId is not null
                && _recoveryFreshAccountId == ActiveAccountId
                && _clock.GetUtcNow() < _recoveryFreshUntil;
            return new CloudRecoveryStatusSnapshot(_recoveryStatus, fresh);
        }
    }

    private async Task PollRecoveryLoopAsync(string grantId, string deviceSecret, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow().Add(RecoveryPollCap);
        try
        {
            using var timer = new PeriodicTimer(RecoveryPollInterval, _clock);
            while (!ct.IsCancellationRequested && _clock.GetUtcNow() < deadline)
            {
                CloudApiResult<CloudRecoveryPollResponse>? result = null;
                try
                {
                    result = await _api.RecoveryPollAsync(
                        new CloudRecoveryPollRequest { GrantId = grantId, DeviceSecret = deviceSecret }, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[cloud] recovery poll failed, retrying: {ex.Message}");
                }

                if (result is { Success: true, Value: { } poll })
                {
                    if (!string.IsNullOrEmpty(poll.AccessToken) && !string.IsNullOrEmpty(poll.RefreshToken) && poll.Account is not null)
                    {
                        HandleRecoveryApproved(grantId, poll);
                        return;
                    }
                    if (string.Equals(poll.Status, "expired", StringComparison.OrdinalIgnoreCase))
                    {
                        SetRecoveryStatus(grantId, "expired");
                        return;
                    }
                }

                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            SetRecoveryStatus(grantId, "expired");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer StartRecoveryAsync call, or shutdown. Not an error.
        }
    }

    private void HandleRecoveryApproved(string grantId, CloudRecoveryPollResponse poll)
    {
        // ApplySession's activation event is pushed to clients, which then read
        // the recovery-fresh window, so the window is written before it fires.
        lock (_recoveryLock)
        {
            if (_recoveryGrantId == grantId)
            {
                _recoveryStatus = "approved";
            }
            _recoveryFreshAccountId = poll.Account!.Id;
            _recoveryFreshUntil = _clock.GetUtcNow().Add(RecoveryFreshWindow);
        }

        ApplySession(new CloudAuthSession
        {
            AccessToken = poll.AccessToken!,
            RefreshToken = poll.RefreshToken!,
            Account = poll.Account,
        });
    }

    private void SetRecoveryStatus(string grantId, string status)
    {
        lock (_recoveryLock)
        {
            if (_recoveryGrantId == grantId)
            {
                _recoveryStatus = status;
            }
        }
    }

    // ── shared helpers ───────────────────────────────────────────────────

    private static void ApplyAccountFields(CloudAccountRecord rec, CloudAccountDto dto)
    {
        rec.Email = dto.Email;
        rec.Username = dto.Username;
        rec.IsPrivate = dto.IsPrivate;
        rec.EmailVerified = dto.EmailVerified;
        rec.AvatarLarge = dto.Avatar?.Large ?? "";
        rec.AvatarSmall = dto.Avatar?.Small ?? "";
        if (string.IsNullOrEmpty(rec.CreatedAt))
        {
            rec.CreatedAt = dto.CreatedAt;
        }
    }

    private string ResolveDeviceName()
    {
        var host = _store.Load().HostDisplayName;
        if (!string.IsNullOrWhiteSpace(host))
        {
            return host;
        }
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "Nexus PC";
        }
    }

    private static string GenerateDeviceSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>Marks the last successful sync time shown in the account switcher (ListAccounts). Called by CloudProfileSyncService after each completed sync pass.</summary>
    public void MarkSynced(string accountId, DateTimeOffset when)
    {
        _store.Update(s =>
        {
            var rec = s.Auth?.CloudAccounts.FirstOrDefault(a => a.AccountId == accountId);
            rec?.LastSyncAt = when.ToString("o");
        });
    }

    /// <summary>
    /// Stable per-machine id for authenticated cloud calls (device
    /// registration, profile sync attribution). Reads/writes the same
    /// Telemetry.InstallId field <see cref="InstallIdentity.Resolve"/> uses,
    /// but without the anonymous-data opt-out gate: an authenticated cloud
    /// account attaches a device via an explicit opt-in (login), a separate
    /// consent from the anonymous fleet heartbeat. See
    /// plans/account-system.md "installId privacy invariant". Shared by
    /// CloudDeviceReporter and CloudProfileSyncService.
    /// </summary>
    internal string ResolveStableInstallId()
    {
        var id = _store.Load().Telemetry.InstallId;
        if (!string.IsNullOrEmpty(id))
        {
            return id;
        }
        var generated = Guid.NewGuid().ToString("N");
        _store.Update(s => s.Telemetry.InstallId = generated);
        return generated;
    }
}

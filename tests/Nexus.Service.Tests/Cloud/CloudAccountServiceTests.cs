using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

public sealed class CloudAccountServiceTests
{
    private static CloudAccountDto Account(string id, string username = "nicola") => new()
    {
        Id = id,
        Email = "nicola@example.com",
        Username = username,
        EmailVerified = true,
        IsPrivate = false,
        CreatedAt = "2026-01-01T00:00:00Z",
    };

    private static (CloudAccountService svc, FakeCloudApiClient api, InMemoryConfigStore store) Make()
    {
        var api = new FakeCloudApiClient();
        var store = new InMemoryConfigStore();
        var svc = new CloudAccountService(api, store, new ManualTimeProvider(DateTimeOffset.UtcNow));
        return (svc, api, store);
    }

    [Fact]
    public async Task LoginAsync_stores_account_and_sets_active()
    {
        var (svc, api, store) = Make();
        api.OnLogin = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            Account = Account("acct-1"),
        });

        var (result, accountId) = await svc.LoginAsync("nicola@example.com", "password1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("acct-1", accountId);
        Assert.Equal("acct-1", svc.ActiveAccountId);
        var rec = Assert.Single(store.Load().Auth!.CloudAccounts);
        Assert.Equal("refresh-1", rec.RefreshToken);
        Assert.Equal("nicola", rec.Username);
    }

    [Fact]
    public async Task LoginAsync_surfaces_the_cloud_error_code()
    {
        var (svc, api, _) = Make();
        api.OnLogin = _ => CloudApiResult<CloudAuthSession>.Fail(403, "email_unverified", "Verify your email first.");

        var (result, accountId) = await svc.LoginAsync("nicola@example.com", "password1", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(accountId);
        Assert.Equal("email_unverified", result.ErrorCode);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task First_login_fires_activated_only_second_account_fires_switching_then_activated()
    {
        var (svc, api, _) = Make();
        var events = new List<string>();
        svc.OnAccountActivated += id => events.Add($"activated:{id}");
        svc.OnAccountSwitching += (from, to) => events.Add($"switching:{from}->{to}");

        api.OnLogin = req => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "a1",
            RefreshToken = "r1",
            Account = Account("acct-A"),
        });
        await svc.LoginAsync("a@example.com", "pw", CancellationToken.None);
        Assert.Equal(new[] { "activated:acct-A" }, events);

        events.Clear();
        api.OnLogin = req => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "a2",
            RefreshToken = "r2",
            Account = Account("acct-B"),
        });
        await svc.LoginAsync("b@example.com", "pw", CancellationToken.None);

        Assert.Equal(new[] { "switching:acct-A->acct-B", "activated:acct-B" }, events);
    }

    [Fact]
    public async Task RefreshAsync_persists_rotated_token_then_caches_the_new_access_token()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "old-refresh" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });

        api.OnRefresh = token =>
        {
            Assert.Equal("old-refresh", token);
            return CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
            {
                AccessToken = "new-access",
                RefreshToken = "new-refresh",
                Account = Account("acct-1"),
            });
        };

        var token = await svc.RefreshAsync("acct-1", CancellationToken.None);

        Assert.Equal("new-access", token);
        // The rotated refresh token is durable (in the store) - the sole source
        // of truth a restart would read, independent of the in-memory access-
        // token cache this same call also populated.
        Assert.Equal("new-refresh", store.Load().Auth!.CloudAccounts[0].RefreshToken);

        // A second GetValidAccessTokenAsync call must hit the in-memory cache,
        // not the network - proving the cache was populated (after the persist
        // above, per source order in CloudAccountService.RefreshAsync).
        var cached = await svc.GetValidAccessTokenAsync("acct-1", CancellationToken.None);
        Assert.Equal("new-access", cached);
        Assert.Equal(1, api.RefreshCalls);
    }

    [Fact]
    public async Task RefreshAsync_rotated_refresh_token_alone_resumes_the_session_after_a_restart()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "old-refresh" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        api.OnRefresh = token => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-after-first-refresh",
            RefreshToken = "rotated-refresh",
            Account = Account("acct-1"),
        });
        await svc.RefreshAsync("acct-1", CancellationToken.None);

        // Fresh CloudAccountService over the SAME store - simulates a process
        // restart, so the first instance's in-memory access-token cache is gone
        // and only the persisted (rotated) refresh token remains.
        var restarted = new CloudAccountService(api, store, new ManualTimeProvider(DateTimeOffset.UtcNow));
        api.OnRefresh = token =>
        {
            Assert.Equal("rotated-refresh", token); // must use the ROTATED token, not the original
            return CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
            {
                AccessToken = "access-after-restart",
                RefreshToken = "rotated-again",
                Account = Account("acct-1"),
            });
        };

        var token = await restarted.GetValidAccessTokenAsync("acct-1", CancellationToken.None);
        Assert.Equal("access-after-restart", token);
    }

    [Fact]
    public async Task RefreshAsync_dead_refresh_token_drops_the_local_session()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "reused-token" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Fail(401, "invalid_refresh", "Token reuse detected.");

        var loggedOut = new List<string>();
        svc.OnAccountLoggedOut += id => loggedOut.Add(id);

        var token = await svc.RefreshAsync("acct-1", CancellationToken.None);

        Assert.Null(token);
        Assert.Empty(store.Load().Auth!.CloudAccounts);
        Assert.Null(store.Load().Auth!.ActiveCloudAccountId);
        Assert.Equal(new[] { "acct-1" }, loggedOut);
    }

    [Fact]
    public async Task RefreshAsync_403_drops_the_local_session()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "revoked-token" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Fail(403, "session_revoked", "Password changed.");

        var token = await svc.RefreshAsync("acct-1", CancellationToken.None);

        Assert.Null(token);
        Assert.Empty(store.Load().Auth!.CloudAccounts);
    }

    [Fact]
    public async Task RefreshAsync_transient_server_error_keeps_the_session_for_a_retry()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        // A 502/503 blip is not the server saying the refresh token is dead -
        // the previous behavior (drop on any non-offline failure) logged every
        // machine out on a transient outage.
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Fail(502, "bad_gateway", "Upstream unavailable.");

        var token = await svc.RefreshAsync("acct-1", CancellationToken.None);

        Assert.Null(token);
        Assert.Single(store.Load().Auth!.CloudAccounts);
        Assert.Equal("acct-1", store.Load().Auth!.ActiveCloudAccountId);
    }

    [Fact]
    public async Task RefreshAsync_malformed_success_body_keeps_the_session_for_a_retry()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        // Success=true (StatusCode 200) but an empty access token - a
        // malformed response, not a rejection. Must not be treated the same
        // as a definitive 401/403.
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "",
            RefreshToken = "refresh-1",
            Account = Account("acct-1"),
        });

        var token = await svc.RefreshAsync("acct-1", CancellationToken.None);

        Assert.Null(token);
        Assert.Single(store.Load().Auth!.CloudAccounts);
    }

    [Fact]
    public async Task RefreshAsync_offline_keeps_the_session_for_a_retry()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.NetworkError("dns failure");

        var token = await svc.RefreshAsync("acct-1", CancellationToken.None);

        Assert.Null(token);
        // Offline is not "the server rejected us" - the session stays local so
        // the next tick can retry once connectivity returns.
        Assert.Single(store.Load().Auth!.CloudAccounts);
    }

    [Fact]
    public async Task LogoutAsync_removes_the_account_and_calls_server_logout()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });

        string? loggedOutRefreshToken = null;
        api.OnLogout = rt => { loggedOutRefreshToken = rt; return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance); };

        await svc.LogoutAsync("acct-1", CancellationToken.None);

        Assert.Empty(store.Load().Auth!.CloudAccounts);
        Assert.Null(svc.ActiveAccountId);
        Assert.Equal("refresh-1", loggedOutRefreshToken);
    }

    [Fact]
    public void Activate_switches_between_two_stored_accounts()
    {
        var (svc, _, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-A", RefreshToken = "r-a" });
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-B", RefreshToken = "r-b" });
            s.Auth.ActiveCloudAccountId = "acct-A";
        });

        var events = new List<(string from, string to)>();
        svc.OnAccountSwitching += (from, to) => events.Add((from, to));

        var result = svc.Activate("acct-B");

        Assert.True(result.Success);
        Assert.Equal("acct-B", svc.ActiveAccountId);
        Assert.Equal(new[] { ("acct-A", "acct-B") }, events);
    }

    [Fact]
    public void Activate_unknown_account_fails_without_changing_active()
    {
        var (svc, _, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-A", RefreshToken = "r-a" });
            s.Auth.ActiveCloudAccountId = "acct-A";
        });

        var result = svc.Activate("does-not-exist");

        Assert.False(result.Success);
        Assert.Equal("acct-A", svc.ActiveAccountId);
    }

    [Fact]
    public async Task UploadAvatarAsync_maps_top_level_wire_fields_and_persists_urls()
    {
        var (svc, api, store) = Make();
        api.OnLogin = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            Account = Account("acct-1"),
        });
        await svc.LoginAsync("nicola@example.com", "password1", CancellationToken.None);

        // nexus-api's POST /account/avatar returns {large, small} top-level,
        // not nested under an "avatar" key - this is the real wire shape.
        api.OnUploadAvatar = (_, _, _) => CloudApiResult<CloudAvatarUploadResponse>.Ok(new CloudAvatarUploadResponse
        {
            Large = "https://usercontent.hellonexus.com/avatars/acct-1/1-large.webp",
            Small = "https://usercontent.hellonexus.com/avatars/acct-1/1-small.webp",
        });

        var result = await svc.UploadAvatarAsync(new byte[] { 1, 2, 3 }, "image/png", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://usercontent.hellonexus.com/avatars/acct-1/1-large.webp", result.Value!.Large);
        Assert.Equal("https://usercontent.hellonexus.com/avatars/acct-1/1-small.webp", result.Value!.Small);

        var rec = Assert.Single(store.Load().Auth!.CloudAccounts);
        Assert.Equal("https://usercontent.hellonexus.com/avatars/acct-1/1-large.webp", rec.AvatarLarge);
        Assert.Equal("https://usercontent.hellonexus.com/avatars/acct-1/1-small.webp", rec.AvatarSmall);
    }

    [Fact]
    public async Task UploadAvatarAsync_malformed_success_body_is_a_real_failure_not_an_empty_200_fail()
    {
        var (svc, api, _) = Make();
        api.OnLogin = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            Account = Account("acct-1"),
        });
        await svc.LoginAsync("nicola@example.com", "password1", CancellationToken.None);

        // The real HTTP client's ToResultAsync never builds Success=true with
        // a null Value - but the type allows it via a direct object
        // initializer, so this must not surface as a 200 with empty error
        // fields.
        api.OnUploadAvatar = (_, _, _) => new CloudApiResult<CloudAvatarUploadResponse> { Success = true, StatusCode = 200 };

        var result = await svc.UploadAvatarAsync(new byte[] { 1, 2, 3 }, "image/png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task RefreshAsync_serializes_concurrent_calls_for_the_same_account()
    {
        var (svc, api, store) = Make();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "token-gen-0" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var seenTokens = new List<string>();
        var generation = 0;

        api.OnRefresh = token =>
        {
            lock (seenTokens) { seenTokens.Add(token); }
            // Block the first call until manually released, so a concurrent
            // second call can prove it actually waits for the gate instead of
            // racing in with the same pre-rotation refresh token (which would
            // trip the server's reuse-detection and revoke the whole family).
            if (token == "token-gen-0")
            {
                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
            var gen = Interlocked.Increment(ref generation);
            return CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
            {
                AccessToken = "access-" + gen,
                RefreshToken = "token-gen-" + gen,
                Account = Account("acct-1"),
            });
        };

        // Task.Run so the blocking OnRefresh call above runs on a worker
        // thread, not the test's own thread - otherwise the block would
        // deadlock the only thread that could later call releaseFirst.SetResult().
        var first = Task.Run(() => svc.RefreshAsync("acct-1", CancellationToken.None));
        // Deterministic: waits for the signal the first call fires the moment
        // it enters OnRefresh (and starts blocking), rather than a fixed delay
        // that only probably gives it enough time.
        await firstEntered.Task;

        var second = Task.Run(() => svc.RefreshAsync("acct-1", CancellationToken.None));
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        // second cannot have acquired the per-account gate (and so cannot have
        // called OnRefresh) until first's whole gated critical section -
        // including persisting the rotated token - completed, since first held
        // the gate the entire time it was blocked above. The token sequence is
        // the proof: second read "token-gen-1" (the ROTATED token), not
        // "token-gen-0" again, so the two calls never presented the same
        // pre-rotation token concurrently.
        Assert.Equal(2, seenTokens.Count);
        Assert.Equal("token-gen-0", seenTokens[0]);
        Assert.Equal("token-gen-1", seenTokens[1]);
    }

    [Fact]
    public async Task StartRecoveryAsync_refused_by_the_cloud_reports_idle_not_an_expired_link()
    {
        var (svc, api, _) = Make();
        api.OnRecoveryStart = _ => CloudApiResult<CloudRecoveryStartResponse>.Fail(429, "too_many_requests", "Slow down.");

        var result = await svc.StartRecoveryAsync("nicola@example.com", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("idle", svc.GetRecoveryStatus().Status);
    }

    [Fact]
    public async Task StartRecoveryAsync_hands_back_the_code_the_cloud_minted()
    {
        var (svc, api, _) = Make();
        CloudRecoveryStartRequest? sent = null;
        api.OnRecoveryStart = body =>
        {
            sent = body;
            return CloudApiResult<CloudRecoveryStartResponse>.Ok(new CloudRecoveryStartResponse { Code = "ABC-DEF" });
        };

        var result = await svc.StartRecoveryAsync("nicola@example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("nicola@example.com", sent!.Email);
        Assert.Equal("ABC-DEF", result.Value);
    }

    [Fact]
    public async Task StartRecoveryAsync_succeeds_even_if_the_cloud_returns_no_code()
    {
        var (svc, api, _) = Make();
        api.OnRecoveryStart = _ => CloudApiResult<CloudRecoveryStartResponse>.Ok(new CloudRecoveryStartResponse());

        var result = await svc.StartRecoveryAsync("nicola@example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }
}

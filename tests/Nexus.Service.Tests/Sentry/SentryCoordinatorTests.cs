using System.Text.Json;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Sentry;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Power;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Cloud;
using Xunit;
using SentryCoordinator = Nexus.Service.Sentry.SentryCoordinator;

namespace Nexus.Service.Tests.Sentry;

/// <summary>Never the real lock: a real call would lock the machine running the tests.</summary>
public sealed class FakePowerProvider : ISystemPowerProvider
{
    public int LockCalls;
    public bool LockResult = true;

    /// <summary>Runs inside Lock(), to model the session lock arriving (or not).</summary>
    public Action? OnLock;

    public bool Lock()
    {
        LockCalls++;
        OnLock?.Invoke();
        return LockResult;
    }

    public bool Sleep() => false;
    public bool Shutdown() => false;
    public bool Restart() => false;
    public bool Logout() => false;
}

public sealed class SentryCoordinatorTests
{
    private const long Hour = 3_600_000;
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryConfigStore _store = new();
    private readonly FakeCloudApiClient _cloud = new();
    private readonly FakePowerProvider _power = new();
    private readonly PanelPhonePairingService _pairing;
    private readonly List<bool> _watch = new();
    private DateTimeOffset _now = Start;
    private readonly List<SentryPushRequest> _sent = new();
    private Func<SentryPushRequest, string> _respond;

    public SentryCoordinatorTests()
    {
        _pairing = new PanelPhonePairingService(_store, new MultiplexHub());
        _respond = req => Response(req, "sent");
        _cloud.OnPostRaw = (path, body, token) =>
        {
            Assert.Equal("/push/sentry", path);
            Assert.Null(token);
            var request = JsonSerializer.Deserialize(body, AppJsonContext.Default.SentryPushRequest)!;
            _sent.Add(request);
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = _respond(request) });
        };
        AddPhone("phone-1", "tok-1");
    }

    private bool? _realLockState = false;

    private SentryCoordinator Create(bool supported = true)
    {
        var coordinator = new SentryCoordinator(
            _store, _pairing, _cloud, _power, lockListener: null, clock: () => _now,
            readLockState: () => _realLockState, lockWait: TimeSpan.FromMilliseconds(100));
        if (supported)
        {
            coordinator.LockInputWatch = _watch.Add;
        }
        return coordinator;
    }

    private static SentryCoordinator.ArmOutcome Arm(SentryCoordinator coordinator, bool lockFirst) =>
        coordinator.ArmAsync(lockFirst).GetAwaiter().GetResult();

    private SentryCoordinator Armed()
    {
        var coordinator = Create();
        coordinator.OnLockChanged(true);
        Assert.Equal(SentryCoordinator.ArmOutcome.Armed, Arm(coordinator, lockFirst: false));
        return coordinator;
    }

    private void AddPhone(string id, string token, string title = "Sentry", string body = "Someone is typing on {pc}")
    {
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            s.Auth.PanelPhoneSessions.Add(new PanelPhoneSessionToken
            {
                Id = id,
                PushTarget = new PanelPhonePushTarget
                {
                    Platform = "ios",
                    Token = token,
                    Environment = "production",
                    Title = title,
                    Body = body,
                },
            });
        });
    }

    private static string Response(SentryPushRequest request, string status) =>
        JsonSerializer.Serialize(
            new SentryPushResponse
            {
                Results = request.Targets.Select(t => new SentryPushResult { Token = t.Token, Status = status }).ToList(),
            },
            AppJsonContext.Default.SentryPushResponse);

    private void Advance(TimeSpan by) => _now += by;

    [Fact]
    public async Task Input_inside_the_arm_grace_never_alerts()
    {
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(9));

        await coordinator.OnInputAsync();

        Assert.Empty(_sent);
        Advance(TimeSpan.FromSeconds(2));
        await coordinator.OnInputAsync();
        Assert.Single(_sent);
    }

    [Fact]
    public async Task Alert_fills_pc_with_the_machine_name_and_records_the_time()
    {
        _store.Update(s => s.HostDisplayName = "Studio PC");
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();

        var target = Assert.Single(Assert.Single(_sent).Targets);
        Assert.Equal("Someone is typing on Studio PC", target.Body);
        Assert.Equal("Sentry", target.Title);
        Assert.Equal("tok-1", target.Token);
        Assert.Equal(_now.ToUnixTimeMilliseconds(), _store.Load().Sentry.LastAlertAt);
        Assert.Equal(_now.ToUnixTimeMilliseconds(), coordinator.GetStatus().LastAlertAt);
    }

    [Fact]
    public async Task One_alert_per_hour()
    {
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));
        await coordinator.OnInputAsync();

        Advance(TimeSpan.FromMinutes(59));
        await coordinator.OnInputAsync();
        Assert.Single(_sent);

        Advance(TimeSpan.FromMinutes(1));
        await coordinator.OnInputAsync();
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task Cooldown_survives_a_restart()
    {
        var first = Armed();
        Advance(TimeSpan.FromSeconds(11));
        await first.OnInputAsync();

        var restarted = Create();
        Advance(TimeSpan.FromSeconds(11));
        await restarted.OnInputAsync();

        Assert.Single(_sent);
    }

    [Fact]
    public async Task Rate_limited_counts_as_an_alert()
    {
        _respond = req => Response(req, "rate_limited");
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();

        Assert.NotNull(_store.Load().Sentry.LastAlertAt);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("error")]
    public async Task A_failed_send_keeps_the_cooldown_clear_and_backs_off(string status)
    {
        _respond = req => Response(req, status);
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();
        Assert.Null(_store.Load().Sentry.LastAlertAt);

        Advance(TimeSpan.FromSeconds(5));
        await coordinator.OnInputAsync();
        Assert.Single(_sent);

        Advance(SentryCoordinator.RetryBackoff);
        await coordinator.OnInputAsync();
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task An_unreachable_cloud_does_not_set_the_cooldown()
    {
        _cloud.OnPostRaw = (_, _, _) => CloudApiResult<CloudRawResponse>.NetworkError("offline");
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();

        Assert.Null(_store.Load().Sentry.LastAlertAt);
    }

    [Fact]
    public async Task No_registered_phone_sends_nothing_and_keeps_the_cooldown_clear()
    {
        _store.Update(s => s.Auth!.PanelPhoneSessions.Clear());
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();

        Assert.Equal(0, _cloud.PostRawCalls);
        Assert.Null(_store.Load().Sentry.LastAlertAt);
        Assert.Equal(0, coordinator.GetStatus().AlertPhones);
    }

    [Fact]
    public async Task Invalid_targets_are_removed_and_the_rest_still_alert()
    {
        AddPhone("phone-2", "tok-2");
        _respond = req => JsonSerializer.Serialize(
            new SentryPushResponse
            {
                Results = req.Targets
                    .Select(t => new SentryPushResult { Token = t.Token, Status = t.Token == "tok-1" ? "invalid" : "sent" })
                    .ToList(),
            },
            AppJsonContext.Default.SentryPushResponse);
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();

        Assert.Equal(new[] { "tok-2" }, _pairing.GetPushTargets().Select(t => t.Token));
        Assert.NotNull(_store.Load().Sentry.LastAlertAt);
        Assert.Equal(1, coordinator.GetStatus().AlertPhones);
    }

    [Fact]
    public async Task Only_invalid_targets_leave_no_alert_and_no_phones()
    {
        _respond = req => Response(req, "invalid");
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();

        Assert.Empty(_pairing.GetPushTargets());
        Assert.Null(_store.Load().Sentry.LastAlertAt);
    }

    [Fact]
    public async Task The_same_token_on_two_sessions_is_sent_once()
    {
        AddPhone("phone-2", "tok-1");
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        await coordinator.OnInputAsync();

        Assert.Single(Assert.Single(_sent).Targets);
    }

    [Fact]
    public async Task Disarmed_or_unlocked_input_is_ignored()
    {
        var coordinator = Create();
        Advance(TimeSpan.FromMinutes(1));
        await coordinator.OnInputAsync();
        Assert.Empty(_sent);

        coordinator.OnLockChanged(true);
        await coordinator.OnInputAsync();
        Assert.Empty(_sent);
    }

    [Fact]
    public void Unlock_disarms_persists_and_releases_the_watch()
    {
        var coordinator = Armed();
        Assert.True(_store.Load().Sentry.Armed);
        Assert.Equal(new[] { true }, _watch);

        coordinator.OnLockChanged(false);

        Assert.False(coordinator.GetStatus().Armed);
        Assert.False(coordinator.GetStatus().Locked);
        Assert.False(_store.Load().Sentry.Armed);
        Assert.Equal(new[] { true, false }, _watch);
    }

    [Fact]
    public async Task Input_after_an_unlock_is_ignored_even_past_the_grace()
    {
        var coordinator = Armed();
        coordinator.OnLockChanged(false);
        Advance(TimeSpan.FromMinutes(1));

        await coordinator.OnInputAsync();

        Assert.Empty(_sent);
    }

    [Fact]
    public void Arming_without_a_lock_while_unlocked_is_not_locked_and_never_locks()
    {
        var coordinator = Create();

        Assert.Equal(SentryCoordinator.ArmOutcome.NotLocked, Arm(coordinator, lockFirst: false));

        Assert.Equal(0, _power.LockCalls);
        Assert.False(coordinator.GetStatus().Armed);
        Assert.Empty(_watch);
    }

    [Fact]
    public void Arming_with_a_lock_locks_once_then_arms_after_the_lock_is_seen()
    {
        var coordinator = Create();
        _power.OnLock = () => coordinator.OnLockChanged(true);

        Assert.Equal(SentryCoordinator.ArmOutcome.Armed, Arm(coordinator, lockFirst: true));

        Assert.Equal(1, _power.LockCalls);
        var status = coordinator.GetStatus();
        Assert.True(status.Armed);
        Assert.True(status.Locked);
        Assert.Equal(new[] { true }, _watch);
    }

    [Fact]
    public void A_lock_that_is_never_observed_does_not_arm_or_mark_locked()
    {
        var coordinator = Create();

        Assert.Equal(SentryCoordinator.ArmOutcome.LockNotConfirmed, Arm(coordinator, lockFirst: true));

        Assert.Equal(1, _power.LockCalls);
        var status = coordinator.GetStatus();
        Assert.False(status.Armed);
        Assert.False(status.Locked);
        Assert.False(_store.Load().Sentry.Armed);
        Assert.Empty(_watch);
    }

    [Fact]
    public async Task Two_concurrent_lock_arms_both_succeed_on_one_observed_lock()
    {
        var issued = 0;
        _power.OnLock = () => Interlocked.Increment(ref issued);
        var wait = new SentryCoordinator(
            _store, _pairing, _cloud, _power, lockListener: null, clock: () => _now,
            readLockState: () => false, lockWait: TimeSpan.FromSeconds(5));
        wait.LockInputWatch = _watch.Add;

        var first = wait.ArmAsync(lockFirst: true);
        var second = wait.ArmAsync(lockFirst: true);
        Assert.Equal(2, Volatile.Read(ref issued));
        wait.OnLockChanged(true);

        Assert.Equal(SentryCoordinator.ArmOutcome.Armed, await first);
        Assert.Equal(SentryCoordinator.ArmOutcome.Armed, await second);
    }

    [Fact]
    public async Task A_late_lock_after_the_wait_does_not_arm()
    {
        var coordinator = Create();
        Assert.Equal(SentryCoordinator.ArmOutcome.LockNotConfirmed, await coordinator.ArmAsync(lockFirst: true));

        coordinator.OnLockChanged(true);

        Assert.False(coordinator.GetStatus().Armed);
        Assert.Empty(_watch);
    }

    [Fact]
    public void Arming_with_a_lock_on_an_already_locked_pc_does_not_wait()
    {
        var coordinator = Create();
        coordinator.OnLockChanged(true);

        Assert.Equal(SentryCoordinator.ArmOutcome.Armed, Arm(coordinator, lockFirst: true));
    }

    [Fact]
    public void A_failed_lock_does_not_arm()
    {
        _power.LockResult = false;
        var coordinator = Create();

        Assert.Equal(SentryCoordinator.ArmOutcome.LockFailed, Arm(coordinator, lockFirst: true));

        Assert.False(coordinator.GetStatus().Armed);
        Assert.Empty(_watch);
    }

    [Fact]
    public void The_phone_path_never_marks_the_pc_locked()
    {
        var coordinator = Create();

        Assert.Equal(SentryCoordinator.ArmOutcome.NotLocked, Arm(coordinator, lockFirst: false));

        Assert.False(coordinator.GetStatus().Locked);
    }

    [Fact]
    public void Logon_disarms_like_an_unlock()
    {
        var coordinator = Armed();

        coordinator.OnSessionLogon();

        Assert.False(coordinator.GetStatus().Armed);
        Assert.False(coordinator.GetStatus().Locked);
        Assert.False(_store.Load().Sentry.Armed);
    }

    [Fact]
    public void A_restart_on_a_locked_pc_reports_locked_so_the_phone_can_arm()
    {
        _realLockState = true;

        var coordinator = Create();

        Assert.True(coordinator.GetStatus().Locked);
        Assert.Equal(SentryCoordinator.ArmOutcome.Armed, Arm(coordinator, lockFirst: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_persisted_armed_state_is_dropped_unless_the_session_is_verifiably_locked(bool? state)
    {
        _store.Update(s => s.Sentry.Armed = true);
        _realLockState = state;

        var coordinator = Create();

        Assert.False(coordinator.GetStatus().Armed);
        Assert.False(_store.Load().Sentry.Armed);
        Assert.Empty(_watch);
    }

    [Fact]
    public void Without_an_input_watch_it_is_unsupported_and_never_locks()
    {
        var coordinator = Create(supported: false);

        Assert.False(coordinator.GetStatus().Supported);
        Assert.Equal(SentryCoordinator.ArmOutcome.Unsupported, Arm(coordinator, lockFirst: true));
        Assert.Equal(0, _power.LockCalls);
    }

    [Fact]
    public void Disarm_is_always_allowed()
    {
        var coordinator = Create();
        coordinator.Disarm();
        Assert.False(coordinator.GetStatus().Armed);

        coordinator.OnLockChanged(true);
        Arm(coordinator, lockFirst: false);
        coordinator.Disarm();
        Assert.False(coordinator.GetStatus().Armed);
        Assert.False(_store.Load().Sentry.Armed);
        // The first, no-op disarm still releases the watch: harmless and idempotent.
        Assert.Equal(new[] { false, true, false }, _watch);
    }

    [Fact]
    public void A_persisted_armed_state_resumes_locked_and_reasserts_the_watch()
    {
        _store.Update(s => s.Sentry.Armed = true);
        _realLockState = true;

        var coordinator = Create();

        var status = coordinator.GetStatus();
        Assert.True(status.Armed);
        Assert.True(status.Locked);
        Assert.Equal(new[] { true }, _watch);
    }

    [Fact]
    public void Status_reports_the_contract_fields()
    {
        var status = Create().GetStatus();

        Assert.True(status.Supported);
        Assert.False(status.Armed);
        Assert.Equal(1, status.AlertPhones);
        Assert.Null(status.LastAlertAt);
        Assert.Equal(3600, status.CooldownSeconds);
    }

    [Fact]
    public void Status_writes_an_explicit_null_for_a_missing_last_alert()
    {
        var json = JsonSerializer.Serialize(Create().GetStatus(), AppJsonContext.Default.SentryStatusResponse);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("lastAlertAt").ValueKind);
        Assert.Equal(3600, doc.RootElement.GetProperty("cooldownSeconds").GetInt32());
    }

    [Theory]
    [InlineData("Someone is typing on {pc}", "Studio", 200, "Someone is typing on Studio")]
    [InlineData("{pc} {pc}", "A", 200, "A A")]
    [InlineData("no placeholder", "Studio", 200, "no placeholder")]
    [InlineData("{pc}", "abcdefghij", 4, "abcd")]
    public void Fill_substitutes_and_truncates(string template, string pc, int max, string expected)
    {
        Assert.Equal(expected, SentryCoordinator.Fill(template, pc, max));
    }

    [Fact]
    public void Fill_never_splits_a_surrogate_pair()
    {
        var result = SentryCoordinator.Fill("{pc}", "ab\U0001F600cd", 3);

        Assert.Equal("ab", result);
    }

    [Fact]
    public async Task Concurrent_inputs_send_one_request()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var calls = 0;
        // The fake client is synchronous, so hold the first send open through a
        // blocking response and prove a second input meanwhile is dropped.
        _cloud.OnPostRaw = (_, body, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            gate.Task.Wait(TimeSpan.FromSeconds(5));
            var request = JsonSerializer.Deserialize(body, AppJsonContext.Default.SentryPushRequest)!;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = Response(request, "sent") });
        };
        var coordinator = Armed();
        Advance(TimeSpan.FromSeconds(11));

        var first = Task.Run(() => coordinator.OnInputAsync());
        await started.Task;
        await coordinator.OnInputAsync();
        gate.SetResult();
        await first;

        Assert.Equal(1, calls);
    }
}

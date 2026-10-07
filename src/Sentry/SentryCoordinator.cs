using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cloud;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Sentry;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Power;
using Nexus.Service.Serialization;

namespace Nexus.Service.Sentry;

/// <summary>
/// While armed on a locked PC, the first input pushes an alert to every paired
/// phone that registered a push target, at most once an hour.
///
/// It reads no input itself. It only consumes the "input happened" event of the
/// lock input watch Nexus already ships (the Windows helper's LockInputPoller,
/// MacLockInputWatch), which carries no key identity. The bootstraps own that
/// watch and OR this class's <see cref="LockInputWatch"/> demand with the lock
/// blackout's, so it runs while either needs it.
/// </summary>
public sealed class SentryCoordinator : IHostedService, IDisposable
{
    /// <summary>Input this soon after arming never alerts: the click that armed it and the walk-away.</summary>
    internal static readonly TimeSpan ArmGrace = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

    /// <summary>Pause after a failed send, so held keys against an unreachable cloud do not become a request per input.</summary>
    internal static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    /// <summary>nexus-api's limits on a push target; the routes validate against the same numbers.</summary>
    internal const int MaxTitleLength = 64;
    internal const int MaxBodyLength = 200;

    /// <summary>How long a lock-then-arm waits to see the session actually lock before giving up.</summary>
    internal static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(10);

    private const int MaxTargets = 10;
    private const string PcPlaceholder = "{pc}";

    private readonly IConfigStore _store;
    private readonly PanelPhonePairingService _pairing;
    private readonly ICloudApiClient _cloud;
    private readonly ISystemPowerProvider _power;
    private readonly SessionLockListener? _lockListener;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<bool?> _readLockState;
    private readonly TimeSpan _lockWait;
    private readonly object _gate = new();

    private Action<bool>? _lockInputWatch;
    private bool _armed;
    private bool _locked;
    private long _armedAtMs;
    private long _retryAtMs;
    private bool _sending;
    private TaskCompletionSource? _lockWaiter;

    public SentryCoordinator(
        IConfigStore store,
        PanelPhonePairingService pairing,
        ICloudApiClient cloud,
        ISystemPowerProvider power,
        SessionLockListener? lockListener = null,
        Func<DateTimeOffset>? clock = null,
        Func<bool?>? readLockState = null,
        TimeSpan? lockWait = null)
    {
        _store = store;
        _pairing = pairing;
        _cloud = cloud;
        _power = power;
        _lockListener = lockListener;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _readLockState = readLockState ?? SentryLockState.Read;
        _lockWait = lockWait ?? DefaultLockWait;

        // The real lock state, read once: a restart on a locked PC must report
        // locked, and a reboot's first sign-in raises a logon, not an unlock, so
        // a persisted armed state is only trusted while the session is verifiably
        // locked. An unreadable state counts as not armed.
        var state = _readLockState();
        _locked = state == true;
        _armed = store.Load().Sentry.Armed && _locked;
        if (store.Load().Sentry.Armed && !_armed)
        {
            store.Update(s => s.Sentry.Armed = false);
        }
        _armedAtMs = NowMs();
        _lockListener?.LockChanged += OnLockChanged;
    }

    /// <summary>
    /// Arms and disarms the lock input watch: the Windows helper's via
    /// TrayBootstrap, MacLockInputWatch via MacAppBootstrap; null on Linux,
    /// which is what makes Sentry unsupported there. Assigning it while a
    /// restored armed state is waiting re-asserts the watch.
    /// </summary>
    public Action<bool>? LockInputWatch
    {
        get => _lockInputWatch;
        set
        {
            _lockInputWatch = value;
            bool resume;
            lock (_gate)
            {
                resume = _armed && _locked;
            }
            if (resume)
            {
                SetWatch(true);
            }
        }
    }

    public bool Supported => _lockInputWatch is not null;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _lockListener?.LockChanged -= OnLockChanged;
    }

    public SentryStatusResponse GetStatus()
    {
        bool armed;
        bool locked;
        lock (_gate)
        {
            armed = _armed;
            locked = _locked;
        }
        return new SentryStatusResponse
        {
            Supported = Supported,
            Armed = armed,
            Locked = locked,
            AlertPhones = _pairing.GetPushTargets().Count,
            LastAlertAt = _store.Load().Sentry.LastAlertAt,
            CooldownSeconds = (int)Cooldown.TotalSeconds,
        };
    }

    public enum ArmOutcome
    {
        Armed,
        Unsupported,
        NotLocked,
        LockFailed,
        LockNotConfirmed,
    }

    /// <summary>
    /// <paramref name="lockFirst"/> locks the PC and then arms (Settings), but
    /// only once the session lock is actually observed; otherwise arms only while
    /// the session is already locked (the phone).
    /// </summary>
    public async Task<ArmOutcome> ArmAsync(bool lockFirst)
    {
        if (!Supported)
        {
            return ArmOutcome.Unsupported;
        }

        if (!lockFirst)
        {
            lock (_gate)
            {
                return _locked ? CommitArm() : ArmOutcome.NotLocked;
            }
        }

        TaskCompletionSource? waiter = null;
        lock (_gate)
        {
            if (!_locked)
            {
                // Registered before the lock is issued so the transition cannot slip
                // past. A concurrent arm shares the in-flight waiter.
                waiter = _lockWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (!_power.Lock())
        {
            return ArmOutcome.LockFailed;
        }

        if (waiter is not null)
        {
            // True only means the request was issued; the session still has to lock.
            var done = await Task.WhenAny(waiter.Task, Task.Delay(_lockWait)).ConfigureAwait(false);
            if (done != waiter.Task)
            {
                ServiceLog.Info("[sentry] lock was not observed in time; not armed");
                return ArmOutcome.LockNotConfirmed;
            }
        }

        lock (_gate)
        {
            return _locked ? CommitArm() : ArmOutcome.LockNotConfirmed;
        }
    }

    /// <summary>
    /// Arms. Caller holds <see cref="_gate"/> and has checked <see cref="_locked"/>;
    /// the persisted write and the watch change stay inside it, so an unlock
    /// racing an arm (it takes the same gate) can never leave an armed state
    /// behind on an unlocked desktop.
    /// </summary>
    private ArmOutcome CommitArm()
    {
        _armed = true;
        _armedAtMs = NowMs();
        _retryAtMs = 0;
        _store.Update(s => s.Sentry.Armed = true);
        SetWatch(true);
        ServiceLog.Info("[sentry] armed");
        return ArmOutcome.Armed;
    }

    public void Disarm()
    {
        lock (_gate)
        {
            DisarmLocked();
        }
    }

    private void DisarmLocked()
    {
        var wasArmed = _armed;
        _armed = false;
        if (_store.Load().Sentry.Armed)
        {
            _store.Update(s => s.Sentry.Armed = false);
        }
        // The blackout may still need the watch; the bootstrap ORs the demands.
        SetWatch(false);
        if (wasArmed)
        {
            ServiceLog.Info("[sentry] disarmed");
        }
    }

    /// <summary>Session lock transition from SessionLockListener. Unlock disarms.</summary>
    internal void OnLockChanged(bool locked)
    {
        lock (_gate)
        {
            _locked = locked;
            if (locked)
            {
                // Completes every arm sharing the waiter; a waiter nobody completes
                // stays for the next arm to reuse, which the same lock satisfies.
                _lockWaiter?.TrySetResult();
                _lockWaiter = null;
            }
            else
            {
                DisarmLocked();
            }
        }
    }

    /// <summary>
    /// A user signed in. After a reboot the first sign-in is a logon, never an
    /// unlock, so it must end the lock the same way.
    /// </summary>
    public void OnSessionLogon() => OnLockChanged(false);

    /// <summary>Input happened at the lock screen, from the shared watch. Never blocks the watch's thread.</summary>
    public void OnLockScreenInput() => _ = Task.Run(() => OnInputAsync());

    internal async Task OnInputAsync()
    {
        var nowMs = NowMs();
        lock (_gate)
        {
            if (!_armed || !_locked || _sending)
            {
                return;
            }
            if (nowMs - _armedAtMs < (long)ArmGrace.TotalMilliseconds || nowMs < _retryAtMs)
            {
                return;
            }
            var last = _store.Load().Sentry.LastAlertAt;
            if (last is not null && nowMs - last.Value < (long)Cooldown.TotalMilliseconds)
            {
                return;
            }
            _sending = true;
        }

        try
        {
            await SendAlertAsync(nowMs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Backoff();
            ServiceLog.Info($"[sentry] alert failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _sending = false;
            }
        }
    }

    private async Task SendAlertAsync(long nowMs)
    {
        var registered = _pairing.GetPushTargets();
        if (registered.Count == 0)
        {
            return;
        }

        var pc = _pairing.MachineName;
        var request = new SentryPushRequest
        {
            Targets = registered
                .Take(MaxTargets)
                .Select(t => new SentryPushTarget
                {
                    Platform = t.Platform,
                    Token = t.Token,
                    Environment = t.Environment,
                    Title = Fill(t.Title, pc, MaxTitleLength),
                    Body = Fill(t.Body, pc, MaxBodyLength),
                })
                .ToList(),
        };

        var json = JsonSerializer.Serialize(request, AppJsonContext.Default.SentryPushRequest);
        var response = await _cloud.PostRawAsync("/push/sentry", json, null, CancellationToken.None).ConfigureAwait(false);
        if (!response.Success || response.StatusCode != 200 || response.Value is null)
        {
            Backoff();
            ServiceLog.Info($"[sentry] push request failed (status {response.StatusCode}, offline {response.Offline})");
            return;
        }

        var parsed = JsonSerializer.Deserialize(response.Value.Body, AppJsonContext.Default.SentryPushResponse);
        if (parsed is null)
        {
            Backoff();
            ServiceLog.Info("[sentry] push response was not readable");
            return;
        }

        var invalid = parsed.Results
            .Where(r => r.Status == "invalid")
            .Select(r => r.Token)
            .ToList();
        if (invalid.Count > 0)
        {
            _pairing.RemovePushTargets(invalid);
        }

        if (parsed.Results.Any(r => r.Status is "sent" or "rate_limited"))
        {
            _store.Update(s => s.Sentry.LastAlertAt = nowMs);
            ServiceLog.Info($"[sentry] alert sent to {parsed.Results.Count(r => r.Status == "sent")} phone(s)");
        }
        else
        {
            Backoff();
            ServiceLog.Info("[sentry] no phone accepted the alert");
        }
    }

    private void Backoff()
    {
        var until = NowMs() + (long)RetryBackoff.TotalMilliseconds;
        lock (_gate)
        {
            _retryAtMs = until;
        }
    }

    internal static string Fill(string template, string pc, int maxLength)
    {
        var text = template.Replace(PcPlaceholder, pc, StringComparison.Ordinal);
        if (text.Length <= maxLength)
        {
            return text;
        }
        // Never cut a surrogate pair in half.
        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut];
    }

    private void SetWatch(bool enabled)
    {
        try { _lockInputWatch?.Invoke(enabled); }
        catch (Exception ex)
        {
            ServiceLog.Info($"[sentry] input watch {(enabled ? "arm" : "disarm")} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private long NowMs() => _clock().ToUnixTimeMilliseconds();
}

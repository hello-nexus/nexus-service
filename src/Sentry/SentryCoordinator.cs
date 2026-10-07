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

    private const int MaxTargets = 10;
    private const int MaxTitleLength = 64;
    private const int MaxBodyLength = 200;
    private const string PcPlaceholder = "{pc}";

    private readonly IConfigStore _store;
    private readonly PanelPhonePairingService _pairing;
    private readonly ICloudApiClient _cloud;
    private readonly ISystemPowerProvider _power;
    private readonly SessionLockListener? _lockListener;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private Action<bool>? _lockInputWatch;
    private bool _armed;
    private bool _locked;
    private long _armedAtMs;
    private long _retryAtMs;
    private bool _sending;

    public SentryCoordinator(
        IConfigStore store,
        PanelPhonePairingService pairing,
        ICloudApiClient cloud,
        ISystemPowerProvider power,
        SessionLockListener? lockListener = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _pairing = pairing;
        _cloud = cloud;
        _power = power;
        _lockListener = lockListener;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        _armed = store.Load().Sentry.Armed;
        // Nothing re-derives lock state at startup (see SessionLockListener), and
        // Armed is only ever saved true while locked with an unlock clearing it.
        // So a restart that finds it set resumes locked; the next unlock disarms.
        _locked = _armed;
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
    }

    /// <summary>
    /// <paramref name="lockFirst"/> locks the PC and then arms (Settings);
    /// otherwise arms only while the session is already locked (the phone).
    /// </summary>
    public ArmOutcome Arm(bool lockFirst)
    {
        if (!Supported)
        {
            return ArmOutcome.Unsupported;
        }

        if (lockFirst)
        {
            if (!_power.Lock())
            {
                return ArmOutcome.LockFailed;
            }
        }
        else
        {
            lock (_gate)
            {
                if (!_locked)
                {
                    return ArmOutcome.NotLocked;
                }
            }
        }

        lock (_gate)
        {
            _armed = true;
            // The lock transition arrives on its own hop shortly; the lock this
            // call just issued is not in doubt, so record it now.
            _locked = true;
            _armedAtMs = NowMs();
            _retryAtMs = 0;
        }
        _store.Update(s => s.Sentry.Armed = true);
        SetWatch(true);
        ServiceLog.Info("[sentry] armed");
        return ArmOutcome.Armed;
    }

    public void Disarm()
    {
        bool wasArmed;
        lock (_gate)
        {
            wasArmed = _armed;
            _armed = false;
        }
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
        }
        if (!locked)
        {
            Disarm();
        }
    }

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

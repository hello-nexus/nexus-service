using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Sentry;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sentry;
using Nexus.Service.Serialization;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>Pushes a Watchlist alert to paired phones; gated by the entry's NotifyMobile and its own per-entry cooldown, not the desktop master switch.</summary>
public sealed class SensorAlertPushSender
{
    private const int MaxTargets = 10;
    private const int MaxEntryLength = 32;
    private static readonly TimeSpan UrgentCooldown = TimeSpan.FromMinutes(5);

    private readonly IConfigStore _store;
    private readonly PanelPhonePairingService _pairing;
    private readonly ICloudApiClient _cloud;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);
    private readonly LogThrottle _warnings = new(TimeSpan.FromMinutes(1));

    public SensorAlertPushSender(IConfigStore store, PanelPhonePairingService pairing, ICloudApiClient cloud, TimeProvider? time = null)
    {
        _store = store;
        _pairing = pairing;
        _cloud = cloud;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gates on the per-entry cooldown, then sends off the caller's thread. The returned task never faults.</summary>
    public Task Raise(string key, DiagnosticsAlertNotice notice)
    {
        try
        {
            var cooldown = TimeSpan.FromMinutes(_store.Load().Diagnostics.Notifications.CooldownMinutes);
            if (notice.Urgent && cooldown > UrgentCooldown)
            {
                cooldown = UrgentCooldown;
            }
            var now = _time.GetUtcNow();
            lock (_lastSent)
            {
                if (_lastSent.TryGetValue(key, out var last) && now - last < cooldown)
                {
                    return Task.CompletedTask;
                }
                _lastSent[key] = now;
            }
            return Task.Run(() => SendAsync(key, notice));
        }
        catch (Exception ex)
        {
            Warn("gate", $"[sensor-alerts] mobile push gate failed: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    private async Task SendAsync(string key, DiagnosticsAlertNotice notice)
    {
        try
        {
            var targets = _pairing.GetPushTargets();
            if (targets.Count == 0)
            {
                return;
            }
            var request = new AlertPushRequest
            {
                Entry = EntryId(key),
                Urgent = notice.Urgent,
                Title = SentryCoordinator.Fill("Sensor alert on {pc}", _pairing.MachineName, SentryCoordinator.MaxTitleLength),
                Body = SentryCoordinator.Truncate(notice.Text, SentryCoordinator.MaxBodyLength),
                Targets = targets
                    .Take(MaxTargets)
                    .Select(t => new AlertPushTarget { Platform = t.Platform, Token = t.Token, Environment = t.Environment })
                    .ToList(),
            };
            var json = JsonSerializer.Serialize(request, AppJsonContext.Default.AlertPushRequest);
            var response = await _cloud.PostRawAsync("/push/alert", json, null, default).ConfigureAwait(false);
            if (!response.Success || response.StatusCode != 200 || response.Value is null)
            {
                Warn("post", $"[sensor-alerts] mobile push failed (status {response.StatusCode}, offline {response.Offline})");
                return;
            }
            var parsed = JsonSerializer.Deserialize(response.Value.Body, AppJsonContext.Default.SentryPushResponse);
            var invalid = parsed?.Results.Where(r => r.Status == "invalid").Select(r => r.Token).ToList();
            if (invalid is { Count: > 0 })
            {
                _pairing.RemovePushTargets(invalid);
            }
        }
        catch (Exception ex)
        {
            Warn("send", $"[sensor-alerts] mobile push failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The rule id is not sent: an opaque, pattern-safe id per entry for the api's own cooldown.</summary>
    internal static string EntryId(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.StartsWith("alert:", StringComparison.Ordinal) ? key[6..] : key)))
            .ToLowerInvariant()[..MaxEntryLength];

    private void Warn(string kind, string message)
    {
        if (_warnings.ShouldLog(kind, _time.GetUtcNow()))
        {
            ServiceLog.Warn(message);
        }
    }
}

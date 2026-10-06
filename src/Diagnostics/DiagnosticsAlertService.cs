using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Diagnostics;

/// <summary>Native-notification request for a diagnostics component that
/// needs attention. Consumed the same way as
/// <see cref="Nexus.Service.Transfer.TransferAttentionNotice"/>: platform
/// bootstraps subscribe and surface a tray balloon / native banner.</summary>
/// <summary>Kind is the component kind ("storage", "gpu", "memory", "system", "cooling") or "thermalGuard" for a thermal guard trip, which is also the diagnostics tab slug the alert deep-links to.</summary>
public sealed record DiagnosticsAlertNotice(string Title, string Text, string Kind);

/// <summary>
/// Polls <see cref="DiagnosticsHealthModel"/> every 5 minutes (first check 2
/// minutes after start) and logs one warning line per "act" component, the
/// first time its id is seen this service run. A component id is never
/// re-alerted within the same run, even if it clears and re-triggers.
///
/// Separately, raises <see cref="AlertNeedsAttention"/> for a native
/// notification per component whose mapped category is enabled under
/// <see cref="DiagnosticsSettings.Notifications"/>, gated by the master
/// enable + that category's toggle and rate-limited to one notification per
/// <see cref="DiagnosticsNotifications.CooldownMinutes"/> for the same
/// component id. Both watch and act severities can notify; only the log line
/// above is act-only and per-run.
/// </summary>
public sealed class DiagnosticsAlertService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly DiagnosticsHealthModel _health;
    private readonly IConfigStore _store;
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastNotifiedUtc = new(StringComparer.Ordinal);
    private readonly FeatureGates _gates;

    /// <summary>Raised when a component needs attention and the user's
    /// notification settings allow it. Platform bootstraps subscribe to
    /// surface a native notification (Windows tray balloon, macOS banner,
    /// Linux notify-send).</summary>
    public event Action<DiagnosticsAlertNotice>? AlertNeedsAttention;

    public DiagnosticsAlertService(DiagnosticsHealthModel health, IConfigStore store, FeatureGates? gates = null)
    {
        _health = health;
        _store = store;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    /// <summary>Raises a notice outside the health poll (the thermal guard trip), still honoring the master notifications switch.</summary>
    public void Raise(DiagnosticsAlertNotice notice)
    {
        if (!_store.Load().Diagnostics.Notifications.Enabled)
        {
            return;
        }
        try
        {
            AlertNeedsAttention?.Invoke(notice);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[diagnostics-alert] notify subscriber failed: {ex.Message}");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { Tick(); }
            catch (Exception ex) { ServiceLog.Warn($"[diagnostics-alert] tick failed: {ex.Message}"); }
        }
        while (await WaitForNextTickSafe(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    internal void Tick()
    {
        if (!_gates.Diagnostics)
        {
            return;
        }
        var health = _health.BuildHealth();
        var notifications = _store.Load().Diagnostics.Notifications;
        var now = DateTime.UtcNow;

        foreach (var component in health.Components)
        {
            if (component.Status != HealthStatuses.Act || !_alerted.Add(component.Id))
            {
                continue;
            }
            var topReason = component.Reasons.FirstOrDefault(r => r.Severity == HealthStatuses.Act)
                ?? component.Reasons.FirstOrDefault();
            var summary = topReason?.Summary ?? "no detail available";
            ServiceLog.Warn($"[diagnostics-alert] {component.Kind} issue: {component.Name} - {summary}");
        }

        foreach (var notice in EvaluateNotifications(health.Components, notifications, _lastNotifiedUtc, now))
        {
            try
            {
                AlertNeedsAttention?.Invoke(notice);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[diagnostics-alert] notify subscriber failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Pure evaluation of which components should raise a native notification
    /// this tick: gated by the master enable + the reason's category toggle,
    /// rate-limited per component id to one notification per CooldownMinutes.
    /// lastNotifiedUtc is read and updated in place (mirrors the per-run
    /// _alerted set above), so tests can drive it across repeated calls
    /// without constructing a real DiagnosticsHealthModel.
    /// </summary>
    internal static IReadOnlyList<DiagnosticsAlertNotice> EvaluateNotifications(
        IReadOnlyList<HealthComponent> components,
        DiagnosticsNotifications notifications,
        IDictionary<string, DateTime> lastNotifiedUtc,
        DateTime nowUtc)
    {
        if (!notifications.Enabled)
        {
            return Array.Empty<DiagnosticsAlertNotice>();
        }

        var notices = new List<DiagnosticsAlertNotice>();
        foreach (var component in components)
        {
            if (component.Status != HealthStatuses.Act && component.Status != HealthStatuses.Watch)
            {
                continue;
            }
            if (CategoryFor(component) is not { } category || !CategoryEnabled(notifications, category))
            {
                continue;
            }
            if (lastNotifiedUtc.TryGetValue(component.Id, out var last) &&
                nowUtc - last < TimeSpan.FromMinutes(notifications.CooldownMinutes))
            {
                continue;
            }

            var reason = component.Reasons.FirstOrDefault(r => r.Severity == component.Status)
                ?? component.Reasons.FirstOrDefault();
            lastNotifiedUtc[component.Id] = nowUtc;
            notices.Add(new DiagnosticsAlertNotice(component.Name, reason?.Summary ?? "Needs attention.", component.Kind ?? ""));
        }
        return notices;
    }

    /// <summary>SPA path for a diagnostics alert, or null when the kind has no tab; the route is /{section}/{view}/{subtab} per nexus-web useRoute.ts and the tab slugs are DiagnosticsView's own.</summary>
    public static string? AlertPath(string kind) => kind switch
    {
        "storage" => "/system/diagnostics/storage",
        "memory" => "/system/diagnostics/memory",
        "system" => "/system/diagnostics/system",
        // GPU throttling is reported on the cooling tab.
        "cooling" or "gpu" or "thermalGuard" => "/system/diagnostics/cooling",
        _ => null,
    };

    /// <summary>Maps a component to the notification category it belongs to.
    /// The "cooling" kind is split by id: per-device stall components
    /// (id "cooling:&lt;deviceId&gt;") are the cooling category, while the
    /// aggregate (id "cooling") carries only sustained-high-temp reasons and
    /// maps to highTemp.</summary>
    private static string? CategoryFor(HealthComponent component) => component.Kind switch
    {
        "storage" => "storageHealth",
        "gpu" => "gpuThrottle",
        "memory" => "memoryTest",
        "system" => "systemDevices",
        "cooling" => component.Id == "cooling" ? "highTemp" : "cooling",
        _ => null,
    };

    private static bool CategoryEnabled(DiagnosticsNotifications notifications, string category) => category switch
    {
        "highTemp" => notifications.HighTemp,
        "storageHealth" => notifications.StorageHealth,
        "cooling" => notifications.Cooling,
        "memoryTest" => notifications.MemoryTest,
        "systemDevices" => notifications.SystemDevices,
        "gpuThrottle" => notifications.GpuThrottle,
        _ => false,
    };
}

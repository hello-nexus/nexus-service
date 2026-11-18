#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Triggers a TouchMappingGuard pass on helper connect and on the same
/// displays-changed push DisplayTopologyWatcher consumes (WM_DISPLAYCHANGE,
/// USB re-enumeration, sleep/wake) - the events that can silently revert
/// Windows' digitizer-to-monitor association.
/// POST /displays/touch-mapping/repair calls the same
/// TouchMappingGuard singleton directly for a manual, synchronous pass.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TouchMappingGuardService : BackgroundService
{
    private const int DebounceMs = 2000;

    private readonly HelperRegistry _helpers;
    private readonly TouchMappingGuard _guard;
    private readonly Timer _debounce;
    private readonly object _timerLock = new();
    private DateTime? _dueUtc;

    public TouchMappingGuardService(HelperRegistry helpers, TouchMappingGuard guard)
    {
        _helpers = helpers;
        _guard = guard;
        _debounce = new Timer(_ => RunPass(), null, Timeout.Infinite, Timeout.Infinite);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _helpers.InboundEnvelope += OnEnvelope;
        _helpers.Connected += OnHelperEdge;
        stoppingToken.Register(() =>
        {
            _helpers.InboundEnvelope -= OnEnvelope;
            _helpers.Connected -= OnHelperEdge;
            _debounce.Dispose();
        });
        return Task.CompletedTask;
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        if (env.Type == DisplayTopologyCommands.ChangedType) Kick();
    }

    private void OnHelperEdge(HelperConnection _) => Kick();

    private void Kick()
    {
        lock (_timerLock)
        {
            _dueUtc = DateTime.UtcNow.AddMilliseconds(DebounceMs);
            try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Arms the bounded follow-up unless an event-driven fire is already due
    /// sooner: an earlier pending fire is never pushed later.
    /// </summary>
    private void ArmFollowUp(TimeSpan delay)
    {
        lock (_timerLock)
        {
            var now = DateTime.UtcNow;
            if (_dueUtc is { } due && due > now && due <= now + delay) return;
            _dueUtc = now + delay;
            try { _debounce.Change(delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    private void RunPass()
    {
        lock (_timerLock) _dueUtc = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await _guard.RunPassAsync().ConfigureAwait(false);
                // One-shot follow-up only while a digitizer-absent window is
                // pending; null once the digitizer is seen or the restart is spent.
                if (outcome.RecheckAfter is { } delay) ArmFollowUp(delay);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[touch-map] pass failed: {ex.Message}"); }
        });
    }
}
#endif

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel;

/// <summary>
/// The one place that decides whether the overlay host should be running and
/// starts it when it is not: a 5 s tick plus a wake on every settings change.
/// A host that will not stay up backs off to five minutes; anything that newly
/// wants one (a pinned widget, a panel appearing) clears that.
/// </summary>
public sealed class OverlaySupervisor : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IOverlayHost _host;
    private readonly IConfigStore _store;
    private readonly Func<bool> _y70Present;
    private readonly Func<bool> _streamsWanted;
    private readonly Func<bool>? _identifyActive;
    private readonly Func<NexusSettings, bool>? _migrationCanAdvance;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private TimeSpan _backoff = MinBackoff;
    private DateTime _nextAttempt = DateTime.MinValue;
    private bool _wasDesired;
    private long? _pendingSinceMs;

    /// <summary>Monotonic, so a wall-clock jump neither shortens nor stretches the migration bound.</summary>
    internal Func<long> MonoMs { get; set; } = () => Environment.TickCount64;

    public OverlaySupervisor(IOverlayHost host, IConfigStore store, Func<bool> y70Present, Func<bool> streamsWanted, Func<bool>? identifyActive = null, Func<NexusSettings, bool>? migrationCanAdvance = null)
    {
        _migrationCanAdvance = migrationCanAdvance;
        _host = host;
        _store = store;
        _y70Present = y70Present;
        _streamsWanted = streamsWanted;
        _identifyActive = identifyActive;
    }

    public static bool LegacyPending(NexusSettings s)
        => s.Overlay.Enabled == true && s.Overlay.HasWidgets && !s.Overlay.MigratedToPanels;

    // Pending legacy widgets need a host: the Windows migration waits for the monitor map it publishes.
    public static bool Desired(NexusSettings s, bool y70Present, bool streamsWanted, bool migrationWantsHost = true)
        => (LegacyPending(s) && migrationWantsHost)
           || (s.Panel.AutoLaunch && y70Present)
           || streamsWanted
           || s.PanelDevices.Values.Any(r => !string.IsNullOrEmpty(r.DisplayId) && PanelHosting.IsOn(r));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the host's startup thread, so nothing here delays the bind.
        await Task.Yield();

        void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
        _store.OnChanged += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { Reconcile(DateTime.UtcNow); }
                catch (Exception ex) { ServiceLog.Error($"[overlay-supervisor] {ex.GetType().Name}: {ex.Message}"); }
                try { await _wake.WaitAsync(Tick, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _store.OnChanged -= Wake;
        }
    }

    internal void Reconcile(DateTime now)
    {
        var settings = _store.Load();
        var desired = Desired(settings, _y70Present(), _streamsWanted(), MigrationWantsHost(settings));
        if (desired && !_wasDesired) ResetBackoff();
        _wasDesired = desired;

        if (!desired)
        {
            // The macOS helper renders only widgets and has no teardown path of
            // its own. Windows self-exits when idle, and the Linux kiosks are
            // reconciled by their own host.
            if (OperatingSystem.IsMacOS() && _host.IsRunning && _identifyActive?.Invoke() != true) _host.Stop();
            return;
        }
        if (_host.IsRunning || now < _nextAttempt) return;

        _nextAttempt = now + _backoff;
        // A host that declined (no console user yet) is retried at the base
        // interval; the backoff is for one that started and did not stay.
        if (_host.Start()) _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        else _backoff = MinBackoff;
    }

    // The term lapses after the migration's own grace, so an unresolvable target stops restarting the host.
    private bool MigrationWantsHost(NexusSettings settings)
    {
        if (!LegacyPending(settings))
        {
            _pendingSinceMs = null;
            return false;
        }
        var mono = MonoMs();
        _pendingSinceMs ??= mono;
        if (mono - _pendingSinceMs.Value >= DesktopWidgetsMigration.MissingMonitorGraceMs) return false;
        return _migrationCanAdvance?.Invoke(settings) ?? true;
    }

    private void ResetBackoff()
    {
        _backoff = MinBackoff;
        _nextAttempt = DateTime.MinValue;
    }
}

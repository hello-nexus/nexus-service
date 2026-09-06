using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Linux equivalent of <see cref="PowerEventListener"/>: bounces the OpenRGB
/// subprocess on resume and forwards both edges to the Q-series watcher (sleep
/// the panel before suspend; on resume, mark the USB re-enumeration as a resume
/// so it is not read as a reseat and rebooted) via systemd-logind's
/// <c>PrepareForSleep(bool start)</c> signal on the SYSTEM bus. The shared session-bus <see cref="DBusConnection"/>
/// (tray, media) cannot see this signal, so this owns a private
/// <see cref="DBusBusKind.System"/> connection instead. Fail-soft: a
/// non-systemd distro (no logind on the bus) logs and leaves resume-bounce
/// off for the run; a mid-run bus drop (dbus-daemon restart) re-subscribes on
/// a bounded retry budget instead of ending the watch permanently.
/// </summary>
public sealed class LinuxResumeListener : IHostedService, IDisposable
{
    private const string LoginManagerPath = "/org/freedesktop/login1";
    private const string LoginManagerInterface = "org.freedesktop.login1.Manager";
    private const int MaxConsecutiveReconnectFailures = 5;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly RgbBridge _bridge;
    private readonly Nexus.Service.QSeries.QSeriesPortWatcher? _qseries;
    private DBusConnection? _dbus;
    private CancellationTokenSource? _cts;

    public LinuxResumeListener(RgbBridge bridge, Nexus.Service.QSeries.QSeriesPortWatcher? qseries = null)
    {
        _bridge = bridge;
        _qseries = qseries;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _dbus?.Dispose();
        _dbus = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _dbus?.Dispose();

    private async Task RunAsync(CancellationToken ct)
    {
        // A local reference: StopAsync/Dispose null the field concurrently
        // with this loop's own use of the connection, which a field re-read
        // on the next iteration could race.
        var dbus = new DBusConnection(DBusBusKind.System);
        _dbus = dbus;
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await dbus.StartAsync().ConfigureAwait(false);
                await dbus.AddMatchAsync(
                    $"type='signal',interface='{LoginManagerInterface}',member='PrepareForSleep'").ConfigureAwait(false);
                Console.Error.WriteLine("[power-events] subscribed to logind PrepareForSleep for resume-bounce");
                consecutiveFailures = 0;

                while (!ct.IsCancellationRequested)
                {
                    DBusMessage signal;
                    try
                    {
                        // Long wait, re-armed on each timeout; a suspend/resume cycle
                        // landing in the re-arm gap is missed, harmless for a
                        // user-driven sleep/wake that will not repeat that fast.
                        signal = await dbus.WaitForSignalAsync(LoginManagerPath, "PrepareForSleep", 600_000)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    // PrepareForSleep(true) fires just before suspend; false fires on
                    // resume, the only edge that needs the bridge bounced.
                    if (new DBusReader(signal.Body).ReadBool())
                    {
                        _qseries?.OnHostSuspending();
                    }
                    else
                    {
                        ServiceLog.Info("[power-events] system resumed - bouncing OpenRGB subprocess");
                        _bridge.OnSystemResume();
                        _qseries?.OnHostResumed();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                if (consecutiveFailures > MaxConsecutiveReconnectFailures)
                {
                    Console.Error.WriteLine(
                        $"[power-events] resume-bounce watch stopped after repeated failures: {ex.Message}");
                    return;
                }
                Console.Error.WriteLine($"[power-events] system D-Bus connection lost, retrying: {ex.Message}");
                try { await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}

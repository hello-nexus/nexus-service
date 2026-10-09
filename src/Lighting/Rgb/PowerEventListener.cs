using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

#if WINDOWS
using Microsoft.Win32;
using Nexus.Service.Platform;
#endif

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Subscribes to OS power events: bounces the OpenRGB subprocess when the
/// system resumes from sleep so devices re-init after the USB stack
/// re-enumerates, and drives <see cref="SleepBlackoutCoordinator"/> across the
/// suspend/resume pair so devices that keep their bus powered (RAM over SMBus)
/// do not sit lit all night.
///
/// Windows: hooks <c>SystemEvents.PowerModeChanged</c> via Microsoft.Win32.
/// Linux has its own equivalent, <see cref="Nexus.Service.Platform.Linux.LinuxResumeListener"/>,
/// subscribed to logind over D-Bus. macOS is a no-op here (no resume hook wired yet).
/// </summary>
public sealed class PowerEventListener : IHostedService, IDisposable
{
    private readonly RgbBridge _bridge;
    private readonly SleepBlackoutCoordinator _blackout;
    private readonly Nexus.Service.Peripherals.LianLi.LianLiHubSet? _lianLi;
    private readonly Nexus.Service.Peripherals.LianLiWireless.Slv3Hub? _slv3;
#if WINDOWS
    private bool _subscribed;
#endif

    public PowerEventListener(RgbBridge bridge, SleepBlackoutCoordinator blackout, Nexus.Service.Peripherals.LianLi.LianLiHubSet? lianLi = null, Nexus.Service.Peripherals.LianLiWireless.Slv3Hub? slv3 = null)
    {
        _slv3 = slv3;
        _bridge = bridge;
        _blackout = blackout;
        _lianLi = lianLi;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[power-events] failed to subscribe: {ex.Message}");
            }
        }
#endif
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Suspend runs INLINE and the resume leg hands off, the same split
        // QSeriesPortWatcher uses: the machine stops once every subscriber
        // returns, so a blackout dispatched to the thread pool loses the race,
        // while a resume has no deadline. OnSuspending is budget-capped so it
        // cannot hold this shared pump thread open.
        if (e.Mode == PowerModes.Suspend)
        {
            _blackout.OnSuspending();
            try { _slv3?.OnSystemSuspending(); } catch (Exception ex) { ServiceLog.Warn($"[lianli-wireless] suspend SaveCfg failed: {ex.Message}"); }
        }
        else if (e.Mode == PowerModes.Resume)
        {
            ServiceLog.Info("[power-events] system resumed - bouncing OpenRGB subprocess");
            _blackout.OnResumed();
            _bridge.OnSystemResume();
            _lianLi?.OnSystemResumed();
        }
    }
#endif

    private void Unsubscribe()
    {
#if WINDOWS
        if (_subscribed && OperatingSystem.IsWindows())
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            _subscribed = false;
        }
#endif
    }

    public void Dispose() => Unsubscribe();
}

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel;

/// <summary>Stores a "desktop" backdrop on unset desktop-capable panels once Wallpaper Engine is seen running; closing it later never flips the kiosk back.</summary>
public sealed class WallpaperEngineBackdropLatch : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly PanelDeviceRegistry _registry;
    private readonly Func<bool> _isWallpaperEngineRunning;
    private readonly Action<string> _notifyPanelChanged;

    public WallpaperEngineBackdropLatch(PanelDeviceRegistry registry, Action<string> notifyPanelChanged)
        : this(registry, IsWallpaperEngineRunning, notifyPanelChanged) { }

    internal WallpaperEngineBackdropLatch(PanelDeviceRegistry registry, Func<bool> isWallpaperEngineRunning, Action<string> notifyPanelChanged)
    {
        _registry = registry;
        _isWallpaperEngineRunning = isWallpaperEngineRunning;
        _notifyPanelChanged = notifyPanelChanged;
    }

    internal void Tick()
    {
        if (!_registry.HasUnsetDesktopBackdrop() || !_isWallpaperEngineRunning()) return;
        foreach (var id in _registry.LatchDesktopBackdrop())
        {
            ServiceLog.Info($"[panel] Wallpaper Engine is running: backdrop of '{id}' set to desktop");
            _notifyPanelChanged(id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { ServiceLog.Warn($"[panel] Wallpaper Engine backdrop check failed: {ex.Message}"); }
            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static bool IsWallpaperEngineRunning() => AnyRunning("wallpaper64") || AnyRunning("wallpaper32");

    private static bool AnyRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }
}

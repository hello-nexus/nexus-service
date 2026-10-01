using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class WallpaperEngineBackdropLatchTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "nexus-we-latch-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;
    private readonly List<string> _notified = new();

    public WallpaperEngineBackdropLatchTests()
    {
        _store = new JsonConfigStore(_path);
        _registry = new PanelDeviceRegistry(_store) { SeeThroughHost = true };
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private string AddMonitorPanel() =>
        _registry.AllocateForDisplay("DISP-1", null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }).Record.Id;

    [Fact]
    public void Tick_WallpaperEngineNotRunning_LeavesBackdropUnset()
    {
        var id = AddMonitorPanel();
        new WallpaperEngineBackdropLatch(_registry, () => false, _notified.Add).Tick();

        Assert.Null(_registry.Get(id)!.Backdrop);
        Assert.Empty(_notified);
    }

    [Fact]
    public void Tick_WallpaperEngineRunning_StoresDesktopAndNotifies()
    {
        var id = AddMonitorPanel();
        new WallpaperEngineBackdropLatch(_registry, () => true, _notified.Add).Tick();

        Assert.Equal("desktop", _registry.Get(id)!.Backdrop);
        Assert.Equal(new[] { id }, _notified);
    }

    [Fact]
    public void Tick_AfterLatch_WallpaperEngineClosing_KeepsDesktop()
    {
        var id = AddMonitorPanel();
        var running = true;
        var latch = new WallpaperEngineBackdropLatch(_registry, () => running, _notified.Add);
        latch.Tick();
        running = false;
        latch.Tick();

        Assert.Equal("desktop", _registry.Get(id)!.Backdrop);
        Assert.Single(_notified);
    }

    [Fact]
    public void Tick_NothingUnset_SkipsTheProcessScan()
    {
        var id = AddMonitorPanel();
        _registry.Patch(id, new PanelDevicePatch { Backdrop = "theme" });
        var scans = 0;
        new WallpaperEngineBackdropLatch(_registry, () => { scans++; return true; }, _notified.Add).Tick();

        Assert.Equal(0, scans);
        Assert.Equal("theme", _registry.Get(id)!.Backdrop);
    }
}

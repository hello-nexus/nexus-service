#if MACOS
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Panel;
using Nexus.Service.Sockets;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// macOS counterpart to <see cref="DisplayTopologyWatcher"/> (Windows).
/// CoreGraphics posts a reconfiguration callback for every attach, detach,
/// mode and origin change, which is the native equivalent of the
/// WM_DISPLAYCHANGE push the helper relays on Windows. A quiet-period timer
/// coalesces the burst a single arrangement change produces. The callback is
/// delivered on the main thread's run loop, which the service pumps through
/// MacStatusBar.RunLoop; without that pump only the boot pass would run.
/// </summary>
public sealed class MacDisplayTopologyWatcher : BackgroundService
{
    private const int DebounceMs = 500;

    // The CoreGraphics callback is a plain C function pointer with no
    // managed context, so the running watcher is reached through a static.
    // One watcher is registered per process (singleton hosted service).
    private static MacDisplayTopologyWatcher? _current;

    private readonly MultiplexHub _hub;
    private readonly DisplayTopologyService _topology;
    private readonly PanelAutoPromotion _autoPromotion;
    private readonly Timer _debounce;

    public MacDisplayTopologyWatcher(MultiplexHub hub, DisplayTopologyService topology, PanelAutoPromotion autoPromotion)
    {
        _hub = hub;
        _topology = topology;
        _autoPromotion = autoPromotion;
        _debounce = new Timer(_ => Broadcast(), null, Timeout.Infinite, Timeout.Infinite);
    }

    protected override unsafe Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _current = this;
        try
        {
            var error = CGDisplayRegisterReconfigurationCallback(&OnReconfigured, IntPtr.Zero);
            if (error != 0)
                Console.Error.WriteLine($"[displays-mac] reconfiguration callback registration failed: CGError {error}; only the boot pass will run");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-mac] reconfiguration callback registration failed: {ex.Message}");
        }
        _topology.PromotedPanelCapabilitiesChanged += OnPromotedCapabilitiesChanged;

        stoppingToken.Register(() =>
        {
            try { CGDisplayRemoveReconfigurationCallback(&OnReconfigured, IntPtr.Zero); } catch { }
            _topology.PromotedPanelCapabilitiesChanged -= OnPromotedCapabilitiesChanged;
            _current = null;
            _debounce.Dispose();
        });

        // A display attached before the service started never produces a
        // callback, so the promotion pass has to run once on its own.
        Kick();
        return Task.CompletedTask;
    }

    /// <summary>Editors key their grid on the record's capabilities, so a
    /// refresh (rotation, scaling change) must push panel/device.</summary>
    private void OnPromotedCapabilitiesChanged(IReadOnlyList<string> recordIds)
    {
        try
        {
            foreach (var id in recordIds) PanelTopics.BroadcastPanelDevice(_hub, id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-mac] panel capability broadcast failed: {ex.Message}");
        }
    }

    private void Kick()
    {
        try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Broadcast()
    {
        try
        {
            // Re-enumerate before broadcasting: GetTopology runs the
            // promoted-record capability sync, so display-bound records
            // track rotation/rescale even when no client refetches topology.
            var topology = _topology.GetTopology();
            foreach (var recordId in _autoPromotion.Reconcile(topology))
            {
                PanelTopics.BroadcastPanelDevice(_hub, recordId);
            }
            PanelTopics.BroadcastDisplays(_hub);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-mac] topology broadcast failed: {ex.Message}");
        }
    }

    // -- P/Invoke -----------------------------------------------------------

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // Posted before the configuration is applied; the topology read at that
    // point still describes the old arrangement, so it is not a useful edge.
    private const uint BeginConfigurationFlag = 1u << 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnReconfigured(uint display, uint flags, IntPtr userInfo)
    {
        try
        {
            if ((flags & BeginConfigurationFlag) != 0) return;
            _current?.Kick();
        }
        catch { }
    }

    [DllImport(CoreGraphics)]
    private static extern unsafe int CGDisplayRegisterReconfigurationCallback(
        delegate* unmanaged[Cdecl]<uint, uint, IntPtr, void> callback, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    private static extern unsafe int CGDisplayRemoveReconfigurationCallback(
        delegate* unmanaged[Cdecl]<uint, uint, IntPtr, void> callback, IntPtr userInfo);
}
#endif

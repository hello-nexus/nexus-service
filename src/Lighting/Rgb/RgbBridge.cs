using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Orchestrates the OpenRGB headless subprocess and the TCP controller, and
/// fans out frames from the <see cref="LightingEngine"/> to every detected
/// device.
///
/// Lifecycle:
/// <list type="number">
///   <item><see cref="Activate"/> is called by <see cref="Lighting.LightingProvider"/>
///         when the first effect starts. We launch the subprocess (if not already
///         running), connect via TCP, fetch the device list, switch each device
///         to direct-control mode, then subscribe to <see cref="LightingEngine.OnFrame"/>.</item>
///   <item><see cref="Deactivate"/> is called when <c>StopAll</c> hits and there's no
///         active effect. We send a final all-black frame, unsubscribe, and stop
///         the subprocess.</item>
/// </list>
///
/// While active, a background loop re-fetches the device list every
/// <see cref="DeviceRefreshInterval"/> so that devices OpenRGB detects after our
/// initial connect (Corsair, Gigabyte mobo, NVIDIA GPU all take a few seconds
/// after the fast Razer HID detect) become visible without the user having to
/// restart the effect.
///
/// On Windows the system power events trigger a subprocess restart so devices
/// re-init after sleep. The bridge transparently reconnects when the TCP socket
/// drops by trying TryConnectAsync on the next frame tick.
/// </summary>
public sealed class RgbBridge : IDisposable
{
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Poll cadence. OpenRGB pushes DEVICE_LIST_UPDATED on hardware change, but
    /// our writer-only PushFrameAsync path never drains unsolicited packets from
    /// the socket, so events are only dispatched during an actual request/reply
    /// (e.g. GetDevicesAsync). The periodic GetDevices doubles as a drain: any
    /// queued DEVICE_LIST_UPDATED packets are processed inside ReadExpectedLocked
    /// and trigger <see cref="OnDeviceListChanged"/>. Diff-based refresh leaves
    /// stable devices unchanged, so this poll is low-cost.
    /// </summary>
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(3);
    /// <summary>Debounce bursts of DeviceListChanged events into a single refresh.</summary>
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(250);
    /// <summary>
    /// OpenRGB's headless daemon does not re-run its detection plugins on USB
    /// hot-plug (no message pump). We watch the OS USB enumeration ourselves and
    /// bounce the subprocess when topology changes so new devices are picked up.
    /// Every-other refresh tick keeps pnputil spawning to ~once per 6s.
    /// </summary>
    private const int UsbCheckEveryNTicks = 2;
    /// <summary>Minimum gap between subprocess bounces so a plug burst collapses into one restart.</summary>
    private static readonly TimeSpan BounceCooldown = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Max time the rescan "grace" stays active after a subprocess bounce. During
    /// grace, we never shrink the visible device list - partial detection results
    /// from the restarting OpenRGB daemon are absorbed without dropping devices
    /// the user was already seeing. If detection is slower than this, fallback to
    /// normal refresh semantics so an actually-disconnected device still
    /// eventually disappears.
    /// </summary>
    private static readonly TimeSpan RescanGracePeriod = TimeSpan.FromSeconds(15);
    /// <summary>
    /// Minimum time the "scanning" flag stays true on initial boot (baseline 0).
    /// OpenRGB trickles devices in over a few seconds - Razer HID is first and
    /// almost instant, then Corsair/Gigabyte/NVIDIA take 2-6s more. Holds the
    /// spinner for this window rather than clearing when the first device lands.
    /// </summary>
    private static readonly TimeSpan InitialRescanMinHold = TimeSpan.FromSeconds(5);

    private readonly OpenRgbProcessManager _proc;
    private readonly IRgbController _controller;
    private readonly LightingEngine _engine;
    private readonly IConfigStore _store;
    private readonly IUsbEnumerator _usb;

    private readonly object _lock = new();
    private bool _active;
    private bool _disposed;
    private Action<ReadOnlyMemory<byte>>? _frameHandler;
    private Action? _deviceListHandler;
    private IReadOnlyList<RgbDevice> _devices = Array.Empty<RgbDevice>();
    private HashSet<int> _directModeApplied = new();
    // StableIds seen drivable (LedCount > 0) on a settled, post-hold commit. A
    // device that ever settles drivable stays shown even if a later fetch
    // glitches it to 0 LEDs; a device only ever seen non-drivable stays hidden.
    // Only serial/location ids latch - the index fallback is reused across
    // bounces. Survives a subprocess bounce (same real hardware re-enumerates);
    // cleared only on Deactivate. Guarded by _lock.
    private readonly HashSet<string> _drivableLatched = new(StringComparer.Ordinal);
    private CancellationTokenSource? _refreshCts;
    private Task? _shutdownTask;
    private long _lastConnectAttemptTicks; // DateTime.UtcNow.Ticks; updated via Interlocked
    private long _lastBounceTicks;         // DateTime.UtcNow.Ticks; updated via Interlocked
    private long _rescanStartedTicks;      // 0 = no active rescan; else = UtcNow ticks at bounce start
    private int _rescanBaselineCount;      // device count snapshotted when rescan started
    private int _refreshPending;           // 0 = idle, 1 = refresh scheduled/running
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private int _lastUsbCount = -1;        // -1 = not yet observed; no bounce on first read

    // Identify: split id -> start + expiration. OnFrame consults this so the
    // active effect keeps running but the user's chosen zone flashes white on
    // and off so they know which physical strip it is. The flash period is
    // fixed (see IdentifyFlashPeriodMs) and applied uniformly to all queued
    // identifies so adjacent zones blink in phase if both are running.
    private readonly ConcurrentDictionary<string, (long startTicks, long expirationTicks)> _identifyOverrides = new();
    private const int IdentifyFlashPeriodMs = 500;
    // Zone resize requests enqueued from SetZoneLedCount. Drained and applied
    // via OpenRGB's RESIZEZONE opcode inside RefreshDevicesAsync under the
    // refresh semaphore so it never races with the frame push path.
    private readonly ConcurrentQueue<(int physIdx, int zoneIdx, int newSize)> _pendingZoneResizes = new();
    // Per-physical-device colour buffers. When a motherboard is split into zones
    // in the engine, each zone's DeviceFrame writes its slice into this buffer
    // at the zone's LED offset; the whole physical device is then pushed once.
    // ConcurrentDictionary so the 30fps OnFrame reader can't tear when
    // SyncPhysicalBuffers mutates on the refresh thread.
    private readonly ConcurrentDictionary<int, RgbColor[]> _physBuffers = new();
    // Reused across frames - set of physical indices that had at least one zone
    // written in the current OnFrame. Cleared at the start of every frame.
    // Only touched inside OnFrame which the engine serialises, so a plain HashSet
    // is safe here.
    private readonly HashSet<int> _touchedPhysicals = new();
    // Reused across frames - physical index -> true when every zone frame
    // mapped to it is uncontrolled, so the whole physical device is skipped
    // rather than pushed. Only touched inside OnFrame.
    private readonly Dictionary<int, bool> _physFullyUncontrolled = new();
    // Ids of this bridge's own OpenRGB frames as of the last refresh, set
    // once per RefreshDevicesAsync before contributor frames are appended.
    // See ComputeFullyUncontrolledPhysicals for why this filter is needed.
    // Replaced wholesale (never mutated) so OnFrame reads it without
    // synchronization.
    private HashSet<string> _bridgeFrameIds = new(StringComparer.Ordinal);

    private readonly IReadOnlyList<ILightingFrameContributor> _frameContributors;
    private readonly Nexus.Service.Lighting.Mappings.ContributorFrameLayouts _contributorLayouts;
    // Contributors that also implement IOpenRgbDeviceOwner. Cached at construction;
    // used in RefreshDevicesAsync to skip seeding engine frames for devices these
    // providers drive directly so OpenRGB never pushes conflicting colors to hardware.
    private readonly IReadOnlyList<Nexus.Service.Lighting.IOpenRgbDeviceOwner> _deviceOwners;

    public RgbBridge(OpenRgbProcessManager proc, IRgbController controller, LightingEngine engine, IConfigStore store, IUsbEnumerator usb,
        IEnumerable<ILightingFrameContributor>? frameContributors = null,
        Nexus.Service.Lighting.Mappings.ContributorFrameLayouts? contributorLayouts = null)
    {
        _proc = proc;
        _controller = controller;
        _engine = engine;
        _store = store;
        _usb = usb;
        _contributorLayouts = contributorLayouts ?? new Nexus.Service.Lighting.Mappings.ContributorFrameLayouts();
        _frameContributors = frameContributors is null
            ? Array.Empty<ILightingFrameContributor>()
            : new List<ILightingFrameContributor>(frameContributors);
        var owners = new List<Nexus.Service.Lighting.IOpenRgbDeviceOwner>();
        foreach (var c in _frameContributors)
        {
            if (c is Nexus.Service.Lighting.IOpenRgbDeviceOwner owner)
            {
                owners.Add(owner);
            }
        }
        _deviceOwners = owners;
        // Re-run the device refresh whenever a contributor's topology changes
        // (e.g. NP50 hot-plug or LS10 added/removed on a port) so its frames
        // appear/disappear from the engine without a polling step.
        foreach (var c in _frameContributors)
        {
            c.DevicesChanged += OnContributorDevicesChanged;
        }
    }

    private void OnContributorDevicesChanged()
    {
        // Cheap fire-and-forget - RefreshDevicesAsync is the same path the
        // periodic refresh loop uses and is debounced by _refreshSemaphore.
        _ = RefreshDevicesAsync();
    }

    /// <summary>Latest device snapshot. Updated on connect, refresh tick, and DEVICE_LIST_UPDATED.</summary>
    public IReadOnlyList<RgbDevice> Devices
    {
        get
        {
            lock (_lock)
            {
                return _devices;
            }
        }
    }

    /// <summary>
    /// Snapshot of the StableIds latched drivable (see <c>_drivableLatched</c>).
    /// Consulted by card emission so a device that settled drivable is never
    /// hidden by a later transient 0-LED fetch.
    /// </summary>
    public IReadOnlySet<string> DrivableIds
    {
        get
        {
            lock (_lock)
            {
                return new HashSet<string>(_drivableLatched, StringComparer.Ordinal);
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    public bool IsConnected => _controller.IsConnected;

    /// <summary>
    /// Bring the bridge online. Idempotent. Non-blocking - the actual subprocess
    /// boot + handshake happens on a background task. The first lighting frame
    /// arrives ~1-3 seconds later (subject to OpenRGB device detection cost).
    /// </summary>
    public void Activate()
    {
        Action<ReadOnlyMemory<byte>>? newFrameHandler;
        Action? newDeviceListHandler;
        CancellationTokenSource? newCts;
        Task? prevShutdown;

        lock (_lock)
        {
            if (_disposed || _active)
            {
                return;
            }

            prevShutdown = _shutdownTask;
            _shutdownTask = null;

            _active = true;

            newFrameHandler = OnFrame;
            _frameHandler = newFrameHandler;

            newDeviceListHandler = OnDeviceListChanged;
            _deviceListHandler = newDeviceListHandler;

            newCts = new CancellationTokenSource();
            _refreshCts = newCts;
        }

        // Wait for any in-flight shutdown to finish (port release, process exit)
        // before starting a new subprocess.
        if (prevShutdown is not null)
        {
            try
            { prevShutdown.GetAwaiter().GetResult(); }
            catch { }
        }

        _engine.OnFrame += newFrameHandler;
        _controller.DeviceListChanged += newDeviceListHandler;

        // Initial boot is itself a rescan from the user's perspective: OpenRGB
        // subprocess starts, detection plugins run, devices trickle in. Set the
        // rescan flag with baseline 0 so the UI spinner runs from activate until
        // at least one device shows up (or the grace period expires).
        lock (_lock)
        { _rescanBaselineCount = 0; }
        Interlocked.Exchange(ref _rescanStartedTicks, DateTime.UtcNow.Ticks);

        _proc.Start();

        _ = Task.Run(EnsureConnectedAsync);
        _ = Task.Run(() => DeviceRefreshLoopAsync(newCts.Token));
    }

    /// <summary>
    /// Take the bridge offline. Sends a final all-black frame to every device,
    /// disconnects from the SDK server, and stops the subprocess.
    /// </summary>
    public void Deactivate()
    {
        Action<ReadOnlyMemory<byte>>? frameHandler;
        Action? deviceListHandler;
        CancellationTokenSource? cts;

        lock (_lock)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            frameHandler = _frameHandler;
            _frameHandler = null;
            deviceListHandler = _deviceListHandler;
            _deviceListHandler = null;
            cts = _refreshCts;
            _refreshCts = null;
            _devices = Array.Empty<RgbDevice>();
            _directModeApplied = new();
            _drivableLatched.Clear();
        }

        if (frameHandler is not null)
        {
            try
            { _engine.OnFrame -= frameHandler; }
            catch { }
        }
        if (deviceListHandler is not null)
        {
            try
            { _controller.DeviceListChanged -= deviceListHandler; }
            catch { }
        }
        try
        { cts?.Cancel(); }
        catch { }
        try
        { cts?.Dispose(); }
        catch { }

        var task = Task.Run(async () =>
        {
            try
            { await _controller.DisconnectAsync().ConfigureAwait(false); }
            catch { }
            _proc.Stop();
        });

        lock (_lock)
        {
            _shutdownTask = task;
        }
    }

    /// <summary>
    /// Block until the shutdown task from the last <see cref="Deactivate"/> has
    /// finished (socket closed, process killed). No-op if nothing is in flight.
    /// </summary>
    public void AwaitShutdown()
    {
        Task? task;
        lock (_lock)
        { task = _shutdownTask; }
        if (task is not null)
        {
            try
            { task.GetAwaiter().GetResult(); }
            catch { }
        }
    }

    /// <summary>
    /// Triggered by the OS power-resume event handler. Tears down and restarts
    /// the subprocess so devices re-init after the USB stack re-enumerates.
    /// </summary>
    public void OnSystemResume() => BounceSubprocess();

    /// <summary>
    /// User-initiated rescan. The OpenRGB SDK has no RESCAN opcode, so the only
    /// way to force its detection plugins to re-run is to restart the subprocess.
    /// Use this when a device was plugged but OpenRGB never fired DEVICE_LIST_UPDATED
    /// (plugin that only scans at startup, etc.).
    /// </summary>
    public void ForceRescan() => BounceSubprocess();

    /// <summary>
    /// Queue a motherboard ARGB zone resize. Applied via OpenRGB's RESIZEZONE
    /// opcode on the next refresh pass under the refresh semaphore, so it never
    /// collides with an in-flight frame push. Idempotent.
    /// </summary>
    public void RequestZoneResize(int physicalIndex, int zoneIndex, int newSize)
    {
        if (physicalIndex < 0 || zoneIndex < 0 || newSize < 0)
            return;
        _pendingZoneResizes.Enqueue((physicalIndex, zoneIndex, newSize));
        if (IsActive && _controller.IsConnected)
        {
            _ = Task.Run(async () =>
            {
                try
                { await RefreshDevicesAsync().ConfigureAwait(false); }
                catch (Exception ex) { Console.Error.WriteLine($"[rgb-bridge] resize-triggered refresh failed: {ex.Message}"); }
            });
        }
    }

    /// <summary>
    /// Start identifying a logical lighting device (whole OpenRGB device or a
    /// single motherboard zone). OnFrame consults the override and flashes the
    /// zone's slice white on / off until the duration elapses. The active
    /// lighting effect keeps running underneath; identify recovers automatically
    /// when the window closes.
    /// </summary>
    public void BeginIdentify(string id, int durationMs)
    {
        if (string.IsNullOrEmpty(id))
            return;
        durationMs = Math.Clamp(durationMs, 250, 10000);
        var now = DateTime.UtcNow.Ticks;
        var expiration = now + TimeSpan.FromMilliseconds(durationMs).Ticks;
        _identifyOverrides[id] = (now, expiration);
    }

    private void BounceSubprocess()
    {
        if (!IsActive)
        {
            return;
        }
        Interlocked.Exchange(ref _lastConnectAttemptTicks, 0);
        // Snapshot the current device count so RefreshDevicesAsync can hold the
        // visible list steady until OpenRGB's detection plugins report at least
        // that many devices again (or the grace period expires).
        lock (_lock)
        {
            _rescanBaselineCount = _devices.Count;
        }
        Interlocked.Exchange(ref _rescanStartedTicks, DateTime.UtcNow.Ticks);
        _ = Task.Run(async () =>
        {
            try
            { await _controller.DisconnectAsync().ConfigureAwait(false); }
            catch { }
            _proc.Stop();
            await Task.Delay(500).ConfigureAwait(false);
            _proc.Start();
            await EnsureConnectedAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// True while a subprocess bounce is in progress and the OpenRGB daemon has
    /// not yet reported a device list at least as large as the snapshot taken
    /// when the bounce started. Exposed via /lighting/status so the UI can show
    /// a spinner on the rescan button without clearing the visible device list.
    /// </summary>
    public bool IsRescanning
    {
        get
        {
            var started = Interlocked.Read(ref _rescanStartedTicks);
            if (started == 0)
            {
                return false;
            }
            var age = DateTime.UtcNow.Ticks - started;
            if (age >= RescanGracePeriod.Ticks)
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Debounced refresh trigger for DEVICE_LIST_UPDATED bursts. OpenRGB can
    /// fire several in quick succession (one per detected plugin result); we
    /// only want to query the device list once per burst.
    /// </summary>
    private void OnDeviceListChanged() => ScheduleRefresh();

    /// <summary>
    /// Schedule a debounced device refresh. Coalesces using a 0/1 flag: the
    /// first call schedules the refresh, subsequent calls while the delay is
    /// running or the refresh is in flight are no-ops.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RefreshDebounce).ConfigureAwait(false);
                Interlocked.Exchange(ref _refreshPending, 0);
                if (IsActive && _controller.IsConnected)
                {
                    await RefreshDevicesAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _refreshPending, 0);
                Console.Error.WriteLine($"[rgb-bridge] scheduled refresh failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Connect (if not already), fetch the device list, and switch any newly
    /// seen devices to direct mode. Idempotent and rate-limited so a flood of
    /// frame ticks doesn't spam connect attempts.
    /// </summary>
    private async Task EnsureConnectedAsync()
    {
        if (!IsActive)
        {
            return;
        }

        // Atomic cooldown: only the thread that swaps the timestamp gets to attempt.
        var now = DateTime.UtcNow.Ticks;
        var prev = Interlocked.Read(ref _lastConnectAttemptTicks);
        if (!_controller.IsConnected && (now - prev) < ReconnectCooldown.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastConnectAttemptTicks, now, prev) != prev)
        {
            return;
        }

        if (!_controller.IsConnected)
        {
            var connected = await _controller.TryConnectAsync().ConfigureAwait(false);
            if (!connected)
            {
                return;
            }

            // Subprocess restarted (crash recovery or explicit bounce): every
            // controller in the new OpenRGB instance is in its cold-start mode.
            // Clear _directModeApplied so RefreshDevicesAsync re-issues
            // SetCustomMode for each device. Critical for ENE DRAM - without
            // re-issuing, the controller stays in whatever hardware preset
            // mode it boots into (typically a rainbow/breathing effect) and
            // silently ignores per-LED UpdateLEDs writes.
            lock (_lock)
            {
                _directModeApplied = new();
            }
        }

        await RefreshDevicesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Re-query the device list from the controller and apply direct mode to
    /// any new devices. Called on a timer and on initial connect.
    /// </summary>
    private async Task RefreshDevicesAsync()
    {
        if (!IsActive || !_controller.IsConnected)
        {
            return;
        }

        await _refreshSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsActive || !_controller.IsConnected)
            {
                return;
            }

            IReadOnlyList<RgbDevice> devices;
            try
            {
                devices = await _controller.GetDevicesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[rgb-bridge] device refresh failed: {ex.Message}");
                return;
            }

            // Apply any queued or persisted motherboard zone resizes BEFORE the
            // device list becomes the engine's source of truth. Re-fetch the
            // list afterwards so zone LED counts reflect the new sizes.
            if (await ApplyZoneResizesAsync(devices).ConfigureAwait(false))
            {
                try
                {
                    devices = await _controller.GetDevicesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[rgb-bridge] re-fetch after resize failed: {ex.Message}");
                }
            }

            // Defensive: if we suddenly observe an empty list after previously
            // having devices, ignore the result. The transient empty is almost
            // always a desync inside GetDevicesAsync (e.g. unsolicited packet
            // skipped) rather than a real "all devices vanished" event. We'll
            // pick up the actual state on the next refresh tick.
            if (devices.Count == 0)
            {
                int prevCount;
                lock (_lock)
                {
                    prevCount = _devices.Count;
                }

                if (prevCount > 0)
                {
                    return;
                }
            }

            // Rescan state machine. Bounce path (baseline > 0) keeps the visible
            // list steady until the daemon reports at least as many devices as
            // before (wholesale replace when count recovers). Initial-boot path
            // (baseline == 0) streams devices into the visible list as each
            // detection plugin finishes; the list only grows during the hold and
            // reconciles on the first post-hold refresh.
            var rescanStarted = Interlocked.Read(ref _rescanStartedTicks);
            var inInitialHold = false;
            if (rescanStarted != 0)
            {
                var rescanAge = DateTime.UtcNow.Ticks - rescanStarted;
                var graceExpired = rescanAge >= RescanGracePeriod.Ticks;
                if (!graceExpired)
                {
                    if (_rescanBaselineCount == 0 && rescanAge < InitialRescanMinHold.Ticks)
                    {
                        inInitialHold = true;
                    }
                    else if (_rescanBaselineCount > 0 && devices.Count < _rescanBaselineCount)
                    {
                        return;
                    }
                }
            }

            // Apply direct mode to any device we haven't seen yet. A fully
            // uncontrolled device is skipped (and left out of _directModeApplied)
            // so its firmware/vendor lighting stays live; the next refresh
            // tick retries, so re-enabling controlled claims it within one
            // refresh interval without a service restart.
            var settingsSnapshot = _store.Load();
            foreach (var dev in devices)
            {
                bool isNew;
                lock (_lock)
                {
                    isNew = !_directModeApplied.Contains(dev.Index);
                }

                if (!isNew)
                {
                    continue;
                }

                if (OpenRgbZoneSupport.IsFullyUncontrolled(dev, settingsSnapshot))
                {
                    continue;
                }

                try
                {
                    await _controller.SetDirectModeAsync(dev).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _directModeApplied.Add(dev.Index);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[rgb-bridge] SetDirectMode device {dev.Index} failed: {ex.Message}");
                }
            }

            IReadOnlyList<RgbDevice> finalList;
            lock (_lock)
            {
                if (inInitialHold)
                {
                    // Union: fresh fetch wins on index collision; any device
                    // previously committed during the hold but absent from this
                    // fetch is retained. Reconciliation happens on the first
                    // refresh after the hold expires.
                    var merged = new List<RgbDevice>(_devices.Count + devices.Count);
                    var present = new HashSet<int>(devices.Count);
                    foreach (var d in devices)
                    {
                        merged.Add(d);
                        present.Add(d.Index);
                    }
                    foreach (var d in _devices)
                    {
                        if (!present.Contains(d.Index))
                        {
                            merged.Add(d);
                        }
                    }
                    merged.Sort((a, b) => a.Index.CompareTo(b.Index));
                    finalList = merged;
                }
                else
                {
                    finalList = devices;
                    if (rescanStarted != 0)
                    {
                        Interlocked.Exchange(ref _rescanStartedTicks, 0);
                    }
                    // Settled list is authoritative: latch every drivable device
                    // so a later transient 0-LED fetch can't hide it. Only
                    // hardware-id devices latch; index-only ids can be reassigned
                    // to a different controller across a bounce.
                    foreach (var d in finalList)
                    {
                        if (d.LedCount > 0 && d.HasStableHardwareId)
                        {
                            _drivableLatched.Add(d.StableId);
                        }
                    }
                }
                _devices = finalList;
            }

            // Sync engine's DeviceFrame array with the current hardware list. For a
            // device that's still present at the same LED count, we REUSE the existing
            // DeviceFrame instance - its LED buffer keeps its last rendered colors so
            // the device doesn't flash black for one tick while the engine re-samples.
            // Only new or reshaped devices get a fresh DeviceFrame.
            //
            // One frame per zone of the device's partition. With no custom
            // partition this is the legacy emission: split motherboards get
            // one frame per ARGB header, everything else one whole-device
            // frame. Custom zones are contiguous device-space runs (validator
            // rule), so offset + count still describes each frame's slice of
            // the physical buffer.
            var existingFrames = _engine.Devices;
            var framesList = new List<DeviceFrame>(devices.Count);
            var layouts = settingsSnapshot.Lighting.DeviceLayouts;
            int logicalOrdinal = 0;
            int cardSlot = 0;
            int stripSlot = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                if (IsOwnedByFirstParty(d)) continue;
                var baseId = d.StableId;
                var isSplitMotherboard = OpenRgbZoneSupport.IsSplitMotherboard(d);
                var structure = OpenRgbZoneSupport.BuildStructure(d, settingsSnapshot);
                var zones = Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(structure, settingsSnapshot);
                var isDefault = zones.Count > 0 && zones[0].IsDefault;

                if (isDefault && !isSplitMotherboard)
                {
                    BuildOrReuseFrame(baseId, d, physicalIndex: d.Index, zoneIndex: -1, zoneOffset: 0, zoneLedCount: d.LedCount,
                        existingFrames, layouts, cardSlot, logicalOrdinal, framesList, isStrip: false, settingsSnapshot,
                        structure, zones[0]);
                    cardSlot++;
                    logicalOrdinal++;
                    continue;
                }

                if (isDefault)
                {
                    int zoneOffset = 0;
                    for (int z = 0; z < d.Zones.Count; z++)
                    {
                        var zone = d.Zones[z];
                        var zoneId = $"{baseId}-{z}";
                        BuildOrReuseFrame(zoneId, d, physicalIndex: d.Index, zoneIndex: z, zoneOffset: zoneOffset, zoneLedCount: zone.LedCount,
                            existingFrames, layouts, stripSlot, logicalOrdinal, framesList, isStrip: true, settingsSnapshot,
                            structure, z < zones.Count ? zones[z] : null);
                        zoneOffset += zone.LedCount;
                        stripSlot++;
                        logicalOrdinal++;
                    }
                    continue;
                }

                foreach (var zone in zones)
                {
                    var frameOffset = Nexus.Service.Lighting.Zones.ZoneResolution.FrameOffset(structure, zone);
                    var wholeSegment = Nexus.Service.Lighting.Zones.ZoneResolution.WholeResizableSegment(structure, zone);
                    BuildOrReuseFrame(zone.Id, d, physicalIndex: d.Index, zoneIndex: wholeSegment, zoneOffset: frameOffset,
                        zoneLedCount: zone.FrameLedCount, existingFrames, layouts,
                        isSplitMotherboard ? stripSlot : cardSlot, logicalOrdinal, framesList,
                        isStrip: isSplitMotherboard, settingsSnapshot, structure, zone);
                    if (isSplitMotherboard)
                        stripSlot++;
                    else
                        cardSlot++;
                    logicalOrdinal++;
                }
            }

            var bridgeFrameIds = new HashSet<string>(framesList.Count, StringComparer.Ordinal);
            foreach (var f in framesList)
            {
                bridgeFrameIds.Add(f.Id);
            }
            _bridgeFrameIds = bridgeFrameIds;

            // Pre-resolve the zone layout of contributor devices that expose
            // structures (keeb) so each contributed frame's user overrides
            // resolve through its zone's segment slices.
            List<(Nexus.Service.Lighting.Zones.DeviceStructure structure, IReadOnlyList<Nexus.Service.Lighting.Zones.ResolvedZone> zones)>? contributorZones = null;
            foreach (var contributor in _frameContributors)
            {
                if (contributor is not Nexus.Service.Lighting.Zones.IDeviceStructureSource source)
                    continue;
                foreach (var s in source.GetStructures())
                {
                    (contributorZones ??= new()).Add((s, Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(s, settingsSnapshot)));
                }
            }

            // Append device frames contributed by non-OpenRGB lighting
            // subsystems (NP50 and other hubs). They start at the next ordinal
            // so SerializeAndBroadcast's per-device Index space stays packed
            // and SampleDevicesFromCanvas processes them in one pass.
            // Each contributed frame then gets the resolver stack applied
            // (provider defaults -> applied mapping -> user deltas) so custom
            // layouts survive topology rebuilds; structure-authored segment
            // defaults seed zone-backed frames, and frame-authored UVs are
            // snapshotted as the pristine baseline for the rest.
            foreach (var contributor in _frameContributors)
            {
                var extra = contributor.BuildFrames(framesList.Count);
                for (var i = 0; i < extra.Count; i++)
                {
                    if (FindContributorZone(contributorZones, extra[i].Id) is { } hit)
                    {
                        var (seedU, seedV) = Nexus.Service.Lighting.Zones.ZoneResolution.DefaultUv(hit.Structure, hit.Zone);
                        _contributorLayouts.Refresh(extra[i], settingsSnapshot,
                            Nexus.Service.Lighting.Zones.ZoneResolution.ContextOf(hit.Structure, hit.Zone), seedU, seedV);
                    }
                    else
                    {
                        _contributorLayouts.Refresh(extra[i], settingsSnapshot);
                    }
                    framesList.Add(extra[i]);
                }
            }

            var frames = framesList.ToArray();
            var liveIds = new List<string>(frames.Length);
            foreach (var f in frames) liveIds.Add(f.Id);
            _contributorLayouts.Prune(liveIds);
            _engine.UpdateDevices(frames);

            // Rebuild per-physical LED buffers sized to the OpenRGB device's full
            // length. OnFrame writes each zone's slice into the corresponding offset
            // and then pushes once per physical device.
            SyncPhysicalBuffers(devices);
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    private void BuildOrReuseFrame(string id, RgbDevice physicalDevice, int physicalIndex, int zoneIndex, int zoneOffset, int zoneLedCount,
        DeviceFrame[] existingFrames, Dictionary<string, DeviceLayout> layouts, int canvasSlot, int logicalOrdinal,
        List<DeviceFrame> framesList, bool isStrip, NexusSettings settings,
        Nexus.Service.Lighting.Zones.DeviceStructure? structure = null,
        Nexus.Service.Lighting.Zones.ResolvedZone? zone = null)
    {
        // Match on id so a motherboard zone keeps its existing buffer across refreshes
        // (no flash-black tick on unrelated hardware changes).
        DeviceFrame? existing = null;
        for (int i = 0; i < existingFrames.Length; i++)
        {
            if (existingFrames[i].Id == id)
            {
                existing = existingFrames[i];
                break;
            }
        }
        if (existing is not null && existing.LedCount == zoneLedCount
            && existing.PhysicalIndex == physicalIndex && existing.ZoneIndex == zoneIndex
            && existing.ZoneOffset == zoneOffset && existing.Index == logicalOrdinal)
        {
            existing.Archetype = ArchetypeForDevice(physicalDevice);
            framesList.Add(existing);
            return;
        }

        layouts.TryGetValue(id, out var layout);
        var rot = existing?.Rotation ?? layout?.Rotation ?? 0;
        rot = ((rot % 360) + 360) % 360;
        if (rot != 0 && rot != 90 && rot != 180 && rot != 270)
        { rot = 0; }
        // Defaults come from the same helpers the HTTP DTO uses, so DeviceFrame
        // + LightingDevice agree on on-canvas size/position when nothing's been
        // persisted yet.
        var (dx, dy, dw, dh) = isStrip
            ? OpenRgbLightingDeviceProvider.DefaultStripLayout(canvasSlot)
            : OpenRgbLightingDeviceProvider.DefaultCardLayout(canvasSlot);
        var frame = new DeviceFrame(
            index: logicalOrdinal,
            id: id,
            ledCount: zoneLedCount,
            x: existing?.X ?? layout?.X ?? dx,
            y: existing?.Y ?? layout?.Y ?? dy,
            w: existing?.W ?? layout?.W ?? dw,
            h: existing?.H ?? layout?.H ?? dh,
            rotation: rot,
            physicalIndex: physicalIndex,
            zoneIndex: zoneIndex,
            zoneOffset: zoneOffset);
        // Full resolver stack (defaults -> applied mapping -> user deltas).
        // Default-partition zones keep the legacy zone-index resolution with
        // overrides mapped through their slices; custom zones resolve through
        // the partition-aware path.
        Nexus.Service.Lighting.Mappings.ResolvedLedLayout resolved;
        if (structure is not null && zone is not null && !zone.IsDefault)
        {
            resolved = Nexus.Service.Lighting.Mappings.LedLayoutResolver.ResolveZoneOpenRgb(
                physicalDevice, structure, zone, settings);
        }
        else
        {
            var ctx = structure is not null && zone is not null
                ? Nexus.Service.Lighting.Zones.ZoneResolution.ContextOf(structure, zone)
                : null;
            resolved = Nexus.Service.Lighting.Mappings.LedLayoutResolver.ResolveOpenRgb(
                physicalDevice, zoneIndex, id, settings, ctx);
        }
        Nexus.Service.Lighting.Mappings.LedLayoutResolver.ApplyToFrame(frame, resolved);
        frame.Archetype = ArchetypeForDevice(physicalDevice);
        framesList.Add(frame);
    }

    private bool IsOwnedByFirstParty(RgbDevice d)
    {
        for (int i = 0; i < _deviceOwners.Count; i++)
        {
            if (_deviceOwners[i].OwnsOpenRgbDevice(d))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ArchetypeForDevice(RgbDevice d) => OpenRgbZoneSupport.OpenRgbTypeName(d.Type) switch
    {
        "keyboard" => "keyboard",
        "mouse" => "mouse",
        "mousemat" => "mousepad",
        "headset" => "headset",
        _ => null
    };

    private static (Nexus.Service.Lighting.Zones.DeviceStructure Structure, Nexus.Service.Lighting.Zones.ResolvedZone Zone)? FindContributorZone(
        List<(Nexus.Service.Lighting.Zones.DeviceStructure structure, IReadOnlyList<Nexus.Service.Lighting.Zones.ResolvedZone> zones)>? contributorZones,
        string frameId)
    {
        if (contributorZones is null)
            return null;
        foreach (var (structure, zones) in contributorZones)
        {
            foreach (var zone in zones)
            {
                if (zone.Id == frameId)
                    return (structure, zone);
            }
        }
        return null;
    }

    /// <summary>
    /// Re-run the device/frame sync after a zone partition change so cards
    /// and engine frames reflect the new zones without waiting for the next
    /// poll tick. No-op while the bridge is inactive (no frames exist then).
    /// When the bridge is active but the controller is transiently
    /// disconnected, the request falls back to <see cref="ScheduleRefresh"/>
    /// so the rebuild is queued rather than dropped; if the controller is
    /// still down when the debounce elapses, the reconnect path
    /// (<see cref="EnsureConnectedAsync"/>) rebuilds frames from current
    /// settings as soon as the connection returns.
    /// </summary>
    public void RequestTopologyRefresh()
    {
        if (!IsActive)
            return;
        if (!_controller.IsConnected)
        {
            ScheduleRefresh();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            { await RefreshDevicesAsync().ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[rgb-bridge] topology refresh failed: {ex.Message}"); }
        });
    }

    private void SyncPhysicalBuffers(IReadOnlyList<RgbDevice> devices)
    {
        // Drop buffers for devices that no longer exist.
        var toRemove = new List<int>();
        foreach (var kv in _physBuffers)
        {
            var stillPresent = false;
            foreach (var d in devices)
            {
                if (d.Index == kv.Key)
                { stillPresent = true; break; }
            }
            if (!stillPresent)
                toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove)
            _physBuffers.TryRemove(k, out _);

        // Allocate or resize buffers for current devices.
        foreach (var d in devices)
        {
            if (d.LedCount <= 0)
                continue;
            if (!_physBuffers.TryGetValue(d.Index, out var buf) || buf.Length < d.LedCount)
            {
                _physBuffers[d.Index] = new RgbColor[d.LedCount];
            }
        }
    }

    /// <summary>
    /// Typical ARGB strip is 30 LEDs/metre; 60 covers two metres / most case loops.
    /// First-time-seen addressable motherboard zones get this value auto-applied so
    /// the user's default experience lights something up rather than showing a row
    /// of "0 LEDs" cards.
    /// </summary>
    private const int DefaultArgbZoneLedCount = 60;

    /// <summary>
    /// Drain queued zone resize requests, apply persisted ZoneLedCounts, and apply
    /// the default LED count (60) to any resizable linear motherboard zone that
    /// OpenRGB reports as 0 AND the user has never configured. ARGB is one-way so
    /// OpenRGB's reported 0 is really "unset"; the default lights the strip
    /// without the user configuring it first.
    /// Returns true if any resize opcode was actually sent (caller should re-fetch).
    /// </summary>
    private async Task<bool> ApplyZoneResizesAsync(IReadOnlyList<RgbDevice> devices)
    {
        var sent = false;

        while (_pendingZoneResizes.TryDequeue(out var req))
        {
            try
            {
                await _controller.ResizeZoneAsync(req.physIdx, req.zoneIdx, req.newSize).ConfigureAwait(false);
                sent = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[rgb-bridge] queued resize dev={req.physIdx} zone={req.zoneIdx} -> {req.newSize} failed: {ex.Message}");
            }
        }

        var persisted = _store.Load().Devices.ZoneLedCounts;

        // Apply user-configured counts.
        foreach (var d in devices)
        {
            if (d.Type != 0 || d.Zones.Count < 2)
                continue;
            for (int z = 0; z < d.Zones.Count; z++)
            {
                var zoneId = $"{d.StableId}-{z}";
                if (!persisted.TryGetValue(zoneId, out var desired))
                    continue;
                if (desired == d.Zones[z].LedCount)
                    continue;
                try
                {
                    await _controller.ResizeZoneAsync(d.Index, z, desired).ConfigureAwait(false);
                    sent = true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[rgb-bridge] apply persisted size {zoneId} -> {desired} failed: {ex.Message}");
                }
            }
        }

        // Auto-default motherboard zones the user hasn't touched yet. Both
        // single (12V RGB) and linear (5V ARGB) headers get the default when
        // OpenRGB reports "nothing configured" state (<= 1 LED). The 1-LED
        // case covers boards that report a placeholder LED on unconfigured
        // digital headers (AORUS B850I etc.) - a handful of boards do ship
        // real onboard 1-LED indicators, but those live on non-header
        // zones that don't pass the d.Zones.Count < 2 filter, and the user
        // can always override the seeded 60 via the LED-count editor.
        var toDefault = new List<(int physIdx, int zoneIdx, string id)>();
        foreach (var d in devices)
        {
            if (d.Type != 0 || d.Zones.Count < 2)
                continue;
            for (int z = 0; z < d.Zones.Count; z++)
            {
                var zone = d.Zones[z];
                var zoneId = $"{d.StableId}-{z}";
                if (persisted.ContainsKey(zoneId))
                    continue;
                if (zone.ZoneType != 0 && zone.ZoneType != 1)
                    continue;
                if (zone.LedCount > 1)
                    continue;
                toDefault.Add((d.Index, z, zoneId));
            }
        }
        if (toDefault.Count > 0)
        {
            _store.Update(s =>
            {
                foreach (var t in toDefault)
                {
                    s.Devices.ZoneLedCounts[t.id] = DefaultArgbZoneLedCount;
                }
            });
            foreach (var t in toDefault)
            {
                try
                {
                    await _controller.ResizeZoneAsync(t.physIdx, t.zoneIdx, DefaultArgbZoneLedCount).ConfigureAwait(false);
                    sent = true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[rgb-bridge] default size {t.id} -> {DefaultArgbZoneLedCount} failed: {ex.Message}");
                }
            }
        }

        return sent;
    }

    /// <summary>
    /// Periodic refresh. Does double duty: the GetDevicesAsync call inside
    /// RefreshDevicesAsync drains any unsolicited DEVICE_LIST_UPDATED packets
    /// OpenRGB pushed since last poll (they fire the DeviceListChanged event
    /// which in turn schedules a debounced second refresh). Diff-based reuse
    /// leaves stable devices unchanged, so each poll is low-cost.
    /// </summary>
    private async Task DeviceRefreshLoopAsync(CancellationToken ct)
    {
        var tick = 0;
        try
        {
            while (!ct.IsCancellationRequested && IsActive)
            {
                try
                { await Task.Delay(DeviceRefreshInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (!IsActive || ct.IsCancellationRequested)
                {
                    return;
                }
                if (!_controller.IsConnected)
                {
                    continue;
                }
                await RefreshDevicesAsync().ConfigureAwait(false);

                // USB hot-plug watcher: cheap-ish enumeration every other tick.
                // If the count changes, the OpenRGB subprocess likely missed the
                // new hardware (headless daemon doesn't auto-rescan), so we bounce.
                if (++tick % UsbCheckEveryNTicks == 0)
                {
                    CheckUsbTopology();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rgb-bridge] refresh loop crashed: {ex.Message}");
        }
    }

    private void CheckUsbTopology()
    {
        int currentCount;
        try
        {
            currentCount = _usb.Enumerate().Count;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rgb-bridge] usb enumerate failed: {ex.Message}");
            return;
        }

        var previousCount = _lastUsbCount;
        _lastUsbCount = currentCount;
        if (previousCount < 0 || previousCount == currentCount)
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        var lastBounce = Interlocked.Read(ref _lastBounceTicks);
        if (lastBounce != 0 && now - lastBounce < BounceCooldown.Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref _lastBounceTicks, now);
        ServiceLog.Info($"[rgb-bridge] usb topology changed ({previousCount} -> {currentCount}), bouncing subprocess for re-detect");
        BounceSubprocess();
    }

    private void OnFrame(ReadOnlyMemory<byte> frameMem)
    {
        if (!IsActive)
        {
            return;
        }

        if (!_controller.IsConnected)
        { _ = Task.Run(EnsureConnectedAsync); return; }

        var frame = frameMem.Span;
        int pos;
        if (frame.Length >= 5 && frame[0] == 0x03)
        {
            var cw = (ushort)(frame[1] | (frame[2] << 8));
            var ch = (ushort)(frame[3] | (frame[4] << 8));
            pos = 5 + cw * ch * 3;
        }
        else if (frame.Length >= 2 && frame[0] == 0x02)
        {
            pos = 2;
        }
        else
        {
            return;
        }

        if (pos >= frame.Length)
        {
            return;
        }

        // engine.Devices is a volatile DeviceFrame[]; read once for a stable
        // view of this frame. Each DeviceFrame carries its physical OpenRGB
        // index + zone offset so we can aggregate split motherboard zones back
        // into a single per-physical-device push.
        var deviceFrames = _engine.Devices;
        if (deviceFrames.Length == 0)
        {
            return;
        }

        // Safe because SetPower/SetDisabled replace the list reference rather than
        // mutating in place - whatever we read here won't change under us.
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var disabledCount = disabled.Count;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var uncontrolledCount = uncontrolled.Count;
        var devicePrefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        _touchedPhysicals.Clear();
        ComputeFullyUncontrolledPhysicals(deviceFrames, _bridgeFrameIds, uncontrolled, _physFullyUncontrolled);

        var deviceCount = frame[pos++];
        for (int d = 0; d < deviceCount && pos + 3 <= frame.Length; d++)
        {
            var logicalIndex = frame[pos++];
            var ledCount = (ushort)(frame[pos] | (frame[pos + 1] << 8));
            pos += 2;
            var rgbSize = ledCount * 3;
            if (pos + rgbSize > frame.Length)
            {
                break;
            }

            DeviceFrame? dev = null;
            for (int i = 0; i < deviceFrames.Length; i++)
            {
                if (deviceFrames[i].Index == logicalIndex)
                { dev = deviceFrames[i]; break; }
            }
            if (dev is null || dev.LedCount <= 0
                || !IsBridgeFrame(dev, _bridgeFrameIds)
                || !_physBuffers.TryGetValue(dev.PhysicalIndex, out var buffer) || buffer is null)
            {
                pos += rgbSize;
                continue;
            }

            var zoneOffset = dev.ZoneOffset;
            var writeLen = Math.Min(Math.Min(dev.LedCount, ledCount), buffer.Length - zoneOffset);
            if (writeLen <= 0)
            {
                pos += rgbSize;
                continue;
            }

            if (uncontrolledCount > 0 && _physFullyUncontrolled.TryGetValue(dev.PhysicalIndex, out var physUncontrolled) && physUncontrolled)
            {
                pos += rgbSize;
                continue;
            }

            var isOff = (disabledCount > 0 && disabled.Contains(dev.Id))
                || (uncontrolledCount > 0 && uncontrolled.Contains(dev.Id));
            var hasIdentify = _identifyOverrides.TryGetValue(dev.Id, out var idOverride)
                && nowTicks < idOverride.expirationTicks;
            if (!hasIdentify && idOverride.expirationTicks != 0)
            {
                _identifyOverrides.TryRemove(dev.Id, out _);
            }

            // Effective brightness multiplier: the master level caps the
            // per-device slider, so a zone never renders brighter than master
            // (effective = min(device/100, global)). Identify ignores brightness
            // so the flash always reads as max-bright white even when the user
            // has dimmed the device or the master level.
            // The Dictionary<,> on LightingDevicePrefs is mutated in place by
            // SetBrightness writers; a concurrent insert during this read can
            // throw InvalidOperationException. Catch it and fall back to full
            // brightness for this frame; the next frame will see the new state.
            int devBrightness;
            try { devBrightness = devicePrefs.TryGetValue(dev.Id, out var pref) ? pref.Brightness : 100; }
            catch (InvalidOperationException) { devBrightness = 100; }
            var brightnessMul = Math.Min(Math.Clamp(devBrightness, 0, 100) / 100.0, globalBrightness);

            if (isOff)
            {
                for (int led = 0; led < writeLen; led++)
                {
                    buffer[zoneOffset + led] = RgbColor.Black;
                }
            }
            else if (hasIdentify)
            {
                // 500ms cycle (250ms on, 250ms off) -> 4 flashes per 2s window.
                // Math.Max guards against the const ever being lowered below 2,
                // which would zero the integer divisor and throw DivideByZero.
                var elapsedMs = (nowTicks - idOverride.startTicks) / TimeSpan.TicksPerMillisecond;
                var halfPeriod = Math.Max(1, IdentifyFlashPeriodMs / 2);
                var on = (elapsedMs / halfPeriod) % 2 == 0;
                var flash = on ? new RgbColor(255, 255, 255) : RgbColor.Black;
                for (int led = 0; led < writeLen; led++)
                {
                    buffer[zoneOffset + led] = flash;
                }
            }
            else if (brightnessMul >= 0.999)
            {
                for (int led = 0; led < writeLen; led++)
                {
                    var off2 = pos + led * 3;
                    buffer[zoneOffset + led] = new RgbColor(frame[off2], frame[off2 + 1], frame[off2 + 2]);
                }
            }
            else if (brightnessMul <= 0.0)
            {
                for (int led = 0; led < writeLen; led++)
                {
                    buffer[zoneOffset + led] = RgbColor.Black;
                }
            }
            else
            {
                for (int led = 0; led < writeLen; led++)
                {
                    var off2 = pos + led * 3;
                    buffer[zoneOffset + led] = new RgbColor(frame[off2], frame[off2 + 1], frame[off2 + 2]).Scale(brightnessMul);
                }
            }
            _touchedPhysicals.Add(dev.PhysicalIndex);
            pos += rgbSize;
        }

        foreach (var physIdx in _touchedPhysicals)
        {
            if (_physBuffers.TryGetValue(physIdx, out var buf))
            {
                _ = _controller.PushFrameAsync(physIdx, buf);
            }
        }
    }

    /// <summary>
    /// True when a wire-frame-matched DeviceFrame is one of this bridge's own
    /// OpenRGB frames. A contributor frame (NP50, Keeb, hubs) defaults its
    /// PhysicalIndex to the engine ordinal, which can equal a real OpenRGB
    /// device index once first-party-owned devices are excluded from seeding;
    /// without this check its bytes would land in that device's buffer and
    /// push to the wrong hardware. Keyed on frame id, not physical index,
    /// because split-motherboard zone frames legitimately share one physical
    /// index. Static and bridge-free so tests cover it with fake frame data.
    /// </summary>
    internal static bool IsBridgeFrame(DeviceFrame dev, IReadOnlySet<string> bridgeFrameIds) =>
        bridgeFrameIds.Contains(dev.Id);

    /// <summary>
    /// Fills <paramref name="result"/> (cleared first) with physical index ->
    /// true when every bridge-built zone frame mapped to it is uncontrolled.
    /// Frames whose id is absent from <paramref name="bridgeFrameIds"/> are
    /// skipped: see <see cref="IsBridgeFrame"/> for why a controlled contributor
    /// must not veto an uncontrolled OpenRGB device sharing its index.
    /// </summary>
    internal static void ComputeFullyUncontrolledPhysicals(
        IReadOnlyList<DeviceFrame> deviceFrames,
        IReadOnlySet<string> bridgeFrameIds,
        IReadOnlyList<string> uncontrolled,
        Dictionary<int, bool> result)
    {
        result.Clear();
        if (uncontrolled.Count == 0)
        {
            return;
        }
        foreach (var df in deviceFrames)
        {
            if (!bridgeFrameIds.Contains(df.Id))
            {
                continue;
            }
            var zoneUncontrolled = uncontrolled.Contains(df.Id);
            result[df.PhysicalIndex] = result.TryGetValue(df.PhysicalIndex, out var allSoFar)
                ? allSoFar && zoneUncontrolled
                : zoneUncontrolled;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
        Deactivate();
    }
}

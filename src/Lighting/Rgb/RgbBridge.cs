using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lifecycle;
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
///         active effect. We unsubscribe and stop the subprocess; blacking the
///         hardware out first is the caller's job, via
///         <see cref="BlackoutAsync"/>.</item>
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

    // Covers the PawnIO install gate LhmComputer waits on plus the open itself.
    private static readonly TimeSpan LhmEnumerationWait = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Minimum time the "scanning" flag stays true on initial boot (baseline 0).
    /// OpenRGB trickles devices in over a few seconds - Razer HID is first and
    /// almost instant, then Corsair/Gigabyte/NVIDIA take 2-6s more. Holds the
    /// spinner for this window rather than clearing when the first device lands.
    /// </summary>
    private static readonly TimeSpan InitialRescanMinHold = TimeSpan.FromSeconds(5);
    /// <summary>Distinct settled lists remembered for log de-duplication before the set is dropped.</summary>
    private const int MaxLoggedDeviceSignatures = 16;
    // TryMarkLogged forgets its whole set past the cap, so this must hold every
    // zone of every board at once or an unconverged zone re-logs each tick.
    private const int MaxLoggedResizeOutcomes = 64;

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

    // Deferred spawn from the last Activate. Deactivate's shutdown task awaits
    // it before stopping, so a start that already passed its guard is always
    // stopped rather than left running unowned.
    private Task? _pendingStart;
    private long _lastConnectAttemptTicks; // DateTime.UtcNow.Ticks; updated via Interlocked
    private long _lastBounceTicks;         // DateTime.UtcNow.Ticks; updated via Interlocked
    private long _rescanStartedTicks;      // 0 = no active rescan; else = UtcNow ticks at bounce start
    private int _rescanBaselineCount;      // device count snapshotted when rescan started
    private int _refreshPending;           // 0 = idle, 1 = refresh scheduled/running
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private Dictionary<string, int>? _lastUsbKeys; // key -> unit count; null = not yet observed, no bounce on first read
    // Settled-list contents already written to the log, so the refresh poll re-logs
    // only shapes not seen yet. A set rather than the last value: a device that
    // flaps between two shapes would otherwise re-log on every poll for the life of
    // the run, and the service log has no size cap. Guarded by _lock.
    private readonly HashSet<string> _loggedDeviceSignatures = new(StringComparer.Ordinal);
    // Resize outcomes already logged; the resize pass reruns every refresh tick.
    // Guarded by _lock.
    private readonly HashSet<string> _loggedResizeOutcomes = new(StringComparer.Ordinal);
    // StableIds already warned about an exclusion the daemon did not honor.
    private readonly HashSet<string> _warnedIneffectiveExclusions = new(StringComparer.Ordinal);

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

    // Dedup + heartbeat, mirroring Slv3LightingFrameWriter's _lastSent /
    // _lastPushTicks. A frame identical to the last one pushed is skipped, but
    // every device is re-pushed at least every HeartbeatMs regardless, so a
    // device that silently lost state - direct-mode timeout, USB
    // re-enumeration, another OpenRGB client writing over us - still self-heals
    // the way the unconditional 30fps push did, just a second later instead of
    // 33ms. Cleared wholesale in SyncPhysicalBuffers (device list changed) and
    // after BlackoutAsync, so a stale entry can never suppress a real write.
    private readonly ConcurrentDictionary<int, RgbColor[]> _lastPushed = new();
    private readonly ConcurrentDictionary<int, long> _lastPushTicks = new();
    private const long HeartbeatMs = 1000;
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

    private readonly FeatureGates _gates;

    public RgbBridge(OpenRgbProcessManager proc, IRgbController controller, LightingEngine engine, IConfigStore store, IUsbEnumerator usb,
        IEnumerable<ILightingFrameContributor>? frameContributors = null,
        Nexus.Service.Lighting.Mappings.ContributorFrameLayouts? contributorLayouts = null,
        FeatureGates? gates = null)
    {
        _proc = proc;
        _controller = controller;
        _engine = engine;
        _store = store;
        _usb = usb;
        _gates = gates ?? FeatureGates.AllEnabled;
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
            // Hard backstop: OpenRGB may never spawn while Lighting is off,
            // regardless of caller (routes, MCP, Deck, auto-restore, a power-
            // resume reconnect). Re-checked here, not before the lock, so a
            // caller that read the gate as true cannot win a race against a
            // Suspend() that flips it and deactivates under the same lock.
            if (!_gates.Lighting || _disposed || _active)
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

        // Captured before any await: Deactivate disposes the source, and
        // reading .Token off a disposed CancellationTokenSource throws.
        var refreshToken = newCts.Token;

        var pendingStart = Task.Run(async () =>
        {
            await Nexus.Service.Lifecycle.StartupDelayGate.WaitAsync().ConfigureAwait(false);

            // OpenRGB reaches DIMM RGB controllers over the SMBus through
            // PawnIO, and its detection runs exactly once - a bus probe that
            // returns nothing leaves that stick missing for the session. Two
            // startup hazards make that likely, and waiting for LHM's open
            // clears both: PawnIO may still be installing (OpenRGB then finds
            // zero busses and skips DIMMs entirely), and LHM's own SPD reads
            // drive the same controller without taking OpenRGB's
            // Global\Access_SMBUS.HTP.Method mutex, so concurrent transactions
            // are unarbitrated. Completes immediately once startup is past.
            if (Nexus.Service.Lifecycle.PawnIoBootGate.IsArmed)
            {
                await Nexus.Service.Lifecycle.PawnIoBootGate
                    .WaitForLhmOpenAsync(LhmEnumerationWait).ConfigureAwait(false);
            }

            lock (_lock)
            {
                // A Deactivate() landing inside the wait must not leave an
                // orphaned daemon behind, and an Activate/Deactivate/Activate
                // burst must spawn one daemon, not one per deferred task -
                // only the task still owning the current cycle proceeds.
                if (!_active || _disposed || !ReferenceEquals(_refreshCts, newCts))
                {
                    return;
                }

                // Initial boot is itself a rescan from the user's perspective:
                // OpenRGB subprocess starts, detection plugins run, devices
                // trickle in. Set the rescan flag with baseline 0 so the UI
                // spinner runs until at least one device shows up (or the
                // grace period expires).
                _rescanBaselineCount = 0;
            }
            Interlocked.Exchange(ref _rescanStartedTicks, DateTime.UtcNow.Ticks);

            _proc.Start();

            _ = Task.Run(EnsureConnectedAsync);
            _ = Task.Run(() => DeviceRefreshLoopAsync(refreshToken));
        });

        lock (_lock)
        {
            _pendingStart = pendingStart;
        }
    }

    /// <summary>
    /// Take the bridge offline: disconnect from the SDK server and hard-kill the
    /// subprocess. Nothing is written to hardware here - a caller that needs the
    /// devices dark awaits <see cref="BlackoutAsync"/> first.
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
            _loggedDeviceSignatures.Clear();
            _loggedResizeOutcomes.Clear();
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

        Task? pendingStart;
        lock (_lock)
        { pendingStart = _pendingStart; }

        var task = Task.Run(async () =>
        {
            // A deferred spawn that already passed its guard still has to be
            // stopped, so let it finish starting before killing the process.
            if (pendingStart is not null)
            {
                try
                { await pendingStart.ConfigureAwait(false); }
                catch { }
            }
            try
            { await _controller.DisconnectAsync().ConfigureAwait(false); }
            catch { }
            _proc.Stop();
        });

        lock (_lock)
        {
            _shutdownTask = task;
            _pendingStart = null;
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

    /// <summary>Physicals holding a frame buffer, which a blackout is limited to; populated after <see cref="Devices"/> is committed, not with it.</summary>
    internal int PhysicalBufferCount => _physBuffers.Count;

    /// <summary>
    /// Pushes an all-black frame to every device this bridge drives and AWAITS
    /// each write. The <see cref="OnFrame"/> path fires its pushes and forgets
    /// them, which is fine at 30 fps and useless on the suspend path, where the
    /// caller has to know the bytes reached the hardware before the machine
    /// stops. Devices the user marked not-controlled are skipped: Nexus does not
    /// drive them, so it has no business blanking them either.
    /// </summary>
    public async Task<int> BlackoutAsync(CancellationToken ct = default)
    {
        if (!IsActive || !_controller.IsConnected)
        {
            return 0;
        }
        var pushed = 0;
        // Local scratch, not the _physFullyUncontrolled field OnFrame owns:
        // this runs on the power-event thread while the engine may still tick.
        var fullyUncontrolled = new Dictionary<int, bool>();
        ComputeFullyUncontrolledPhysicals(
            _engine.Devices, _bridgeFrameIds, _store.Load().Devices.UncontrolledLightingDevices, fullyUncontrolled);
        foreach (var kv in _physBuffers)
        {
            // PushFrameAsync swallows its own failures (cancellation included)
            // and drops the socket, so this is the only place the budget is
            // honoured - and the only reason a partial run is safe: resume
            // bounces the subprocess, which reconnects from scratch.
            ct.ThrowIfCancellationRequested();
            if (!_controller.IsConnected)
            {
                return pushed;
            }
            if (fullyUncontrolled.TryGetValue(kv.Key, out var skip) && skip)
            {
                continue;
            }
            var buffer = kv.Value;
            Array.Clear(buffer, 0, buffer.Length);
            await _controller.PushFrameAsync(kv.Key, buffer, ct).ConfigureAwait(false);
            pushed++;
        }

        // This path wrote black outside OnFrame's bookkeeping; drop the
        // baselines so the first frame after resume always reaches hardware.
        _lastPushed.Clear();
        _lastPushTicks.Clear();
        return pushed;
    }

    /// <summary>
    /// Triggered by the OS power-resume event handler. Tears down and restarts
    /// the subprocess so devices re-init after the USB stack re-enumerates.
    /// </summary>
    public void OnSystemResume() => BounceSubprocess("system-resume");

    /// <summary>
    /// User-initiated rescan. The OpenRGB SDK has no RESCAN opcode, so the only
    /// way to force its detection plugins to re-run is to restart the subprocess.
    /// Use this when a device was plugged but OpenRGB never fired DEVICE_LIST_UPDATED
    /// (plugin that only scans at startup, etc.).
    /// </summary>
    public void ForceRescan() => BounceSubprocess("user-rescan");

    /// <summary>Restarts the daemon so it re-reads OpenRGB.json; manual device registrations are only picked up at launch.</summary>
    public void BounceForManualDevices() => BounceSubprocess("manual-devices");

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

    private void BounceSubprocess(string reason)
    {
        if (!IsActive)
        {
            ServiceLog.Info($"[rgb-bridge] bounce skipped ({reason}): bridge not active");
            return;
        }

        Task? pendingStart;
        lock (_lock)
        { pendingStart = _pendingStart; }
        if (pendingStart is not null && !pendingStart.IsCompleted)
        {
            // The deferred spawn has not run yet. It performs the detection a
            // bounce would force, and starting the process here would skip the
            // SMBus wait that spawn exists for.
            ServiceLog.Info($"[rgb-bridge] bounce skipped ({reason}): deferred spawn still pending");
            return;
        }

        Interlocked.Exchange(ref _lastConnectAttemptTicks, 0);
        // Snapshot the current device count so RefreshDevicesAsync can hold the
        // visible list steady until OpenRGB's detection plugins report at least
        // that many devices again (or the grace period expires).
        int baseline;
        lock (_lock)
        {
            _rescanBaselineCount = _devices.Count;
            baseline = _rescanBaselineCount;
            // A bounce reconstructs every controller, so an identical list is still
            // new information. Only these two reasons can settle unchanged; a
            // topology or exclusion bounce alters the list, so its signature re-logs
            // on its own and clearing here would re-print the block on every flap.
            if (reason is "user-rescan" or "system-resume")
            {
                _loggedDeviceSignatures.Clear();
            }
            _loggedResizeOutcomes.Clear();
        }
        ServiceLog.Info($"[rgb-bridge] bouncing subprocess ({reason}), baseline {baseline} device(s)");
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
            var resizeAttempts = await ApplyZoneResizesAsync(devices).ConfigureAwait(false);
            if (resizeAttempts.Count > 0)
            {
                var refetched = false;
                try
                {
                    devices = await _controller.GetDevicesAsync().ConfigureAwait(false);
                    refetched = true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[rgb-bridge] re-fetch after resize failed: {ex.Message}");
                }
                // The pre-resize list reports every attempt as failed, and the
                // transient empty the block below absorbs reports -1.
                if (refetched && devices.Count > 0)
                {
                    LogZoneResizeOutcomes(resizeAttempts, devices);
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

            // Settled commits reconcile detector exclusions: a device the user
            // fully un-controlled gets snapshotted + denylisted (and the
            // subprocess bounced once to release it); a re-controlled one gets
            // its exclusion lifted the same way.
            if (!inInitialHold)
            {
                LogSettledDevices(finalList);
                ReconcileDetectorExclusions(finalList, settingsSnapshot);
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
                // Excluded devices render from their persisted snapshot; the
                // live entry (pre-bounce real device or the fork's placeholder
                // dummy) must not occupy an engine frame or a canvas slot.
                if (settingsSnapshot.Devices.OpenRgbDetectorExclusions.ContainsKey(d.StableId)) continue;
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

    /// <summary>
    /// Logs each distinct settled list once, so a boot and each rescan are directly
    /// comparable. Location is the transport path the daemon opened, the only signal
    /// saying which of a device's matching HID collections it bound - identity reads
    /// look plausible over any of them. Zero devices logs too: that is a diagnosis.
    /// </summary>
    private void LogSettledDevices(IReadOnlyList<RgbDevice> devices)
    {
        var current = BuildDeviceSignature(devices);
        lock (_lock)
        {
            if (!TryMarkLogged(_loggedDeviceSignatures, current))
            {
                return;
            }
        }

        ServiceLog.Info($"[rgb-bridge] settled on {devices.Count} device(s):");
        foreach (var d in devices)
        {
            ServiceLog.Info($"[rgb-bridge]   [{d.Index}] {d.Name} leds={d.LedCount}{DescribeZones(d)}"
                + $" sn={(string.IsNullOrEmpty(d.Serial) ? "-" : d.Serial)}"
                + $" loc={(string.IsNullOrEmpty(d.Location) ? "-" : d.Location)}");
            var zoneTotal = SumZoneLeds(d);
            if (d.Zones.Count > 0 && zoneTotal != d.LedCount)
            {
                // These diverge when a RESIZEZONE changed a zone's count without the
                // controller rebuilding its LED buffer, leaving every later zone
                // reading at a stale offset.
                ServiceLog.Warn($"[rgb-bridge]   [{d.Index}] {d.Name} zone total {zoneTotal} != device leds {d.LedCount}: zone data lands at the wrong offsets on this device");
            }
        }
    }

    /// <summary>
    /// Records a signature as logged, returning false when it already was. Forgets
    /// everything once the set outgrows its cap so churn cannot grow without bound.
    /// </summary>
    internal static bool TryMarkLogged(HashSet<string> logged, string signature, int cap = MaxLoggedDeviceSignatures)
    {
        if (!logged.Add(signature))
        {
            return false;
        }
        if (logged.Count > cap)
        {
            logged.Clear();
            logged.Add(signature);
        }
        return true;
    }

    /// <summary>
    /// Change key for the settled list. Covers every field the log line carries, so
    /// a device that re-enumerates onto a different transport path re-logs even
    /// though its name, index and LED count are unchanged. Zone counts are included
    /// because a controller can hold its device LED count steady while its zone
    /// table moves.
    /// </summary>
    internal static string BuildDeviceSignature(IReadOnlyList<RgbDevice> devices)
    {
        var signature = new StringBuilder();
        foreach (var d in devices)
        {
            signature.Append(d.Index).Append('|').Append(d.Name).Append('|').Append(d.LedCount)
                .Append('|').Append(d.Serial).Append('|').Append(d.Location)
                .Append('|').Append(DescribeZones(d)).Append('\n');
        }
        return signature.ToString();
    }

    /// <summary>
    /// Persist the exclusion delta computed from a settled device list, then
    /// bounce the subprocess once so the relaunched daemon reads the rewritten
    /// OpenRGB.json denylist (the config is only read at process start).
    /// </summary>
    private void ReconcileDetectorExclusions(IReadOnlyList<RgbDevice> settledList, NexusSettings settingsSnapshot)
    {
        OpenRgbDetectorExclusions.Delta delta;
        try
        {
            // Reloaded per reconcile: the daemon rewrites the map whenever its
            // device list changes, including after this pass started.
            var detectorMap = OpenRgbDetectorMap.Load(OpenRgbProcessManager.ResolveConfigDir());
            delta = OpenRgbDetectorExclusions.Compute(settledList, settingsSnapshot, detectorMap);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rgb-bridge] exclusion compute failed: {ex.Message}");
            return;
        }
        if (delta.IsEmpty)
        {
            // Steady state. A drivable device still live under an existing
            // exclusion means the denylisted name did not match a detector
            // string and detector-map.json could not correct it (an old daemon
            // writes no map; a device detected after the map was written is
            // missing from it) - the denylist write is then a silent no-op in
            // the daemon, so surface it once per device. The
            // bounce-age gate keeps a refresh that raced the exclusion's own
            // bounce (fetched from the not-yet-killed daemon) from warning
            // spuriously and permanently eating the one warn per id.
            var lastBounce = Interlocked.Read(ref _lastBounceTicks);
            var bounceSettled = lastBounce == 0 || DateTime.UtcNow.Ticks - lastBounce >= RescanGracePeriod.Ticks;
            if (bounceSettled && Interlocked.Read(ref _rescanStartedTicks) == 0)
            {
                foreach (var d in settledList)
                {
                    if (d.LedCount > 0
                        && settingsSnapshot.Devices.OpenRgbDetectorExclusions.ContainsKey(d.StableId)
                        && _warnedIneffectiveExclusions.Add(d.StableId))
                    {
                        ServiceLog.Warn($"[rgb-bridge] '{d.Name}' is still detected despite its detector exclusion; detector-map.json has no entry naming its detector");
                    }
                }
            }
            return;
        }

        _store.Update(s => OpenRgbDetectorExclusions.Apply(s, delta));
        ServiceLog.Info($"[rgb-bridge] detector exclusions changed (+{delta.Add.Count}/-{delta.Remove.Count}), applying");
        Interlocked.Exchange(ref _lastBounceTicks, DateTime.UtcNow.Ticks);
        BounceSubprocess("detector-exclusions");
    }

    /// <summary>True when this device's bytes differ from the last push, or the
    /// heartbeat is due. Records what it approves as the new baseline.
    /// Compares as bytes: RgbColor has no IEquatable, so Span.SequenceEqual over
    /// the structs themselves would fall back to reflective ValueType.Equals and
    /// cost more than the push it saves.</summary>
    private bool ShouldPush(int physIdx, RgbColor[] buf)
    {
        var now = Environment.TickCount64;
        if (_lastPushed.TryGetValue(physIdx, out var last)
            && last.Length == buf.Length
            && _lastPushTicks.TryGetValue(physIdx, out var when)
            && now - when < HeartbeatMs
            && System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan())
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(last.AsSpan())))
        {
            return false;
        }

        var copy = last is not null && last.Length == buf.Length ? last : new RgbColor[buf.Length];
        buf.AsSpan().CopyTo(copy);
        _lastPushed[physIdx] = copy;
        _lastPushTicks[physIdx] = now;
        return true;
    }

    private void SyncPhysicalBuffers(IReadOnlyList<RgbDevice> devices)
    {
        // Indices are being re-derived, so every dedup baseline is suspect.
        _lastPushed.Clear();
        _lastPushTicks.Clear();

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
    /// A header we may seed with <see cref="DefaultArgbZoneLedCount"/>: a single or
    /// linear zone with nothing configured yet. ZoneType alone does not mean
    /// resizable, so zones the controller pins are excluded: ASRock Polychrome
    /// stamps every zone linear, including 12V headers fixed at one LED.
    /// </summary>
    internal static bool CanSeedDefaultZoneCount(RgbZone zone)
        => (zone.ZoneType == 0 || zone.ZoneType == 1) && zone.LedCount <= 1 && !zone.IsFixedSize;

    /// <summary>
    /// Drain queued zone resize requests, apply persisted ZoneLedCounts, and apply
    /// the default LED count (60) to any resizable linear motherboard zone that
    /// OpenRGB reports as 0 AND the user has never configured. ARGB is one-way so
    /// OpenRGB's reported 0 is really "unset"; the default lights the strip
    /// without the user configuring it first.
    /// Returns every resize sent, so the caller can re-fetch and report the outcome.
    /// </summary>
    private async Task<List<ZoneResizeAttempt>> ApplyZoneResizesAsync(IReadOnlyList<RgbDevice> devices)
    {
        var attempts = new List<ZoneResizeAttempt>();

        while (_pendingZoneResizes.TryDequeue(out var req))
        {
            var queued = DescribeZone(devices, req.physIdx, req.zoneIdx);
            try
            {
                await _controller.ResizeZoneAsync(req.physIdx, req.zoneIdx, req.newSize).ConfigureAwait(false);
                attempts.Add(new ZoneResizeAttempt(req.physIdx, req.zoneIdx, queued.Name, queued.Current, req.newSize, "queued"));
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
                // Resizing a zone the controller pins never holds, and this pass
                // reruns every refresh, so an unsatisfiable count would retry forever.
                if (d.Zones[z].IsFixedSize)
                    continue;
                try
                {
                    await _controller.ResizeZoneAsync(d.Index, z, desired).ConfigureAwait(false);
                    attempts.Add(new ZoneResizeAttempt(d.Index, z, ZoneName(d.Zones[z], z), d.Zones[z].LedCount, desired, "persisted"));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[rgb-bridge] apply persisted size {zoneId} -> {desired} failed: {ex.Message}");
                }
            }
        }

        // Auto-default motherboard zones the user hasn't touched yet. Both
        // single (12V RGB) and linear (5V ARGB) headers get the default when
        // OpenRGB reports "nothing configured" state (<= 1 LED), which on most
        // boards is a placeholder LED on an unconfigured digital header (AORUS
        // B850I etc.). A header genuinely fixed at one LED reports leds_min ==
        // leds_max and is excluded; the user can override the seeded 60 via the
        // LED-count editor.
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
                if (!CanSeedDefaultZoneCount(zone))
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
                    var seeded = DescribeZone(devices, t.physIdx, t.zoneIdx);
                    attempts.Add(new ZoneResizeAttempt(t.physIdx, t.zoneIdx, seeded.Name, seeded.Current, DefaultArgbZoneLedCount, "default"));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[rgb-bridge] default size {t.id} -> {DefaultArgbZoneLedCount} failed: {ex.Message}");
                }
            }
        }

        return attempts;
    }

    /// <summary>One RESIZEZONE we sent, carried to the post-refetch pass that reports whether it took.</summary>
    private readonly record struct ZoneResizeAttempt(int PhysIdx, int ZoneIdx, string ZoneName, int From, int To, string Source);

    /// <summary>
    /// RESIZEZONE has no reply, so a re-read is the only confirmation. Both totals
    /// print because a controller can accept the zone change and leave its device
    /// LED buffer at the old size, which verifies fine per zone and is still wrong.
    /// </summary>
    private void LogZoneResizeOutcomes(IReadOnlyList<ZoneResizeAttempt> attempts, IReadOnlyList<RgbDevice> devices)
    {
        foreach (var a in attempts)
        {
            var observed = DescribeZone(devices, a.PhysIdx, a.ZoneIdx).Current;
            var device = FindDevice(devices, a.PhysIdx);
            var deviceLeds = device is null ? -1 : device.LedCount;
            var zoneTotal = device is null ? -1 : SumZoneLeds(device);
            lock (_lock)
            {
                if (!TryMarkLogged(_loggedResizeOutcomes, $"{a.PhysIdx}:{a.ZoneIdx}:{a.To}:{observed == a.To}", MaxLoggedResizeOutcomes))
                {
                    continue;
                }
            }
            var zone = device is not null && a.ZoneIdx < device.Zones.Count ? device.Zones[a.ZoneIdx] : null;
            if (zone is not null && zone.IsFixedSize)
            {
                ServiceLog.Warn($"[rgb-bridge] dev={a.PhysIdx} zone={a.ZoneIdx} '{a.ZoneName}' advertises leds_min=leds_max={zone.LedsMin}, so it is not resizable and the resize we sent it cannot hold");
            }
            var what = $"dev={a.PhysIdx} zone={a.ZoneIdx} '{a.ZoneName}' {a.From} -> {a.To} ({a.Source})";
            var totals = $"device leds={deviceLeds}, zone total={zoneTotal}";
            if (observed == a.To)
            {
                ServiceLog.Info($"[rgb-bridge] resize took: {what}; {totals}");
            }
            else
            {
                ServiceLog.Warn($"[rgb-bridge] resize did not take: {what}, zone still reports {observed}; {totals}");
            }
        }
    }

    private static RgbDevice? FindDevice(IReadOnlyList<RgbDevice> devices, int index)
    {
        foreach (var d in devices)
        {
            if (d.Index == index)
            {
                return d;
            }
        }
        return null;
    }

    private static string ZoneName(RgbZone zone, int zoneIdx)
        => string.IsNullOrWhiteSpace(zone.Name) ? $"zone {zoneIdx}" : zone.Name;

    /// <summary>Zone name and current LED count, or a placeholder when the index is gone.</summary>
    private static (string Name, int Current) DescribeZone(IReadOnlyList<RgbDevice> devices, int physIdx, int zoneIdx)
    {
        var d = FindDevice(devices, physIdx);
        if (d is not null && zoneIdx >= 0 && zoneIdx < d.Zones.Count)
        {
            return (ZoneName(d.Zones[zoneIdx], zoneIdx), d.Zones[zoneIdx].LedCount);
        }
        return ($"zone {zoneIdx}", -1);
    }

    internal static int SumZoneLeds(RgbDevice d)
    {
        var total = 0;
        foreach (var z in d.Zones)
        {
            total += z.LedCount;
        }
        return total;
    }

    /// <summary>Per-zone LED counts for the log. Omitted below two zones, where the device total already says it.</summary>
    internal static string DescribeZones(RgbDevice d)
    {
        if (d.Zones.Count < 2)
        {
            return "";
        }
        var sb = new StringBuilder(" zones=[");
        for (int i = 0; i < d.Zones.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            var z = d.Zones[i];
            sb.Append(z.LedCount);
            // Printing the bound, not just "fixed", is what shows a count the
            // controller cannot be holding: a zone pinned at 1 reporting 60.
            if (z.IsFixedSize)
            {
                sb.Append("(fixed@").Append(z.LedsMin).Append(')');
            }
            else if (z.LedsMax > 0 && (z.LedCount < z.LedsMin || z.LedCount > z.LedsMax))
            {
                sb.Append('(').Append(z.LedsMin).Append('-').Append(z.LedsMax).Append(')');
            }
        }
        return sb.Append(']').ToString();
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
                // A diff on the per-device key set from an RGB-capable vendor
                // means the OpenRGB subprocess likely missed new hardware
                // (headless daemon doesn't auto-rescan), so we bounce.
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
        Dictionary<string, int> current;
        try
        {
            current = UsbTopologyFilter.BuildKeys(_usb.Enumerate());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rgb-bridge] usb enumerate failed: {ex.Message}");
            return;
        }

        var previous = _lastUsbKeys;
        _lastUsbKeys = current;
        if (previous is null)
        {
            return;
        }

        var (added, removed, relevant) = UsbTopologyFilter.Classify(previous, current, Nexus.Service.Peripherals.LightingDevicesCatalog.UsbVendorIds);
        if (added == 0 && removed == 0)
        {
            return;
        }
        if (!relevant)
        {
            // A flash drive / phone / dock churn. OpenRGB has no detector for
            // the vendor, so a full re-detect would only glitch live lighting.
            ServiceLog.Info($"[rgb-bridge] usb topology changed (+{added}/-{removed}), no RGB-capable vendor involved, skipping re-detect");
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        var lastBounce = Interlocked.Read(ref _lastBounceTicks);
        if (lastBounce != 0 && now - lastBounce < BounceCooldown.Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref _lastBounceTicks, now);
        ServiceLog.Info($"[rgb-bridge] usb topology changed (+{added}/-{removed}, rgb-relevant), re-detect needed");
        BounceSubprocess("usb-topology");
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
            // One lookup feeds both the brightness and the colour trim.
            int devBrightness;
            var adjust = Nexus.Service.Lighting.DeviceColorAdjust.Identity;
            try
            {
                if (devicePrefs.TryGetValue(dev.Id, out var pref) && pref is not null)
                {
                    devBrightness = pref.Brightness;
                    adjust = Nexus.Service.Lighting.DeviceColorAdjust.For(pref);
                }
                else
                {
                    devBrightness = 100;
                }
            }
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
            else if (!adjust.IsIdentity && brightnessMul > 0.0)
            {
                for (int led = 0; led < writeLen; led++)
                {
                    var off2 = pos + led * 3;
                    adjust.Apply(frame[off2], frame[off2 + 1], frame[off2 + 2], brightnessMul,
                        out var ar, out var ag, out var ab);
                    buffer[zoneOffset + led] = new RgbColor(ar, ag, ab);
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
            if (_physBuffers.TryGetValue(physIdx, out var buf) && ShouldPush(physIdx, buf))
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

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
    /// <summary>Connect retry period while a daemon inside <see cref="DaemonWarmup"/> is not accepting yet.</summary>
    private static readonly TimeSpan ListenRetry = TimeSpan.FromMilliseconds(250);
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
    /// The visible list holds still while the daemon detects (a rescan empties its
    /// list and refills it). Without a STARTED seen on this session, detection counts as
    /// running only while progress arrives within this window; with one seen, it runs until
    /// COMPLETE or <see cref="DetectionStallLimit"/> of silence.
    /// </summary>
    private static readonly TimeSpan DetectionQuietWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BlackoutSettlePoll = TimeSpan.FromMilliseconds(25);
    /// <summary>A requested in-place rescan with no COMPLETE by now is a wedged detection pass: the next re-detect restarts the daemon instead.</summary>
    private static readonly TimeSpan DetectionStallLimit = TimeSpan.FromSeconds(60);
    /// <summary>A known RGB device that arrived over USB gets this long for the daemon's own hotplug to register it before a rescan is spent on it.</summary>
    private static readonly TimeSpan HotplugGrace = TimeSpan.FromSeconds(4);
    /// <summary>Consecutive connect/refresh failures (each one bounded socket timeout) against a warmed-up daemon before the watchdog restarts it.</summary>
    private const int WatchdogFailureThreshold = 2;
    /// <summary>A daemon younger than this is still binding its port; a refused connect is not a wedge.</summary>
    private static readonly TimeSpan DaemonWarmup = TimeSpan.FromSeconds(20);
    /// <summary>Minimum gap between watchdog bounces, so a daemon that re-wedges on every re-detect cannot flap lighting.</summary>
    private static readonly TimeSpan WatchdogFloor = TimeSpan.FromSeconds(60);
    /// <summary>Watchdog bounces per <see cref="WatchdogWindow"/> before it gives up until the next user rescan.</summary>
    private const int WatchdogMaxBounces = 3;
    private static readonly TimeSpan WatchdogWindow = TimeSpan.FromMinutes(10);

    // Covers the PawnIO install gate LhmComputer waits on plus the open itself.
    private static readonly TimeSpan LhmEnumerationWait = TimeSpan.FromSeconds(30);
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
    private CancellationTokenSource? _refreshCts;
    private Task? _shutdownTask;

    // Deferred spawn from the last Activate. Deactivate's shutdown task awaits
    // it before stopping, so a start that already passed its guard is always
    // stopped rather than left running unowned.
    private Task? _pendingStart;
    private long _lastConnectAttemptTicks; // DateTime.UtcNow.Ticks; updated via Interlocked
    // Watchdog state: counted where failures happen, decided inline, no timer.
    private int _daemonFailures;            // consecutive; Interlocked
    private bool _connectFailureLogged;     // one log line per disconnected episode
    private long _lastWatchdogBounceTicks;
    private long _watchdogWindowStartTicks;
    private int _watchdogBouncesInWindow;
    private bool _watchdogGaveUp;
    /// <summary>Test stand-in for both the watchdog's uptime and the reconnect's time since spawn (the process manager is sealed).</summary>
    internal Func<TimeSpan>? DaemonUptimeProbe { get; set; }
    private long _lastBounceTicks;         // DateTime.UtcNow.Ticks; updated via Interlocked
    // Last detection activity (our re-detect request, DETECTION_STARTED/PROGRESS); 0 once COMPLETE arrives. UtcNow ticks, Interlocked.
    private long _detectionActivityTicks;
    // 1 between a STARTED seen on this session and its COMPLETE.
    private int _detectionStartedSeen;
    // When the outstanding re-detect was requested; 0 when none or once COMPLETE arrives. UtcNow ticks, Interlocked.
    private long _redetectRequestedTicks;
    // Addresses of the committed list, compared against the cheap poll. Guarded by _lock.
    private int[] _committedAddresses = Array.Empty<int>();
    private long _lastRejectLogTicks;
    private long _lastRejectRefreshTicks;
    // A re-detect asked for while a pass was running, which a rescan would skip; replayed on COMPLETE. Guarded by _lock.
    private string? _deferredRedetectReason;
    private bool _deferredDetectorsChanged;
    // Completed and replaced on every committed list, so a USB arrival can wait for the device to show up.
    private TaskCompletionSource _listCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    // Resolved to a controller address only at drain, so a daemon restart between request and drain cannot aim it at another unit.
    private readonly ConcurrentQueue<PendingIdResize> _pendingIdZoneResizes = new();

    /// <summary>How long a resize waits for its device to be in the list before it is discarded.</summary>
    internal static readonly TimeSpan PendingResizeLifetime = TimeSpan.FromSeconds(60);

    internal readonly record struct PendingIdResize(string DeviceId, int ZoneIdx, int NewSize, long EnqueuedTicks);

    /// <summary>Splits queued resizes into those whose device is listed, those still waiting for it, and those past their lifetime.</summary>
    internal static void PartitionPendingResizes(IReadOnlyList<RgbDevice> devices, IEnumerable<PendingIdResize> pending, long nowTicks,
        List<(int physIdx, int zoneIdx, int newSize)> resolved, List<PendingIdResize> waiting, List<PendingIdResize> expired)
    {
        // Newest request per zone wins; an older one for the same zone would only resize it twice.
        var newest = new Dictionary<(string, int), PendingIdResize>();
        foreach (var p in pending)
        {
            if (!newest.TryGetValue((p.DeviceId, p.ZoneIdx), out var seen) || p.EnqueuedTicks >= seen.EnqueuedTicks)
            {
                newest[(p.DeviceId, p.ZoneIdx)] = p;
            }
        }
        foreach (var p in newest.Values)
        {
            var address = AddressOfDevice(devices, p.DeviceId);
            if (address >= 0)
            {
                resolved.Add((address, p.ZoneIdx, p.NewSize));
            }
            else if (nowTicks - p.EnqueuedTicks > PendingResizeLifetime.Ticks)
            {
                expired.Add(p);
            }
            else
            {
                waiting.Add(p);
            }
        }
    }
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
    // Physical index -> user cap in Hz for devices that have one, rebuilt every frame.
    private readonly Dictionary<int, int> _physCapsHz = new();
    // Physical index -> stable OpenRGB device id, the key LightingMaxRates uses. Replaced wholesale on device-set change.
    private volatile Dictionary<int, string> _physStableIds = new();
    private readonly PushRateLimiter<int> _pushLimiter = new();
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
    // Written by RefreshDevicesAsync (serialized by _refreshSemaphore); Deactivate resets it.
    private string _frameIdSignature = "";

    /// <summary>Raised when the engine's frame set changes (a device or zone appears, goes away, or gains its LEDs), under the refresh lock, and after a contributor change even when the frame set did not change, outside the lock; handlers must not block.</summary>
    public event Action? DeviceSetChanged;

    /// <summary>Raised once per refresh pass after a resize it sent shows its new count in the re-read, so views recount even though the device set did not change.</summary>
    public event Action? ZoneResizeApplied;

    private readonly IReadOnlyList<ILightingFrameContributor> _frameContributors;
    private readonly Nexus.Service.Lighting.Mappings.ContributorFrameLayouts _contributorLayouts;
    // Contributors that also implement IOpenRgbDeviceOwner. Cached at construction;
    // used in RefreshDevicesAsync to skip seeding engine frames for devices these
    // providers drive directly so OpenRGB never pushes conflicting colors to hardware.
    private readonly IReadOnlyList<Nexus.Service.Lighting.IOpenRgbDeviceOwner> _deviceOwners;

    private readonly FeatureGates _gates;
    private readonly Nexus.Service.Devices.DeviceControlGate? _controlGate;
    private readonly Nexus.Service.Peripherals.Hid.IHidEnumerator? _hid;

    public RgbBridge(OpenRgbProcessManager proc, IRgbController controller, LightingEngine engine, IConfigStore store, IUsbEnumerator usb,
        IEnumerable<ILightingFrameContributor>? frameContributors = null,
        Nexus.Service.Lighting.Mappings.ContributorFrameLayouts? contributorLayouts = null,
        FeatureGates? gates = null,
        Nexus.Service.Devices.DeviceControlGate? controlGate = null,
        Nexus.Service.Peripherals.Hid.IHidEnumerator? hid = null)
    {
        _proc = proc;
        _hid = hid;
        _controller = controller;
        _engine = engine;
        _store = store;
        _usb = usb;
        _gates = gates ?? FeatureGates.AllEnabled;
        _controlGate = controlGate;
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
        _controller.DetectionStateChanged += OnDetectionStateChanged;
        _controller.DetectionProgress += OnDetectionProgress;
        _controller.WriteRejected += OnWriteRejected;
        // Last: the handler runs on the caller's thread and reads this instance.
        if (controlGate is { } gate)
        {
            gate.Changed += OnControlGateChanged;
        }
    }

    /// <summary>
    /// The SMBus toggle rewrites the daemon's DRAM-detector denylist, and a native driver's toggle
    /// moves its OpenRGB detector on or off; the next detection pass reads both. Every other
    /// handler's gate is honored by its own connection worker.
    /// </summary>
    private void OnControlGateChanged(string handlerId, bool enabled)
    {
        if (string.Equals(handlerId, Nexus.Service.Devices.Handlers.SmbusDramHandler.HandlerId, StringComparison.OrdinalIgnoreCase))
        {
            ServiceLog.Info($"[rgb-bridge] SMBus DRAM detectors {(enabled ? "re-enabled" : "disabled")}: Nexus Control for the memory device turned {(enabled ? "on" : "off")}");
            Redetect("smbus-dram-gate", detectorsChanged: true);
            return;
        }
        if (NativeDetectorClaims.AffectsDetectors(handlerId) && _proc.NativeDetectorSetChanged())
        {
            ServiceLog.Info($"[rgb-bridge] native driver '{handlerId}' turned {(enabled ? "on" : "off")}: its OpenRGB detector follows");
            Redetect("native-driver-gate", detectorsChanged: true);
        }
    }

    // Fire-and-forget - RefreshDevicesAsync is the same path the periodic refresh loop
    // uses and is debounced by _refreshSemaphore.
    private void OnContributorDevicesChanged() => _ = RefreshAfterContributorChangeAsync();

    // A contributor change touching only cards with no engine frame (device page cards) leaves the frame set
    // as it was, so the lighting views are told here when the refresh did not already announce it.
    private async Task RefreshAfterContributorChangeAsync()
    {
        var before = _frameIdSignature;
        await RefreshDevicesAsync().ConfigureAwait(false);
        if (_frameIdSignature == before)
        {
            DeviceSetChanged?.Invoke();
        }
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
        CancellationToken refreshToken;
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
            ResetWatchdogLocked();

            newFrameHandler = OnFrame;
            _frameHandler = newFrameHandler;

            newDeviceListHandler = OnDeviceListChanged;
            _deviceListHandler = newDeviceListHandler;

            newCts = new CancellationTokenSource();
            _refreshCts = newCts;
            // Read under the lock: a Deactivate during the shutdown wait below
            // disposes the source, and .Token on a disposed source throws.
            refreshToken = newCts.Token;
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

        var pendingStart = Task.Run(async () =>
        {
            await Nexus.Service.Lifecycle.StartupDelayGate.WaitAsync().ConfigureAwait(false);

            // OpenRGB reaches DIMM RGB controllers over the SMBus through
            // PawnIO, and its detection runs exactly once - a bus probe that
            // returns nothing leaves that stick missing for the session. Two
            // startup hazards make that likely, and waiting for LHM's open
            // clears both: PawnIO may still be installing (OpenRGB then finds
            // zero busses and skips DIMMs entirely), and LHM's own SPD reads
            // drive the same controller; both sides take the
            // Global\Access_SMBUS.HTP.Method mutex per transaction, but a probe
            // that loses the race reads as an empty address for the whole
            // session. Completes immediately once startup is past.
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

            }
            // The list starts empty, so detection streams devices straight in;
            // this only drives the scanning spinner.
            Volatile.Write(ref _detectionStartedSeen, 0);
            MarkDetectionActivity();

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
            // Views counted the emptied list on the restart broadcast; the first refresh after Activate must re-announce even an unchanged set.
            _frameIdSignature = "";
            _directModeApplied = new();
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
            // Kill before disconnect: the kill is what frees a request parked on a silent daemon.
            _proc.Stop();
            try
            { await _controller.DisconnectAsync().ConfigureAwait(false); }
            catch { }
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
        => (await BlackoutCoreAsync(null, ct).ConfigureAwait(false)).Pushed;

    /// <summary>
    /// The same push as <see cref="BlackoutAsync"/>, but first waits until
    /// <paramref name="settleUntilUtc"/> for an in-progress detection or device
    /// refresh to commit, so the black frame reaches the full device list instead
    /// of the partial one a rescan leaves, and reports what it wrote. A wait that
    /// runs out, or a null deadline, still pushes to the devices known by then.
    /// </summary>
    internal Task<BlackoutReport> BlackoutSettledAsync(DateTime? settleUntilUtc, CancellationToken ct = default)
        => BlackoutCoreAsync(settleUntilUtc, ct);

    /// <summary>True while a detection pass or the refresh that commits its device list is outstanding; a rescan with no COMPLETE past the stall limit no longer counts.</summary>
    private bool DeviceListSettling()
    {
        var requested = Interlocked.Read(ref _redetectRequestedTicks);
        var requestedLive = requested != 0 && DateTime.UtcNow.Ticks - requested <= DetectionStallLimit.Ticks;
        return DetectionInProgress()
            || requestedLive
            || Volatile.Read(ref _refreshPending) != 0
            || _refreshSemaphore.CurrentCount == 0;
    }

    private async Task<BlackoutReport> BlackoutCoreAsync(DateTime? settleUntilUtc, CancellationToken ct)
    {
        if (!IsActive)
        {
            return new BlackoutReport(0, 0, BlackoutSkip.Inactive);
        }
        var unsettled = false;
        if (settleUntilUtc is { } until)
        {
            var confirmed = false;
            while (IsActive && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                if (DeviceListSettling())
                {
                    confirmed = false;
                }
                else if (confirmed)
                {
                    break;
                }
                else
                {
                    // The completion handler clears its flags a moment before the
                    // refresh that commits the list raises its own; one more poll
                    // tells that gap from settled.
                    confirmed = true;
                }
                try { await Task.Delay(BlackoutSettlePoll, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            unsettled = IsActive && DeviceListSettling();
        }
        if (!_controller.IsConnected)
        {
            return new BlackoutReport(0, _physBuffers.Count, BlackoutSkip.Disconnected);
        }
        var pushed = 0;
        var known = _physBuffers.Count;
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
                return new BlackoutReport(pushed, known, BlackoutSkip.Disconnected);
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
        return new BlackoutReport(pushed, known, unsettled ? BlackoutSkip.Unsettled : BlackoutSkip.None);
    }

    /// <summary>
    /// Triggered by the OS power-resume event handler. Tears down and restarts
    /// the subprocess so devices re-init after the USB stack re-enumerates.
    /// </summary>
    public void OnSystemResume()
    {
        lock (_lock)
        { ResetWatchdogLocked(); }
        BounceSubprocess("system-resume");
    }

    /// <summary>User-initiated rescan: re-runs detection inside the daemon (restarting it only when that cannot work).</summary>
    public void ForceRescan()
    {
        lock (_lock)
        { ResetWatchdogLocked(); }
        Redetect("user-rescan", detectorsChanged: false);
    }

    /// <summary>A competing RGB app exited: re-run detection for the controllers it held. Resets the watchdog, as the app may have been what the daemon was failing on.</summary>
    public void RecoverAfterConflictExit()
    {
        lock (_lock)
        { ResetWatchdogLocked(); }
        Redetect("conflict-exit", detectorsChanged: false);
    }

    private void ResetWatchdogLocked()
    {
        _watchdogGaveUp = false;
        _watchdogBouncesInWindow = 0;
        _watchdogWindowStartTicks = 0;
        _connectFailureLogged = false;
        Interlocked.Exchange(ref _daemonFailures, 0);
    }

    /// <summary>Restarts the daemon so it re-reads OpenRGB.json; manual device registrations are only picked up at launch.</summary>
    public void BounceForManualDevices() => BounceSubprocess("manual-devices");

    /// <summary>Queues a resize for the device with this stable id; its controller address is looked up when the resize is applied.</summary>
    public void RequestZoneResizeById(string deviceId, int zoneIndex, int newSize)
    {
        if (string.IsNullOrEmpty(deviceId) || zoneIndex < 0 || newSize < 0)
            return;
        _pendingIdZoneResizes.Enqueue(new PendingIdResize(deviceId, zoneIndex, newSize, DateTime.UtcNow.Ticks));
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

    /// <summary>The controller address of the device with this stable id, or -1 when it is not in the list.</summary>
    internal static int AddressOfDevice(IReadOnlyList<RgbDevice> devices, string deviceId)
    {
        foreach (var d in devices)
        {
            if (d.StableId == deviceId)
                return d.Address;
        }
        return -1;
    }

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

    /// <summary>
    /// Re-run detection inside the running daemon: push the rewritten detector
    /// settings when they changed, then REQUEST_RESCAN, which closes every
    /// controller and detects again. Falls back to a restart when there is no
    /// connection, the daemon refuses, or the previous pass never completed
    /// (a wedged pass makes every later rescan a no-op).
    /// </summary>
    private void Redetect(string reason, bool detectorsChanged)
    {
        if (RefuseRestartDuringShutdown(reason))
        {
            return;
        }
        if (!IsActive)
        {
            ServiceLog.Info($"[rgb-bridge] re-detect skipped ({reason}): bridge not active");
            return;
        }
        Task? pendingStart;
        lock (_lock)
        { pendingStart = _pendingStart; }
        if (pendingStart is not null && !pendingStart.IsCompleted)
        {
            ServiceLog.Info($"[rgb-bridge] re-detect skipped ({reason}): deferred spawn still pending");
            return;
        }
        var requested = Interlocked.Read(ref _redetectRequestedTicks);
        if (!_controller.IsConnected || (requested != 0 && DateTime.UtcNow.Ticks - requested > DetectionStallLimit.Ticks))
        {
            BounceSubprocess(reason);
            return;
        }
        if (DetectionInProgress())
        {
            // The daemon skips a rescan while a pass runs, and that pass may have
            // read the detector settings before this change.
            lock (_lock)
            {
                _deferredRedetectReason = reason;
                _deferredDetectorsChanged |= detectorsChanged;
            }
            ServiceLog.Info($"[rgb-bridge] re-detect ({reason}) deferred until the running detection completes");
            return;
        }

        lock (_lock)
        {
            if (reason is "user-rescan" or "conflict-exit")
            {
                _loggedDeviceSignatures.Clear();
            }
            _loggedResizeOutcomes.Clear();
        }
        ServiceLog.Info($"[rgb-bridge] re-detecting in place ({reason})");
        MarkDetectionActivity();
        Interlocked.Exchange(ref _redetectRequestedTicks, DateTime.UtcNow.Ticks);
        _ = Task.Run(async () =>
        {
            try
            {
                if (detectorsChanged)
                {
                    var detectors = _proc.RefreshDetectorOverrides();
                    if (detectors is null || !await _controller.SetSettingsAsync("Detectors", detectors).ConfigureAwait(false))
                    {
                        ServiceLog.Warn($"[rgb-bridge] re-detect ({reason}): daemon did not take the detector settings, restarting it");
                        BounceSubprocess(reason);
                        return;
                    }
                }
                if (!await _controller.RescanAsync().ConfigureAwait(false))
                {
                    ServiceLog.Warn($"[rgb-bridge] re-detect ({reason}): rescan refused, restarting the daemon");
                    BounceSubprocess(reason);
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[rgb-bridge] re-detect ({reason}) failed: {ex.Message}; restarting the daemon");
                BounceSubprocess(reason);
            }
        });
    }

    /// <summary>
    /// A device with neither serial nor location (an RTX Founders Edition) would
    /// take its id from its list position, which moves whenever a device ahead of
    /// it comes or goes and drags every per-device setting with it. Each keeps the
    /// id it first had, pinned by name and same-name ordinal. Runs before
    /// anything reads <see cref="RgbDevice.StableId"/>, which is cached.
    /// </summary>
    private void PinFallbackIds(IReadOnlyList<RgbDevice> devices)
    {
        var added = OpenRgbPinnedIds.Assign(devices, _store.Load().Devices.OpenRgbPinnedIds);
        if (added.Count == 0)
        {
            return;
        }
        _store.Update(s =>
        {
            var next = new Dictionary<string, string>(s.Devices.OpenRgbPinnedIds, StringComparer.Ordinal);
            foreach (var kv in added)
            {
                next[kv.Key] = kv.Value;
            }
            s.Devices.OpenRgbPinnedIds = next;
        });
    }

    /// <summary>Whether the OS is going down; replaceable so a test need not flip the process-wide flag.</summary>
    internal Func<bool> OsShuttingDown { get; set; } = () => Lifecycle.HostShutdown.IsOsShutdown;

    /// <summary>Once the OS is going down, a daemon restart or rescan only competes with the lights-off.</summary>
    private bool RefuseRestartDuringShutdown(string reason)
    {
        if (!OsShuttingDown())
        {
            return false;
        }
        ServiceLog.Info($"[rgb-bridge] {reason} skipped: OS shutting down");
        // A rescan already requested cannot be retried by the bounce it falls back to,
        // and no COMPLETE will clear these, so the lights-off must not wait on them.
        Interlocked.Exchange(ref _redetectRequestedTicks, 0);
        Interlocked.Exchange(ref _detectionActivityTicks, 0);
        Volatile.Write(ref _detectionStartedSeen, 0);
        OpenRgbDetectorMap.EndHold();
        return true;
    }

    private void MarkDetectionActivity()
    {
        Interlocked.Exchange(ref _detectionActivityTicks, DateTime.UtcNow.Ticks);
        OpenRgbDetectorMap.BeginHold();
    }

    /// <summary>True while detection is visibly running, so a commit would show a half-detected list.</summary>
    private bool DetectionInProgress()
    {
        var holdGeneration = OpenRgbDetectorMap.HoldGeneration;
        var last = Interlocked.Read(ref _detectionActivityTicks);
        var holds = last != 0
            && DetectionHolds(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - last), Volatile.Read(ref _detectionStartedSeen) != 0, _controller.IsConnected);
        if (!holds && OpenRgbDetectorMap.HoldActive)
        {
            OpenRgbDetectorMap.EndHold(holdGeneration);
        }
        return holds;
    }

    /// <summary>A detector can run long with no progress packet, so a seen STARTED holds until COMPLETE (or the stall limit); without one only the quiet window holds.</summary>
    internal static bool DetectionHolds(TimeSpan sinceActivity, bool startedSeen, bool connected)
        => sinceActivity < DetectionQuietWindow || (startedSeen && connected && sinceActivity < DetectionStallLimit);

    /// <summary>
    /// Progress only extends a pass already under way. Hotplug runs a detector
    /// for the arriving device with no STARTED around it, and holding the list
    /// for that would keep the new device undriven for the whole window.
    /// </summary>
    private void OnDetectionProgress()
    {
        if (DetectionInProgress())
        {
            MarkDetectionActivity();
        }
    }

    private void OnDetectionStateChanged(bool detecting)
    {
        if (detecting)
        {
            Volatile.Write(ref _detectionStartedSeen, 1);
            MarkDetectionActivity();
            return;
        }
        Volatile.Write(ref _detectionStartedSeen, 0);
        OpenRgbDetectorMap.EndHold();
        Interlocked.Exchange(ref _detectionActivityTicks, 0);
        Interlocked.Exchange(ref _redetectRequestedTicks, 0);
        string? deferred;
        bool deferredDetectors;
        lock (_lock)
        {
            deferred = _deferredRedetectReason;
            deferredDetectors = _deferredDetectorsChanged;
            _deferredRedetectReason = null;
            _deferredDetectorsChanged = false;
        }
        if (!IsActive)
        {
            return;
        }
        if (deferred is not null && !OsShuttingDown())
        {
            Redetect(deferred, deferredDetectors);
            return;
        }
        ScheduleRefresh();
    }

    /// <summary>A refused write means our map of the daemon is wrong (sizes or ids): re-read it.</summary>
    private void OnWriteRejected(int address, uint packetId, uint status)
    {
        // During a rescan every push targets ids that no longer exist; COMPLETE re-reads.
        if (!IsActive || DetectionInProgress())
        {
            return;
        }
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastRejectLogTicks);
        if (now - last > TimeSpan.FromSeconds(30).Ticks && Interlocked.CompareExchange(ref _lastRejectLogTicks, now, last) == last)
        {
            ServiceLog.Warn($"[rgb-bridge] daemon refused packet {packetId} for controller {address} (status {status}); re-reading the device list");
        }
        // Bounded, so a refusal that survives a re-read cannot turn into a fetch per frame.
        var lastRefresh = Interlocked.Read(ref _lastRejectRefreshTicks);
        if (now - lastRefresh > TimeSpan.FromSeconds(2).Ticks && Interlocked.CompareExchange(ref _lastRejectRefreshTicks, now, lastRefresh) == lastRefresh)
        {
            ScheduleRefresh();
        }
    }

    private void BounceSubprocess(string reason)
    {
        if (RefuseRestartDuringShutdown(reason))
        {
            return;
        }
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
        lock (_lock)
        {
            // A bounce reconstructs every controller, so an identical list is still
            // new information. Only these reasons can settle unchanged; a
            // topology or exclusion bounce alters the list, so its signature re-logs
            // on its own and clearing here would re-print the block on every flap.
            if (reason is "user-rescan" or "system-resume" or "watchdog" or "conflict-exit")
            {
                _loggedDeviceSignatures.Clear();
            }
            _loggedResizeOutcomes.Clear();
        }
        ServiceLog.Info($"[rgb-bridge] bouncing subprocess ({reason})");
        Volatile.Write(ref _detectionStartedSeen, 0);
        MarkDetectionActivity();
        Interlocked.Exchange(ref _redetectRequestedTicks, 0);
        Interlocked.Exchange(ref _daemonFailures, 0);
        _ = Task.Run(async () =>
        {
            _proc.Stop();
            try
            { await _controller.DisconnectAsync().ConfigureAwait(false); }
            catch { }
            await Task.Delay(500).ConfigureAwait(false);
            _proc.Start();
            // The relaunch rewrote the overrides and detects from scratch.
            lock (_lock)
            {
                _deferredRedetectReason = null;
                _deferredDetectorsChanged = false;
            }
            ServiceLog.Info($"[rgb-bridge] bounce ({reason}): daemon restarted, reconnecting");
            await EnsureConnectedAsync().ConfigureAwait(false);
        });
    }

    /// <summary>One connect/refresh failure; past the threshold and outside the floor and per-window cap, bounces the daemon.</summary>
    private void RecordDaemonFailure(string what, string detail)
    {
        var uptime = (DaemonUptimeProbe ?? (() => _proc.Uptime))();
        if (uptime < DaemonWarmup)
        {
            return;
        }
        var now = DateTime.UtcNow.Ticks;
        lock (_lock)
        {
            // A fresh window re-arms a watchdog that gave up in the previous one.
            if (_watchdogWindowStartTicks == 0 || now - _watchdogWindowStartTicks > WatchdogWindow.Ticks)
            {
                _watchdogWindowStartTicks = now;
                _watchdogBouncesInWindow = 0;
                _watchdogGaveUp = false;
            }
            if (_watchdogGaveUp)
            {
                return;
            }
        }
        var failures = Interlocked.Increment(ref _daemonFailures);
        ServiceLog.Warn($"[rgb-bridge] daemon {what} failed ({failures}/{WatchdogFailureThreshold}, daemon up {uptime.TotalSeconds:F0}s): {detail}");
        if (failures < WatchdogFailureThreshold)
        {
            return;
        }
        Interlocked.Exchange(ref _daemonFailures, 0);

        lock (_lock)
        {
            if (_lastWatchdogBounceTicks != 0 && now - _lastWatchdogBounceTicks < WatchdogFloor.Ticks)
            {
                return;
            }
            if (_watchdogBouncesInWindow >= WatchdogMaxBounces)
            {
                _watchdogGaveUp = true;
                ServiceLog.Warn($"[rgb-bridge] watchdog: {WatchdogMaxBounces} restarts in {WatchdogWindow.TotalMinutes:F0} min and the daemon keeps wedging; giving up until the next rescan");
                return;
            }
            _watchdogBouncesInWindow++;
            _lastWatchdogBounceTicks = now;
        }
        BounceSubprocess("watchdog");
    }

    /// <summary>True while the daemon is detecting. Exposed via /lighting/status for the rescan spinner.</summary>
    public bool IsRescanning => DetectionInProgress();

    /// <summary>
    /// Debounced refresh trigger for DEVICE_LIST_UPDATED bursts. OpenRGB can
    /// fire several in quick succession (one per detected plugin result); we
    /// only want to query the device list once per burst.
    /// </summary>
    private void OnDeviceListChanged()
    {
        OpenRgbDetectorMap.Invalidate();
        ScheduleRefresh();
    }

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
        var sinceSpawn = (DaemonUptimeProbe ?? (() => _proc.SinceLastSpawn))();
        var starting = sinceSpawn > TimeSpan.Zero && sinceSpawn < DaemonWarmup;
        if (!_controller.IsConnected && (now - prev) < (starting ? ListenRetry : ReconnectCooldown).Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastConnectAttemptTicks, now, prev) != prev)
        {
            return;
        }

        if (!_controller.IsConnected)
        {
            // A new daemon may number its controllers differently, so nothing is
            // pushed until a refresh commits this daemon's own list. A restarted
            // daemon reuses ids from 0, so the committed id list is reset too or
            // the poll would read the new list as unchanged.
            _physBuffers.Clear();
            lock (_lock)
            {
                _committedAddresses = Array.Empty<int>();
            }
            bool connected;
            try
            { connected = await _controller.TryConnectAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                RecordDaemonFailure("connect", $"{ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (!connected)
            {
                if (!_connectFailureLogged)
                {
                    _connectFailureLogged = true;
                    ServiceLog.Info($"[rgb-bridge] connect to daemon failed (running={_proc.IsRunning}, spawned {sinceSpawn.TotalSeconds:F1}s ago); retrying");
                }
                RecordDaemonFailure("connect", "no SDK handshake");
                if (starting)
                {
                    // A refused connect inside warm-up raced the daemon's bind; poll until it accepts.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(ListenRetry).ConfigureAwait(false);
                        await EnsureConnectedAsync().ConfigureAwait(false);
                    });
                }
                return;
            }
            _connectFailureLogged = false;

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

            // A rescan empties the daemon's list and refills it, so a shown list
            // holds still while detection runs; the COMPLETE handler refreshes.
            // An empty shown list (first start) takes devices as they arrive.
            // Frames meanwhile carry the old ids, which no longer exist after a
            // rescan, and a restarted daemon starts with no push buffers at all.
            if (DetectionInProgress())
            {
                lock (_lock)
                {
                    if (_devices.Count > 0)
                    {
                        return;
                    }
                }
            }

            IReadOnlyList<RgbDevice> devices;
            try
            {
                devices = await _controller.GetDevicesAsync().ConfigureAwait(false);
                PinFallbackIds(devices);
            }
            catch (Exception ex)
            {
                // The controller dropped the socket on the way out; the next tick reconnects.
                RecordDaemonFailure("refresh", ex.Message);
                return;
            }
            Interlocked.Exchange(ref _daemonFailures, 0);

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
                    PinFallbackIds(devices);
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
                    if (AnyResizeTook(resizeAttempts, devices))
                    {
                        ZoneResizeApplied?.Invoke();
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
                    isNew = !_directModeApplied.Contains(dev.Address);
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
                        _directModeApplied.Add(dev.Address);
                    }
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[rgb-bridge] SetDirectMode controller {dev.Address} failed: {ex.Message}");
                }
            }

            IReadOnlyList<RgbDevice> finalList = devices;
            var inDetection = DetectionInProgress();
            lock (_lock)
            {
                _devices = finalList;
                var addresses = new int[finalList.Count];
                for (var i = 0; i < addresses.Length; i++)
                {
                    addresses[i] = finalList[i].Address;
                }
                _committedAddresses = addresses;
                // Ids are never reused within a daemon run, so ones gone from the list are dead.
                _directModeApplied.IntersectWith(addresses);
            }
            Interlocked.Exchange(ref _listCommitted, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            // Move any legacy per-device state on a split motherboard onto its
            // ARGB header ids before this refresh resolves partitions and
            // builds frames below. OpenRgbLightingDeviceProvider.GetStructures
            // runs the same migration, but only when some other caller asks
            // for structures first - too late once BuildOrReuseFrame has
            // already cached a frame for the unmigrated shape.
            if (SplitMotherboardDeviceMigration.NeedsApply(settingsSnapshot, finalList))
            {
                _store.Update(s => SplitMotherboardDeviceMigration.Apply(s, finalList));
                settingsSnapshot = _store.Load();
            }

            // Settled commits reconcile detector exclusions: a device the user
            // fully un-controlled gets snapshotted + denylisted (and re-detected
            // to release it); a re-controlled one gets its exclusion lifted the
            // same way.
            if (!inDetection)
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
                // Excluded devices render from their persisted snapshot; a live
                // entry fetched before the exclusion's re-detect must not occupy
                // an engine frame or a canvas slot.
                if (settingsSnapshot.Devices.OpenRgbDetectorExclusions.ContainsKey(d.StableId)) continue;
                var baseId = d.StableId;

                // A split motherboard is one device per ARGB header, so each
                // port resolves its OWN partition: a chain wired to one header
                // emits a frame per product while the others keep their single
                // frame. Resolving the board instead would find no partition
                // (chains are stored under the port id) and every chained card
                // would go frameless - visible as cards that never light.
                if (OpenRgbZoneSupport.IsSplitMotherboard(d))
                {
                    foreach (var port in OpenRgbZoneSupport.BuildStructures(d, settingsSnapshot))
                    {
                        // A chain's slices are in the port's EFFECTIVE count,
                        // which only equals the hardware count once the header
                        // accepted the RESIZEZONE. On one that ignores it (the
                        // 12V case) the later links would otherwise resolve
                        // past this header and write the next one's LEDs, so
                        // the port's own region is a hard wall.
                        var portEnd = port.FrameBaseOffset + port.Segments[0].FrameLedCount;
                        foreach (var portZone in Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(port, settingsSnapshot))
                        {
                            var frameOffset = Nexus.Service.Lighting.Zones.ZoneResolution.FrameOffset(port, portZone);
                            BuildOrReuseFrame(portZone.Id, d, physicalIndex: d.Address,
                                zoneIndex: portZone.IsDefault
                                    ? portZone.LegacyZoneIndex
                                    : Nexus.Service.Lighting.Zones.ZoneResolution.WholeResizableSegment(port, portZone, settingsSnapshot),
                                zoneOffset: Math.Min(frameOffset, portEnd),
                                zoneLedCount: Math.Max(0, Math.Min(portZone.FrameLedCount, portEnd - frameOffset)),
                                existingFrames, layouts, stripSlot, logicalOrdinal, framesList,
                                isStrip: true, settingsSnapshot, port, portZone);
                            stripSlot++;
                            logicalOrdinal++;
                        }
                    }
                    continue;
                }

                var structure = OpenRgbZoneSupport.BuildStructure(d, settingsSnapshot);
                var zones = Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(structure, settingsSnapshot);
                var isDefault = zones.Count > 0 && zones[0].IsDefault;

                if (isDefault)
                {
                    BuildOrReuseFrame(baseId, d, physicalIndex: d.Address, zoneIndex: -1, zoneOffset: 0, zoneLedCount: d.LedCount,
                        existingFrames, layouts, cardSlot, logicalOrdinal, framesList, isStrip: false, settingsSnapshot,
                        structure, zones[0]);
                    cardSlot++;
                    logicalOrdinal++;
                    continue;
                }

                foreach (var zone in zones)
                {
                    var frameOffset = Nexus.Service.Lighting.Zones.ZoneResolution.FrameOffset(structure, zone);
                    var wholeSegment = Nexus.Service.Lighting.Zones.ZoneResolution.WholeResizableSegment(structure, zone, settingsSnapshot);
                    BuildOrReuseFrame(zone.Id, d, physicalIndex: d.Address, zoneIndex: wholeSegment, zoneOffset: frameOffset,
                        zoneLedCount: zone.FrameLedCount, existingFrames, layouts,
                        cardSlot, logicalOrdinal, framesList,
                        isStrip: false, settingsSnapshot, structure, zone);
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
            if (settingsSnapshot.MaximizedCanvasDefaults)
            {
                // Under the store lock: preset load and batch layouts Clear() and refill DeviceLayouts in place.
                _store.Read(s => CanvasGridLayout.MaximizeUnplaced(frames, s.Lighting.DeviceLayouts));
            }
            var liveIds = new List<string>(frames.Length);
            foreach (var f in frames) liveIds.Add(f.Id);
            _contributorLayouts.Prune(liveIds);
            _engine.UpdateDevices(frames);

            // The LED bit counts: /devices/lighting-devices/all hides a 0-LED device until its LEDs arrive.
            var setKeys = new string[frames.Length];
            for (var i = 0; i < frames.Length; i++)
            {
                // The frame index rides in the key: clients map ids to frame bytes
                // through it, so a reorder with the same ids must notify too.
                setKeys[i] = (frames[i].LedCount > 0 ? frames[i].Id : frames[i].Id + "#0") + "@" + frames[i].Index;
            }
            Array.Sort(setKeys, StringComparer.Ordinal);
            var signature = string.Join('\n', setKeys);
            if (signature != _frameIdSignature)
            {
                _frameIdSignature = signature;
                DeviceSetChanged?.Invoke();
            }

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
            existing.DeviceId = structure?.DeviceId ?? id;
            framesList.Add(existing);
            return;
        }

        layouts.TryGetValue(id, out var layout);
        var rot = existing?.Rotation ?? layout?.Rotation ?? 0;
        rot = ((rot % 360) + 360) % 360;
        // Defaults come from the same helpers the HTTP DTO uses, so DeviceFrame
        // + LightingDevice agree on on-canvas size/position when nothing's been
        // persisted yet.
        var (dx, dy, dw, dh) = isStrip
            ? OpenRgbLightingDeviceProvider.DefaultStripLayout(canvasSlot)
            : OpenRgbLightingDeviceProvider.DefaultCardLayout(canvasSlot, physicalDevice);
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
        frame.DeviceId = structure?.DeviceId ?? id;
        if (frame.Archetype == "keyboard")
        {
            frame.LedKeys = zone is null || zone.IsDefault
                ? LedNameSlice(physicalDevice, resolved.GlobalOffset, zoneLedCount)
                : LedNamesForSlices(physicalDevice, zone.Slices, zoneLedCount);
        }
        framesList.Add(frame);
    }

    private static string?[]? LedNameSlice(RgbDevice d, int offset, int count)
    {
        if (offset < 0 || count <= 0 || d.LedNames.Count < offset + count) return null;
        var names = new string?[count];
        for (var i = 0; i < count; i++) names[i] = d.LedNames[offset + i];
        return names;
    }

    /// <summary>Names for a custom partition: each slice is a run inside one OpenRGB zone, whose LEDs start after every earlier zone's.</summary>
    private static string?[] LedNamesForSlices(RgbDevice d, IReadOnlyList<ZoneSlice> slices, int count)
    {
        var names = new string?[Math.Max(0, count)];
        var pos = 0;
        foreach (var slice in slices)
        {
            var zoneStart = 0;
            for (var z = 0; z < slice.Segment && z < d.Zones.Count; z++) zoneStart += d.Zones[z].LedCount;
            for (var i = 0; i < slice.Count && pos + i < names.Length; i++)
            {
                var led = zoneStart + slice.Start + i;
                if (led >= 0 && led < d.LedNames.Count) names[pos + i] = d.LedNames[led];
            }
            pos += slice.Count;
        }
        return names;
    }

    /// <summary>A device Nexus drives through OpenRGB: not claimed by a native provider, not detector-excluded and not left uncontrolled.</summary>
    internal bool IsSizableDevice(RgbDevice d, NexusSettings settings)
        => !IsOwnedByFirstParty(d)
            && !settings.Devices.OpenRgbDetectorExclusions.ContainsKey(d.StableId)
            && !OpenRgbZoneSupport.IsFullyUncontrolled(d, settings);

    internal bool IsOwnedByFirstParty(RgbDevice d)
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
            ServiceLog.Info($"[rgb-bridge]   [{d.Index}] id={d.Address} {d.Name} leds={d.LedCount}{DescribeZones(d)}"
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
    /// re-detect with the rewritten denylist.
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
            // the daemon, so surface it once per device. Skipped while the
            // exclusion's own re-detect is outstanding, so a list fetched before
            // it ran never spends the one warn per id.
            if (Interlocked.Read(ref _redetectRequestedTicks) == 0)
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
        Redetect("detector-exclusions", detectorsChanged: true);
    }

    /// <summary>True when this device's bytes differ from the last push, or the
    /// heartbeat is due. Records what it approves as the new baseline.
    /// Compares as bytes: RgbColor has no IEquatable, so Span.SequenceEqual over
    /// the structs themselves would fall back to reflective ValueType.Equals and
    /// cost more than the push it saves.</summary>
    internal bool ShouldPush(int physIdx, RgbColor[] buf, int capHz = 0, long? nowMs = null)
    {
        var now = nowMs ?? Environment.TickCount64;
        if (_lastPushed.TryGetValue(physIdx, out var last)
            && last.Length == buf.Length
            && _lastPushTicks.TryGetValue(physIdx, out var when)
            && now - when < HeartbeatMs
            && System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan())
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(last.AsSpan())))
        {
            return false;
        }

        // After the dedupe so an identical frame never spends the gap. A frame
        // dropped here is not recorded as pushed, so the next OnFrame offers
        // the then-current buffer again and sends it once the gap has elapsed.
        if (capHz > 0 && !_pushLimiter.TryPass(physIdx, capHz, now))
        {
            return false;
        }

        var copy = last is not null && last.Length == buf.Length ? last : new RgbColor[buf.Length];
        buf.AsSpan().CopyTo(copy);
        _lastPushed[physIdx] = copy;
        _lastPushTicks[physIdx] = now;
        return true;
    }

    /// <summary>
    /// Fills <paramref name="caps"/> (cleared first) with physical index -> Hz for
    /// every bridge frame whose owning device has a cap. The cap is keyed by the
    /// physical device, so all zone frames sharing a push resolve the same value.
    /// </summary>
    internal static void ComputePhysicalCaps(
        IReadOnlyList<DeviceFrame> deviceFrames,
        IReadOnlySet<string> bridgeFrameIds,
        IReadOnlyDictionary<int, string> physStableIds,
        IReadOnlyDictionary<string, int> maxRates,
        Dictionary<int, int> caps)
    {
        caps.Clear();
        if (maxRates.Count == 0)
        {
            return;
        }
        foreach (var f in deviceFrames)
        {
            if (IsBridgeFrame(f, bridgeFrameIds)
                && physStableIds.TryGetValue(f.PhysicalIndex, out var key)
                && maxRates.TryGetValue(key, out var hz) && hz > 0)
            {
                caps[f.PhysicalIndex] = hz;
            }
        }
    }

    internal void SyncPhysicalBuffers(IReadOnlyList<RgbDevice> devices)
    {
        // Indices are being re-derived, so every dedup baseline is suspect.
        _lastPushed.Clear();
        _lastPushTicks.Clear();
        _pushLimiter.Reset();
        var stableIds = new Dictionary<int, string>();
        foreach (var d in devices)
        {
            if (d.LedCount > 0)
            {
                stableIds[d.Address] = d.StableId;
            }
        }
        _physStableIds = stableIds;
        SizePhysicalBuffers(_physBuffers, devices);
    }

    /// <summary>
    /// One buffer per drivable index, sized to exactly its LED count: the daemon
    /// rejects an UpdateLEDs carrying more colours than the controller has, so a
    /// buffer left by a larger device that held the index before a reshuffle
    /// would silence the device now there.
    /// </summary>
    internal static void SizePhysicalBuffers(ConcurrentDictionary<int, RgbColor[]> buffers, IReadOnlyList<RgbDevice> devices)
    {
        var drivable = new HashSet<int>();
        foreach (var d in devices)
        {
            if (d.LedCount <= 0)
                continue;
            drivable.Add(d.Address);
            if (!buffers.TryGetValue(d.Address, out var buf) || buf.Length != d.LedCount)
            {
                buffers[d.Address] = new RgbColor[d.LedCount];
            }
        }
        foreach (var k in buffers.Keys)
        {
            if (!drivable.Contains(k))
                buffers.TryRemove(k, out _);
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

        var drained = new List<PendingIdResize>();
        while (_pendingIdZoneResizes.TryDequeue(out var byId))
        {
            drained.Add(byId);
        }
        if (drained.Count > 0)
        {
            var resolved = new List<(int physIdx, int zoneIdx, int newSize)>();
            var waiting = new List<PendingIdResize>();
            var expired = new List<PendingIdResize>();
            PartitionPendingResizes(devices, drained, DateTime.UtcNow.Ticks, resolved, waiting, expired);
            var settings = _store.Load();
            foreach (var r in resolved)
            {
                var target = FindDevice(devices, r.physIdx);
                if (target is null || !IsSizableDevice(target, settings))
                {
                    ServiceLog.Warn($"[rgb-bridge] resize of controller {r.physIdx} zone {r.zoneIdx} dropped: the device is no longer sizable through OpenRGB");
                    continue;
                }
                _pendingZoneResizes.Enqueue(r);
            }
            foreach (var w in waiting)
            {
                _pendingIdZoneResizes.Enqueue(w);
            }
            foreach (var e in expired)
            {
                ServiceLog.Warn($"[rgb-bridge] resize of {e.DeviceId} zone {e.ZoneIdx} to {e.NewSize} discarded: the device did not appear within {PendingResizeLifetime.TotalSeconds:0}s");
            }
        }

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
            if (!OpenRgbZoneSupport.IsSplitMotherboard(d))
                continue;
            for (int z = 0; z < d.Zones.Count; z++)
            {
                var zoneId = $"{d.StableId}-{z}";
                if (!persisted.TryGetValue(zoneId, out var desired))
                    continue;
                // Covers counts persisted before the cap or edited by hand.
                desired = OpenRgbZoneSupport.ClampPortLedCount(d, z, desired);
                if (desired == d.Zones[z].LedCount)
                    continue;
                // Resizing a zone the controller pins never holds, and this pass
                // reruns every refresh, so an unsatisfiable count would retry forever.
                if (d.Zones[z].IsFixedSize)
                    continue;
                try
                {
                    await _controller.ResizeZoneAsync(d.Address, z, desired).ConfigureAwait(false);
                    attempts.Add(new ZoneResizeAttempt(d.Address, z, ZoneName(d.Zones[z], z), d.Zones[z].LedCount, desired, "persisted"));
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
        // leds_max and is excluded; the user can override the seeded count via
        // the LED-count editor. Port controllers are never seeded: a hardware-counted
        // one (NZXT Hue+) reporting 0 would have the guess saved over its own count.
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
                toDefault.Add((d.Address, z, zoneId));
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
    internal readonly record struct ZoneResizeAttempt(int PhysIdx, int ZoneIdx, string ZoneName, int From, int To, string Source);

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

    /// <summary>True when any attempted resize now reads back as its requested count.</summary>
    internal static bool AnyResizeTook(IReadOnlyList<ZoneResizeAttempt> attempts, IReadOnlyList<RgbDevice> devices)
    {
        foreach (var a in attempts)
        {
            if (DescribeZone(devices, a.PhysIdx, a.ZoneIdx).Current == a.To)
            {
                return true;
            }
        }
        return false;
    }

    private static RgbDevice? FindDevice(IReadOnlyList<RgbDevice> devices, int address)
    {
        foreach (var d in devices)
        {
            if (d.Address == address)
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
                    // With no effect publishing frames this is the only reconnect path; it rate-limits itself.
                    await EnsureConnectedAsync().ConfigureAwait(false);
                    continue;
                }
                await PollAsync().ConfigureAwait(false);

                // USB hot-plug watcher: cheap-ish enumeration every other tick.
                if (++tick % UsbCheckEveryNTicks == 0)
                {
                    CheckUsbTopology();
                    CheckHeldOffGamepads();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rgb-bridge] refresh loop crashed: {ex.Message}");
        }
    }

    /// <summary>
    /// Liveness and change check on the refresh cadence. The controller-count
    /// request takes no controller lock, so a controller stuck on a contended bus
    /// cannot time it out and restart the daemon the way a full per-controller
    /// fetch every tick could. The full fetch runs when the id list moved;
    /// events (list updated, detection complete, a refused write) trigger it too.
    /// </summary>
    private async Task PollAsync()
    {
        if (DetectionInProgress())
        {
            return;
        }
        // A COMPLETE can be missed (session dropped mid-pass, or connected after it).
        string? deferred;
        bool deferredDetectors;
        lock (_lock)
        {
            deferred = _deferredRedetectReason;
            deferredDetectors = _deferredDetectorsChanged;
            _deferredRedetectReason = null;
            _deferredDetectorsChanged = false;
        }
        if (deferred is not null && !OsShuttingDown())
        {
            Redetect(deferred, deferredDetectors);
            return;
        }
        // A full fetch holds the socket for one request per controller; waiting
        // behind it would read as a daemon failure and restart a healthy daemon.
        if (_refreshSemaphore.CurrentCount == 0)
        {
            return;
        }
        IReadOnlyList<int> addresses;
        try
        {
            addresses = await _controller.GetControllerAddressesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordDaemonFailure("refresh", ex.Message);
            return;
        }
        Interlocked.Exchange(ref _daemonFailures, 0);
        bool changed;
        lock (_lock)
        {
            changed = !SameAddresses(addresses, _committedAddresses);
        }
        if (changed)
        {
            await RefreshDevicesAsync().ConfigureAwait(false);
        }
    }

    private static bool SameAddresses(IReadOnlyList<int> a, int[] b)
    {
        if (a.Count != b.Length)
        {
            return false;
        }
        for (var i = 0; i < b.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when every arrived "VVVV:PPPP:..." key has a committed device whose HID path names that vid/pid.</summary>
    private bool IsListed(List<string> arrivals)
    {
        lock (_lock)
        {
            foreach (var key in arrivals)
            {
                if (key.Length < 9)
                {
                    return false;
                }
                var token = $"VID_{key.Substring(0, 4)}&PID_{key.Substring(5, 4)}";
                var found = false;
                foreach (var d in _devices)
                {
                    if (d.Location.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Shows or drops the off card of each held-off device (<see cref="OpenRgbGamepadDefaults"/>) as it comes and goes. HID enumeration sees Bluetooth pads too and opens at most query-only, never sending a report.</summary>
    private void CheckHeldOffGamepads()
    {
        if (_hid is null)
        {
            return;
        }
        var heldOff = OpenRgbGamepadDefaults.HeldOffDetectors(_store.Load());
        if (heldOff.Count == 0)
        {
            return;
        }
        var present = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var e in OpenRgbGamepadDefaults.Entries)
            {
                if (heldOff.Contains(e.Detector) && !present.Contains(e.Detector) && _hid.Find(e.VendorId, e.ProductId).Count > 0)
                {
                    present.Add(e.Detector);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rgb-bridge] gamepad presence check failed: {ex.Message}");
            return;
        }
        // Update marks settings dirty (flush + profile upload), so only when the cards move.
        if (!OpenRgbGamepadDefaults.PresenceChanges(_store.Load(), present))
        {
            return;
        }
        var changed = false;
        _store.Update(s => changed = OpenRgbGamepadDefaults.ApplyPresence(s, present));
        if (changed)
        {
            ServiceLog.Info($"[rgb-bridge] held-off devices present: {(present.Count == 0 ? "none" : string.Join(", ", present))}");
            RequestTopologyRefresh();
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

        var (added, removed, _) = UsbTopologyFilter.Classify(previous, current, Nexus.Service.Peripherals.LightingDevicesCatalog.UsbVendorIds);
        if (added == 0 && removed == 0)
        {
            return;
        }
        // Every detection pass walks every HID device on the bus, which USB
        // audio devices have been seen to choke on, so one is spent only on a
        // known RGB device the daemon's own hotplug did not pick up.
        var arrivals = UsbTopologyFilter.KnownRgbArrivals(previous, current, Nexus.Service.Peripherals.LightingDevicesCatalog.OpenRgbUsbIds);
        var settings = _store.Load();
        arrivals.RemoveAll(key => UsbTopologyFilter.TryParseVidPid(key, out var vid, out var pid) && OpenRgbGamepadDefaults.IsHeldOff(settings, vid, pid));
        if (arrivals.Count == 0)
        {
            ServiceLog.Info($"[rgb-bridge] usb topology changed (+{added}/-{removed}), no known RGB device arrived, no re-detect");
            return;
        }
        _ = Task.Run(async () =>
        {
            // The daemon's hotplug often registers the device before this poll
            // even sees it arrive, so the test is presence, not a fresh event.
            var deadline = DateTime.UtcNow + HotplugGrace;
            var registered = IsListed(arrivals);
            while (!registered && IsActive)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                var committed = Volatile.Read(ref _listCommitted).Task;
                await Task.WhenAny(committed, Task.Delay(remaining)).ConfigureAwait(false);
                registered = IsListed(arrivals);
            }
            if (!IsActive)
            {
                return;
            }
            if (registered)
            {
                ServiceLog.Info($"[rgb-bridge] usb arrival {arrivals[0]} registered by the daemon's hotplug, no re-detect");
                return;
            }
            var now = DateTime.UtcNow.Ticks;
            var lastBounce = Interlocked.Read(ref _lastBounceTicks);
            if (lastBounce != 0 && now - lastBounce < BounceCooldown.Ticks)
            {
                return;
            }
            Interlocked.Exchange(ref _lastBounceTicks, now);
            ServiceLog.Info($"[rgb-bridge] usb arrival {arrivals[0]} not registered within {HotplugGrace.TotalSeconds:F0}s, re-detecting");
            Redetect("usb-topology", detectorsChanged: false);
        });
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
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);
        var nowTicks = DateTime.UtcNow.Ticks;

        _touchedPhysicals.Clear();
        ComputeFullyUncontrolledPhysicals(deviceFrames, _bridgeFrameIds, uncontrolled, _physFullyUncontrolled);
        ComputePhysicalCaps(deviceFrames, _bridgeFrameIds, _physStableIds, settings.Devices.LightingMaxRates, _physCapsHz);

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
            if (_physBuffers.TryGetValue(physIdx, out var buf)
                && ShouldPush(physIdx, buf, _physCapsHz.GetValueOrDefault(physIdx)))
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
        if (_controlGate is { } gate)
        {
            gate.Changed -= OnControlGateChanged;
        }
        _controller.DetectionStateChanged -= OnDetectionStateChanged;
        _controller.DetectionProgress -= OnDetectionProgress;
        _controller.WriteRejected -= OnWriteRejected;
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

/// <summary>Why a blackout push wrote nothing, stopped short, or ran against an unsettled device list.</summary>
internal enum BlackoutSkip
{
    None,
    Inactive,
    Disconnected,
    Unsettled,
}

/// <summary>What a blackout push did: physicals written, physicals known, and why it stopped short if it did.</summary>
internal readonly record struct BlackoutReport(int Pushed, int Known, BlackoutSkip Skipped)
{
    public override string ToString() => Skipped switch
    {
        BlackoutSkip.None => $"pushed {Pushed}/{Known}",
        BlackoutSkip.Inactive => "skipped:inactive",
        BlackoutSkip.Disconnected => $"skipped:disconnected (pushed {Pushed}/{Known})",
        _ => $"pushed {Pushed}/{Known} (device list still settling)",
    };
}
